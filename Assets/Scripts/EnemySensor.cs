using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敵人的偵測範圍，取代 EnemyDetectRange 的兩個 trigger（球形 SphereCollider + 視野錐 MeshCollider）。
///
/// 為什麼不用 trigger：
///   每隻敵人身上掛兩個半徑 30 公尺的大 trigger，數百隻互相重疊，
///   物理引擎每一步都要處理它們（WriteColliderPoses、trigger 配對），
///   而且所有射線查詢都得穿過這些巨大的包圍盒，連地面偵測都跟著變慢。
///   改成 Brain 思考時每隔 scanInterval 秒做一次 OverlapSphere，物理世界裡就少了這些碰撞體。
///
/// 半徑和距離是世界單位（公尺），不會跟著物件縮放。偏移（Offset）是本地座標，會跟著縮放。
///
/// 行為與原本的 EnemyDetection.OnTriggerEnter 相同：
///   · 只在目標「進入」範圍的那一次呼叫 AddTarget（離開再進來才會再加一次）
///   · 目標已經在 brain.targets 裡就不處理
///   · 依 tag 決定目標類型與優先度（tagRules）
///
/// 範圍在 Scene 視圖裡可以直接拖曳編輯（選取掛著這個元件的物件）：
///   · 黃色球：近距離感知（360°）—— 拖球面上的點改半徑
///   · 紅色錐：視野 —— 拖錐尖的點改距離，拖側邊的點改角度
///
/// 掛在會跟著敵人轉向的物件上（例如 Wolf Mesh），視野錐才會朝向敵人的正面。
/// 這個元件本身沒有碰撞體，跟著轉動不會增加任何物理成本。
/// </summary>
[DisallowMultipleComponent]
public class EnemySensor : MonoBehaviour
{
    [System.Serializable]
    public class TagRule
    {
        public string tag;
        public TargetType type;
        public float priority;
        public float priorityDecreaseMultiplier = 1f;
    }

    [Header("Owner")]
    [Tooltip("留空會自動找父物件上的 ModularEntityBrain。")]
    [SerializeField] private ModularEntityBrain brain;

    [Header("Targets")]
    [Tooltip("會被偵測的層。預設等於原本 Detect Range 層在碰撞矩陣裡勾選的層：Player、Defence Fortifications、Obstacle。")]
    [SerializeField] private LayerMask targetLayers;

    [Tooltip("要不要偵測 trigger 碰撞體。原本的 trigger 會對其他 trigger 觸發 OnTriggerEnter，所以預設 Collide。")]
    [SerializeField] private QueryTriggerInteraction triggerInteraction = QueryTriggerInteraction.Collide;

    [Tooltip("依碰撞體的 tag 決定目標類型與優先度。數值與原本 EnemyDetection 相同。")]
    [SerializeField] private List<TagRule> tagRules = new List<TagRule>
    {
        new TagRule { tag = "Player",                 type = TargetType.Player,   priority = 25f, priorityDecreaseMultiplier = 1f },
        new TagRule { tag = "Defence Fortifications", type = TargetType.Building, priority = 50f, priorityDecreaseMultiplier = 1f },
        new TagRule { tag = "Obstacle",               type = TargetType.Obstacle, priority = 10f, priorityDecreaseMultiplier = 1f },
    };

    [Header("Proximity (360°)")]
    [SerializeField] private bool useProximity = true;
    [Tooltip("近距離感知半徑（公尺，世界單位，不受物件縮放影響）。\n" +
             "原本的 SphereCollider 半徑是 30，但 Wolf 根物件縮放 1.5，實際是 45 公尺。")]
    [SerializeField, Min(0f)] private float proximityRadius = 45f;
    [Tooltip("球心相對這個物件的位置（本地座標）。")]
    [SerializeField] private Vector3 proximityOffset = Vector3.zero;

