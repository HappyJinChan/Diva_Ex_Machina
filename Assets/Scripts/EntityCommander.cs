using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Boss 的「指揮能力」基底類別：把附近的小兵納入指揮，覆寫它們在 Combat 狀態的移動目的地。
/// 掛在 Boss 身上（根物件或子物件），自己定時思考，不需要 Boss 的 Brain 做任何配合。
///
/// 跟 EntityAbility 的差別：能力是互斥的（一次一個、發動中 Boss 會停下），
/// 指揮是常駐的 —— Boss 在 Combat 狀態期間一直生效，可以和蓄力攻擊等能力同時進行，
/// 而且完全不影響 Boss 自己的移動。
///
/// 流程（每 Think Interval 秒一次）：
///   1. Boss 不在 Combat、沒有目標、死亡或停用 → 解除所有部下，結束。
///   2. Boss 換目標 → 部下跟著換鎖定的目標。
///   3. 部下陣亡（停用或 IsDead）→ 空出位置。
///   4. 招募：依位置的優先順序（索引 0 最優先）補人，每個位置挑離它最近的候選。
///   5. 遞補：還有空位但招不到人 → 後面的部下往前遞補。
///   6. 子類別更新陣形（OnCommandTick）。
///
/// 部下這邊（ModularEntityBrain）被指揮時：仇恨鎖定、永遠可以攻擊、Combat 的目的地向這裡要。
/// 其他行為（近戰、砲塔、失衡）照舊。
///
/// 子類別要實作：有幾個位置（SlotCount）、每個位置的站位（GetSlotPosition）、
/// 每個位置現在要去哪裡（TryGetSlotDestination）。
/// </summary>
public abstract class EntityCommander : MonoBehaviour
{
    [Header("Command")]
    [Tooltip("招募範圍（公尺，以 Boss 為中心）。")]
    [SerializeField, Min(0f)] protected float recruitRadius = 80f;

    [Tooltip("可以招募的指揮群組（對應 ModularEntityStats.Command Group）。\n" +
             "關卡腳本呼叫 SetScriptedUnits 指定部下時，不看群組。")]
    [SerializeField] protected string[] commandGroups = { "Hound" };

    [Tooltip("Commander 多久想一次（秒）：檢查陣亡、補位、更新陣形。")]
    [SerializeField, Min(0.02f)] protected float thinkInterval = 0.2f;

    [Tooltip("部下重算路徑的間隔（秒）。一般 Combat 是 1–3 秒一次，追不上側面、背後這種一直在變的位置。")]
    [SerializeField, Min(0.05f)] protected float repathInterval = 0.25f;

    /// <summary>Boss 自己的 Brain。</summary>
    protected ModularEntityBrain owner;
    /// <summary>各個位置目前的部下（null = 空位）。索引越小越優先。</summary>
    protected ModularEntityBrain[] slots;
    /// <summary>目前指揮的目標（= Boss 的交戰目標）。</summary>
    protected Transform target;

    public bool IsCommanding { get; private set; }
    public float RepathInterval => repathInterval;
    public Transform CommandTarget => target;

    private ModularEntityStats _ownerStats;
    private float _nextThinkTime;

    // 關卡腳本指定的部下名單。null = 照群組招募。
    private List<ModularEntityBrain> _scriptedUnits;

    private readonly List<ModularEntityBrain> _candidates = new List<ModularEntityBrain>();
    private readonly Dictionary<GameObject, ModularEntityBrain> _brainCache = new Dictionary<GameObject, ModularEntityBrain>();
    private readonly Dictionary<ModularEntityBrain, ModularEntityStats> _statsCache = new Dictionary<ModularEntityBrain, ModularEntityStats>();

    // ───────────────────────── 子類別實作 ─────────────────────────

    /// <summary>位置數量。索引 0 最優先。</summary>
    protected abstract int SlotCount { get; }

    /// <summary>這個位置的站位（世界座標）。招募時用它挑最近的候選。</summary>
    protected abstract Vector3 GetSlotPosition(int slot);

    /// <summary>這個位置的部下現在要去哪裡。回傳 false = 這次不覆寫（部下用自己原本的 Combat 目的地）。</summary>
    protected abstract bool TryGetSlotDestination(int slot, ModularEntityBrain unit, out Vector3 destination);

