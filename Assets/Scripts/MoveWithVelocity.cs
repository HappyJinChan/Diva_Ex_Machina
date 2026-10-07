using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[RequireComponent(typeof(Rigidbody))]
public class MoveWithVelocity : MonoBehaviour
{
    public SplineContainer spline;
    [Range(0f, 1f)] public float t = 0f;
    public float speed = 5f;
    public bool loop = true;

    [Tooltip("反方向行駛（沿著 spline 的 knot 順序倒著走）。\n" +
             "要改變船繞圈的方向（順時針 / 逆時針）時勾這個，不用重新排 knot。")]
    public bool reverse = false;

    [Tooltip("開始時從船目前的位置，找 spline 上最近的點當起點。\n\n" +
             "關掉的話會從上面的 t 開始 —— 船如果沒有剛好擺在那個點上，\n" +
             "會先往 t 的位置開過去、追上之後再掉頭，看起來就像走到盡頭折返。")]
    public bool startFromNearestPoint = true;

    [Header("Steering")]
    public float maxAccel = 50f;
    public float lookAhead = 0.01f;

    [Header("Facing")]
    public bool faceMoveDirection = true;
    public float turnSpeed = 12f;
    public bool keepUpright = true;
    public float minSpeedToTurn = 0.05f;

    private Rigidbody rb;
    private float approxSplineLength = 10f;

    [Header("Camera Rotation Sync")]
    public Transform cameraTransform;

    [Header("Ignore Spline Height")]
    public float fixedHeight = 1.6f;   // 船固定的 Y（世界高度）

    [Tooltip("每個 physics step 把 rb.position.y 硬寫回 fixedHeight。\n\n" +
             "原本 fixedHeight 是死程式碼 —— targetPos.y 設了值，但下一行的\n" +
             "toTarget.y = 0f 立刻把它清掉，所以它對船的實際高度沒有任何影響，\n" +
             "而且沒有任何力量會把被推離的船拉回目標高度。\n\n" +
             "直接寫入位置能免疫所有來源的洩漏（碰撞去穿透、殘餘力、其他腳本），\n" +
             "跟這個腳本已經在用 rb.MoveRotation 直接寫入旋轉是一致的做法。")]
    public bool pinHeight = true;

    [Tooltip("同時把垂直速度清零。\n\n" +
             "這很重要：船的 linearVelocity 會透過 ShipPassenger.PlatformVelocity\n" +
             "傳給船上所有實體，ModularEntityMovement 會把它整個加回自己的速度上，\n" +
             "包含 y 分量。船身殘餘的垂直速度會讓甲板上的敵人跟著緩慢上浮或下沉。")]
    public bool zeroVerticalVelocity = true;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        approxSplineLength = spline != null ? Mathf.Max(0.01f, spline.CalculateLength()) : 10f;
    }

    void Start()
    {
        if (startFromNearestPoint) SnapToNearestPoint();
    }

    /// <summary>把 t 設成 spline 上離船目前位置最近的點。</summary>
    [ContextMenu("Snap t To Nearest Point")]
    private void SnapToNearestPoint()
    {
        if (spline == null) return;

        Vector3 worldPos = (rb != null) ? rb.position : transform.position;

        // GetNearestPoint 用的是 spline 的區域座標
        float3 local = (float3)spline.transform.InverseTransformPoint(worldPos);
        SplineUtility.GetNearestPoint(spline.Spline, local, out float3 _, out float nearestT);
        t = nearestT;
    }

    void FixedUpdate()
    {
        if (spline == null) return;

        float dir = reverse ? -1f : 1f;

        float dt = (speed / approxSplineLength) * Time.fixedDeltaTime;
        t += dt * dir;

        if (loop) t = Mathf.Repeat(t, 1f);
        else t = Mathf.Clamp01(t);

        float tAhead = loop ? Mathf.Repeat(t + lookAhead * dir, 1f) : Mathf.Clamp01(t + lookAhead * dir);

        Vector3 targetPos = spline.EvaluatePosition(tAhead);
        targetPos.y = fixedHeight;   // ★ 忽略 spline 的 y，鎖定在固定高度

        Vector3 toTarget = (targetPos - rb.position);
        toTarget.y = 0f;             // ★ 只用水平分量算移動方向，y 不參與驅動
        Vector3 desiredVel = toTarget.sqrMagnitude > 0.000001f ? toTarget.normalized * speed : Vector3.zero;

        Vector3 dv = desiredVel - rb.linearVelocity;

        // dv.y 在這裡刻意保留 0 之外的值不做處理 —— 垂直改由下面的 PinHeight 硬解。
        // 原本讓 dv.y 進 ClampMagnitude 有個副作用：ClampMagnitude 縮的是整個向量，
        // 起步和轉彎時水平的 dv/dt 遠超過 maxAccel（15/0.02 = 750 vs 50），
        // 整個向量會被縮到約 1/15 —— 垂直分量也一起被削弱，
        // 結果是最需要抑制垂直漂移的時候，抑制力反而最小。
        dv.y = 0f;

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
                    // 原本這裡是 return —— 那會連同下面的高度鎖一起跳過。
                    // 改成只跳過轉向，高度鎖照常執行。
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

    private void ApplyFacing(Vector3 forward)
    {
        Quaternion oldRot = rb.rotation;
        Quaternion targetRot = Quaternion.LookRotation(forward, Vector3.up);
        Quaternion newRot = Quaternion.Slerp(oldRot, targetRot, turnSpeed * Time.fixedDeltaTime);

        // Apply rotation to rigidbody
        rb.MoveRotation(newRot);

        // Sync camera by the same delta rotation
        if (cameraTransform != null)
        {
            Quaternion delta = newRot * Quaternion.Inverse(oldRot);
            cameraTransform.rotation = delta * cameraTransform.rotation;
        }
    }

    /// <summary>
    /// 把高度釘回 fixedHeight，並清掉垂直速度。
    ///
    /// 放在 FixedUpdate 最後：這一步所有會影響 rb 的操作都做完了，
    /// 最後寫入的值才是這個 physics step 真正生效的值。
    /// </summary>
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

    /// <summary>
    /// 把 fixedHeight 設成目前的實際高度。
    ///
    /// 用途：船漂移過之後，你想以「現在的位置」為準重新定義基準高度，
    /// 而不是回到某個已經不知道對不對的舊數值。
    /// 執行期和編輯期都能用（Inspector 右鍵選單）。
    /// </summary>
    [ContextMenu("Set Fixed Height From Current Y")]
    private void SetFixedHeightFromCurrent()
    {
        fixedHeight = transform.position.y;
        Debug.Log($"[MoveWithVelocity] fixedHeight 設為 {fixedHeight}", this);
    }
}