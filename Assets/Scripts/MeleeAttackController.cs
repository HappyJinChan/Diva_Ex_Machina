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
    [Tooltip("玩家特效（MMF、粒子、推進器火焰）。留空會從 playerAnimation 的物件上自動抓。")]
    [SerializeField] private PlayerEffectManager playerEffects;
    private PlayerEffectManager Effects
    {
        get
        {
            if (playerEffects == null && playerAnimation != null)
                playerEffects = playerAnimation.GetComponent<PlayerEffectManager>();
            return playerEffects;
        }
    }

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

    [Header("Slash VFX")]
    [Tooltip("播放劍風的元件。留空會在角色模型（PlayerAnimation 的物件）上找，沒有就自動加一個。")]
    [SerializeField] private MeleeSlashVfx slashVfx;

    [Header("Two Stage")]
    [Tooltip("武器沒有最大突進距離（Melee Dash Range = 0）時使用的預設值（公尺）。")]
    [SerializeField] private float defaultDashDistance = 15f;

    [Tooltip("突進階段的保險絲：預估時間（距離 ÷ 速度）再多等這麼久還沒到達，就直接出刀。")]
    [SerializeField] private float approachTimeoutPadding = 0.4f;

    [Header("Debug")]
    [SerializeField] private bool logStateChanges = false;

    [Tooltip("攻擊不是照 StepEnd 正常結束時（Dash / 突擊推進取消、保險絲、突進逾時），在 Console 印出原因。")]
    [SerializeField] private bool logAbnormalEnds = true;

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

    private float _stepDeadline;           // maxStepDuration 保險絲的到期時刻
    private float _leftCooldownUntil;      // 左手冷卻到什麼時候
    private float _rightCooldownUntil;

    private int _meleeLayerIndex = -1;

    // 兩段式：突進階段中（突進動畫播放、還沒出刀）
    private bool _approaching;
    private float _approachDeadline;

    // 目前這一段正在播的 State（攻擊 / 突進）。只接受這兩個 State 發出的 Animation Event
    private int _activeStateHash;
    private int _approachStateHash;

    public bool IsAttacking => _attacking;

    /// <summary>兩段式的突進階段中（還沒出刀）。</summary>
    public bool IsApproaching => _approaching;
    public int ComboIndex => _comboIndex;

    /// <summary>正在揮擊的那把武器的 hitbox（沒有在攻擊 = null）。給 AttackEffectGate 判斷「是不是這把武器在揮」。</summary>
    public MeleeHitbox ActiveHitbox => (_attacking && _activeWeapon != null) ? _activeWeapon.melee.hitbox : null;

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

        if (slashVfx == null && playerAnimation != null)
        {
            slashVfx = playerAnimation.GetComponent<MeleeSlashVfx>();
            if (slashVfx == null) slashVfx = playerAnimation.gameObject.AddComponent<MeleeSlashVfx>();
        }

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
            EndStep("保險絲（Max Step Duration）");
        }

        // 兩段式保險絲：突進太久還沒到達（例如被擋住）→ 直接出刀
        if (_approaching && Time.time >= _approachDeadline)
        {
            if (logAbnormalEnds)
                Debug.Log($"[Melee] 突進逾時，直接出刀：'{GetActiveStateName()}'" +
                          (playerMovement != null && !playerMovement.IsMeleeDashing ? "（突進已被外部停掉，例如換裝硬直）" : "（被擋住或追不上）"), this);
            if (playerMovement != null) playerMovement.StopMeleeDash("ApproachTimeout");
            OnApproachArrived();
        }

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

        // 彈藥條：像子彈一樣，這一段一觸發就立刻扣掉一格 → 剩餘段數 / 總段數。
        // 換手時共用計數器已經走了很遠，會直接跳到對應的格數 —— 這個跳動正是在告訴玩家「你接的是第幾段」。
        // 終結技一定是最後一段，所以直接扣到 0（這一串結束了）。
        // 斷招 / 被 Dash 取消：已經扣掉的不退回，BeginCooldown 會把剩下的歸零再填回。
        w.meleeRuntime.barFill = (float)(stepCount - playedIndex - 1) / stepCount;

        // 上一段的 hitbox 可能還開著（連段接得很快時），先關掉
        CloseAllHitboxes();

        // 上一段的突進也要停 —— 新的一段有自己的 DashStart
        if (playerMovement != null)
            playerMovement.StopMeleeDash("NextComboStep");

        float speed = Mathf.Max(0.01f, w.melee.meleeSpeed);
        _approaching = false;

        if (IsTwoStage(step))
        {
            BeginApproach(w, step, isLeft);
        }
        else
        {
            SetStepDeadline(step);
            PlayAnimation(step, isLeft, speed);
        }

        if (logStateChanges)
        {
            Debug.Log($"[Melee] combo={comboIndex} played={playedIndex}/{stepCount - 1} " +
                      $"hand={(isLeft ? "L" : "R")} {(_activeIsFinisher ? "FINISHER" : "")} " +
                      $"state={step.GetStateName(isLeft)}", this);
        }
    }

    // ────────────────────────────────────────────────
    //  兩段式：突進 → 到達 → 出刀
    // ────────────────────────────────────────────────

    private static bool IsTwoStage(MeleeAttackStep step) =>
        step != null && step.stepMode == MeleeStepMode.TwoStage && step.dashMode != MeleeDashMode.None;

    private void SetStepDeadline(MeleeAttackStep step)
    {
        float speed = (_activeWeapon != null) ? Mathf.Max(0.01f, _activeWeapon.melee.meleeSpeed) : 1f;
        _stepDeadline = (step.maxStepDuration > 0f) ? Time.time + (step.maxStepDuration / speed) : 0f;
    }

    private float GetMaxDashDistance(Weapon w, MeleeAttackStep step)
    {
        float baseDist = (w != null && w.melee.dashDistance > 0f) ? w.melee.dashDistance : defaultDashDistance;
        return baseDist * Mathf.Max(0f, step.dashDistanceMultiplier);
    }

    /// <summary>
    /// 開始突進階段。已在攻擊範圍內 → 直接出刀；否則播突進動畫，由 PlayerMovement 衝到
    /// 目標（停止距離）或用完最大突進距離，再回呼 OnApproachArrived 出刀。
    /// 沒有目標時沿面向衝滿最大突進距離。
    /// </summary>
    private void BeginApproach(Weapon w, MeleeAttackStep step, bool isLeft)
    {
        Rigidbody target = (step.dashMode == MeleeDashMode.ToTarget) ? ResolveMeleeTarget() : null;

        if (playerMovement == null ||
            (target != null && playerMovement.IsWithinMeleeStopDistance(target, step.dashStopDistance)))
        {
            PlayStrike(step, isLeft, fromApproach: false);
            return;
        }

        float dashSpeed = ResolveDashSpeed();
        float maxDist = GetMaxDashDistance(w, step);
        if (dashSpeed <= 0f || maxDist <= 0f)
        {
            PlayStrike(step, isLeft, fromApproach: false);
            return;
        }

        Vector3 dir = (target != null)
            ? (target.position - playerMovement.transform.position)
            : GetFacingDirection();

        _approaching = true;
        _stepDeadline = 0f;   // 保險絲從出刀才開始算
        _activeStateHash = 0; // 上一段攻擊動畫的事件一律不接受
        _approachDeadline = Time.time + maxDist / dashSpeed + approachTimeoutPadding;

        PlayApproachAnimation(step, isLeft);

        if (logStateChanges)
            Debug.Log($"[Melee] approach start: target={(target != null ? target.name : "none")} " +
                      $"maxDist={maxDist:F1} speed={dashSpeed:F1}", this);

        // 到達時 PlayerMovement 會呼叫 OnApproachArrived（可能就在這一行裡面，例如方向無效時）
        playerMovement.BeginMeleeDash(dir, dashSpeed, target, step.dashStopDistance, maxDist, OnApproachArrived);
    }

    /// <summary>到達 / 距離用完 / 目標消失 / 保險絲：結束突進階段，出刀。</summary>
    private void OnApproachArrived()
    {
        if (!_approaching || !_attacking || _activeStep == null) return;
        _approaching = false;

        if (logStateChanges) Debug.Log("[Melee] approach arrived → strike", this);

        PlayStrike(_activeStep, _activeIsLeft, fromApproach: true);
    }

    private void PlayStrike(MeleeAttackStep step, bool isLeft, bool fromApproach)
    {
        SetStepDeadline(step);
        float speed = (_activeWeapon != null) ? Mathf.Max(0.01f, _activeWeapon.melee.meleeSpeed) : 1f;
        PlayAnimation(step, isLeft, speed, fromApproach ? step.strikeCrossFadeTime : step.crossFadeTime);
    }

    private void PlayApproachAnimation(MeleeAttackStep step, bool isLeft)
    {
        if (playerAnimation == null || playerAnimation.anim == null || _meleeLayerIndex < 0) return;

        string stateName = step.GetApproachStateName(isLeft);
        if (string.IsNullOrWhiteSpace(stateName))
        {
            Debug.LogWarning($"[MeleeAttackController] TwoStage 的段沒有設定 Approach State Name，突進時不會換動畫。", this);
            return;
        }

        _approachStateHash = Animator.StringToHash(stateName);
        playerAnimation.SetToAttackLayer();
        playerAnimation.anim.CrossFade(stateName, step.approachCrossFadeTime, _meleeLayerIndex, 0f);
    }

    /// <summary>
    /// 這一段的突進速度。能量在「這一串連段第一個真正突進的段」結算一次：
    ///   夠 → 扣能量，使用 dashSpeed；不夠 → 不扣，退化成 sprintSpeed。
    /// 之後同一串的突進段沿用第一次的結果。
    /// </summary>
    private float ResolveDashSpeed()
    {
        var ps = PlayerStats.Instance;
        if (ps == null) return 0f;

        if (!_dashEnergyResolved)
        {
            _dashEmpowered = (ps.currentEnergy >= ps.dashEnergyCost);
            if (_dashEmpowered)
                ps.currentEnergy -= ps.dashEnergyCost;

            _dashEnergyResolved = true;

            if (logStateChanges)
                Debug.Log($"[Melee] dash energy resolved: {(_dashEmpowered ? "EMPOWERED" : "WEAKENED")}", this);
        }

        return _dashEmpowered ? ps.dashSpeed : ps.sprintSpeed;
    }

    private void PlayAnimation(MeleeAttackStep step, bool isLeft, float speed, float crossFade = -1f)
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
        playerAnimation.anim.CrossFade(stateName, crossFade >= 0f ? crossFade : step.crossFadeTime, _meleeLayerIndex, 0f);
    }

    // ────────────────────────────────────────────────
    //  Animation Event 回呼（由 PlayerAnimation 轉發）
    // ────────────────────────────────────────────────

    /// <summary>刀刃開始有傷害。</summary>
    public void OnHitboxOn()
    {
        if (!_attacking || _activeWeapon == null || _activeStep == null) return;

        // 劍風：這一段有設定就用這一段的，否則用武器的（MeleeWeapon.swordSlash）
        if (slashVfx != null)
        {
            GameObject slashPrefab = _activeStep.slashVfxOverride != null ? _activeStep.slashVfxOverride : _activeWeapon.melee.slashVfx;
            slashVfx.Play(slashPrefab, _activeStep, _activeIsLeft);
        }

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
                _activeWeapon.damage.coldDamage * mul,
                _activeWeapon.damage.impact * mul),   // 衝擊值：跟傷害同倍率（meleeOutput × 該段倍率）
            // 暴擊：以武器本體為基礎 + 零件 / 手甲 / 頭盔的加成（由 AttackManager 算好）
            criticalChance = _activeWeapon.damage.criticalChance,
            criticalMultiplier = _activeWeapon.damage.criticalMultiplier,
            knockback = _activeStep.knockback,
            hittableLayers = hittableLayers,
            // Arc 判定：扇形的原點與面向跟著角色模型，形狀來自這一段
            facing = (playerAnimation != null) ? playerAnimation.transform : null,
            arcRange = _activeStep.arcRange,
            arcAngle = _activeStep.arcAngle,
            arcPitchAngle = _activeStep.arcPitchAngle,
        });

        // 訂閱命中事件。先退訂再訂閱，避免同一個 hitbox 被重複掛上
        // （連段每一段都會走這裡，武器 instance 是同一個）。
        hitbox.OnHitTarget -= HandleMeleeHit;
        hitbox.OnHitTarget += HandleMeleeHit;

        hitbox.Open();

        // 方案 B：命中判定開始時錨定到目標，繼承其部分速度。
        // 高速交戰下，不錨定的話 dashStopDistance 只夠撐不到一個物理幀，
        // 打中了也會立刻滑開、接不上下一段。
        if (playerMovement != null)
        {
            var targetRb = ResolveMeleeTarget();
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

        // 兩段式的突進由 BeginApproach 負責；攻擊動畫上殘留的 DashStart 不要再衝一次
        if (IsTwoStage(_activeStep)) return;

        // 突進由動畫時間窗（DashStart → Brake）控制，另外再用最大突進距離（MeleeDashDistance × 倍率）當硬上限：
        // 先到哪個就停哪個。有能量時用 dashSpeed；能量不足時用 sprintSpeed。
        float speed = ResolveDashSpeed();
        if (speed <= 0f) return;

        // ToTarget：目標見 ResolveMeleeTarget（鎖定圈內的目標，或 AutoAim 中的目標）。
        // 傳 Rigidbody 而不是 Transform —— PlayerMovement 需要目標速度來算攔截點。
        Rigidbody target = (_activeStep.dashMode == MeleeDashMode.ToTarget) ? ResolveMeleeTarget() : null;

        // 沒有目標（或 Forward 模式）→ 沿角色當前面向
        Vector3 dir = (target != null)
            ? (target.position - playerMovement.transform.position)
            : GetFacingDirection();

        float maxDist = GetMaxDashDistance(_activeWeapon, _activeStep);
        playerMovement.BeginMeleeDash(dir, speed, target, _activeStep.dashStopDistance, maxDist);
    }

    /// <summary>
    /// 近戰的目標：
    ///   1. 螢幕鎖定圈裡的目標（PlayerAiming.GetTargetRigidbody）
    ///   2. 圈裡沒有時，AutoAim（中鍵鎖定）中的目標 —— 它跑出鎖定圈（例如突進穿過它、跑到身後）也照樣追
    ///   3. 都沒有 → null（沿面向突進）
    /// 沒有 AutoAim 時行為跟以前一樣：只追圈內的目標。
    /// </summary>
    private static Rigidbody ResolveMeleeTarget()
    {
        PlayerAiming aim = PlayerAiming.Instance;
        if (aim == null) return null;

        Rigidbody rb = aim.GetTargetRigidbody();
        if (rb != null) return rb;

        if (aim.TryGetAutoAimTarget(out _, out _, out Rigidbody autoRb) && autoRb != null)
            return autoRb;

        return null;
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
    public void CancelByDash(string source = "Dash")
    {
        if (!_attacking) return;
        EndStep($"被 {source} 取消");
    }

    /// <summary>
    /// 這個 Animation Event 是不是「目前這一段」的 State 發出的（PlayerAnimation 轉發前呼叫）。
    /// 不在攻擊中時一律放行（各回呼自己有守衛）。
    /// </summary>
    public bool AcceptsAnimEvent(AnimationEvent e, string eventName)
    {
        if (!_attacking || e == null) return true;

        AnimatorStateInfo st = e.animatorStateInfo;
        bool match = Matches(st, _activeStateHash) || (_approaching && Matches(st, _approachStateHash));

        if (!match && logStateChanges)
            Debug.Log($"[Melee] 忽略上一段殘留的事件 {eventName}（目前：'{GetActiveStateName()}'{(_approaching ? "，突進中" : "")}）", this);
        return match;
    }

    // State 名稱可能填短名或完整路徑，兩種都比對
    private static bool Matches(AnimatorStateInfo st, int hash) =>
        hash != 0 && (st.shortNameHash == hash || st.fullPathHash == hash);

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
        PlayerEffectManager fx = Effects;
        if (fx == null) return;

        Vector3 pos = feedbackAtHitPoint ? result.point : fx.transform.position;
        fx.PlayMeleeHitFeedback(pos);
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
    public void OnStepEnd() => EndStep(null);

    /// <summary>結束這一段。reason = null 表示正常結束（AnimEvent_MeleeStepEnd）。</summary>
    private void EndStep(string reason)
    {
        if (!_attacking) return;

        if (reason != null && slashVfx != null) slashVfx.CancelPending();   // 被取消時，還沒播的延遲劍風不要再冒出來

        if (reason != null && logAbnormalEnds)
            Debug.Log($"[Melee] 攻擊中途結束：{reason}（'{GetActiveStateName()}'{(_approaching ? "，突進中" : "")}）", this);
        else if (reason == null && logStateChanges)
            Debug.Log($"[Melee] step end（'{GetActiveStateName()}'）", this);

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
        _approaching = false;
        _activeStateHash = 0;
        _approachStateHash = 0;
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