    protected virtual void OnCommandBegin() { }
    protected virtual void OnCommandEnd() { }
    protected virtual void OnCommandTick() { }
    /// <summary>有部下換位置、陣亡、遞補時呼叫。</summary>
    protected virtual void OnSlotsChanged() { }

    // ───────────────────────── 生命週期 ─────────────────────────

    protected virtual void Awake()
    {
        owner = GetComponentInParent<ModularEntityBrain>();
        if (owner != null) _ownerStats = owner.GetComponent<ModularEntityStats>();
        slots = new ModularEntityBrain[Mathf.Max(0, SlotCount)];
    }

    protected virtual void OnDisable()
    {
        // Boss 死亡 / 回收到物件池
        EndCommand();
    }

    protected virtual void Update()
    {
        if (Time.time < _nextThinkTime) return;
        _nextThinkTime = Time.time + thinkInterval;

        Transform t = ResolveOwnerTarget();
        if (t == null)
        {
            if (IsCommanding) EndCommand();
            return;
        }

        if (!IsCommanding)
        {
            IsCommanding = true;
            target = t;
            OnCommandBegin();
        }
        else if (t != target)
        {
            target = t;
            for (int i = 0; i < slots.Length; i++)
                if (slots[i] != null) slots[i].SetCommandTarget(t);
        }

        PruneSlots();
        Recruit();
        PromoteSlots();
        OnCommandTick();
    }

    // ───────────────────────── 給部下的 Brain 呼叫 ─────────────────────────

    /// <summary>部下的 Brain 在 Combat 的 PathUpdate 呼叫：我現在要去哪裡。</summary>
    public bool TryGetDestination(ModularEntityBrain unit, out Vector3 destination)
    {
        destination = default;
        if (!IsCommanding || target == null) return false;

        int slot = IndexOf(unit);
        if (slot < 0) return false;
        return TryGetSlotDestination(slot, unit, out destination);
    }

    // ───────────────────────── 給關卡腳本呼叫 ─────────────────────────

    /// <summary>指定這個 Boss 只能指揮哪些敵人（不看群組，但仍要在招募範圍內）。</summary>
    public void SetScriptedUnits(IEnumerable<ModularEntityBrain> units)
    {
        _scriptedUnits = units != null ? new List<ModularEntityBrain>(units) : null;
    }

    /// <summary>取消腳本指定，回到依群組招募。</summary>
    public void ClearScriptedUnits() => _scriptedUnits = null;

    // ───────────────────────── 內部 ─────────────────────────

    private Transform ResolveOwnerTarget()
    {
        if (owner == null || !owner.isActiveAndEnabled) return null;
        if (_ownerStats != null && _ownerStats.IsDead) return null;
        if (owner.currentState != EntityState.Combat) return null;
        return owner.CombatTarget != null ? owner.CombatTarget : owner.FindTarget();
    }

