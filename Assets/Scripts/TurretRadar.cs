using System.Collections.Generic;
using UnityEngine;

public class TurretRadar : MonoBehaviour
{
    public FirearmControlSystem firearmControl;
    public float radarRange = 50f;

    [Header("Timing")]
    public float scanInterval = 0.1f;

    [Header("Angle")]
    [Tooltip("If turretRotationLimit is 360 or <= 0, use this half-angle (degrees) for target assignment.")]
    public float defaultAssignHalfAngleDeg = 180f;

    [Tooltip("How close the barrel must be aiming at its current aim point before firing (degrees).")]
    public float fireConeDeg = 6f;

    [Header("Behavior")]
    public bool preferUniqueTargets = true;
    public bool ignoreDeadObjects = true;

    [Header("Line of Sight")]
    [Tooltip("開啟時，被牆擋住的敵人不會被選為目標。")]
    [SerializeField] private bool requireLineOfSight = true;

    [Tooltip("會擋住視線的層。敵人層不要勾，否則敵人會互相擋住。\n" +
             "砲塔自己的 collider 會自動忽略，不用特別排除。\n" +
             "留空（Nothing）會在執行時自動用 Default、Ground、Mobile Platform、Defence Fortifications、Obstacle（跟玩家輔助瞄準一樣）。")]
    [SerializeField] private LayerMask lineOfSightBlockers;

    [Tooltip("目前的目標躲到牆後之後，還會繼續朝它開火幾秒才放棄（秒）。")]
    [SerializeField, Min(0f)] private float lostSightGraceTime = 1f;

    private float _scanTimer;

    // 每門砲：目前目標最後一次「看得到」的時間（給 lostSightGraceTime 用）
    private float[] _lastSeenTime;

    // 掃描用的暫存（避免每次掃描都配置記憶體）
    // _inRange：這次掃描時，雷達範圍內的敵人（已依距離由近到遠排好）。
    // 每次掃描只走一次全部敵人、讀一次位置，各門砲共用這份短名單。
    private readonly List<Candidate> _inRange = new List<Candidate>();
    private readonly HashSet<int> _used = new HashSet<int>();
    private readonly RaycastHit[] _losHits = new RaycastHit[8];

    private struct Candidate
    {
        public GameObject go;
        public int id;
        public Vector3 pos;
        public float distSqr;
    }

    private static readonly System.Comparison<Candidate> ByDistance = (a, b) => a.distSqr.CompareTo(b.distSqr);

    private void Awake()
    {
        if (lineOfSightBlockers.value == 0)
            lineOfSightBlockers = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Defence Fortifications", "Obstacle");

        // 錯開掃描時間：所有雷達同時從 0 開始的話，會擠在同一幀一起掃
        _scanTimer = Random.Range(0f, Mathf.Max(0.01f, scanInterval));
    }

    void Update()
    {
        if (firearmControl == null) return;
        if (firearmControl.firearms == null || firearmControl.firearms.Length == 0) return;

        _scanTimer -= Time.deltaTime;
        if (_scanTimer <= 0f)
        {
            _scanTimer = Mathf.Max(0.01f, scanInterval);
            AssignTargetsFromGameManager();
        }

        firearmControl.RequestFireAll(fireConeDeg);
    }

    private void AssignTargetsFromGameManager()
    {
        List<GameObject> enemies = null;

        if (GameManager.Instance != null)
        {
            enemies = GameManager.Instance.GetEnemies();
        }

        if (enemies == null)
        {
            enemies = new List<GameObject>(GameObject.FindGameObjectsWithTag("Enemy"));
        }

        int n = firearmControl.firearms.Length;
        if (_lastSeenTime == null || _lastSeenTime.Length != n)
            _lastSeenTime = new float[n];

        CollectInRange(enemies);

        _used.Clear();
        HashSet<int> used = preferUniqueTargets ? _used : null;

        for (int i = 0; i < n; i++)
        {
            var fa = firearmControl.firearms[i];
            if (fa == null)
                continue;

            Transform aimFrom = GetYawReference(fa);
            if (aimFrom == null)
            {
                fa.target = null;
                fa.targetRigidbody = null;
                continue;
            }

            float halfAngle = GetAssignHalfAngle(fa);
            Transform previous = fa.target;

            // 先選沒被其他砲占用的；都沒有才允許跟別門砲打同一個
            GameObject best = PickClosestVisible(i, fa, aimFrom, enemies, halfAngle, used);
            if (best == null && preferUniqueTargets)
                best = PickClosestVisible(i, fa, aimFrom, enemies, halfAngle, null);

            Transform bestT = best != null ? best.transform : null;

            // 換了新目標：選中的當下一定看得到，從現在開始計時
            if (bestT != null && bestT != previous)
                _lastSeenTime[i] = Time.time;

            // 目標沒換就沿用原本的 Rigidbody，不用每次掃描都 GetComponent
            Rigidbody bestRb = null;
            if (best != null)
                bestRb = (bestT == previous && fa.targetRigidbody != null) ? fa.targetRigidbody : best.GetComponent<Rigidbody>();

            fa.target = bestT;
            fa.targetRigidbody = bestRb;

            if (preferUniqueTargets && used != null && bestT != null)
                used.Add(best.GetInstanceID());
        }
    }

