using System;
using MoreMountains.Feedbacks;
using UnityEngine;

/// <summary>
/// 實體的特殊狀態管理。目前只有「失衡」，之後的燃燒、脆化、脈衝幹擾也放在這裡。
/// 掛在敵人的根物件上（跟 ModularEntityStats 同一個物件）。
/// 沒有掛的話，ModularEntityStats 第一次用到時會自動補上一個（數值用這裡的預設值），
/// 想個別調整的敵人請直接在 prefab 上加這個元件。
///
/// ── 失衡（ACS：Attitude Control System，姿態控制）──
///
///   · 受到攻擊時，ACS 減去這一擊的 Impact（DamageInfo.impact）。
///     Impact 不吃防禦、不吃暴擊；子彈的 Impact 跟傷害一樣受距離衰減。
///   · ACS 減到 0 → 失衡 staggerDuration 秒：
///       - 無法行動（移動、轉向、近戰全部停止，Brain 不做決策）
///       - 所有砲塔停止攻擊（連發到一半也會中斷），飛彈不發射
///       - 受到的傷害 × staggerDamageTakenMultiplier（防禦之後再乘）
///       - 期間 ACS 保持 0，Impact 不再累積
///   · 失衡結束 → ACS 直接回滿。
///   · 沒有失衡的時候 ACS 不會自然回復。
///
/// 為什麼沒有 Update：場上敵人有好幾百隻，這裡不每幀跑。
/// 失衡的結束時間在每次讀取 IsStaggered / CurrentACS 時檢查 ——
/// Movement 每個物理步都會讀，所以結束的時間點最多晚一個物理步。
/// </summary>
[DisallowMultipleComponent]
public class EntityStatusEffects : MonoBehaviour
{
    // ═══════════════════════ 失衡 ═══════════════════════

    [Header("ACS（姿態控制）")]
    [Tooltip("ACS 上限。受到攻擊時減去 Impact，減到 0 就失衡。\n0 = 這個實體不會失衡。")]
    [Min(0f)] public float maxACS = 100f;

    [Tooltip("目前的 ACS（執行中顯示用）。")]
    [SerializeField] private float currentACS;

    [Header("失衡")]
    [Tooltip("失衡持續多久（秒）。")]
    [Min(0f)] public float staggerDuration = 3f;

    [Tooltip("失衡期間受到的傷害倍率（防禦計算之後再乘）。1.5 = 多受 50%。")]
    [Min(0f)] public float staggerDamageTakenMultiplier = 1.5f;

    [Tooltip("Animator 上的 bool 參數名稱。失衡期間為 true。Animator 沒有這個參數時自動略過。")]
    [SerializeField] private string staggerAnimBool = "stagger";

    [Tooltip("失衡開始時播放、結束時停止（例如火花、警報燈）。可以留空。")]
    [SerializeField] private MMF_Player staggerFeedback;

    /// <summary>失衡開始。</summary>
    public event Action OnStaggerStart;

    /// <summary>失衡結束（ACS 已經回滿）。死亡或被停用時不會觸發。</summary>
    public event Action OnStaggerEnd;

    private bool _staggered;
    private float _staggerUntil;

    private Animator _animator;
    private bool _animResolved;
    private bool _hasAnimParam;
    private int _animHash;

    /// <summary>是否正在失衡。</summary>
    public bool IsStaggered
    {
        get { RefreshStagger(); return _staggered; }
    }

    /// <summary>目前的 ACS。</summary>
    public float CurrentACS
    {
        get { RefreshStagger(); return currentACS; }
    }

    /// <summary>ACS 比例 0～1（給血條之類的 UI）。不會失衡的實體固定回傳 1。</summary>
    public float ACSRatio => maxACS > 0f ? Mathf.Clamp01(CurrentACS / maxACS) : 1f;

    /// <summary>失衡剩餘秒數。沒有失衡時為 0。</summary>
    public float StaggerTimeRemaining => IsStaggered ? Mathf.Max(0f, _staggerUntil - Time.time) : 0f;

    /// <summary>受到傷害的倍率（失衡中為 staggerDamageTakenMultiplier，否則 1）。</summary>
    public float DamageTakenMultiplier => IsStaggered ? staggerDamageTakenMultiplier : 1f;

    private void Awake()
    {
        currentACS = maxACS;
    }

    private void OnDisable()
    {
        // 死亡、回收到物件池：安靜地歸零，不發事件（下一次上場是全新的狀態）
        if (_staggered)
        {
            _staggered = false;
            SetAnim(false);
            if (staggerFeedback != null) staggerFeedback.StopFeedbacks();
        }
        currentACS = maxACS;
    }

    /// <summary>
    /// 受到衝擊：ACS 減去 impact，減到 0 就失衡。回傳這一擊是否觸發了失衡。
    /// 失衡期間、maxACS 為 0、impact ≤ 0 時什麼都不做。
    /// </summary>
    public bool ApplyImpact(float impact)
    {
        if (impact <= 0f || maxACS <= 0f) return false;
        if (IsStaggered) return false;   // 期間 ACS 保持 0

        currentACS = Mathf.Max(0f, currentACS - impact);
        if (currentACS > 0f) return false;

        BeginStagger();
        return true;
    }

    private void BeginStagger()
    {
        _staggered = true;
        currentACS = 0f;
        _staggerUntil = Time.time + staggerDuration;

        SetAnim(true);
        if (staggerFeedback != null) staggerFeedback.PlayFeedbacks(transform.position);
        OnStaggerStart?.Invoke();
    }

    private void EndStagger()
    {
        _staggered = false;
        currentACS = maxACS;   // 恢復後直接回滿

        SetAnim(false);
        if (staggerFeedback != null) staggerFeedback.StopFeedbacks();
        OnStaggerEnd?.Invoke();
    }

    private void RefreshStagger()
    {
        if (_staggered && Time.time >= _staggerUntil)
            EndStagger();
    }

    private void SetAnim(bool on)
    {
        if (!_animResolved)
        {
            _animResolved = true;
            _animator = GetComponentInChildren<Animator>();
            if (_animator != null && !string.IsNullOrEmpty(staggerAnimBool))
            {
                _animHash = Animator.StringToHash(staggerAnimBool);
                foreach (AnimatorControllerParameter p in _animator.parameters)
                {
                    if (p.nameHash == _animHash && p.type == AnimatorControllerParameterType.Bool)
                    {
                        _hasAnimParam = true;
                        break;
                    }
                }
            }
        }

        if (_hasAnimParam && _animator != null)
            _animator.SetBool(_animHash, on);
    }

    // ═══════════════════════ 之後的狀態 ═══════════════════════
    //
    // 燃燒（高熱）：可疊加的持續傷害
    // 脆化（極寒）：可疊加的減防
    // 脈衝幹擾（能量）：異常值累積到臨界點時造成一次大傷害，並降低機動性與鎖定性能
}
