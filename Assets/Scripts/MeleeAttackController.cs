using UnityEngine;

/// <summary>
/// 近戰連段狀態機。
///
/// 【核心設計：共用計數器】
/// comboIndex 屬於「這一串連段」，不屬於任何一隻手。換手時計數器繼續往前走，
/// 只是換一把武器的招式表去查。
///
///     playedIndex = min(comboIndex, 該手總段數 - 1)
///     是終結技    = (playedIndex == 該手總段數 - 1)
///
/// 效果：短連段武器等於一張「隨時可插入的終結技牌」。長劍只有 3 段，代表
/// comboIndex ≥ 2 的任何時刻按長劍都會直接打出終結技；錘子 5 段則提供更長的
/// 傷害鋪陳。兩種玩法共用同一條規則，不需要任何特例。
///
/// 【冷卻的單一規則】
/// 任何一隻手只要不再是 activeHand，就立即進入 meleeReloadTime。
/// 涵蓋換手、終結技播完、窗口關閉中斷三種情況。
///
/// 【時間點全部由 Animation Event 驅動】
/// clip 上放四個 event，經 PlayerAnimation 轉發到這裡：
///   AnimEvent_MeleeHitboxOn / Off / ComboWindow / StepEnd
/// maxStepDuration 是唯一的保險絲 —— 任一 clip 漏放 StepEnd，玩家會卡住。
///
/// 掛在跟 AttackManager 同一個 GameObject 上。
/// </summary>
[RequireComponent(typeof(AttackManager))]
public class MeleeAttackController : MonoBehaviour
{
    [SerializeField] private AttackManager attackManager;
    [SerializeField] private PlayerAnimation playerAnimation;

    [Tooltip("突進的實際位移由 PlayerMovement 執行（那裡才有 Rigidbody 與平台速度）。")]
    [SerializeField] private PlayerMovement playerMovement;

    [Header("Data")]
    [Tooltip("武器類型 × 握持 → 連段資料。整個專案通常只需要一份資產。")]
    [SerializeField] private MeleeComboLibrary comboLibrary;

    [Header("Damage")]
    [Tooltip("傷害的歸屬者，通常是玩家根物件。用來排除打到自己。留空則自動抓 AttackManager.playerRb。")]
    [SerializeField] private GameObject attacker;

    [Tooltip("近戰允許打到的層，設成敵人層")]
    [SerializeField] private LayerMask hittableLayers;

    [Header("Animation")]
    [Tooltip("Animator 裡近戰動畫所在的層名稱")]
    [SerializeField] private string meleeLayerName = "One_Hand_Melee_Attack";

    [Header("Hit Feedback")]
    [Tooltip("命中回饋播在命中點（勾選）還是玩家身上（取消）。\n" +
             "命中點通常比較好 —— 火花會出現在刀刃碰到敵人的位置。")]
    [SerializeField] private bool feedbackAtHitPoint = true;

    [Header("Input Buffer")]
    [Tooltip("終結技播放中按下的鍵是否丟掉。\n" +
             "勾選（建議）＝丟掉：冷卻結束後不會自動冒出一刀玩家早就不想要的揮擊。\n" +
             "取消＝保留：冷卻一結束立刻開新的一串連段，攻擊壓力更大但比較難停手。")]
    [SerializeField] private bool dropBufferDuringFinisher = true;

    [Header("Debug")]
    [SerializeField] private bool logStateChanges = false;

    // ───── 連段狀態（單一實例，因為同時只有一隻手在揮）─────

    private int _comboIndex = -1;          // 共用計數器，-1 = 待機
    private bool _attacking;               // 是否有一段正在播
    private bool _windowOpen;              // 連段窗口是否開啟
    private bool _activeIsLeft;            // 目前是哪隻手在揮
    private Weapon _activeWeapon;
    private MeleeAttackStep _activeStep;
    private bool _activeIsFinisher;        // 終結技不接受後續輸入

    // 突進的能量結算：屬於「一整串連段」，不屬於任何一隻手。
    // 第一隻真正突進的手付入場費，換手後不再付；連段 reset 才重新結算。
    private bool _dashEnergyResolved;
    private bool _dashEmpowered;           // true = 有能量的強化模式

    // 彈藥條：這一段開始與結束時的 fill 值，段內依動畫進度插值
    private float _barFillFrom = 1f;
    private float _barFillTo = 1f;
    private int _activeStateHash;

    private float _stepDeadline;           // maxStepDuration 保險絲的到期時刻
    private float _leftCooldownUntil;      // 左手冷卻到什麼時候
    private float _rightCooldownUntil;