    [Header("Sight Cone")]
    [SerializeField] private bool useSight = true;
    [Tooltip("視野距離（公尺，世界單位，不受物件縮放影響）。\n" +
             "原本的視野錐長 30，乘上 Wolf 根物件的縮放 1.5，實際是 45 公尺。")]
    [SerializeField, Min(0f)] private float sightRange = 45f;
    [Tooltip("視野錐的完整張角（度）。原本的視野錐約 19°。")]
    [SerializeField, Range(0f, 179f)] private float sightAngle = 19f;
    [Tooltip("錐尖相對這個物件的位置（本地座標）。預設是原本視野錐的位置。")]
    [SerializeField] private Vector3 sightOffset = new Vector3(0f, 0.39f, 0.733f);

    [Header("Scan")]
    [Tooltip("每隔幾秒偵測一次。原本的 trigger 是每個物理步都在算；0.25 秒對「發現目標」來說通常看不出差別。")]
    [SerializeField, Min(0.02f)] private float scanInterval = 0.25f;

    [Header("Gizmo")]
    [Tooltip("沒選取時也畫出範圍（場景裡敵人很多時會很亂，平常建議關閉）。")]
    [SerializeField] private bool drawWhenNotSelected = false;
    [SerializeField] private Color proximityColor = new Color(1f, 0.85f, 0.2f, 0.9f);
    [SerializeField] private Color sightColor = new Color(1f, 0.35f, 0.25f, 0.9f);

    // 所有感測器共用的查詢緩衝區（主執行緒上依序使用，不會同時被兩個感測器用到）
    private static readonly Collider[] s_Hits = new Collider[128];
    private static bool s_WarnedFull;

    // 上一次與這一次偵測到的碰撞體 —— 用來判斷「剛進入」
    private HashSet<Collider> _inside = new HashSet<Collider>();
    private HashSet<Collider> _current = new HashSet<Collider>();

    private float _nextScanTime = -1f;

    // ── 給 Editor 與 Gizmo 用的世界座標 ─────────────────────────────────
    public Vector3 ProximityCenter => transform.TransformPoint(proximityOffset);
    public Vector3 SightApex => transform.TransformPoint(sightOffset);
    public Vector3 SightForward => transform.forward;
    public bool UseProximity => useProximity;
    public bool UseSight => useSight;
    public float ProximityRadius => proximityRadius;
    public float SightRange => sightRange;
    public float SightAngle => sightAngle;
    public Color ProximityColor => proximityColor;
    public Color SightColor => sightColor;

    private void Reset()
    {
        targetLayers = LayerMask.GetMask("Player", "Defence Fortifications", "Obstacle");
        brain = GetComponentInParent<ModularEntityBrain>();
    }

    private void Awake()
    {
        if (brain == null) brain = GetComponentInParent<ModularEntityBrain>();
        if (targetLayers.value == 0)
            targetLayers = LayerMask.GetMask("Player", "Defence Fortifications", "Obstacle");
    }

    private void OnDisable()
    {
        // 停用後重新啟用時，範圍內的目標要能再觸發一次「進入」
        _inside.Clear();
        _current.Clear();
        _nextScanTime = -1f;
    }

    /// <summary>
    /// 由 ModularEntityBrain 每次思考時呼叫。沒到 scanInterval 就直接返回。
    /// 第一次偵測的時間隨機錯開，避免所有敵人擠在同一步。
    /// </summary>
    public void Scan(float now)
    {
        if (brain == null) return;

        if (_nextScanTime < 0f)
            _nextScanTime = now + Random.Range(0f, scanInterval);

        if (now < _nextScanTime) return;
        _nextScanTime = now + scanInterval;

        ScanNow();
    }

