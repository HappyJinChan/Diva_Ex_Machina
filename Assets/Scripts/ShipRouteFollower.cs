using System;
using UnityEngine;

/// <summary>
/// 讓母艦沿著 ShipRouteGraph 航行。取代 MoveWithVelocity（Spline 版）；
/// 推進、轉向、鏡頭同步、高度固定的做法跟 MoveWithVelocity 相同，只是路徑來源換成航線圖。
///
/// 分叉：
///   · 船一進入「通往分叉點」的那一段，就先決定好分叉後要走哪條（PlannedNext），並觸發 JunctionAhead。
///   · 抵達分叉點之前，隨時可以用 ChooseBranch 改選（UI、劇情、任務都可以呼叫）。
///   · 沒有人改的話，依 branchPolicy：Default = 節點的 defaultBranch；Random = 隨機。
///
/// 停船：SetStopped(true)。船會用 maxAccel 減速停下，之後 SetStopped(false) 從原地繼續。
/// 死路：走到沒有出口的節點會自動減速停在節點上。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class ShipRouteFollower : MonoBehaviour
{
    public enum BranchPolicy { Default, Random }

    [Header("Route")]
    public ShipRouteGraph graph;
    [Tooltip("開始時接到最近的一段航線上。關掉的話從 startNode 出發。")]
    public bool startFromNearestPoint = true;
    [Tooltip("startFromNearestPoint 關閉時的出發點。")]
    public ShipRouteNode startNode;
    public BranchPolicy branchPolicy = BranchPolicy.Default;

    [Header("Speed")]
    public float speed = 5f;
    [Tooltip("停船中。母艦會減速到 0；取消後從原地繼續。")]
    public bool stopped = false;

    [Header("Steering")]
    public float maxAccel = 50f;
    [Tooltip("往前看多遠（公尺）決定航向。越大轉彎越圓、越會切彎。")]
    public float lookAheadDistance = 40f;

    [Header("Facing")]
    public bool faceMoveDirection = true;
    public float turnSpeed = 12f;
    public bool keepUpright = true;
    public float minSpeedToTurn = 0.05f;

    [Header("Camera Rotation Sync")]
    public Transform cameraTransform;

    [Header("Height")]
    public float fixedHeight = 1.6f;   // 固定的 Y（世界高度）
    public bool pinHeight = true;
    public bool zeroVerticalVelocity = true;

    // ── 狀態 ─────────────────────────────────────────────────────────────

    /// <summary>目前這段航線的起點。</summary>
    public ShipRouteNode From { get; private set; }
    /// <summary>目前這段航線的終點（正要前往的點）。null = 沒有航線。</summary>
    public ShipRouteNode To { get; private set; }
    /// <summary>抵達 To 之後要走的下一個點。To 是分叉點時，就是目前選定的分支。</summary>
    public ShipRouteNode PlannedNext { get; private set; }

    /// <summary>即將抵達分叉點（參數 = 分叉點）。這時可以呼叫 ChooseBranch。</summary>
    public event Action<ShipRouteNode> JunctionAhead;
    /// <summary>抵達某個節點（參數 = 該節點）。</summary>
    public event Action<ShipRouteNode> NodeReached;

    public bool IsAtDeadEnd => To != null && PlannedNext == null && To.IsDeadEnd;

    private Rigidbody rb;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
    }

    void Start()
    {
        if (startFromNearestPoint) SnapToNearestEdge();
        else if (startNode != null) StartFrom(startNode);
    }

    // ── 對外 API ─────────────────────────────────────────────────────────

    /// <summary>選擇抵達目前分叉點（To）之後要走的出口。不是 To 的出口就回傳 false。</summary>
    public bool ChooseBranch(ShipRouteNode exit)
    {
        if (To == null || !To.HasExit(exit)) return false;
        PlannedNext = exit;
        return true;
    }

    /// <summary>用 To.next 的索引選擇出口。</summary>
    public bool ChooseBranch(int index)
    {
        if (To == null || index < 0 || index >= To.next.Count) return false;
        return ChooseBranch(To.next[index]);
    }

    public void SetStopped(bool value) => stopped = value;

    /// <summary>接到最近的一段航線上（只看 XZ）。</summary>
    [ContextMenu("Snap To Nearest Edge")]
    public void SnapToNearestEdge()
    {
        if (graph == null) return;
        Vector3 p = (rb != null) ? rb.position : transform.position;
        if (graph.FindNearestEdge(p, out ShipRouteNode a, out ShipRouteNode b, out _))
            EnterEdge(a, b);
    }

    /// <summary>從指定節點出發，走它的預設出口。</summary>
    public void StartFrom(ShipRouteNode node)
    {
        if (node == null) return;
        ShipRouteNode n = Decide(node);
        if (n != null) EnterEdge(node, n);
        else { From = node; To = node; PlannedNext = null; }   // 起點就是死路：停在原地
    }

    // ── 每步 ─────────────────────────────────────────────────────────────

    void FixedUpdate()
    {
        if (To == null) return;

        Vector3 pos = rb.position;
        AdvanceAlongRoute(pos);

        Vector3 desiredVel = Vector3.zero;
        if (!stopped)
        {
            Vector3 target = LookAheadPoint(pos, out float remainingToDeadEnd);
            Vector3 toTarget = target - pos;
            toTarget.y = 0f;

            float wantSpeed = speed;
            // 前面是死路：用煞得住的速度接近終點（v = √(2·a·d)）
            if (remainingToDeadEnd < float.MaxValue)
                wantSpeed = Mathf.Min(speed, Mathf.Sqrt(2f * maxAccel * Mathf.Max(0f, remainingToDeadEnd)));

            if (toTarget.sqrMagnitude > 0.000001f && wantSpeed > 0.01f)
                desiredVel = toTarget.normalized * wantSpeed;
        }

        Vector3 dv = desiredVel - rb.linearVelocity;
        dv.y = 0f;   // 垂直交給 PinHeight
        Vector3 accel = Vector3.ClampMagnitude(dv / Time.fixedDeltaTime, maxAccel);
        rb.AddForce(accel, ForceMode.Acceleration);

        if (faceMoveDirection)
        {
            Vector3 v = rb.linearVelocity;
            if (v.sqrMagnitude > (minSpeedToTurn * minSpeedToTurn))
            {
                Vector3 forward = v.normalized;
                if (keepUpright)
                {
                    forward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
                    if (forward.sqrMagnitude >= 0.000001f)
                        ApplyFacing(forward);
                }
                else
                {
                    ApplyFacing(forward);
                }
            }
        }

        PinHeight();
    }

    /// <summary>船越過目前這段的終點，就換到下一段（一步最多換幾段，避免高速時漏掉短段）。</summary>
    void AdvanceAlongRoute(Vector3 pos)
    {
        for (int guard = 0; guard < 8; guard++)
        {
            if (From == null || To == null || From == To) return;
            if (ShipRouteGraph.ProjectXZ(pos, From.Position, To.Position) < 1f) return;

            // 抵達 To
            ShipRouteNode reached = To;
            NodeReached?.Invoke(reached);

            if (PlannedNext == null)
            {
                // 死路：From = To 表示「停在 To」，之後不會再重複觸發 NodeReached
                From = reached;
                return;
            }
            EnterEdge(reached, PlannedNext);
        }
    }

    void EnterEdge(ShipRouteNode a, ShipRouteNode b)
    {
        From = a;
        To = b;
        PlannedNext = Decide(b);
        if (b != null && b.IsJunction) JunctionAhead?.Invoke(b);
    }

    ShipRouteNode Decide(ShipRouteNode node)
    {
        if (node == null) return null;
        return branchPolicy == BranchPolicy.Random ? node.RandomNext() : node.DefaultNext;
    }

    /// <summary>
    /// 從船在目前這段上的投影點，沿航線往前走 lookAheadDistance，回傳那個點。
    /// 經過 To 之後走 PlannedNext；再之後走各節點的預設出口（只是看，不會決定）。
    /// 途中遇到死路：回傳死路節點，並在 remainingToDeadEnd 給出到死路的剩餘距離。
    /// </summary>
    Vector3 LookAheadPoint(Vector3 pos, out float remainingToDeadEnd)
    {
        remainingToDeadEnd = float.MaxValue;

        Vector3 a = From != null ? From.Position : To.Position;
        Vector3 b = To.Position;
        float s = (From != null && From != To) ? ShipRouteGraph.ProjectXZ(pos, a, b) : 1f;
        Vector3 cur = Vector3.Lerp(a, b, s);

        float budget = Mathf.Max(0.1f, lookAheadDistance);
        float travelled = 0f;
        ShipRouteNode segEnd = To;
        ShipRouteNode after = PlannedNext;

        for (int guard = 0; guard < 16; guard++)
        {
            Vector3 end = segEnd.Position;
            float segLen = ShipRouteGraph.DistanceXZ(cur, end);

            if (segLen >= budget)
                return cur + (end - cur) * (budget / segLen);

            budget -= segLen;
            travelled += segLen;
            cur = end;

            if (after == null)
            {
                remainingToDeadEnd = travelled;
                return end;
            }

            ShipRouteNode nextAfter = after.DefaultNext;
            segEnd = after;
            after = nextAfter;
        }
        return cur;
    }

    // ── 轉向 / 高度（同 MoveWithVelocity） ─────────────────────────────────

    private void ApplyFacing(Vector3 forward)
    {
        Quaternion oldRot = rb.rotation;
        Quaternion targetRot = Quaternion.LookRotation(forward, Vector3.up);
        Quaternion newRot = Quaternion.Slerp(oldRot, targetRot, turnSpeed * Time.fixedDeltaTime);

        rb.MoveRotation(newRot);

        // 鏡頭跟著轉同樣的角度
        if (cameraTransform != null)
        {
            Quaternion delta = newRot * Quaternion.Inverse(oldRot);
            cameraTransform.rotation = delta * cameraTransform.rotation;
        }
    }

    /// <summary>把高度釘回 fixedHeight，並清掉垂直速度。放在 FixedUpdate 最後，確保是這一步最後寫入的值。</summary>
    private void PinHeight()
    {
        if (pinHeight)
        {
            Vector3 p = rb.position;
            if (p.y != fixedHeight)
            {
                p.y = fixedHeight;
                rb.position = p;
            }
        }

        if (zeroVerticalVelocity)
        {
            Vector3 v = rb.linearVelocity;
            if (v.y != 0f)
            {
                v.y = 0f;
                rb.linearVelocity = v;
            }
        }
    }

    [ContextMenu("Set Fixed Height From Current Y")]
    private void SetFixedHeightFromCurrent()
    {
        fixedHeight = transform.position.y;
        Debug.Log($"[ShipRouteFollower] fixedHeight 設為 {fixedHeight}", this);
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || To == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, To.Position);
        if (PlannedNext != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(To.Position, PlannedNext.Position);
        }
    }
#endif
}
