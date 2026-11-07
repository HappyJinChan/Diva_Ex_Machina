using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 一次揮擊的傷害設定。由 MeleeAttackController 在 Open() 之前寫入。
///
/// baseDamage 已經乘過 meleeOutput 與該段的 damageMultiplier，但「不含暴擊」——
/// 暴擊在每次命中時各別擲骰，跟 Bullet 的行為一致（同一刀掃到兩個敵人，
/// 可能一個爆一個不爆）。
/// </summary>
public struct MeleeHitData
{
    public GameObject attacker;
    public DamageInfo baseDamage;
    public float criticalChance;
    public float criticalMultiplier;
    public float knockback;

    /// <summary>這把武器允許打到的層。玩家的武器設成敵人層，用來分敵我。</summary>
    public LayerMask hittableLayers;

    // ── Arc 判定用 ──
    /// <summary>決定扇形原點與面向的物件（角色模型）。null = 用 attacker。</summary>
    public Transform facing;
    /// <summary>攻擊距離（公尺），量到敵人碰撞體表面。</summary>
    public float arcRange;
    /// <summary>水平扇形角度（度）。</summary>
    public float arcAngle;
    /// <summary>垂直範圍角度（度，上下各一半）。</summary>
    public float arcPitchAngle;
}

/// <summary>
/// 一次命中的結果。丟給控制器做頓幀 / 命中特效 / 音效，
/// hitbox 本身不碰這些東西。
/// </summary>
public struct MeleeHitResult
{
    public IDamageable target;
    public Component targetComponent;
    public Vector3 point;
    public Vector3 direction;
    public bool wasCritical;
    public DamageInfo dealt;
}

/// <summary>
/// 掛在近戰武器 prefab 的刀刃上。由動畫的 Animation Event 透過 MeleeAttackController 呼叫 Open() / Close()。
///
/// 兩種判定（Detection Mode）：
///   Arc（預設）- 開啟期間每個物理步，以角色胸口為原點、角色面向為中心做扇形判定。
///                高速時刀身每一步移動將近 1 公尺，碰撞體判定會整個穿過敵人（離散取樣）；
///                扇形有幾公尺大，不會穿透。範圍內的敵人全部命中（同一刀同一個敵人只算一次）。
///                形狀（距離 / 水平角 / 垂直角）由 MeleeAttackStep 設定。
///                刀身的 Collider 只用來決定命中特效的位置（敵人身上離刀身最近的點）。
///   Trigger    - 舊的判定：刀身的 trigger 碰撞體碰到才算。保留作對照。
/// </summary>
public class MeleeHitbox : MonoBehaviour
{
    [Tooltip("這個 hitbox 使用的 Collider。留空則自動抓本物件上的所有 Collider。")]
    [SerializeField] private Collider[] colliders;

    [Tooltip("敵人若沒有 Rigidbody，Unity 不會送出 trigger 事件。\n" +
             "遇到「明明碰到卻沒傷害」時再打開，會在本物件加一顆 kinematic Rigidbody。\n" +
             "注意：這會在玩家底下形成巢狀 Rigidbody，非必要不要開。")]
    [SerializeField] private bool addKinematicRigidbody = false;

    public enum DetectionMode { Arc, Trigger }

    [Header("Detection")]
    [Tooltip("Arc：扇形判定（不會因為高速穿過敵人）。Trigger：舊的刀身碰撞體判定。")]
    [SerializeField] private DetectionMode detectionMode = DetectionMode.Arc;

    [Tooltip("Arc：扇形原點在角色模型上方多高（公尺），大約胸口。")]
    [SerializeField] private float arcOriginHeight = 1f;

    [Tooltip("Arc：距離小於這個值（公尺）的敵人不看角度直接算命中 —— 已經貼在身上時，角度沒有意義。")]
    [SerializeField] private float arcPointBlank = 0.6f;

    [Header("Debug")]
    [SerializeField] private bool logHits = false;

    [SerializeField] private bool drawGizmoWhenOpen = true;

    [Tooltip("hitbox 關掉之後，Scene 視窗裡的扇形再多顯示幾秒（一刀通常只開零點幾秒，太快看不到）。")]
    [SerializeField, Min(0f)] private float gizmoLinger = 1f;

    /// <summary>每次成功造成傷害時觸發。給控制器做頓幀 / 特效用。</summary>
    public event Action<MeleeHitResult> OnHitTarget;

    public bool IsOpen => _open;

    private MeleeHitData _data;
    private bool _open;
    private float _closedAt = -999f;   // 給 Gizmo 延遲顯示用
    private bool _configured;

    // 同一次揮擊只打同一個目標一次（跟 Bullet 的 _hitEnemyIds 同做法）
    private readonly HashSet<int> _hitThisSwing = new HashSet<int>();