    private void ScanNow()
    {
        _current.Clear();

        if (!useProximity && !useSight)
        {
            SwapSets();
            return;
        }

        Vector3 proxCenter = ProximityCenter;
        Vector3 apex = SightApex;
        Vector3 forward = SightForward;

        // 用一顆球把兩個範圍都包起來，只查詢一次
        Vector3 queryCenter;
        float queryRadius;
        if (useProximity && useSight)
        {
            queryCenter = proxCenter;
            queryRadius = Mathf.Max(proximityRadius, Vector3.Distance(proxCenter, apex) + sightRange);
        }
        else if (useProximity)
        {
            queryCenter = proxCenter;
            queryRadius = proximityRadius;
        }
        else
        {
            queryCenter = apex;
            queryRadius = sightRange;
        }

        int count = Physics.OverlapSphereNonAlloc(queryCenter, queryRadius, s_Hits, targetLayers, triggerInteraction);

        if (count == s_Hits.Length && !s_WarnedFull)
        {
            s_WarnedFull = true;
            Debug.LogWarning($"{nameof(EnemySensor)}：範圍內的碰撞體超過 {s_Hits.Length} 個，超出的會被忽略。" +
                             "可以縮小範圍或收窄 Target Layers。", this);
        }

        float cosHalf = Mathf.Cos(sightAngle * 0.5f * Mathf.Deg2Rad);
        bool queryIsProximity = useProximity && queryRadius <= proximityRadius;   // 查詢球就是感知球，不用再檢查

        for (int i = 0; i < count; i++)
        {
            Collider col = s_Hits[i];
            s_Hits[i] = null;   // 不要讓共用緩衝區留著別人的參考
            if (col == null) continue;

            bool detected =
                (useProximity && (queryIsProximity || InProximity(col, proxCenter))) ||
                (useSight && InSight(col, apex, forward, cosHalf));

            if (!detected) continue;

            _current.Add(col);
            if (!_inside.Contains(col))
                OnEnter(col);
        }

        SwapSets();
    }

    private void SwapSets()
    {
        HashSet<Collider> tmp = _inside;
        _inside = _current;
        _current = tmp;
    }

    private bool InProximity(Collider col, Vector3 center)
    {
        Vector3 cp = ClosestPoint(col, center);
        return (cp - center).sqrMagnitude <= proximityRadius * proximityRadius;
    }

    /// <summary>
    /// 碰撞體有沒有落在視野錐（錐尖 apex、軸 forward、半張角、距離 sightRange 的球冠）裡。
    /// 近似判斷：碰撞體上離錐尖最近的點，或碰撞體的中心，任一個在錐內就算。
    /// </summary>
    private bool InSight(Collider col, Vector3 apex, Vector3 forward, float cosHalf)
    {
        float rangeSqr = sightRange * sightRange;

        Vector3 toClosest = ClosestPoint(col, apex) - apex;
        float sqr = toClosest.sqrMagnitude;
        if (sqr < 0.000001f) return true;          // 錐尖在碰撞體裡面
        if (sqr > rangeSqr) return false;          // 最近的點都超出距離 → 整個都在範圍外
        if (InCone(toClosest, forward, cosHalf)) return true;

        Vector3 toCenter = col.bounds.center - apex;
        return toCenter.sqrMagnitude <= rangeSqr && InCone(toCenter, forward, cosHalf);
    }

    private static bool InCone(Vector3 dir, Vector3 forward, float cosHalf)
    {
        return Vector3.Dot(dir, forward) >= cosHalf * dir.magnitude;
    }

    /// <summary>Collider.ClosestPoint 不支援非凸面的 MeshCollider 和 TerrainCollider，那些改用包圍盒。</summary>
    private static Vector3 ClosestPoint(Collider col, Vector3 point)
    {
        if (col is MeshCollider mc && !mc.convex) return col.bounds.ClosestPoint(point);
        if (col is TerrainCollider) return col.bounds.ClosestPoint(point);
        return col.ClosestPoint(point);
    }