    private int _meleeLayerIndex = -1;

    public bool IsAttacking => _attacking;
    public int ComboIndex => _comboIndex;

    /// <summary>
    /// 這個緩衝中的輸入在當前狀態下是否「永遠等不到」被消耗的機會。
    ///
    /// 給 PlayerMovement 用來提早丟掉沒救的緩衝。沒有這道判斷的話，
    /// 玩家在終結技期間亂按，冷卻一結束就會自動冒出一刀他早就不想要的揮擊 ——
    /// 那是輸入緩衝最典型的失敗模式（「幽靈揮擊」）。
    ///
    /// 注意：手在冷卻中「不算」沒救。冷卻結束的瞬間把緩衝揮出來，
    /// 正是輸入緩衝要達成的手感，不能丟。
    /// </summary>
    public bool IsInputHopeless(Weapon w)
    {
        if (w == null || w.kind != HandWeaponKind.Melee) return true;
        if (attackManager == null) return true;

        bool isLeft = (w == attackManager.leftHandWeapon);
        bool isRight = (w == attackManager.rightHandWeapon);
        if (!isLeft && !isRight) return true;

        // 終結技是連段最後一段，後面沒有窗口會開。
        if (dropBufferDuringFinisher && _attacking && _activeIsFinisher) return true;

        return false;
    }

    private void Awake()
    {
        if (attackManager == null) attackManager = GetComponent<AttackManager>();
        if (playerAnimation == null) playerAnimation = attackManager.playerAnimation;

        if (attacker == null && attackManager.playerRb != null)
            attacker = attackManager.playerRb.gameObject;

        if (playerMovement == null && attackManager.playerRb != null)
            playerMovement = attackManager.playerRb.GetComponentInChildren<PlayerMovement>();

        if (playerMovement == null)
            playerMovement = FindAnyObjectByType<PlayerMovement>();

        if (playerAnimation != null && playerAnimation.anim != null)
        {
            _meleeLayerIndex = playerAnimation.anim.GetLayerIndex(meleeLayerName);
            if (_meleeLayerIndex < 0)
                Debug.LogError($"[MeleeAttackController] Animator 裡找不到層 '{meleeLayerName}'，近戰動畫不會播放。", this);
        }
    }

    private void Reset()
    {
        attackManager = GetComponent<AttackManager>();
    }

    private void Update()
    {
        // 保險絲：clip 漏放 AnimEvent_MeleeStepEnd 時強制收尾
        if (_attacking && _stepDeadline > 0f && Time.time >= _stepDeadline)
        {
            Debug.LogWarning($"[MeleeAttackController] '{GetActiveStateName()}' 超過 maxStepDuration 才結束。" +
                             $"檢查這個 clip 上有沒有放 AnimEvent_MeleeStepEnd。", this);
            OnStepEnd();
        }

        UpdateActiveBarFill();
        PushCooldownToUI();

        if (attackManager != null) attackManager.PushAmmoUI();
    }

    // ────────────────────────────────────────────────
    //  輸入入口（由 AttackManager.TryAttack 呼叫）
    // ────────────────────────────────────────────────

    /// <summary>
    /// 嘗試揮出一段。回傳 true 表示這次輸入被消耗掉了。
    ///
    /// 呼叫時機由 PlayerMovement.ProcessAttackFacingAndAttack 決定 ——
    /// 角色已經轉到面向目標（或對準超時）之後才會進來，跟遠程走同一條路徑。
    /// </summary>
    public bool TryStartMelee(Weapon w)
    {
        if (w == null || w.kind != HandWeaponKind.Melee) return false;
        if (comboLibrary == null)
        {
            Debug.LogWarning("[MeleeAttackController] 沒有指派 MeleeComboLibrary。", this);
            return false;
        }

        bool isLeft = (w == attackManager.leftHandWeapon);
        bool isRight = (w == attackManager.rightHandWeapon);
        if (!isLeft && !isRight) return false;

        // 這隻手還在冷卻 → 忽略輸入，連段狀態不變（等窗口自然關閉）
        if (IsHandOnCooldown(isLeft)) return false;

        // 揮擊中且窗口還沒開 → 太早，忽略
        if (_attacking && !_windowOpen) return false;

        // 終結技是連段的最後一段，後面沒有東西可接。
        // 不擋的話，夾住的機制會讓玩家靠狂按無限重播終結技，
        // OnStepEnd 永遠跑不到，冷卻也永遠不觸發。
        if (_attacking && _activeIsFinisher) return false;

        int stepCount = comboLibrary.GetStepCount(w.melee.weaponClass, w.melee.grip);
        if (stepCount <= 0)
        {
            Debug.LogWarning($"[MeleeAttackController] {w.melee.weaponClass} / {w.melee.grip} 沒有連段資料。", this);
            return false;
        }

        // 共用計數器：起手歸 0，接段則往前走一格
        int nextCombo = (_comboIndex < 0) ? 0 : _comboIndex + 1;

        // 換手時，離開的那隻手立刻進入冷卻
        if (_attacking && _activeIsLeft != isLeft)
            BeginCooldown(_activeIsLeft);

        PlayStep(w, isLeft, nextCombo, stepCount);
        return true;
    }