    private void EndCommand()
    {
        if (slots != null)
        {
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i] != null) slots[i].ExitCommand(this);
                slots[i] = null;
            }
        }

        if (!IsCommanding) return;
        IsCommanding = false;
        target = null;
        OnCommandEnd();
    }

    protected int IndexOf(ModularEntityBrain unit)
    {
        if (unit == null || slots == null) return -1;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] == unit) return i;
        return -1;
    }

    private bool IsUnitAlive(ModularEntityBrain unit)
    {
        if (unit == null || !unit.isActiveAndEnabled) return false;
        ModularEntityStats stats = GetStats(unit);
        return stats == null || !stats.IsDead;
    }

    /// <summary>陣亡、停用、或被別人接手的部下 → 空出位置。</summary>
    private void PruneSlots()
    {
        bool changed = false;
        for (int i = 0; i < slots.Length; i++)
        {
            ModularEntityBrain u = slots[i];
            if (u == null) continue;
            if (IsUnitAlive(u) && u.Commander == this) continue;

            if (u != null) u.ExitCommand(this);
            slots[i] = null;
            changed = true;
        }
        if (changed) OnSlotsChanged();
    }

    /// <summary>依位置的優先順序補人：每個空位挑離站位最近的候選。</summary>
    private void Recruit()
    {
        bool hasEmpty = false;
        for (int i = 0; i < slots.Length; i++)
            if (slots[i] == null) { hasEmpty = true; break; }
        if (!hasEmpty) return;

        CollectCandidates();
        if (_candidates.Count == 0) return;

        bool changed = false;
        for (int i = 0; i < slots.Length && _candidates.Count > 0; i++)
        {
            if (slots[i] != null) continue;

            Vector3 slotPos = GetSlotPosition(i);
            int best = -1;
            float bestSqr = float.MaxValue;
            for (int c = 0; c < _candidates.Count; c++)
            {
                float d = (_candidates[c].transform.position - slotPos).sqrMagnitude;
                if (d < bestSqr) { bestSqr = d; best = c; }
            }

            ModularEntityBrain unit = _candidates[best];
            _candidates.RemoveAt(best);

            unit.EnterCommand(this, target);
            if (unit.Commander != this) continue;   // 被拒絕（例如同一幀被別的 Boss 搶走）

            slots[i] = unit;
            changed = true;
        }
        if (changed) OnSlotsChanged();
    }

    /// <summary>招不到人時，後面的部下往前遞補，讓優先的位置一直有人。</summary>
    private void PromoteSlots()
    {
        bool changed = false;
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i] != null) continue;
            for (int j = i + 1; j < slots.Length; j++)
            {
                if (slots[j] == null) continue;
                slots[i] = slots[j];
                slots[j] = null;
                changed = true;
                break;
            }
        }
        if (changed) OnSlotsChanged();
    }

    private void CollectCandidates()
    {
        _candidates.Clear();
        if (owner == null) return;

        Vector3 center = owner.transform.position;
        float radiusSqr = recruitRadius * recruitRadius;

        if (_scriptedUnits != null)
        {
            for (int i = 0; i < _scriptedUnits.Count; i++)
                TryAddCandidate(_scriptedUnits[i], center, radiusSqr, checkGroup: false);
            return;
        }

        GameManager gm = GameManager.Instance;
        if (gm == null) return;

        List<GameObject> enemies = gm.enemies;
        for (int i = 0; i < enemies.Count; i++)
        {
            GameObject go = enemies[i];
            if (go == null || !go.activeInHierarchy) continue;
            TryAddCandidate(GetBrain(go), center, radiusSqr, checkGroup: true);
        }
    }

    private void TryAddCandidate(ModularEntityBrain unit, Vector3 center, float radiusSqr, bool checkGroup)
    {
        if (unit == null || unit == owner) return;
        if (unit.IsCommanded) return;
        if (!IsUnitAlive(unit)) return;
        if ((unit.transform.position - center).sqrMagnitude > radiusSqr) return;
        if (checkGroup && !IsInGroup(unit)) return;
        if (_candidates.Contains(unit)) return;
        _candidates.Add(unit);
    }

    private bool IsInGroup(ModularEntityBrain unit)
    {
        ModularEntityStats stats = GetStats(unit);
        if (stats == null || string.IsNullOrEmpty(stats.commandGroup)) return false;
        if (commandGroups == null) return false;
        for (int i = 0; i < commandGroups.Length; i++)
            if (commandGroups[i] == stats.commandGroup) return true;
        return false;
    }

    private ModularEntityBrain GetBrain(GameObject go)
    {
        if (!_brainCache.TryGetValue(go, out ModularEntityBrain brain))
        {
            brain = go.GetComponent<ModularEntityBrain>();
            _brainCache[go] = brain;
        }
        return brain;
    }

    private ModularEntityStats GetStats(ModularEntityBrain unit)
    {
        if (!_statsCache.TryGetValue(unit, out ModularEntityStats stats))
        {
            stats = unit.GetComponent<ModularEntityStats>();
            _statsCache[unit] = stats;
        }
        return stats;
    }

    protected virtual void OnDrawGizmosSelected()
    {
        Transform center = owner != null ? owner.transform : transform;
        Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.5f);
        Gizmos.DrawWireSphere(center.position, recruitRadius);

        if (!Application.isPlaying || !IsCommanding || slots == null) return;
        for (int i = 0; i < slots.Length; i++)
        {
            Vector3 p = GetSlotPosition(i);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(p, 1.5f);
            if (slots[i] != null)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(slots[i].transform.position, p);
            }
        }
    }
}
