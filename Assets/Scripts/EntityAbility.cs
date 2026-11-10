using UnityEngine;

/// <summary>
/// 敵人的特殊攻擊模式（能力）的基底類別。掛在敵人身上（根物件或子物件），Brain 會自動抓到。
///
/// 流程（由 ModularEntityBrain 驅動，只在 Combat 狀態）：
///   1. Brain 每隔 Ability Think Interval 想一次：
///      冷卻好了（IsReady）→ 條件成立（CanStart）→ 機率通過（Chance）的能力裡，取 Priority 最高的發動。
///   2. 發動中：Brain 每次思考呼叫 Tick()；Brain 自己負責停下、面向目標、砲塔只瞄不打。
///   3. 子類別在 OnTick 回傳 true = 結束 → 進冷卻，Brain 恢復一般戰鬥。
///   4. 失衡、離開戰鬥、物件停用 → Interrupt()（也進冷卻）。
///
/// 時間請用 Time.time 自己記，不要累加 Tick 的間隔 —— Brain 約 0.1 秒才思考一次。
/// </summary>
public abstract class EntityAbility : MonoBehaviour
{
    [Header("Ability")]
    [Tooltip("結束（或被打斷）後多久才能再發動（秒）。")]
    [SerializeField, Min(0f)] protected float cooldown = 25f;

    [Tooltip("條件成立時發動的機率（每次 Brain 想要不要發動能力時擲一次）。")]
    [SerializeField, Range(0f, 1f)] protected float chance = 0.5f;

    [Tooltip("同時有好幾個能力可以發動時，取數字大的。")]
    [SerializeField] protected int priority = 0;

    [Tooltip("發動中身體轉向目標。")]
    [SerializeField] protected bool faceTarget = true;

    [Tooltip("開場（生成）後多久才能第一次發動（秒）。")]
    [SerializeField, Min(0f)] protected float initialCooldown = 5f;

    protected ModularEntityBrain brain;
    private float _readyTime;

    public bool IsRunning { get; private set; }
    public bool IsReady => !IsRunning && Time.time >= _readyTime;
    public int Priority => priority;
    public bool FaceTarget => faceTarget;

    protected virtual void Awake()
    {
        brain = GetComponentInParent<ModularEntityBrain>();
        _readyTime = Time.time + initialCooldown;
    }

    /// <summary>從物件池重新生成時由 Brain 呼叫。</summary>
    public virtual void ResetForSpawn()
    {
        if (IsRunning) Interrupt();
        _readyTime = Time.time + initialCooldown;
    }

    public bool RollChance() => Random.value < chance;

    /// <summary>發動條件（距離、高度差……）。冷卻與機率由基底處理。</summary>
    public abstract bool CanStart(Transform target);

    public void Begin(Transform target)
    {
        IsRunning = true;
        OnBegin(target);
    }

    public void Tick(Transform target)
    {
        if (!IsRunning) return;
        if (OnTick(target))
        {
            OnFinish();
            EndRun();
        }
    }

    public void Interrupt()
    {
        if (!IsRunning) return;
        OnInterrupt();
        EndRun();
    }

    private void EndRun()
    {
        IsRunning = false;
        _readyTime = Time.time + cooldown;
    }

    protected virtual void OnDisable()
    {
        if (IsRunning) Interrupt();
    }

    protected abstract void OnBegin(Transform target);

    /// <summary>每次 Brain 思考時呼叫。回傳 true = 能力結束。target 可能是 null（目標暫時不見）。</summary>
    protected abstract bool OnTick(Transform target);

    /// <summary>正常結束時。</summary>
    protected virtual void OnFinish() { }

    /// <summary>被打斷時（失衡等）。要負責清掉蓄力特效、動畫參數。</summary>
    protected virtual void OnInterrupt() { }
}