    private void PlayStep(Weapon w, bool isLeft, int comboIndex, int stepCount)
    {
        // 夾住：計數器超過這隻手的段數時，固定打出它的最後一段（終結技）
        int playedIndex = Mathf.Min(comboIndex, stepCount - 1);

        var step = comboLibrary.GetStep(w.melee.weaponClass, w.melee.grip, playedIndex);
        if (step == null) return;

        _comboIndex = comboIndex;
        _activeWeapon = w;
        _activeIsLeft = isLeft;
        _activeStep = step;
        _activeIsFinisher = (playedIndex == stepCount - 1);   // ← 新增
        _attacking = true;
        _windowOpen = false;

        // 近戰對焦：降低鎖定滯後，讓目標穩定在畫面中央。
        // 平時的滯後是刻意設計（目標容易跑出鎖定圈，手動瞄準才有意義），
        // 但近戰需要精準對位，攻擊期間才臨時取消。
        if (PlayerAiming.Instance != null)
            PlayerAiming.Instance.SetMeleeFocus(true);

        w.meleeRuntime.attacking = true;
        w.meleeRuntime.comboIndex = playedIndex;

        // 彈藥條：剩餘段數 / 總段數。
        // 接手的瞬間直接跳到 From —— 換手時共用計數器已經走了很遠，
        // 這個跳動正是在告訴玩家「你接的是第幾段」。
        _barFillFrom = (float)(stepCount - playedIndex) / stepCount;
        _barFillTo = (float)(stepCount - playedIndex - 1) / stepCount;
        w.meleeRuntime.barFill = _barFillFrom;

        // 上一段的 hitbox 可能還開著（連段接得很快時），先關掉
        CloseAllHitboxes();

        // 上一段的突進也要停 —— 新的一段有自己的 DashStart
        if (playerMovement != null)
            playerMovement.StopMeleeDash("NextComboStep");

        float speed = Mathf.Max(0.01f, w.melee.meleeSpeed);
        _stepDeadline = (step.maxStepDuration > 0f)
            ? Time.time + (step.maxStepDuration / speed)
            : 0f;

        PlayAnimation(step, isLeft, speed);

        if (logStateChanges)
        {
            Debug.Log($"[Melee] combo={comboIndex} played={playedIndex}/{stepCount - 1} " +
                      $"hand={(isLeft ? "L" : "R")} {(_activeIsFinisher ? "FINISHER" : "")} " +
                      $"state={step.GetStateName(isLeft)}", this);
        }
    }

    private void PlayAnimation(MeleeAttackStep step, bool isLeft, float speed)
    {
        if (playerAnimation == null || playerAnimation.anim == null) return;
        if (_meleeLayerIndex < 0) return;

        playerAnimation.SetToAttackLayer();

        // 註：meleeSpeed 目前只縮放保險絲，還沒套用到動畫播放速度。
        // 要讓動畫真的變快，需要在每個 State 上加 Speed Multiplier 參數
        // （Animator State Inspector → Speed → Multiplier 勾選 Parameter）。
        // 等基本連段跑通再處理。
        string stateName = step.GetStateName(isLeft);
        _activeStateHash = Animator.StringToHash(stateName);
        playerAnimation.anim.CrossFade(stateName, step.crossFadeTime, _meleeLayerIndex, 0f);
    }

    // ────────────────────────────────────────────────
    //  Animation Event 回呼（由 PlayerAnimation 轉發）
    // ────────────────────────────────────────────────