    /// <summary>跟原本 EnemyDetection.OnTriggerEnter 一樣的處理。</summary>
    private void OnEnter(Collider other)
    {
        Transform t = ResolveTarget(other);

        List<Target> targets = brain.targets;
        for (int x = targets.Count - 1; x >= 0; x--)
        {
            if (targets[x] != null && targets[x].targetTransform == t) return;
        }

        // 用字串比對而不是 CompareTag：tagRules 裡的 tag 不一定有在 Tag Manager 定義
        // （例如 Obstacle），CompareTag 遇到未定義的 tag 會報錯。只有「剛進入」時才會跑到這裡，頻率很低。
        string tag = other.tag;
        for (int i = 0; i < tagRules.Count; i++)
        {
            TagRule rule = tagRules[i];
            if (rule == null || rule.tag != tag) continue;

            brain.AddTarget(t, rule.type, rule.priority, rule.priorityDecreaseMultiplier);
            return;
        }
    }

    /// <summary>
    /// 碰撞體 → 要加進清單的目標。
    ///
    /// 一個可受傷的物件可能有好幾個碰撞體（例如砲塔的 Base 和 TurretHead），
    /// 原本以碰撞體為單位加目標，同一座砲塔會變成兩筆。
    /// 改成記錄 IDamageable 所在的物件（砲塔的 BuildingStats、玩家根物件上的 DamageRelay……），
    /// 一個物件只會有一筆；瞄準位置由 TurretController.GetAimPoint 找它底下的 AimPoint。
    /// 沒有 IDamageable 的（例如障礙物）維持原本的碰撞體。
    /// </summary>
    private static Transform ResolveTarget(Collider col)
    {
        IDamageable damageable = col.GetComponentInParent<IDamageable>();
        return damageable is Component c ? c.transform : col.transform;
    }

    // ═══════════════════════ Gizmo ═══════════════════════

    private void OnDrawGizmos()
    {
        if (drawWhenNotSelected) DrawRangeGizmos();
    }

    private void OnDrawGizmosSelected()
    {
        if (!drawWhenNotSelected) DrawRangeGizmos();
    }

    private void DrawRangeGizmos()
    {
        if (useProximity)
        {
            Gizmos.color = proximityColor;
            Gizmos.DrawWireSphere(ProximityCenter, proximityRadius);
        }

        if (useSight)
        {
            Gizmos.color = sightColor;
            DrawSightCone(SightApex, transform.rotation, sightRange, sightAngle * 0.5f);
        }

        // 執行中：畫線連到目前在範圍內的目標
        if (Application.isPlaying && _inside.Count > 0)
        {
            Gizmos.color = Color.red;
            Vector3 from = transform.position;
            foreach (Collider col in _inside)
            {
                if (col != null) Gizmos.DrawLine(from, col.bounds.center);
            }
        }
    }

    /// <summary>畫出視野錐：底部的圓、幾條母線，以及水平 / 垂直兩條球冠弧線。</summary>
    private static void DrawSightCone(Vector3 apex, Quaternion rotation, float range, float halfAngle)
    {
        const int segments = 32;
        float rad = halfAngle * Mathf.Deg2Rad;
        float baseDist = range * Mathf.Cos(rad);
        float baseRadius = range * Mathf.Sin(rad);

        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= segments; i++)
        {
            float a = (float)i / segments * Mathf.PI * 2f;
            Vector3 p = apex + rotation * new Vector3(Mathf.Cos(a) * baseRadius, Mathf.Sin(a) * baseRadius, baseDist);
            if (i > 0) Gizmos.DrawLine(prev, p);
            if (i % 8 == 0) Gizmos.DrawLine(apex, p);
            prev = p;
        }

        DrawCapArc(apex, rotation, range, halfAngle, Vector3.up);
        DrawCapArc(apex, rotation, range, halfAngle, Vector3.right);
    }

    private static void DrawCapArc(Vector3 apex, Quaternion rotation, float range, float halfAngle, Vector3 localAxis)
    {
        const int segments = 16;
        Vector3 prev = Vector3.zero;
        for (int i = 0; i <= segments; i++)
        {
            float angle = Mathf.Lerp(-halfAngle, halfAngle, (float)i / segments);
            Vector3 p = apex + rotation * (Quaternion.AngleAxis(angle, localAxis) * Vector3.forward) * range;
            if (i > 0) Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
}