    private static readonly Collider[] s_ArcBuffer = new Collider[128];

    private void Awake()
    {
        if (colliders == null || colliders.Length == 0)
            colliders = GetComponents<Collider>();

        if (colliders.Length == 0)
            Debug.LogError($"[MeleeHitbox] '{name}' 上沒有任何 Collider，這個 hitbox 永遠不會命中。", this);

        foreach (var c in colliders)
        {
            if (c == null) continue;
            c.isTrigger = true;
            c.enabled = false;
        }

        if (addKinematicRigidbody && GetComponent<Rigidbody>() == null)
        {
            var rb = gameObject.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
        }
    }

    private void OnDisable() => Close();

    /// <summary>設定這一刀的傷害。必須在 Open() 之前呼叫。</summary>
    public void Configure(in MeleeHitData data)
    {
        _data = data;
        _configured = true;
    }

    /// <summary>開刀鋒。由 Animation Event 驅動。會清空本次揮擊的命中記錄。</summary>
    public void Open()
    {
        if (!_configured)
        {
            Debug.LogWarning($"[MeleeHitbox] '{name}' 還沒 Configure 就被 Open，這一刀不會造成傷害。", this);
            return;
        }

        _hitThisSwing.Clear();
        _open = true;
        SetCollidersEnabled(detectionMode == DetectionMode.Trigger);   // Arc 模式不需要碰撞體觸發

        if (detectionMode == DetectionMode.Arc) SweepArc();   // 開啟的這一刻就先判定一次
    }

    /// <summary>收刀鋒。由 Animation Event 驅動。</summary>
    public void Close()
    {
        if (_open) _closedAt = Time.time;
        _open = false;
        SetCollidersEnabled(false);
    }

    private void SetCollidersEnabled(bool enabled)
    {
        if (colliders == null) return;

        foreach (var c in colliders)
        {
            if (c != null) c.enabled = enabled;
        }
    }

    // OnTriggerStay 是必要的：hitbox 有可能在「已經重疊」的狀態下才打開，
    // 那種情況不會觸發 OnTriggerEnter。去重由 _hitThisSwing 負責。
    private void OnTriggerEnter(Collider other) => TryHit(other);
    private void OnTriggerStay(Collider other) => TryHit(other);

    private void FixedUpdate()
    {
        if (_open && detectionMode == DetectionMode.Arc) SweepArc();
    }

    // ═══════════════════════ Arc 判定 ═══════════════════════

    private Transform ArcFacing =>
        _data.facing != null ? _data.facing : (_data.attacker != null ? _data.attacker.transform : transform);

    private Vector3 ArcOrigin => ArcFacing.position + Vector3.up * arcOriginHeight;

    private float ArcRange => _data.arcRange > 0f ? _data.arcRange : 3.5f;
    private float ArcAngle => _data.arcAngle > 0f ? _data.arcAngle : 120f;
    private float ArcPitchAngle => _data.arcPitchAngle > 0f ? _data.arcPitchAngle : 90f;

    private void SweepArc()
    {
        Transform facing = ArcFacing;
        Vector3 origin = ArcOrigin;
        float range = ArcRange;
        float halfYaw = ArcAngle * 0.5f;
        float halfPitch = ArcPitchAngle * 0.5f;
        Quaternion toLocal = Quaternion.Inverse(facing.rotation);

        int count = Physics.OverlapSphereNonAlloc(origin, range, s_ArcBuffer,
                                                  _data.hittableLayers, QueryTriggerInteraction.Collide);
        for (int i = 0; i < count; i++)
        {
            Collider col = s_ArcBuffer[i];
            s_ArcBuffer[i] = null;
            if (col == null || !_open) continue;

            // 碰撞體上離原點最近的點：大型敵人量到表面，不是中心
            Vector3 p = ClosestPointSafe(col, origin);
            Vector3 local = toLocal * (p - origin);
            float dist = local.magnitude;
            if (dist > range) continue;

            if (dist > arcPointBlank)
            {
                float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
                if (Mathf.Abs(yaw) > halfYaw || Mathf.Abs(pitch) > halfPitch) continue;
            }

            TryHit(col);
        }
    }

    // ClosestPoint 只支援 Box / Sphere / Capsule / Convex Mesh；其他用包圍盒
    private static Vector3 ClosestPointSafe(Collider c, Vector3 point)
    {
        if (c is BoxCollider || c is SphereCollider || c is CapsuleCollider || (c is MeshCollider mc && mc.convex))
            return c.ClosestPoint(point);
        return c.bounds.ClosestPoint(point);
    }

    // ═══════════════════════ 命中結算（兩種判定共用） ═══════════════════════