    /// <summary>刀刃開始有傷害。</summary>
    public void OnHitboxOn()
    {
        if (!_attacking || _activeWeapon == null || _activeStep == null) return;

        var hitbox = _activeWeapon.melee.hitbox;
        if (hitbox == null)
        {
            Debug.LogWarning("[MeleeAttackController] 這把武器沒有 hitbox，揮空氣。", this);
            return;
        }

        float mul = _activeWeapon.melee.meleeOutput * _activeStep.damageMultiplier;

        hitbox.Configure(new MeleeHitData
        {
            attacker = (attacker != null) ? attacker : gameObject,
            baseDamage = new DamageInfo(
                _activeWeapon.damage.physicalDamage * mul,
                _activeWeapon.damage.explosionDamage * mul,
                _activeWeapon.damage.energyDamage * mul,
                _activeWeapon.damage.coldDamage * mul),
            // 暴擊：以武器本體為基礎 + 零件 / 手甲 / 頭盔的加成（由 AttackManager 算好）
            criticalChance = _activeWeapon.damage.criticalChance,
            criticalMultiplier = _activeWeapon.damage.criticalMultiplier,
            knockback = _activeStep.knockback,
            hittableLayers = hittableLayers,
        });

        // 訂閱命中事件。先退訂再訂閱，避免同一個 hitbox 被重複掛上
        // （連段每一段都會走這裡，武器 instance 是同一個）。
        hitbox.OnHitTarget -= HandleMeleeHit;
        hitbox.OnHitTarget += HandleMeleeHit;

        hitbox.Open();

        // 方案 B：命中判定開始時錨定到目標，繼承其部分速度。
        // 高速交戰下，不錨定的話 dashStopDistance 只夠撐不到一個物理幀，
        // 打中了也會立刻滑開、接不上下一段。
        if (playerMovement != null && PlayerAiming.Instance != null)
        {
            var targetRb = PlayerAiming.Instance.GetTargetRigidbody();
            if (targetRb != null)
                playerMovement.BeginMeleeAnchor(targetRb);
        }
    }

    /// <summary>
    /// 開始突進。由 clip 上的 AnimEvent_MeleeDashStart 觸發，
    /// 放在舉刀之後、HitboxOn 之前 —— 先舉刀、再突進、再劈下。
    ///
    /// 能量在「這一串連段第一個真正突進的段」結算一次：
    ///   夠 → 扣能量，使用 dashSpeed
    ///   不夠 → 不扣，退化成 sprintSpeed
    /// 之後同一串的突進段沿用第一次的結果，不再檢查也不再扣。
    /// 突進距離不再有獨立數值：由 DashStart → Brake 的動畫時間窗 × 實際速度決定。
    /// </summary>
    public void OnDashStart()
    {
        if (!_attacking || _activeWeapon == null || _activeStep == null) return;
        if (_activeStep.dashMode == MeleeDashMode.None) return;
        if (playerMovement == null) return;

        var ps = PlayerStats.Instance;
        if (ps == null) return;

        // 一串連段只結算一次能量
        if (!_dashEnergyResolved)
        {
            _dashEmpowered = (ps.currentEnergy >= ps.dashEnergyCost);
            if (_dashEmpowered)
                ps.currentEnergy -= ps.dashEnergyCost;

            _dashEnergyResolved = true;

            if (logStateChanges)
                Debug.Log($"[Melee] dash energy resolved: {(_dashEmpowered ? "EMPOWERED" : "WEAKENED")}", this);
        }

        // 突進距離完全交給動畫時間窗控制。
        // 有能量時用 dashSpeed；能量不足時用 sprintSpeed，所以同一段動畫自然會衝得較短。
        float speed = _dashEmpowered ? ps.dashSpeed : ps.sprintSpeed;

        // ToTarget：用 PlayerAiming 的鎖定目標。它只在 lockOnRange 圈內才會鎖定，
        // 所以「圈外不追」是自動成立的，不需要額外的角度判定。
        // 傳 Rigidbody 而不是 Transform —— PlayerMovement 需要目標速度來算攔截點。
        Rigidbody target = null;
        if (_activeStep.dashMode == MeleeDashMode.ToTarget && PlayerAiming.Instance != null)
            target = PlayerAiming.Instance.GetTargetRigidbody();

        // 沒有目標（或 Forward 模式）→ 沿角色當前面向
        Vector3 dir = (target != null)
            ? (target.position - playerMovement.transform.position)
            : GetFacingDirection();

        playerMovement.BeginMeleeDash(dir, speed, target, _activeStep.dashStopDistance);
    }

    private Vector3 GetFacingDirection()
    {
        var model = (playerAnimation != null) ? playerAnimation.transform : transform;
        Vector3 f = model.forward;
        f.y = 0f;
        return (f.sqrMagnitude > 0.0001f) ? f.normalized : Vector3.forward;
    }

