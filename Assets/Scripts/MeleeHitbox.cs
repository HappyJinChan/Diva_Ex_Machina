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
/// 掛在近戰武器 prefab 的刀刃上。平時 collider 是關的，
/// 由動畫的 Animation Event 透過 MeleeAttackController 呼叫 Open() / Close()。
///
/// 因為 hitbox 跟著刀刃走，而長柄會把握點推遠、刀刃因此離手更遠，
/// 攻擊距離是自動的 —— 這裡不需要任何依 swordLength 的縮放邏輯。
/// </summary>
public class MeleeHitbox : MonoBehaviour
{
    [Tooltip("這個 hitbox 使用的 Collider。留空則自動抓本物件上的所有 Collider。")]
    [SerializeField] private Collider[] colliders;

    [Tooltip("敵人若沒有 Rigidbody，Unity 不會送出 trigger 事件。\n" +
             "遇到「明明碰到卻沒傷害」時再打開，會在本物件加一顆 kinematic Rigidbody。\n" +
             "注意：這會在玩家底下形成巢狀 Rigidbody，非必要不要開。")]
    [SerializeField] private bool addKinematicRigidbody = false;

    [Header("Debug")]
    [SerializeField] private bool logHits = false;

    [SerializeField] private bool drawGizmoWhenOpen = true;

    /// <summary>每次成功造成傷害時觸發。給控制器做頓幀 / 特效用。</summary>
    public event Action<MeleeHitResult> OnHitTarget;

    public bool IsOpen => _open;

    private MeleeHitData _data;
    private bool _open;
    private bool _configured;

    // 同一次揮擊只打同一個目標一次（跟 Bullet 的 _hitEnemyIds 同做法）
    private readonly HashSet<int> _hitThisSwing = new HashSet<int>();

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
        SetCollidersEnabled(true);
    }

    /// <summary>收刀鋒。由 Animation Event 驅動。</summary>
    public void Close()
    {
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
        };

        target.TakeDamage(dealt, _data.attacker);

        Vector3 point = other.ClosestPoint(transform.position);
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
        if (!drawGizmoWhenOpen || !_open) return;
        if (colliders == null) return;

        Gizmos.color = new Color(1f, 0.3f, 0.1f, 0.35f);
        foreach (var c in colliders)
        {
            if (c == null || !c.enabled) continue;
            var b = c.bounds;
            Gizmos.DrawCube(b.center, b.size);
        }
    }
#endif
}