    /// <summary>
    /// 走一次全部敵人：留下雷達範圍內的，記下位置和距離，依距離由近到遠排序。
    /// </summary>
    private void CollectInRange(List<GameObject> enemies)
    {
        _inRange.Clear();
        float rangeSqr = radarRange * radarRange;
        Vector3 origin = transform.position;

        for (int e = 0; e < enemies.Count; e++)
        {
            var go = enemies[e];
            if (go == null) continue;
            if (ignoreDeadObjects && !go.activeInHierarchy) continue;

            Vector3 pos = go.transform.position;
            float dx = pos.x - origin.x, dz = pos.z - origin.z;
            float distSqr = dx * dx + dz * dz;
            if (distSqr > rangeSqr) continue;

            _inRange.Add(new Candidate { go = go, id = go.GetInstanceID(), pos = pos, distSqr = distSqr });
        }

        if (_inRange.Count > 1) _inRange.Sort(ByDistance);
    }

    /// <summary>
    /// 在雷達範圍（_inRange，已排序）、轉向角度內，找最近的「看得到」的敵人。
    /// 從近到遠打視線射線，打到第一個看得到的就停（遠處的敵人不用打射線）。
    /// </summary>
    private GameObject PickClosestVisible(int idx, Firearm fa, Transform aimFrom, List<GameObject> enemies,
                                          float halfAngle, HashSet<int> exclude)
    {
        for (int c = 0; c < _inRange.Count; c++)
        {
            Candidate cand = _inRange[c];
            if (cand.go == null) continue;
            if (exclude != null && exclude.Contains(cand.id)) continue;
            if (!IsWithinYawHalfAngle(aimFrom, cand.pos, halfAngle)) continue;

            if (IsVisibleOrInGrace(idx, fa, cand.go.transform))
                return cand.go;
        }
        return null;
    }

    /// <summary>
    /// 看得到 → true。
    /// 看不到，但它是這門砲目前的目標、而且躲起來還不到 lostSightGraceTime → 也算 true（繼續打一下）。
    /// </summary>
    private bool IsVisibleOrInGrace(int idx, Firearm fa, Transform target)
    {
        if (!requireLineOfSight) return true;

        bool visible = HasLineOfSight(fa, target);
        bool isCurrent = target == fa.target;

        if (visible)
        {
            if (isCurrent) _lastSeenTime[idx] = Time.time;
            return true;
        }

        return isCurrent && Time.time - _lastSeenTime[idx] <= lostSightGraceTime;
    }

    /// <summary>
    /// 從砲口中心看向目標的 AimPoint（跟砲塔實際瞄的同一點），中間有沒有被 lineOfSightBlockers 擋住。
    /// 砲塔自己的 collider（基座、砲管）忽略；打到目標自己也算看得到。
    /// </summary>
    private bool HasLineOfSight(Firearm fa, Transform target)
    {
        Vector3 from;
        if (fa.muzzleCenterPoint != null) from = fa.muzzleCenterPoint.position;
        else if (fa.barrel != null) from = fa.barrel.transform.position;
        else if (fa.turret != null) from = fa.turret.transform.position;
        else from = transform.position;

        Vector3 to = AimPoint.PositionOf(target);
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist < 0.01f) return true;
        dir /= dist;

        int count = Physics.RaycastNonAlloc(from, dir, _losHits, dist, lineOfSightBlockers, QueryTriggerInteraction.Ignore);
        if (count == 0) return true;

        // RaycastNonAlloc 不保證順序 → 找「最近的、不是自己的」那一個
        float nearest = float.PositiveInfinity;
        Collider nearestCol = null;
        for (int h = 0; h < count; h++)
        {
            Collider col = _losHits[h].collider;
            if (col == null || IsOwnCollider(col)) continue;
            if (_losHits[h].distance < nearest)
            {
                nearest = _losHits[h].distance;
                nearestCol = col;
            }
        }

        if (nearestCol == null) return true;                  // 只打到自己
        return nearestCol.transform.IsChildOf(target);        // 第一個擋到的是目標自己 → 看得到
    }

    private bool IsOwnCollider(Collider col)
    {
        Transform t = col.transform;
        if (t.IsChildOf(transform)) return true;
        return firearmControl != null && t.IsChildOf(firearmControl.transform);
    }

    private static Transform GetYawReference(Firearm fa)
    {
        if (fa == null) return null;

        if (fa.turret != null) return fa.turret.transform;
        if (fa.muzzleCenterPoint != null) return fa.muzzleCenterPoint;
        if (fa.barrel != null) return fa.barrel.transform;

        return null;
    }

    private float GetAssignHalfAngle(Firearm fa)
    {
        if (fa == null) return Mathf.Clamp(defaultAssignHalfAngleDeg, 0f, 180f);

        float lim = fa.turretRotationLimit;
        if (lim > 0f && lim < 360f)
            return Mathf.Clamp(lim * 0.5f, 0f, 180f);

        return Mathf.Clamp(defaultAssignHalfAngleDeg, 0f, 180f);
    }

    private static bool IsWithinYawHalfAngle(Transform yawRef, Vector3 targetPos, float halfAngleDeg)
    {
        if (yawRef == null) return false;

        Vector3 fwd = yawRef.forward;
        fwd.y = 0f;

        Vector3 to = targetPos - yawRef.position;
        to.y = 0f;

        if (fwd.sqrMagnitude < 0.0001f) return false;
        if (to.sqrMagnitude < 0.0001f) return true;

        float ang = Vector3.Angle(fwd.normalized, to.normalized);
        return ang <= Mathf.Max(0f, halfAngleDeg);
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, Mathf.Max(0f, radarRange));
    }
}