    /// <summary>
    /// 這一段是否禁止 Dash 取消。PlayerMovement.DashAction 開頭會檢查。
    /// </summary>
    public bool IsBlockingDash =>
        _attacking && _activeStep != null && !_activeStep.cancellableByDash;

    /// <summary>
    /// 揮擊被 Dash 取消。由 PlayerMovement.DashAction 成功時呼叫。
    /// 跟 OnStepEnd 一樣收尾：關 hitbox、停突進、當前手進冷卻、reset。
    /// </summary>
    public void CancelByDash()
    {
        if (!_attacking) return;

        if (logStateChanges)
            Debug.Log("[Melee] cancelled by dash", this);

        OnStepEnd();
    }

    /// <summary>
    /// 煞停突進動量。由 clip 上的 AnimEvent_MeleeBrake 觸發。
    ///
    /// 刻意跟 StepEnd 分開：煞車時機不一定跟收招結束重合。
    /// 劈砍類想在刀刃落到底的瞬間就煞住（衝勢轉成打擊感），
    /// 突刺類則可能想讓動量延續到收招末端。
    ///
    /// 沒放這個 event 的段就不會煞車，突進動量會自然衰減。
    /// </summary>
    public void OnBrake()
    {
        // ★ 這道守衛是必要的：Dash 取消揮擊時，SetOffAttackLayer 只把層權重淡到 0，
        //   clip 本身還在那個 State 上繼續播，而 Animation Event 不管層權重多少
        //   都會照常觸發。沒有這行的話，被取消的那一刀仍會在稍後煞掉玩家的 Dash。
        if (!_attacking) return;

        if (playerMovement == null) return;

        playerMovement.StopMeleeDash("AnimEvent_MeleeBrake");
        playerMovement.BeginMeleeBrake();

        if (logStateChanges)
            Debug.Log("[Melee] brake", this);
    }

    /// <summary>
    /// 實際造成傷害時的回饋。由 MeleeHitbox.OnHitTarget 觸發，
    /// 所以揮空不會播 —— 這是它跟 Animation Event 的差別。
    ///
    /// 同一刀掃到多個敵人會觸發多次（hitbox 對每個目標各結算一次）。
    /// </summary>
    private void HandleMeleeHit(MeleeHitResult result)
    {
        if (playerAnimation == null) return;

        Vector3 pos = feedbackAtHitPoint ? result.point : playerAnimation.transform.position;
        playerAnimation.PlayMeleeHitFeedback(pos);
    }

    /// <summary>刀刃結束傷害判定。</summary>
    public void OnHitboxOff()
    {
        // 沒有 _attacking 守衛 —— 被取消的刀也該確保 hitbox 是關的（關兩次無害）。
        CloseAllHitboxes();
    }

    /// <summary>開始接受下一段輸入。</summary>
    public void OnComboWindow()
    {
        if (!_attacking) return;
        _windowOpen = true;
    }

    /// <summary>
    /// 這一段結束。
    ///
    /// 走到這裡代表玩家沒有在窗口內接下一段（接了的話 TryStartMelee 會直接
    /// PlayStep 覆蓋掉狀態，這個 event 對舊的那一段就不再有意義）。
    /// 所以無論是終結技播完還是中途斷掉，處理方式相同：收招 + 進冷卻。
    /// </summary>
    public void OnStepEnd()
    {
        if (!_attacking) return;

        CloseAllHitboxes();

        if (playerMovement != null)
        {
            playerMovement.StopMeleeDash("AnimEvent_MeleeStepEnd");

            // 滯空撐到這裡才解除 —— 突進停了刀還在揮，中途恢復重力
            // 會讓角色從空中目標旁邊掉下去。
            playerMovement.EndMeleeHover();
        }

        BeginCooldown(_activeIsLeft);
        ResetCombo();
    }

    // ────────────────────────────────────────────────
    //  狀態轉換
    // ────────────────────────────────────────────────