    private void TryHit(Collider other)
    {
        if (!_open || other == null) return;

        // 分敵我
        if ((_data.hittableLayers.value & (1 << other.gameObject.layer)) == 0) return;

        var target = other.GetComponentInParent<IDamageable>();
        if (target == null) return;

        var targetComp = target as Component;

        // 不打自己
        if (_data.attacker != null && targetComp != null &&
            targetComp.transform.IsChildOf(_data.attacker.transform))
            return;

        // 同一次揮擊、同一個目標只結算一次（多 collider 的敵人不會被重複打）
        int id = (targetComp != null) ? targetComp.GetInstanceID() : other.GetInstanceID();
        if (!_hitThisSwing.Add(id)) return;

        // 暴擊在攻擊方結算（暴擊是攻擊者的屬性）
        bool crit = UnityEngine.Random.value < Mathf.Clamp01(_data.criticalChance);
        float critMul = crit ? Mathf.Max(1f, _data.criticalMultiplier) : 1f;

        // 只交出「原始四種傷害 + 衝擊值」，防禦由被打的目標自己套用。衝擊值不吃暴擊。
        var dealt = new DamageInfo(
            _data.baseDamage.physical * critMul,
            _data.baseDamage.explosion * critMul,
            _data.baseDamage.energy * critMul,
            _data.baseDamage.cold * critMul,
            _data.baseDamage.impact)
        {
            kind = DamageKind.Melee,
            isCritical = crit,
        };

        target.TakeDamage(dealt, _data.attacker);

        // 命中特效的位置：敵人身上離刀身最近的點（Arc 模式也一樣，火花出現在刀碰到的地方）
        Vector3 point = ClosestPointSafe(other, transform.position);
        Vector3 direction = ResolveHitDirection(point);

        ApplyKnockback(targetComp, direction);

        if (logHits)
            Debug.Log($"[MeleeHitbox] hit '{(targetComp != null ? targetComp.name : other.name)}' " +
                      $"phys={dealt.physical:F1} crit={crit}", this);

        OnHitTarget?.Invoke(new MeleeHitResult
        {
            target = target,
            targetComponent = targetComp,
            point = point,
            direction = direction,
            wasCritical = crit,
            dealt = dealt,
        });
    }

    private Vector3 ResolveHitDirection(Vector3 point)
    {
        Vector3 dir = (_data.attacker != null)
            ? point - _data.attacker.transform.position
            : transform.forward;

        dir.y = 0f;
        return (dir.sqrMagnitude > 0.0001f) ? dir.normalized : transform.forward;
    }

    private void ApplyKnockback(Component targetComp, Vector3 direction)
    {
        if (_data.knockback <= 0f || targetComp == null) return;

        var rb = targetComp.GetComponent<Rigidbody>();
        if (rb == null || rb.isKinematic) return;

        rb.AddForce(direction * _data.knockback, ForceMode.Impulse);
    }

#if UNITY_EDITOR
    private void OnDrawGizmos()
    {
        if (!drawGizmoWhenOpen || !_configured) return;

        if (detectionMode == DetectionMode.Arc)
        {
            // 開著：亮橘色；剛關掉（Gizmo Linger 秒內）：淡一點，方便看清楚剛才那一刀的範圍
            if (_open) DrawArcGizmo(1f);
            else if (Application.isPlaying && Time.time - _closedAt < gizmoLinger) DrawArcGizmo(0.35f);
            return;
        }

        if (!_open) return;

        if (colliders == null) return;

        Gizmos.color = new Color(1f, 0.3f, 0.1f, 0.35f);
        foreach (var c in colliders)
        {
            if (c == null || !c.enabled) continue;
            var b = c.bounds;
            Gizmos.DrawCube(b.center, b.size);
        }
    }

    // 扇形：水平的弧 + 上下邊界的弧
    private void DrawArcGizmo(float alpha)
    {
        Transform facing = ArcFacing;
        Vector3 o = ArcOrigin;
        float r = ArcRange, half = ArcAngle * 0.5f, halfP = ArcPitchAngle * 0.5f;
        const int seg = 16;

        Gizmos.color = new Color(1f, 0.3f, 0.1f, 0.9f * alpha);
        foreach (float pitch in new[] { 0f, -halfP, halfP })
        {
            Vector3 prev = o;
            for (int s = 0; s <= seg; s++)
            {
                float yaw = Mathf.Lerp(-half, half, s / (float)seg);
                Vector3 dir = facing.rotation * (Quaternion.Euler(-pitch, yaw, 0f) * Vector3.forward);
                Vector3 pt = o + dir * r;
                if (s == 0 || s == seg) Gizmos.DrawLine(o, pt);
                if (s > 0) Gizmos.DrawLine(prev, pt);
                prev = pt;
            }
        }
    }
#endif
}