    private void ResetCombo()
    {
        if (_activeWeapon != null)
        {
            _activeWeapon.meleeRuntime.attacking = false;
            _activeWeapon.meleeRuntime.comboIndex = -1;
        }

        // 錨定跨越整串連段 —— 每一段結束就解除的話，段與段之間會滑開，
        // 正好破壞它要解決的問題。
        if (playerMovement != null)
            playerMovement.EndMeleeAnchor();

        if (PlayerAiming.Instance != null)
            PlayerAiming.Instance.SetMeleeFocus(false);

        _comboIndex = -1;
        _attacking = false;
        _windowOpen = false;
        _activeWeapon = null;
        _activeStep = null;
        _activeIsFinisher = false;
        _stepDeadline = 0f;

        // 能量入場費隨連段一起重置：下一串要重新結算
        _dashEnergyResolved = false;
        _dashEmpowered = false;

        if (playerAnimation != null)
            playerAnimation.SetOffAttackLayer();

        if (logStateChanges)
            Debug.Log("[Melee] combo reset", this);
    }

    private void BeginCooldown(bool isLeft)
    {
        var w = isLeft ? attackManager.leftHandWeapon : attackManager.rightHandWeapon;
        if (w == null || w.kind != HandWeaponKind.Melee) return;

        // 硬直長度單純來自 MeleeReloadTime（武器側屬性，已含 buff）
        float duration = Mathf.Max(0f, w.melee.reloadTime);

        if (isLeft) _leftCooldownUntil = Time.time + duration;
        else _rightCooldownUntil = Time.time + duration;

        w.meleeRuntime.reloading = duration > 0f;
        w.meleeRuntime.cooldownNormalized = 0f;

        // 掉到 0 再填回 —— 中斷跟打完終結技的硬直一樣長，所以視覺上一致。
        w.meleeRuntime.barFill = 0f;
        if (duration <= 0f) w.meleeRuntime.barFill = 1f;

        if (logStateChanges)
            Debug.Log($"[Melee] {(isLeft ? "L" : "R")} cooldown {duration:F2}s", this);
    }

    private bool IsHandOnCooldown(bool isLeft)
    {
        return Time.time < (isLeft ? _leftCooldownUntil : _rightCooldownUntil);
    }

    private void CloseAllHitboxes()
    {
        CloseHitbox(attackManager.leftHandWeapon);
        CloseHitbox(attackManager.rightHandWeapon);
    }

    private void CloseHitbox(Weapon w)
    {
        if (w == null || w.melee.hitbox == null) return;

        // 退訂避免武器換掉後事件還掛在舊實例上
        w.melee.hitbox.OnHitTarget -= HandleMeleeHit;
        w.melee.hitbox.Close();
    }

    private void OnDisable() => CloseAllHitboxes();

    // 正在揮的那隻手：在這一段內平滑下降。
    // 進度取自 Animator 的 normalizedTime，所以之後把 meleeSpeed 接到
    // Speed Multiplier 之後，條的下降速度會自動跟著動畫同步。
    private void UpdateActiveBarFill()
    {
        if (!_attacking || _activeWeapon == null) return;
        if (playerAnimation == null || playerAnimation.anim == null || _meleeLayerIndex < 0) return;

        var st = playerAnimation.anim.GetCurrentAnimatorStateInfo(_meleeLayerIndex);

        // CrossFade 期間可能還回傳上一個 State，那幾幀維持現值不動
        if (st.shortNameHash != _activeStateHash) return;

        float p = Mathf.Clamp01(st.normalizedTime);
        _activeWeapon.meleeRuntime.barFill = Mathf.Lerp(_barFillFrom, _barFillTo, p);
    }

    // 把冷卻進度餵給彈藥環（CalcAmmoBarFill 會讀 cooldownNormalized）
    private void PushCooldownToUI()
    {
        UpdateHandCooldown(attackManager.leftHandWeapon, _leftCooldownUntil, true);
        UpdateHandCooldown(attackManager.rightHandWeapon, _rightCooldownUntil, false);
    }

    private void UpdateHandCooldown(Weapon w, float until, bool isLeft)
    {
        if (w == null || w.kind != HandWeaponKind.Melee) return;
        if (!w.meleeRuntime.reloading) return;

        float remaining = until - Time.time;
        if (remaining <= 0f)
        {
            w.meleeRuntime.reloading = false;
            w.meleeRuntime.cooldownNormalized = 1f;
            w.meleeRuntime.barFill = 1f;      // 冷卻完成 = 滿格，隨時可起手
            return;
        }

        float total = Mathf.Max(0.0001f, w.melee.reloadTime);
        float progress = Mathf.Clamp01(1f - (remaining / total));

        w.meleeRuntime.cooldownNormalized = progress;
        w.meleeRuntime.barFill = progress;    // 冷卻中，條從 0 填回 1
    }

    private string GetActiveStateName()
    {
        return (_activeStep != null) ? _activeStep.GetStateName(_activeIsLeft) : "(none)";
    }
}