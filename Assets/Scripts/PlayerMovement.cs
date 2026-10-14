using UnityEngine;
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.Animations.Rigging;
public class PlayerMovement : MonoBehaviour
{
    [Header("Character orientation")]
    //[SerializeField] private Transform cameraTransform;
    [SerializeField] private Transform characterOrientation;
    [SerializeField] private Transform characterModel;
    [Header("Rigidbody")]
    [SerializeField] private Rigidbody playerRigidbody;
    [Header("Movement Settings")]
    [SerializeField] private bool grounded;
    [SerializeField] private bool readyToJump;
    [SerializeField] private float jumpCooldown = 0.2f;
    [SerializeField] private bool canRegenerateEnergy;
    [SerializeField] private Vector3 moveDirection = new Vector3(0, 0, 0);
    [SerializeField] private float groundCheckDistance = 0.1f;
    [SerializeField] private Transform groundPoint;
    [SerializeField] private LayerMask whatIsGround;
    [SerializeField] private float flyInputBuffer = 0.12f; // 飛行請求緩衝（避免 Update/FixedUpdate 不同步漏掉）
    private float flyRequestUntil = 0f;
    [Header("Dash")]
    [SerializeField] private float dashDuration = 0.1f;     // 主人指定：只持續 0.1s
    [SerializeField] private float dashInputBuffer = 0.12f; // 跟 fly 一樣避免漏吃
    [SerializeField] private float dashCooldown = 0.25f;    // 可自行調
    private float dashRequestUntil = 0f;
    private float dashActiveUntil = 0f;
    private float nextDashTime = 0f;
    private Vector3 dashDir = Vector3.forward;
    private float dashAccel = 0f;        // 固定加速度（m/s^2）
    private float dashTargetSpeed = 0f;  // 目標 dash 速度
    [Tooltip("Dash 期間禁止攻擊/射擊的鎖定秒數。填你 dash 動畫的實際長度；想跟位移視窗一致就填和 dashDuration 一樣。")]
    [SerializeField] private float dashAttackLockDuration = 0.35f;
    private float dashAttackLockUntil = 0f;

    // ───── 突擊推進（Assault Boost）─────
    //
    // 按一下開啟、再按一下關閉。啟動時相對平台速度歸零，之後：
    //   方向：沿著鏡頭正前方直線飛，速度 dashSpeed、加速度 accelerationSpeed（水平、垂直相同，軌跡才會是直線）。
    //         水平 = dashSpeed × cos(俯仰)，垂直 = dashSpeed × sin(俯仰)；俯仰先扣掉 assaultBoostPitchDeadZone。
    //         垂直速度沒有上限，只受鏡頭能轉到的角度限制。
    //         按住左 / 右：以 sprintSpeed × assaultBoostSideSpeedRatio 往側邊移動（類比輸入依推的程度），
    //         放開就收回；側向速度一律以 accelerationSpeed 增減。不另外扣能量、不碰 Dash 冷卻。
    //         前 / 後鍵在推進中沒有作用。
    //   飛行鍵：按住時至少以 flySpeed 往上升（鏡頭已經往上看、爬得比 flySpeed 快時不會被拖慢）。
    //   沒有裝推進器（flySpeed = 0）時只能水平衝刺。期間關閉重力。
    //   能量：啟動扣一次完整的 dashEnergyCost；之後推進期間每秒扣 flyEnergyCost
    //         × assaultBoostEnergyCostMultiplier（預設減半）。
    //         期間不回復。
    //   攔截（只在 AutoAim / 中鍵鎖定時）：推進方向改朝「以推進速度前進、會跟目標相遇的點」，
    //         用的是近戰突進同一套攔截解算與防呆（解不出來就直接朝目標）。不受俯仰死區影響（高度直接對準）。
    //         推進方向每秒最多轉 AimTurnRate 度（自由飛行時也一樣，所以跟原本手感相同）。左右側移照常保留。
    // 結束（再按一次 / 能量耗盡 / 按 Dash / 按近戰）時保留當下速度，交回一般移動慢慢減速。
    [Header("Assault Boost")]
    [Tooltip("鏡頭俯仰在這個角度（度）以內視為水平，不爬升也不俯衝。\n" +
             "瞄準時鏡頭難免有一點上下，沒有死區的話會一直慢慢飄高或沉下去。")]
    [SerializeField, Range(0f, 45f)] private float assaultBoostPitchDeadZone = 10f;

    [Tooltip("推進期間的能量消耗倍率（啟動那一下不打折）。\n" +
             "套用在每秒的 flyEnergyCost。0.5 = 減半。")]
    [SerializeField, Range(0f, 1f)] private float assaultBoostEnergyCostMultiplier = 0.5f;

    [Tooltip("推進中按住左 / 右時的側移速度 = sprintSpeed × 這個比例。\n" +
             "側向速度以 accelerationSpeed 增減，放開就收回 0。")]
    [SerializeField, Range(0f, 2f)] private float assaultBoostSideSpeedRatio = 0.333f;

    [Tooltip("Animator 上的 bool 參數名稱。Animator 沒有這個參數時會自動略過，不會報錯。")]
    [SerializeField] private string assaultBoostAnimBool = "assaultBoost";

    private bool _assaultBoosting;
    private bool _abPrevGravity = true;
    private Vector2 _moveInput;                       // 原始移動輸入（x = 左右，y = 前後）
    private float _abFlyRequestUntil;                 // 推進中按住飛行鍵（同 flyRequestUntil 的緩衝做法）
    private Vector3 _abFacing = Vector3.forward;      // 突擊推進中角色面向（水平）
    private Vector3 _abAnimDir = Vector3.forward;     // 突擊推進中給移動動畫用的方向（水平）
    private Vector3 _abSteerDir = Vector3.forward;    // 實際推進方向（3D）。每步以 AimTurnRate 轉向「鏡頭方向」或「攔截方向」
    private int _abAnimHash;
    private bool? _abAnimHasParam;

    /// <summary>是否正在突擊推進。</summary>
    public bool IsAssaultBoosting => _assaultBoosting;

    // ───── 突擊推進：撞飛小型敵人 ─────
    //
    // 推進中，前方（沿著這一步的移動方向）碰到 entitySize = Small 的敵人就把它撞飛：
    //   方向 = 推進方向，再往「偏離推進路線的那一側」帶一點，看起來像被撞開而不是被推著走；
    //   速度 = 玩家相對速度 × assaultBoostRamSpeedMultiplier，另外往上 assaultBoostRamUpwardSpeed。
    // 中型、大型敵人不受影響（照常發生物理碰撞）。
    //
    // 撞擊傷害（物理）= 係數 × 玩家總重量（kg）× 相對速度（m/s）—— 用動量，不用動能：
    //   動能裡速度是平方，會讓「輕、快」壓過「重」。用動量的話重量和速度一樣重要，
    //   而且剛好跟重量曲線互相拉扯：越重越痛，但重量曲線也讓推進變慢，
    //   所以撞擊最痛的配裝落在中量到臨界（懸崖邊），不是越重越好。
    // 相對速度 = 兩者沿「玩家 → 敵人」方向互相接近的速度：迎面衝來的撞得更痛，逃跑的撞得輕。
    // 玩家不減速、不受反作用傷害。只有突擊推進會撞。
    [Header("Assault Boost Ram")]
    [Tooltip("撞飛判定的層。留空（Nothing）= Enemy。")]
    [SerializeField] private LayerMask assaultBoostRamLayers;

    [Tooltip("撞飛判定的半徑（公尺），從玩家重心沿移動方向往前掃。")]
    [SerializeField, Min(0.1f)] private float assaultBoostRamRadius = 1.5f;

    [Tooltip("被撞飛的水平速度 = 玩家相對速度 × 這個倍率。大於 1 才會比玩家快、被撞到前面去，不會黏在身上。")]
    [SerializeField, Min(0f)] private float assaultBoostRamSpeedMultiplier = 1.2f;

    [Tooltip("被撞飛時往上的速度（公尺/秒）。")]
    [SerializeField, Min(0f)] private float assaultBoostRamUpwardSpeed = 8f;

    [Tooltip("往路線側邊偏的比例。0 = 沿推進方向直直撞出去；越大越往兩側彈開。")]
    [SerializeField, Range(0f, 2f)] private float assaultBoostRamSideBias = 0.6f;

    [Tooltip("被撞飛後，敵人的 AI 移動暫停幾秒（期間完全交給物理）。")]
    [SerializeField, Min(0f)] private float assaultBoostRamKnockbackDuration = 1f;

    [Tooltip("撞擊傷害 = 這個係數 × 玩家總重量（kg）× 相對速度（m/s）。物理傷害，會被敵人的物理防禦減免。\n" +
             "0.005：Crimson Rose 輕型基準約 136、Emerald Storm 中型基準約 308。")]
    [SerializeField, Min(0f)] private float assaultBoostRamDamagePerMomentum = 0.005f;

    [Tooltip("相對速度低於這個值（m/s）只撞飛、不造成傷害。避免推進剛啟動還在加速時也撞出傷害。")]
    [SerializeField, Min(0f)] private float assaultBoostRamMinDamageSpeed = 15f;

    [Tooltip("相對速度上限 = 玩家這一步的推進速度 × 這個倍率。避免迎面撞上高速飛行的敵人時傷害爆表。")]
    [SerializeField, Min(1f)] private float assaultBoostRamMaxRelativeSpeedRatio = 1.5f;

    [Tooltip("同一隻敵人被撞擊傷害後，幾秒內不會再吃撞擊傷害（仍然會被撞飛）。\n" +
             "有移動腳本的敵人在擊退期間本來就不會再被撞；這是給沒有移動腳本、會在膠囊裡停留好幾幀的小型實體用的。")]
    [SerializeField, Min(0f)] private float assaultBoostRamDamageCooldown = 0.5f;

    // ───── 突擊推進踢（Assault Boost Kick）─────
    //
    // 推進中再按一次突擊推進鍵：不是直接結束，而是以一記踢擊收尾。
    //   · 踢擊期間不再推進（不扣能量、維持當下速度、重力仍關閉），面向維持推進方向。
    //   · Animator：踢擊開始時 assaultBoost bool 就關掉、同時觸發 assaultBoostKick Trigger；
    //     踢擊狀態用 Exit Time 轉出去（不要再用 assaultBoost bool 當條件）。
    //   · 時間點由動畫事件決定（跟一般近戰一樣）：
    //       AnimEvent_AssaultKickHitOn  → 開始判定
    //       AnimEvent_AssaultKickHitOff → 結束判定
    //       AnimEvent_AssaultKickEnd    → 踢擊結束，突擊推進正式結束（保留當下速度、恢復重力）
    //   · 判定：從重心沿移動方向往前掃一個膠囊（跟撞擊同一種做法，高速時不會漏判）。
    //     每隻敵人每次踢擊只吃一次傷害；大小不限（撞擊只撞小型）。
    //   · 傷害 = 撞擊傷害 × assaultKickDamageMultiplier
    //          = 撞擊係數 × 玩家總重量 × 相對速度 × 3。物理傷害、近戰類型（DamageKind.Melee）。
    //     踢擊沒有撞擊的最低速度門檻。
    //   · 小型敵人會照撞擊的參數被踢飛；中型、大型只吃傷害。
    //   · Dash、近戰、能量歸零、打開介面一樣會中斷（直接結束推進）。
    [Header("Assault Boost Kick")]
    [Tooltip("踢擊傷害 = 撞擊傷害（係數 × 總重量 × 相對速度）× 這個倍率。")]
    [SerializeField, Min(0f)] private float assaultKickDamageMultiplier = 3f;

    [Tooltip("踢擊判定的半徑（公尺）。")]
    [SerializeField, Min(0.1f)] private float assaultKickRadius = 1.8f;

    [Tooltip("踢擊判定從重心往前延伸多遠（公尺），另外再加上這一步移動的距離。")]
    [SerializeField, Min(0f)] private float assaultKickReach = 2f;

    [Tooltip("保險：超過這個秒數還沒收到 AnimEvent_AssaultKickEnd 就強制結束（事件漏掉時用）。")]
    [SerializeField, Min(0.1f)] private float assaultKickMaxDuration = 1.5f;

    [Tooltip("Animator 上的 Trigger 參數名稱。沒有這個參數時略過動畫，只靠 Max Duration 結束。")]
    [SerializeField] private string assaultKickAnimTrigger = "assaultBoostKick";

    private bool _abKicking;
    private bool _abKickHitOpen;
    private float _abKickEndTime;
    private readonly HashSet<ModularEntityStats> _abKickHit = new HashSet<ModularEntityStats>();
    private int _abKickAnimHash;
    private bool? _abKickAnimHasParam;

    /// <summary>是否正在突擊推進踢（此時 IsAssaultBoosting 也是 true）。</summary>
    public bool IsAssaultKicking => _abKicking;

    // 每隻敵人最後一次吃撞擊傷害的時間
    private readonly Dictionary<ModularEntityStats, float> _ramDamageTimes = new Dictionary<ModularEntityStats, float>();
    private static readonly List<ModularEntityStats> s_RamPrune = new List<ModularEntityStats>();

    private static readonly Collider[] s_RamHits = new Collider[32];
    private static readonly HashSet<ModularEntityStats> s_RamSeen = new HashSet<ModularEntityStats>();

    [Header("Attack Facing")]
    [SerializeField] private float attackRotateSpeed = 25;         // 轉身速度
    [SerializeField] private float attackAngleThreshold = 6f;       // 允許開火的角度誤差
    [SerializeField] private float attackAimRayDistance = 100f;     // 非 lockOn 時，用準星 ray 取點距離
    [SerializeField] private float singleShotBufferTime = 0.25f;    // 單發按下後，最多等待多久才消耗掉這次射擊
    [SerializeField] private float maxAlignTime = 0.25f;            // 最多對準多久就強制放行（避免卡死）

    [Tooltip("近戰按鍵的緩衝時間。按在連段窗口打開之前、或手還在冷卻時，\n" +
             "這次輸入會被保留這麼久，一旦條件成立就立刻揮出。\n" +
             "跟 singleShotBufferTime 分開是因為兩者要對齊的東西不同 ——\n" +
             "槍對齊的是轉身時間，刀對齊的是連段窗口。0.2~0.3 通常最舒服。")]
    [SerializeField] private float meleeInputBufferTime = 0.25f;
    private Weapon attackFacingOwner; // 目前誰在主導 attackFacingActive
    private bool attackFacingActive;
    private Vector3 attackDesiredForward; // XZ 平面朝向

    // key = Weapon instance（AttackManager.leftWeapon/rightWeapon）
    // value = 這次單發請求的失效時間 / 開始對準時間
    private readonly Dictionary<Weapon, float> pendingSingleUntil = new();
    private readonly Dictionary<Weapon, float> alignStartTime = new();

    // 近戰緩衝是「單一插槽」，不是每把武器一格。
    // 因為 comboIndex 是整串連段共用的計數器 —— 左右手同時掛著緩衝的話，
    // 窗口一開會有兩隻手搶同一格，落敗的那一隻還會留著去偷下一個窗口，
    // 玩家會看到一刀他沒按過的揮擊。最新的按鍵直接覆蓋前一個。
    private Weapon meleeBufferedWeapon;
    [Header("Animation")]
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
    [SerializeField] private float movementBlendSpeed = 3f; // 控制 0↔1 的快慢
    private float animX = 0f; // 水平（左右）動畫輸入
    private float animY = 0f; // 垂直（前後）動畫輸入
    [Header("Aim Facing Hold (Anti Jitter)")]
    [SerializeField] private float aimHoldAfterLockLost = 2f;
    [SerializeField] private float aimHoldAfterShootNoLock = 2f;
    private bool _prevLockOn;
    private float _aimHoldUntil;
    private Vector3 _lastAimHoldForward = Vector3.forward;

    // Multi-Aim 同時負責「抵消跑步時髖部扭動」：上半身圖層的遮罩包含脊椎但不含髖部，
    // 權重一歸 0 上半身就會跟著髖部甩。所以權重保持不變，改為切換「看哪裡」：
    // 瞄準 / 向前移動 → 看準星；未瞄準且向左、右、後移動 → 看角色自己的正前方。
    [Header("Aim Rig (Multi-Aim)")]
    [SerializeField] private MultiAimConstraint multiAimBody;
    [SerializeField] private MultiAimConstraint multiAimHead;
    [SerializeField, Range(0f, 1f)] private float aimBodyWeight = 1f;
    [SerializeField, Range(0f, 1f)] private float aimHeadWeight = 1f;
    [Tooltip("兩個 Multi-Aim 的 Source Object。由程式每幀移動，在「準星」與「角色正前方」之間切換。")]
    [SerializeField] private Transform aimRigTarget;
    [Tooltip("代理目標放在離胸口多遠的地方（公尺）。")]
    [SerializeField] private float aimRigTargetDistance = 20f;
    [Tooltip("準星 ↔ 正前方的切換速度（每秒）。0 = 瞬間切換。4 約等於 0.25 秒。")]
    [SerializeField] private float aimRigBlendSpeed = 4f;
    [Tooltip("未瞄準時，移動方向與攝影機朝向的夾角在此角度內視為「向前」，繼續看準星；超過（左、右、後退）則改看角色正前方。")]
    [SerializeField, Range(0f, 180f)] private float aimRigForwardAngle = 60f;
    private float _aimRigAimBlend = 1f; // 1 = 看準星，0 = 看正前方

    [Header("Dust Effect")]
    [SerializeField] private float dustIntervalSlow = 0.4f;   // 慢速：間隔長 = 稀
    [SerializeField] private float dustIntervalFast = 0.08f;  // 全速：間隔短 = 密
    [SerializeField] private float dustMinSpeed = 1f;         // 低於此速度不噴
    [SerializeField] private float dustMaxSpeed = 16f;        // 達此速度 = 最密(60km/h≈16.7)
    private float _nextDustTime = 0f;

    public AttackManager attackManager;
    public void Update()
    {
        EnergyRegenerationCheck();
        RotateCharacter();
        HandleDustEffect();
        if (!grounded)
        {
            if (Effects != null) Effects.StopWalkFeedback();
        }
        UIManager.Instance.speedText.text = playerRigidbody.linearVelocity.magnitude < 0.0001f ? 0.ToString("F2") : playerRigidbody.linearVelocity.magnitude.ToString("F2");
    }
    public void FixedUpdate()
    {
        GroundCheck();          // 同一條射線同時更新 grounded 和 ShipPassenger
        SyncMobilePlatform();   // 讀剛更新的船上狀態
        CarryWithPlatformRotation(Time.fixedDeltaTime);
        if (_assaultBoosting)
        {
            // 突擊推進接管水平與垂直速度；一般移動與飛行暫停。踢擊中改跑踢擊。
            if (_abKicking) ApplyAssaultKickFixed(Time.fixedDeltaTime);
            else ApplyAssaultBoostFixed(Time.fixedDeltaTime);
        }
        else
        {
            ApplyHorizontalMovementFixed(Time.fixedDeltaTime);
            ApplyFlyFixed(Time.fixedDeltaTime); // 新增
        }
        ApplyDashFixed(Time.fixedDeltaTime); // 新增
        ApplyMeleeDashFixed(Time.fixedDeltaTime);
        ApplyMeleeAnchorFixed(Time.fixedDeltaTime);
        ApplyMeleeHoverFixed(Time.fixedDeltaTime);
        ApplyMeleeBrakeFixed(Time.fixedDeltaTime);

    }
    private void ApplyHorizontalMovementFixed(float dt)
    {

        Vector3 platformVel = GetMobilePlatformVelocity();

        // 用「相對平台」速度來做加速/減速
        Vector3 vWorld = playerRigidbody.linearVelocity;
        Vector3 vRel = vWorld - platformVel;

        // 滯空中連垂直速度一起煞掉 —— 沒有重力可以自然衰減它，
        // 殘留的 Y 會讓角色在空中緩慢飄走。
        Vector3 horizontalRel = _meleeHovering
            ? vRel
            : new Vector3(vRel.x, 0f, vRel.z);

        if (moveDirection.sqrMagnitude < 0.0001f)
        {
            horizontalRel = Vector3.MoveTowards(horizontalRel, Vector3.zero, PlayerStats.Instance.decelerationSpeed * dt);
            Vector3 outRel = new Vector3(horizontalRel.x, vRel.y, horizontalRel.z);
            playerRigidbody.linearVelocity = outRel + platformVel;   // ✅ 設定一次，不累加
            return;
        }

        Vector3 targetHorizontalRel = moveDirection * PlayerStats.Instance.sprintSpeed;
        horizontalRel = Vector3.MoveTowards(horizontalRel, targetHorizontalRel, PlayerStats.Instance.accelerationSpeed * dt);

        Vector3 outRel2 = new Vector3(horizontalRel.x, vRel.y, horizontalRel.z);
        playerRigidbody.linearVelocity = outRel2 + platformVel;      // ✅ 設定一次，不累加
    }

    private void ApplyFlyFixed(float dt)
    {
        // 落地就清掉飛行請求
        if (grounded)
        {
            flyRequestUntil = 0f;
            return;
        }

        // 沒有飛行請求就不干預 Y 速度（讓重力自然下落）
        if (Time.time > flyRequestUntil) return;

        var stats = PlayerStats.Instance;
        if (stats == null) return;

        if (stats.currentEnergy <= 0f)
        {
            flyRequestUntil = 0f;
            return;
        }

        Vector3 v = playerRigidbody.linearVelocity;

        // 這裡把 flyForce 當成「目標上升速度（m/s）」來用
        float targetVy = stats.flySpeed;

        // 用 accelerationSpeed 當作「飛行加速度（m/s^2）」讓 vy 逼近 targetVy
        if (stats.flySpeed != 0)
        {
            float newVy = Mathf.MoveTowards(v.y, targetVy, stats.flyAcceleration * dt);

            playerRigidbody.linearVelocity = new Vector3(v.x, newVy, v.z);
        }
        // 能量消耗用 fixed dt（不飄幀率）
        stats.currentEnergy = Mathf.Max(0f, stats.currentEnergy - dt * stats.flyEnergyCost);

        if (stats.currentEnergy <= 0f)
            flyRequestUntil = 0f;
    }
    public void ProcessAttackFacingAndAttack(AttackManager attackManager, Weapon w, InputAction attackInput)
    {
        if (attackManager == null || w == null || attackInput == null) return;
        if (PlayerAiming.Instance == null || characterModel == null) return;
        // Dash 動畫播放期間：禁止任何攻擊/射擊（涵蓋左右手、肩武器、近戰）
        if (IsDashAttackBlocked) return;

        var stats = PlayerStats.Instance;
        bool isLeft = (attackManager != null && w == attackManager.leftHandWeapon);
        bool isRight = (attackManager != null && w == attackManager.rightHandWeapon);


        if (stats != null && (isLeft || isRight))
        {
            var hand = isLeft ? stats.leftHand : stats.rightHand;
        }



        // 近戰一律視為單發：一次按鍵 = 一段揮擊。
        // 不看 firingMode 是刻意的 —— 近戰武器的 range 欄位是空殼，
        // 萬一資料上被設成連發，這裡會變成「按住自動連砍」，緩衝也就失去意義。
        bool isMelee = (w.kind == HandWeaponKind.Melee);
        bool isSingle = isMelee || w.range.firingMode == 0;

        // 緩衝時間依武器種類分流
        float bufferTime = isMelee ? meleeInputBufferTime : singleShotBufferTime;

        if (isSingle)
        {
            if (attackInput.WasPressedThisFrame())
            {
                if (isMelee)
                {
                    // 單一插槽：新的近戰按鍵把另一隻手掛著的緩衝清掉
                    if (meleeBufferedWeapon != null && meleeBufferedWeapon != w)
                    {
                        pendingSingleUntil.Remove(meleeBufferedWeapon);
                        alignStartTime.Remove(meleeBufferedWeapon);
                        ClearAttackFacingOwnerIfSelf(meleeBufferedWeapon);
                    }
                    meleeBufferedWeapon = w;

                    // 突擊推進中按近戰：先結束推進，交給一般近戰（突擊近戰之後再做）。
                    // 一定要在近戰開始之前結束 —— 近戰滯空會記住「當下的重力設定」，
                    // 推進中重力是關的，記錯了結束後就再也不會恢復重力。
                    if (_assaultBoosting) EndAssaultBoost();
                }

                pendingSingleUntil[w] = Time.time + bufferTime;
                alignStartTime[w] = Time.time;
                // 肩武器的舉起 / 收回改由 PlayerAnimation 依「鎖定圈裡有沒有目標」自動控制，這裡不再處理
            }

            // 提早丟掉沒救的近戰緩衝（例如終結技播放中），
            // 免得它撐滿整個 bufferTime 之後才在冷卻結束時冒出一刀幽靈揮擊。
            bool hopeless = isMelee
                            && attackManager.Melee != null
                            && attackManager.Melee.IsInputHopeless(w);

            if (hopeless || !pendingSingleUntil.TryGetValue(w, out float until) || Time.time > until)
            {
                pendingSingleUntil.Remove(w);
                alignStartTime.Remove(w);
                ClearAttackFacingOwnerIfSelf(w);
                ClearMeleeBufferIfSelf(w);
                return;
            }
        }
        else
        {
            // 連發/蓄力：只要按住就持續嘗試
            if (!attackInput.IsPressed())
            {
                alignStartTime.Remove(w);
                ClearAttackFacingOwnerIfSelf(w);
                return;
            }

            if (!alignStartTime.ContainsKey(w))
                alignStartTime[w] = Time.time;
        }

        // 2) 算出這一幀「應該面向哪裡」（lockOn -> aimingPoint；否則 -> 準星 ray）
        Vector3 targetPoint;
        if (PlayerAiming.Instance.lockOn && PlayerAiming.Instance.aimingPoint != null)
            targetPoint = PlayerAiming.Instance.aimingPoint.position;
        else
            targetPoint = PlayerAiming.Instance.GetRay().GetPoint(attackAimRayDistance);

        Vector3 lookDir = targetPoint - characterModel.position;
        lookDir.y = 0f;
        if (lookDir.sqrMagnitude < 0.0001f) return;

        SetAttackFacingOwner(w, lookDir.normalized);

        // 3) 判斷是否已對準（或超時就放行）
        Vector3 flatForward = characterModel.forward;
        flatForward.y = 0f;
        if (flatForward.sqrMagnitude < 0.0001f) flatForward = attackDesiredForward;
        flatForward.Normalize();

        float angle = Vector3.Angle(flatForward, attackDesiredForward);
        bool aligned = angle <= attackAngleThreshold;

        float singleAlignDeadline = Mathf.Min(maxAlignTime, bufferTime * 0.5f);
        bool timedOut = alignStartTime.TryGetValue(w, out float startT)
                        && (Time.time - startT) >= (isSingle ? singleAlignDeadline : maxAlignTime);

        if (aligned || timedOut)
        {
            // 新增：未 lockOn 時，射擊後保留準星朝向一段時間（允許邊走邊朝準星）
            if ((PlayerAiming.Instance != null) && !PlayerAiming.Instance.lockOn)
            {
                // 記一份 fallback，避免極端情況 ray/dir 失效時抖動
                if (TryGetAimForwardFlat(out var aimFwd))
                    _lastAimHoldForward = aimFwd;

                StartAimHold(aimHoldAfterShootNoLock);
            }

            // 肩武器不用等舉起：RangeAttackController 會觸發 fireShoulder_*，
            // 等 Attack_Fire 開始播放才生成子彈（沒目標時武器會直接迅速轉向前方開火）

            // 4) 真正觸發射擊（由 AttackManager 管理 cooldown/bullets）
            bool didShoot = attackManager.TryStartShoot(w);
            // 單發：一旦成功開火就消耗掉這次請求
            if (didShoot && isSingle)
            {
                pendingSingleUntil.Remove(w);
                alignStartTime.Remove(w);
                ClearMeleeBufferIfSelf(w);
            }

        }
    }
    /// <summary>近戰緩衝插槽是這把武器時才清掉，避免誤清掉另一隻手剛按下的輸入。</summary>
    private void ClearMeleeBufferIfSelf(Weapon w)
    {
        if (meleeBufferedWeapon == w) meleeBufferedWeapon = null;
    }

    /// <summary>目前有沒有近戰輸入正在緩衝中。給 UI / 除錯用。</summary>
    public bool HasBufferedMeleeInput =>
        meleeBufferedWeapon != null
        && pendingSingleUntil.TryGetValue(meleeBufferedWeapon, out float t)
        && Time.time <= t;

    // ───── 外部朝向（測試用）─────
    // WeaponBalanceBot 需要角色面向靶子，但它不經過玩家輸入，
    // ProcessAttackFacingAndAttack 每幀都會把 attackFacing 清掉，所以另開一個優先的欄位。
    private Vector3? _externalFacing;

    /// <summary>強制角色面向 worldPoint（只看水平方向）。直到 ClearExternalFacing 為止。</summary>
    public void SetExternalFacing(Vector3 worldPoint)
    {
        if (characterModel == null) return;
        Vector3 dir = worldPoint - characterModel.position;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f) _externalFacing = dir.normalized;
    }

    public void ClearExternalFacing() => _externalFacing = null;

    private void SetAttackFacingOwner(Weapon w, Vector3 desiredForward)
    {
        attackFacingOwner = w;
        attackFacingActive = true;
        attackDesiredForward = desiredForward;
    }
    private void ClearAttackFacingOwnerIfSelf(Weapon w)
    {
        if (attackFacingOwner == w)
        {
            attackFacingOwner = null;
            attackFacingActive = false;
        }
    }
    public void HorizontalMovement(float moveX, float moveZ)
    {
        // 突擊推進中的側移由 ApplyAssaultBoostFixed 讀 _moveInput.x 處理
        _moveInput = new Vector2(moveX, moveZ);

        // 只更新 moveDirection（給 RotateCharacter / 動畫用），不要在 Update 內碰剛體
        Vector3 forward = characterOrientation.forward;
        forward.y = 0f;

        Vector3 right = characterOrientation.right;
        right.y = 0f;

        // 避免極端情況（方向向量太小）造成 NaN
        if (forward.sqrMagnitude > 0.0001f) forward.Normalize();
        if (right.sqrMagnitude > 0.0001f) right.Normalize();

        moveDirection = forward * moveZ + right * moveX;

        if (moveDirection.sqrMagnitude < 0.0001f)
            moveDirection = Vector3.zero;
        else
            moveDirection.Normalize();
    }
    private PlayerJumpDust _jumpDust;
    public bool JumpAction()
    {
        // 突擊推進中：飛行鍵 = 往上升（由 ApplyAssaultBoostFixed 處理）。不起跳 ——
        // 推進中重力是關的，起跳的衝量會讓角色一直往上飄。
        if (_assaultBoosting)
        {
            _abFlyRequestUntil = Time.time + flyInputBuffer;
            return false;
        }

        if (grounded && readyToJump)
        {
            readyToJump = false;
            playerRigidbody.linearVelocity = new Vector3(playerRigidbody.linearVelocity.x, 0, playerRigidbody.linearVelocity.z);
            playerRigidbody.AddForce(Vector3.up * convertJumpHeightToForce(PlayerStats.Instance.jumpHeight), ForceMode.Impulse);
            Invoke("ResetJump", jumpCooldown);

            // 起跳塵土（玩家身上有 PlayerJumpDust 才會噴）
            if (_jumpDust == null) _jumpDust = GetComponent<PlayerJumpDust>();
            if (_jumpDust != null) _jumpDust.PlayJump();
            return true;
        }
        else if (!grounded && readyToJump && PlayerStats.Instance.currentEnergy > 0)
        {
            FlyAction();
        }
        return false;
    }

    private void ApplyDashFixed(float dt)
    {
        if (Time.time > dashRequestUntil) return;

        if (Time.time > dashActiveUntil)
        {
            dashRequestUntil = 0f;
            return;
        }

        Vector3 platformVel = GetMobilePlatformVelocity();

        // 用「相對平台」速度來判斷是否達標
        Vector3 vWorld = playerRigidbody.linearVelocity;
        Vector3 vRel = vWorld - platformVel;

        // 滯空中連垂直速度一起煞掉 —— 沒有重力可以自然衰減它，
        // 殘留的 Y 會讓角色在空中緩慢飄走。
        Vector3 horizontalRel = _meleeHovering
            ? vRel
            : new Vector3(vRel.x, 0f, vRel.z);
        float vParallelRel = Vector3.Dot(horizontalRel, dashDir);

        if (vParallelRel >= dashTargetSpeed) return;

        // 推力仍然是世界空間，會同等增加 world 與 relative（因為平台速度是常數項）
        playerRigidbody.AddForce(dashDir * dashAccel, ForceMode.Acceleration);
    }

    public bool DashAction()
    {
        var stats = PlayerStats.Instance;
        if (stats == null) return false;

        // 近戰揮擊中，且這一段標記為不可取消 → 擋掉 Dash
        if (meleeController != null && meleeController.IsBlockingDash) return false;

        // 突擊推進中按 Dash：一定會打斷推進 —— 放在冷卻 / 能量檢查之前，
        // 所以就算這次 Dash 因冷卻或能量不足而放不出來，推進也照樣結束。
        // 放得出來的話，下面會直接接一般 Dash（緊急閃避）。
        if (_assaultBoosting) EndAssaultBoost();

        if (Time.time < nextDashTime) return false;
        if (stats.currentEnergy <= 0) return false;

        Vector3 dir;
        if (moveDirection.sqrMagnitude > 0.001f) dir = moveDirection;
        else dir = characterModel != null ? characterModel.forward : transform.forward;

        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) return false;
        dir.Normalize();

        Vector3 platformVel = GetMobilePlatformVelocity();

        // 用「相對平台」速度來計算起始差值
        Vector3 vWorld = playerRigidbody.linearVelocity;
        Vector3 vRel = vWorld - platformVel;

        // 滯空中連垂直速度一起煞掉 —— 沒有重力可以自然衰減它，
        // 殘留的 Y 會讓角色在空中緩慢飄走。
        Vector3 horizontalRel = _meleeHovering
            ? vRel
            : new Vector3(vRel.x, 0f, vRel.z);

        dashDir = dir;
        dashTargetSpeed = stats.dashSpeed;

        float vParallelRel = Vector3.Dot(horizontalRel, dashDir);
        float deltaVStart = Mathf.Max(0f, dashTargetSpeed - vParallelRel);

        float dur = Mathf.Max(0.02f, dashDuration);
        dashAccel = deltaVStart / dur;

        stats.currentEnergy -= stats.dashEnergyCost;
        // Dash 期間鎖住攻擊/射擊
        float dashAtkLock = (dashAttackLockDuration > 0f) ? dashAttackLockDuration : dashDuration;
        dashAttackLockUntil = Time.time + dashAtkLock;
        dashRequestUntil = Time.time + dashInputBuffer;
        dashActiveUntil = Time.time + dashDuration;
        nextDashTime = Time.time + dashCooldown;

        // ★ 順序很重要：先收掉近戰，再播 Dash 動畫。
        //   近戰層是 Override 權重 1，CancelByDash → SetOffAttackLayer 需要
        //   attackLayerBlendOutTime 才淡出。若先 setDashTrigger，這段期間
        //   Dash 動畫會被近戰動畫蓋住，看起來像「沒播放」。
        CancelMeleeBrake();
        StopMeleeDash("PlayerDashCancel");
        EndMeleeHover();
        EndMeleeAnchor();

        if (meleeController != null)
            meleeController.CancelByDash();

        playerAnimation.setDashTrigger();

        CancelInvoke("ResetEnergyRegenerate");
        canRegenerateEnergy = false;

        return true;
    }

    // ────────────────────────────────────────────────
    //  近戰突進
    //
    //  跟一般 Dash 的差別：
    //    - 不受 dashCooldown 限制（它是攻擊的一部分，不是逃生手段）
    //    - 不設 dashAttackLockUntil（不然連段會被自己鎖住）
    //    - 能量由 MeleeAttackController 結算（一串連段只付一次入場費）
    //
    //  跟一般 Dash 相同的地方：全部走「相對平台」的速度判定。
    //  在移動中的 Landship 上，共同的船速不應污染突進速度與攔截解算。
    //
    //  突進沒有距離上限，也沒有速度曲線：
    //  AnimEvent_MeleeDashStart 開始，AnimEvent_MeleeBrake 停止。
    // ────────────────────────────────────────────────

    [Header("Melee Dash")]
    [SerializeField] private MeleeAttackController meleeController;

    [Tooltip("突進的加速度。越大越快貼到目標速度，太小會軟綿綿。")]
    [SerializeField] private float meleeDashAccel = 400f;

    [Tooltip("突進開始時保留多少比例的既有速度（0~1）。\n\n" +
             "★ 這個歸零是必要的，不是手感選項。攔截解算假設你會從當前位置以\n" +
             "  dashSpeed 朝攔截點前進；若帶著既有速度進入，實際速度會是\n" +
             "  「原速度 + 突進加速」，方向與大小雙雙偏離解算前提。\n" +
             "  更糟的是 ApplyMeleeDashFixed 只在「未達目標速度」時加力 ——\n" +
             "  初速夠高時一次力都不會加，整段突進純靠殘餘動量滑行。\n\n" +
             "0.1 保留一點慣性，讓起手不會像撞到牆一樣硬停。")]
    [Range(0f, 1f)]
    [SerializeField] private float meleeDashEntrySpeedFactor = 0.1f;

    [Tooltip("攔截解算的速度比上限。目標速度 / 突進速度 超過這個值就不解，直接純追蹤。\n" +
             "接近 1 時二次方程趨於退化，會解出極遠或負的根 —— 那些解看起來合法\n" +
             "（方向也對），實際上是朝一個永遠追不到的幻影衝過去。")]
    [Range(0.3f, 0.99f)]
    [SerializeField] private float interceptMaxSpeedRatio = 0.85f;

    [Tooltip("攔截點距離的上限倍率，相對於「目標當前距離」。\n" +
             "解出的攔截點比目標遠這麼多倍就拒絕 —— 合理的提前量不會離譜到那種程度。")]
    [SerializeField] private float interceptMaxLeadMultiplier = 2.5f;

    [Tooltip("攔截解對應的預估抵達時間上限（秒）。超過就拒絕。\n" +
             "太遠的解在抵達前戰況早就變了，追它不如純追蹤。")]
    [SerializeField] private float interceptMaxSolveTime = 1.0f;

    [Tooltip("突進結束時在 Console 輸出診斷：預測 vs 實際、結束原因、最終偏差分解。")]
    [SerializeField] private bool logMeleeDashDiagnostics = false;

    [Tooltip("AnimEvent_MeleeBrake 的煞車時間（秒）。0.08~0.15 通常就很俐落。")]
    [SerializeField] private float meleeBrakeDuration = 0.12f;

    [Tooltip("煞車後保留多少比例的原速度（0~1）。\n\n" +
             "0 = 完全停住（很生硬，像急煞）。\n" +
             "0.25 = 卸掉大半衝勢但仍在滑行，比較像機體慣性。\n" +
             "方向不變，只縮短度 —— 收招後仍朝原方向緩緩前進。")]
    [Range(0f, 1f)]
    [SerializeField] private float meleeBrakeSpeedFactor = 0.25f;

    [Tooltip("突進期間的最大轉向速率（度/秒）。\n" +
             "高速戰鬥下一次性攔截幾乎必定落空，所以突進全程持續重算攔截點。\n" +
             "但轉向有上限 —— 角度太離譜就是追不上，保留「位置沒站好會 miss」的懲罰。\n" +
             "360 = 每秒一圈，相當靈活；120~180 較誠實。")]
    [SerializeField] private float meleeDashTurnRate = 270f;

    [Tooltip("命中後繼承目標速度的比例（0~1）。\n\n" +
             "0 = 完全不繼承，雙方高速時會立刻滑開（AC6 式，只重視第一下）。\n" +
             "1 = 完全貼住目標（DMC5 式，會失去駕駛機體的感覺）。\n" +
             "0.6~0.8 = 相對漂移大幅降低但仍存在，玩家還是要自己補位。\n\n" +
             "從 hitbox 開啟（命中判定開始）起算，維持到整串連段結束。")]
    [Range(0f, 1f)]
    [SerializeField] private float meleeAnchorFactor = 0.7f;

    [Header("Melee Pitch")]
    [Tooltip("近戰攻擊時，角色朝目標俯仰的最大角度。0 = 停用。\n" +
             "三維突進衝向空中目標時，角色若維持水平會很不協調。")]
    [Range(0f, 80f)]
    [SerializeField] private float meleeMaxPitch = 30f;

    [Tooltip("俯仰角的轉動速度。攻擊結束後也用這個速度轉回 0。")]
    [SerializeField] private float meleePitchSpeed = 8f;

    [Tooltip("近戰滯空期間抵銷多少比例的重力。\n" +
             "從突進開始，一直到這一段攻擊完全結束（StepEnd / 取消）為止。\n" +
             "1 = 完全無重力（打飛行敵人時軌跡筆直），0 = 照常掉落。")]
    [Range(0f, 1f)]
    [SerializeField] private float meleeDashGravityCancel = 1f;

    private bool _meleeDashing;
    private Vector3 _meleeDashDir;
    private float _meleeDashSpeed;        // 這次突進的目標速度
    private float _meleeDashStopDistance; // ToTarget 用；到達目標附近時可早於 Brake 停止
    private Rigidbody _meleeDashTarget;   // null = Forward 模式。用 Rigidbody 才讀得到速度做攔截預判

    // 滯空跟突進是「不同長度」的兩件事：Brake 可以先停掉突進，
    // 但滯空要持續到整段攻擊結束，否則刀還沒揮完人已經開始掉了。
    private bool _meleeHovering;
    private bool _meleeDashPrevGravity = true;

    private bool _meleeBraking;
    private float _meleeBrakeUntil;
    private Vector3 _meleeBrakeTargetVel;

    public bool IsMeleeDashing => _meleeDashing;
    public bool IsMeleeBraking => _meleeBraking;

    /// <summary>近戰滯空中（從突進開始到整段攻擊結束）。給後處理 / VFX 判斷用。</summary>
    public bool IsMeleeHovering => _meleeHovering;

    /// <summary>目前是否錨定在某個近戰目標上。</summary>
    public bool IsMeleeAnchored => _meleeAnchorTarget != null;

    /// <summary>
    /// 收招煞車。由 MeleeAttackController 在 AnimEvent_MeleeStepEnd 時呼叫。
    ///
    /// 只歸零「相對平台」的水平速度 —— 在移動的 Landship 上歸零世界速度
    /// 會讓角色被船直接甩到船尾。垂直速度不動，收招時該落地還是要落地。
    ///
    /// 注意：被 Dash 取消時不會走這裡。玩家按 Shift 是要逃走，煞車會擋住他。
    /// </summary>
    public void BeginMeleeBrake()
    {
        // Dash 進行中不接受煞車。控制器那邊已經有 _attacking 守衛擋掉
        // 「被取消的刀稍後才觸發 Brake」的情況，這裡是第二道防線 ——
        // 煞車把玩家的逃生 Dash 歸零是絕對不能發生的事。
        if (Time.time < dashActiveUntil) return;

        // 在煞車開始的當下擷取速度，目標設成它的一個比例。
        // 每幀重算的話，衰減中的速度會讓目標一路跟著往下掉，最後還是歸零。
        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = (playerRigidbody != null)
            ? playerRigidbody.linearVelocity - platformVel
            : Vector3.zero;

        _meleeBrakeTargetVel = vRel * Mathf.Clamp01(meleeBrakeSpeedFactor);

        _meleeBraking = true;
        _meleeBrakeUntil = Time.time + Mathf.Max(0f, meleeBrakeDuration);
    }

    public void CancelMeleeBrake()
    {
        _meleeBraking = false;
        _meleeBrakeUntil = 0f;
        _meleeBrakeTargetVel = Vector3.zero;
    }

    // 滯空期間的殘留重力。
    //
    // useGravity 在 BeginMeleeHover 就關掉了，所以這裡是「加回沒被抵銷的那部分」，
    // 而不是「抵銷掉一部分」—— 方向搞反的話角色會被往上推飛。
    //   meleeDashGravityCancel = 1 → 完全無重力，什麼都不加
    //   meleeDashGravityCancel = 0 → 加回全部重力，等同沒有滯空
    // ────────────────────────────────────────────────
    //  近戰錨定（部分速度繼承）
    //
    //  高速戰鬥的核心問題：所有近戰數值都在世界空間定義，但交戰發生在一個
    //  高速移動的參考系裡。相對速度 8 m/s 時，dashStopDistance = 0.2 只夠撐
    //  0.025 秒 —— 一個物理幀都不到就滑開了。
    //
    //  錨定把目標速度的一部分注入玩家，讓相對速度降下來，數值才回到「雙方
    //  靜止」的前提。factor 不設 1 是刻意的：保留一點漂移，玩家仍要自己補位，
    //  不會變成貼在敵人身上。
    //
    //  作法是「增量注入」：每幀只補上與上次注入量的差額，不去覆寫速度，
    //  所以突進、煞車、一般移動都能照常運作，互不干擾。
    // ────────────────────────────────────────────────

    private Rigidbody _meleeAnchorTarget;
    private Vector3 _appliedAnchorVel;

    // ───── 突進診斷 ─────
    private enum MeleeDashEndReason { ReachedStopDistance, ExternalStop }

    private float _dbgStartTime;
    private float _dbgTravelled; // 純診斷：累積相對平台位移，不參與停止判定
    private Vector3 _dbgStartPos;
    private Vector3 _dbgTargetStartPos;
    private Vector3 _dbgTargetVelRel;
    private Vector3 _dbgInterceptPoint;
    private bool _dbgHasIntercept;
    private float _dbgPredictedTime;
    private float _dbgPredictedDist;
    private float _dbgPeakSpeed;
    private bool _dbgCaptured;
    private string _dbgStopSource = "unknown";

    // 目前的俯仰角（度，正值 = 低頭 / 往下看，Unity 的 X 軸旋轉方向）
    private float _meleePitchCurrent;

    /// <summary>
    /// 更新近戰俯仰角。
    ///
    /// 只在近戰攻擊進行中朝目標俯仰，攻擊結束就平滑回 0 —— 不然角色會維持
    /// 斜著跑步。整段攻擊都有角度（不只突進），因為近戰在這遊戲是主武器，
    /// 揮刀時對著目標比只在突進時對著更一致。
    /// </summary>
    private void UpdateMeleePitch()
    {
        float desired = 0f;

        bool attacking = (meleeController != null) && meleeController.IsAttacking;

        if (attacking && meleeMaxPitch > 0f && characterModel != null)
        {
            Transform target = GetMeleePitchTarget();
            if (target != null)
            {
                Vector3 dir = target.position - characterModel.position;

                if (dir.sqrMagnitude > 0.0001f)
                {
                    // asin(y) 得到與水平面的夾角。取負是因為 Unity 的 X 軸正旋轉是低頭。
                    float angle = Mathf.Asin(Mathf.Clamp(dir.normalized.y, -1f, 1f)) * Mathf.Rad2Deg;
                    desired = Mathf.Clamp(-angle, -meleeMaxPitch, meleeMaxPitch);
                }
            }
        }

        _meleePitchCurrent = Mathf.Lerp(
            _meleePitchCurrent, desired, Time.deltaTime * Mathf.Max(0.01f, meleePitchSpeed));

        if (Mathf.Abs(_meleePitchCurrent) < 0.01f) _meleePitchCurrent = 0f;
    }

    // 錨定中的目標優先（那是真正在打的對象），否則用鎖定目標
    private Transform GetMeleePitchTarget()
    {
        if (_meleeAnchorTarget != null && _meleeAnchorTarget.gameObject.activeInHierarchy)
            return _meleeAnchorTarget.transform;

        if (PlayerAiming.Instance != null)
        {
            var rb = PlayerAiming.Instance.GetTargetRigidbody();
            if (rb != null) return rb.transform;
        }

        return null;
    }

    /// <summary>
    /// 開始錨定。由 MeleeAttackController 在 hitbox 開啟時呼叫 ——
    /// 從命中判定開始才跟上，感覺是「砍中了所以跟著走」，
    /// 而不是突進階段就被吸過去。
    /// </summary>
    public void BeginMeleeAnchor(Rigidbody target)
    {
        if (target == null || meleeAnchorFactor <= 0f) return;
        if (_meleeAnchorTarget == target) return;

        // 換目標時先把舊的注入量退掉，避免速度累加
        ReleaseAnchorVelocity();
        _meleeAnchorTarget = target;
    }

    /// <summary>結束錨定。連段整串結束或被取消時呼叫。</summary>
    public void EndMeleeAnchor()
    {
        ReleaseAnchorVelocity();
        _meleeAnchorTarget = null;
    }

    // 把先前注入的速度收回。不收的話玩家會永久保留敵人的速度。
    private void ReleaseAnchorVelocity()
    {
        if (_appliedAnchorVel.sqrMagnitude > 0f && playerRigidbody != null)
            playerRigidbody.linearVelocity -= _appliedAnchorVel;

        _appliedAnchorVel = Vector3.zero;
    }

    private void ApplyMeleeAnchorFixed(float dt)
    {
        if (_meleeAnchorTarget == null)
        {
            if (_appliedAnchorVel.sqrMagnitude > 0f) ReleaseAnchorVelocity();
            return;
        }

        if (playerRigidbody == null) { EndMeleeAnchor(); return; }

        // 目標被銷毀（打死了）→ 解除錨定
        if (!_meleeAnchorTarget.gameObject.activeInHierarchy)
        {
            EndMeleeAnchor();
            return;
        }

        // 目標速度要扣掉平台速度：雙方都站在 Landship 上時，
        // 共同的船速不該被當成需要跟隨的相對運動。
        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 targetVelRel = _meleeAnchorTarget.linearVelocity - platformVel;

        Vector3 desired = targetVelRel * Mathf.Clamp01(meleeAnchorFactor);

        // 增量注入：只補差額，不覆寫既有速度
        Vector3 delta = desired - _appliedAnchorVel;
        playerRigidbody.linearVelocity += delta;
        _appliedAnchorVel = desired;
    }

    private void ApplyMeleeHoverFixed(float dt)
    {
        if (!_meleeHovering || playerRigidbody == null) return;

        float residual = 1f - Mathf.Clamp01(meleeDashGravityCancel);
        if (residual <= 0f) return;

        playerRigidbody.AddForce(Physics.gravity * residual, ForceMode.Acceleration);
    }

    private void ApplyMeleeBrakeFixed(float dt)
    {
        // ★ 用 bool 當閘門，不要用「時間 + duration」組合條件。
        //   之前寫成 (Time.time >= until && duration > 0) 的話，duration = 0 會讓
        //   整個條件恆為 false —— 煞車每幀都跑，把 Dash 在內的所有水平移動都吃掉。
        if (!_meleeBraking) return;

        if (playerRigidbody == null) { CancelMeleeBrake(); return; }

        // duration = 0 表示立即歸零，這一幀做完就結束
        if (meleeBrakeDuration > 0f && Time.time >= _meleeBrakeUntil)
        {
            CancelMeleeBrake();
            return;
        }

        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = playerRigidbody.linearVelocity - platformVel;

        // 三維煞車：垂直分量也要煞。
        // 向上突進 25 m/s 打空中目標之後若保留 Y，角色會繼續往上飛。
        // 歸零 Y 之後重力接手，角色停在半空再落下 —— 空中攻擊的標準收尾。
        // 落地狀態下 vRel.y 本來就接近 0，所以不影響地面戰鬥。
        float k = (meleeBrakeDuration > 0f)
            ? Mathf.Clamp01(dt / meleeBrakeDuration * 3f)
            : 1f;

        vRel = Vector3.Lerp(vRel, _meleeBrakeTargetVel, k);

        // 夠接近目標速度就收工。注意這裡比的是「與目標的差距」而不是速度本身 ——
        // 保留 1/4 動量時速度永遠不會歸零，用絕對值判斷會一直煞下去。
        if ((vRel - _meleeBrakeTargetVel).sqrMagnitude < 0.01f || meleeBrakeDuration <= 0f)
        {
            vRel = _meleeBrakeTargetVel;
            CancelMeleeBrake();
        }

        playerRigidbody.linearVelocity = vRel + platformVel;
    }

    /// <summary>
    /// 開始一次近戰突進。由 MeleeAttackController 在 AnimEvent_MeleeDashStart 時呼叫。
    /// 突進會持續到 AnimEvent_MeleeBrake、StepEnd、Dash 取消，或 ToTarget 抵達 stopDistance。
    /// speed 已由 MeleeAttackController 依能量狀態決定。
    /// </summary>
    public void BeginMeleeDash(Vector3 direction, float speed,
                               Rigidbody target, float stopDistance)
    {
        // 三維：不壓平 Y，才能衝向空中的目標。
        if (direction.sqrMagnitude < 0.0001f || speed <= 0f)
        {
            StopMeleeDash("invalidDashParams");
            return;
        }

        _meleeDashDir = direction.normalized;
        _meleeDashSpeed = speed;
        _meleeDashTarget = target;
        _meleeDashStopDistance = Mathf.Max(0f, stopDistance);
        _meleeDashing = true;

        ApplyDashEntryBraking();

        _dbgStartTime = Time.time;
        _dbgTravelled = 0f;
        _dbgStartPos = (playerRigidbody != null) ? playerRigidbody.position : transform.position;
        _dbgTargetStartPos = (target != null) ? target.position : Vector3.zero;
        _dbgPeakSpeed = 0f;
        _dbgCaptured = false;
        _dbgHasIntercept = false;

        BeginMeleeHover();
    }

    // 突進起手的速度歸零。
    //
    // 這不是手感選項，是正確性需求：攔截解算假設你從當前位置以 dashSpeed
    // 朝攔截點前進。帶著既有速度進入的話，實際速度變成「原速度 + 突進加速」，
    // 方向與大小雙雙偏離解算前提。
    //
    // 更糟的是 ApplyMeleeDashFixed 只在「未達目標速度」時加力 —— 初速夠高時
    // 一次力都不會加，整段突進純靠殘餘動量往原方向滑行。
    //
    // 相對平台計算：在移動的 Landship 上歸零世界速度會讓角色被船甩到船尾。
    // 三維歸零（含 Y）：突進本身是三維的，殘留的垂直速度同樣會污染解算。
    private void ApplyDashEntryBraking()
    {
        if (playerRigidbody == null) return;

        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = playerRigidbody.linearVelocity - platformVel;

        playerRigidbody.linearVelocity =
            vRel * Mathf.Clamp01(meleeDashEntrySpeedFactor) + platformVel;

        // 起手歸零後，先前的煞車視窗已無意義 —— 留著會把突進的第一波加速吃掉
        CancelMeleeBrake();
    }

    /// <summary>
    /// 開始滯空。關掉重力，讓突進與後續揮擊維持在同一個高度。
    /// 由 BeginMeleeDash 自動呼叫，持續到 EndMeleeHover。
    /// </summary>
    private void BeginMeleeHover()
    {
        if (_meleeHovering || playerRigidbody == null) return;

        _meleeHovering = true;
        _meleeDashPrevGravity = playerRigidbody.useGravity;
        playerRigidbody.useGravity = false;
    }

    /// <summary>
    /// 結束滯空、恢復重力。由 MeleeAttackController 在整段攻擊結束
    /// （StepEnd / Dash 取消）時呼叫 —— 不是突進結束時。
    /// </summary>
    public void EndMeleeHover()
    {
        if (!_meleeHovering) return;

        _meleeHovering = false;
        if (playerRigidbody != null)
            playerRigidbody.useGravity = _meleeDashPrevGravity;
    }

    /// <summary>停止突進。Brake、ToTarget 停止距離、StepEnd、或被 Dash 取消時呼叫。</summary>
    // 攔截解算 + 防呆。
    //
    // MathToolKit.InterceptionPoint 解的是二次方程，在「目標速度接近或超過自身速度」時
    // 會退化：解出的根可能在極遠處或負的。那些解通過方向檢查（Dot > 0）卻完全不可用 ——
    // 實測見過解出 229 公尺外的攔截點，玩家朝幻影衝過去，最後差了 49 公尺。
    //
    // 三道防呆，任一不過就退回純追蹤（朝目標當前位置，仍然每幀重算）：
    //   1) 速度比太高 → 根本不解
    //   2) 攔截點比目標遠太多倍 → 拒絕
    //   3) 預估抵達時間太長 → 拒絕（那麼久之後戰況早變了）
    //
    // speed：自己的前進速度。不給（≤ 0）= 近戰突進速度；突擊推進的攔截傳推進速度。
    private bool TrySolveIntercept(Vector3 selfPos, Vector3 targetPos,
                                   Vector3 toTarget, Vector3 targetVelRel,
                                   out Vector3 intercept, float speed = -1f)
    {
        intercept = targetPos;

        if (targetVelRel.sqrMagnitude <= 0.01f) return false;

        float dashSpeed = Mathf.Max(0.01f, speed > 0f ? speed : _meleeDashSpeed);

        // 1) 速度比：接近 1 時方程退化
        if (targetVelRel.magnitude / dashSpeed >= interceptMaxSpeedRatio) return false;

        if (!MathToolKit.InterceptionPoint(targetPos, selfPos, targetVelRel, dashSpeed, out var solved))
            return false;

        Vector3 toIntercept = solved - selfPos;
        if (toIntercept.sqrMagnitude <= 0.0001f) return false;

        // 解在背後
        if (Vector3.Dot(toIntercept.normalized, toTarget.normalized) <= 0f) return false;

        float interceptDist = toIntercept.magnitude;
        float targetDist = toTarget.magnitude;

        // 2) 提前量離譜
        if (interceptDist > targetDist * Mathf.Max(1f, interceptMaxLeadMultiplier)) return false;

        // 3) 抵達時間太長
        if (interceptDist / dashSpeed > Mathf.Max(0.05f, interceptMaxSolveTime)) return false;

        intercept = solved;
        return true;
    }

    // 突進結束診斷。回答兩個問題：
    //   1) 我到得夠快嗎？  預測時間 vs 實際時間、峰值 / 平均速度 vs 解算假設速度
    //   2) 我最後差在哪？  把最終誤差分解成「沿目標移動方向」與「側向」兩個分量
    //
    // travelled 只用來觀察這次 Animation Dash Window 實際跑了多遠，不再是距離預算。
    private void LogDashDiagnostics(MeleeDashEndReason reason)
    {
        if (!logMeleeDashDiagnostics || !_meleeDashing) return;

        float elapsed = Time.time - _dbgStartTime;
        Vector3 endPos = (playerRigidbody != null) ? playerRigidbody.position : transform.position;

        float avgSpeed = (elapsed > 0.0001f) ? (_dbgTravelled / elapsed) : 0f;

        string targetPart = "target=none";

        if (_meleeDashTarget != null)
        {
            Vector3 targetPos = _meleeDashTarget.position;
            Vector3 gap = targetPos - endPos;

            // 誤差分解：沿著目標移動方向的分量最有診斷價值。
            // 負值代表目標已經走到我前面去了 —— 我抵達得太慢。
            float along = 0f, lateral = gap.magnitude;

            if (_dbgTargetVelRel.sqrMagnitude > 0.01f)
            {
                Vector3 vDir = _dbgTargetVelRel.normalized;
                along = Vector3.Dot(gap, vDir);
                lateral = (gap - vDir * along).magnitude;
            }

            float targetMoved = (targetPos - _dbgTargetStartPos).magnitude;

            targetPart =
                $"finalGap={gap.magnitude:F2} (沿目標移動方向={along:F2}, 側向={lateral:F2}) | " +
                $"targetSpeed={_dbgTargetVelRel.magnitude:F1} targetMoved={targetMoved:F2} | " +
                $"stopDist={_meleeDashStopDistance:F2}";
        }

        string predictPart = _dbgHasIntercept
            ? $"預測: dist={_dbgPredictedDist:F2} time={_dbgPredictedTime:F3}s (假設 {_meleeDashSpeed:F1} m/s)"
            : "預測: 無攔截解（純追蹤）";

        // 時間比：> 1 表示實際比解算慢，攔截點就會落在目標後方
        float timeRatio = (_dbgPredictedTime > 0.0001f) ? (elapsed / _dbgPredictedTime) : 0f;

        string reasonText = (reason == MeleeDashEndReason.ExternalStop)
            ? $"ExternalStop({_dbgStopSource})"
            : reason.ToString();

        Debug.Log(
            $"[MeleeDash] {reasonText} | {predictPart} | " +
            $"實際: time={elapsed:F3}s (×{timeRatio:F2}) travelled={_dbgTravelled:F2} | " +
            $"speed peak={_dbgPeakSpeed:F1} avg={avgSpeed:F1} | {targetPart}", this);
    }

    /// <summary>
    /// 停止突進。source 只用於診斷 —— 突進被誰中止是最關鍵的除錯資訊，
    /// 因為「還沒靠近就被停掉」跟「到了但打不中」是完全不同的問題。
    /// </summary>
    public void StopMeleeDash(string source = "unknown")
    {
        _dbgStopSource = source;
        StopMeleeDashInternal(MeleeDashEndReason.ExternalStop);
    }

    private void StopMeleeDashInternal(MeleeDashEndReason reason)
    {
        LogDashDiagnostics(reason);

        // 註：這裡刻意不恢復重力。滯空要撐到整段攻擊結束（EndMeleeHover），
        //     否則突進一停人就開始掉，刀還沒揮完就離開目標高度了。
        _meleeDashing = false;
        _meleeDashTarget = null;
    }

    private void ApplyMeleeDashFixed(float dt)
    {
        if (!_meleeDashing) return;
        if (playerRigidbody == null) { StopMeleeDash("noRigidbody"); return; }

        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = playerRigidbody.linearVelocity - platformVel;

        // ToTarget：每幀重算方向，並且朝「攔截點」而不是目標當前位置。
        //
        // 朝當前位置衝是純追尾 —— 你永遠跑向敵人剛剛在的地方，形成尾隨曲線，
        // 橫向移動的敵人幾乎打不到。攔截解算出「我用這個速度前進，會在哪裡跟它相遇」，
        // 效果等同子彈的提前量。
        //
        // 【方向 A】方向修正是「持續」的，但有轉向速率上限（meleeDashTurnRate）。
        // 系統只幫忙修掉高速造成的小誤差，角度偏太多還是追不上 —— 玩家仍然要
        // 負責把自己送到大致正確的位置。
        if (_meleeDashTarget != null)
        {
            Vector3 selfPos = playerRigidbody.position;
            Vector3 targetPos = _meleeDashTarget.position;

            Vector3 toTarget = targetPos - selfPos;   // 三維，不壓平

            if (toTarget.magnitude <= _meleeDashStopDistance)
            {
                StopMeleeDashInternal(MeleeDashEndReason.ReachedStopDistance);
                return;
            }

            // 目標速度也要扣掉平台速度：兩者都站在同一艘船上時，
            // 共同的船速不該被算成需要提前量的相對運動。
            Vector3 targetVelRel = _meleeDashTarget.linearVelocity - platformVel;

            Vector3 aimPoint = targetPos;

            if (TrySolveIntercept(selfPos, targetPos, toTarget, targetVelRel, out var intercept))
            {
                aimPoint = intercept;

                if (!_dbgCaptured)
                {
                    _dbgHasIntercept = true;
                    _dbgInterceptPoint = intercept;
                }
            }

            // 第一幀捕捉解算前提，供結束時比對
            if (!_dbgCaptured)
            {
                _dbgCaptured = true;
                _dbgTargetVelRel = targetVelRel;
                _dbgPredictedDist = (aimPoint - selfPos).magnitude;

                // 解算器假設的是「立刻且持續以 _meleeDashSpeed 前進」
                _dbgPredictedTime = _dbgPredictedDist / Mathf.Max(0.01f, _meleeDashSpeed);
            }

            Vector3 desiredDir = aimPoint - selfPos;

            if (desiredDir.sqrMagnitude > 0.0001f)
            {
                desiredDir.Normalize();

                // 轉向速率上限：保留「角度太離譜就打不中」的懲罰
                float maxRadians = meleeDashTurnRate * Mathf.Deg2Rad * dt;
                _meleeDashDir = Vector3.RotateTowards(_meleeDashDir, desiredDir, maxRadians, 0f).normalized;

                // ★ 角色朝向也要跟著轉。
                //   attackDesiredForward 是攻擊當下設定一次就不動的，只改移動方向
                //   會讓角色橫著飄、刀刃卻朝原方向揮 —— 追到了也砍不到。
                //   RotateCharacter 只處理水平朝向，所以這裡壓平。
                if (attackFacingActive)
                {
                    Vector3 flatAim = new Vector3(_meleeDashDir.x, 0f, _meleeDashDir.z);
                    if (flatAim.sqrMagnitude > 0.0001f)
                        attackDesiredForward = flatAim.normalized;
                }
            }
        }

        // 速度與診斷距離都使用「相對平台」數值，共同船速不計入近戰突進。
        float vParallelRel = Vector3.Dot(vRel, _meleeDashDir);
        if (vParallelRel > 0f)
        {
            _dbgTravelled += vParallelRel * dt;
            if (vParallelRel > _dbgPeakSpeed) _dbgPeakSpeed = vParallelRel;
        }

        // 沒有速度曲線，也沒有距離預算。Dash Window 由動畫事件控制。
        float targetSpeed = _meleeDashSpeed;

        // 重力由 ApplyMeleeHoverFixed 統一處理（滯空期間 useGravity 已關閉）。
        if (vParallelRel >= targetSpeed) return;

        playerRigidbody.AddForce(_meleeDashDir * meleeDashAccel, ForceMode.Acceleration);
    }

    // ────────────────────────────────────────────────
    //  突擊推進（Assault Boost）
    // ────────────────────────────────────────────────

    /// <summary>突擊推進按鍵：沒在推進就開始；正在推進就以踢擊收尾（踢擊中再按沒有作用）。</summary>
    public void AssaultBoostAction()
    {
        if (_assaultBoosting)
        {
            if (!_abKicking) BeginAssaultKick();
        }
        else TryBeginAssaultBoost();
    }

    private bool TryBeginAssaultBoost()
    {
        var stats = PlayerStats.Instance;
        if (stats == null) return false;

        // 近戰揮擊中，且這一段標記為不可取消 → 跟 Dash 一樣擋掉
        if (meleeController != null && meleeController.IsBlockingDash) return false;
        if (stats.currentEnergy <= 0f) return false;

        stats.currentEnergy -= stats.dashEnergyCost;
        CancelInvoke(nameof(ResetEnergyRegenerate));
        canRegenerateEnergy = false;

        // 收掉近戰、一般 Dash 與飛行請求（順序同 DashAction：先收近戰，它會還原重力設定）
        CancelMeleeBrake();
        StopMeleeDash("AssaultBoost");
        EndMeleeHover();
        EndMeleeAnchor();
        if (meleeController != null)
            meleeController.CancelByDash();

        dashRequestUntil = 0f;
        dashActiveUntil = 0f;
        flyRequestUntil = 0f;

        // 相對平台的速度歸零：在船上就是跟著船走，不在船上就是停住
        playerRigidbody.linearVelocity = GetMobilePlatformVelocity();

        _abPrevGravity = playerRigidbody.useGravity;
        playerRigidbody.useGravity = false;

        _abFacing = FlatForwardOr(characterModel != null ? characterModel.forward : transform.forward, Vector3.forward);
        _abAnimDir = _abFacing;
        {
            Camera startCam = (PlayerAiming.Instance != null) ? PlayerAiming.Instance.MainCamera : Camera.main;
            _abSteerDir = (startCam != null) ? startCam.transform.forward : _abFacing;   // 從鏡頭方向開始轉
        }
        _abFlyRequestUntil = 0f;
        _assaultBoosting = true;
        SetAssaultBoostAnim(true);

        // 推進中手動轉鏡頭受 AimTurnRate 限制（推進方向跟著鏡頭，所以推進的轉向也一起受限）
        if (PlayerAiming.Instance != null) PlayerAiming.Instance.SetManualTurnRateLimit(true);
        return true;
    }

    /// <summary>
    /// 結束突擊推進。保留當下速度，之後由一般移動用 decelerationSpeed 慢慢減速、重力恢復。
    /// 能量回復交回 EnergyRegenerationCheck 的一般規則（停止消耗後延遲一段時間才開始回復）。
    /// </summary>
    public void EndAssaultBoost()
    {
        if (!_assaultBoosting) return;

        _assaultBoosting = false;
        playerRigidbody.useGravity = _abPrevGravity;
        SetAssaultBoostAnim(false);

        // 踢擊中被中斷（Dash、近戰、能量歸零…）或正常結束：一起收掉
        if (_abKicking)
        {
            _abKicking = false;
            _abKickHitOpen = false;
            _abKickHit.Clear();
            ResetAssaultKickTrigger();
        }

        if (PlayerAiming.Instance != null) PlayerAiming.Instance.SetManualTurnRateLimit(false);
    }

    private void ApplyAssaultBoostFixed(float dt)
    {
        var stats = PlayerStats.Instance;
        if (stats == null)
        {
            EndAssaultBoost();
            return;
        }

        // 1) 能量：每秒扣 flyEnergyCost（打折），期間不回復；耗盡就結束（保留當下速度）
        stats.currentEnergy -= stats.flyEnergyCost * assaultBoostEnergyCostMultiplier * dt;
        CancelInvoke(nameof(ResetEnergyRegenerate));
        canRegenerateEnergy = false;
        if (stats.currentEnergy <= 0f)
        {
            stats.currentEnergy = 0f;
            EndAssaultBoost();
            return;
        }

        // 2) 方向：畫面正中央（鏡頭正前方）。
        //    不能用 PlayerAiming.GetRay() —— 沒按中鍵時的「輔助瞄準」會把 ray 拉向鎖定圈內最近的敵人，
        //    推進就會自己轉向敵人。按中鍵鎖定（AutoAim）時鏡頭本身會轉向目標，
        //    畫面中央自然就對著目標，所以只有鎖定時推進才會追著目標衝。
        Camera cam = (PlayerAiming.Instance != null) ? PlayerAiming.Instance.MainCamera : Camera.main;
        Vector3 aimDir = (cam != null) ? cam.transform.forward : characterOrientation.forward;
        if (aimDir.sqrMagnitude < 0.0001f) aimDir = _abFacing;
        aimDir.Normalize();

        // 一律以「相對平台」的速度計算（跟一般移動、Dash 相同）
        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = playerRigidbody.linearVelocity - platformVel;

        // 2.5) 攔截：AutoAim（中鍵鎖定）中，推進改朝攔截點（解不出來就直接朝目標）
        bool intercepting = TryGetAssaultBoostInterceptDir(platformVel, stats.dashSpeed, out Vector3 interceptDir);
        Vector3 wantDir = intercepting ? interceptDir : aimDir;

        // 推進方向每秒最多轉 AimTurnRate 度。自由飛行時鏡頭本身的轉速也被同一個值限制，所以手感不變；
        // 攔截時系統只幫忙修正，大角度的閃避仍然甩得開。
        float turnRate = Mathf.Max(0f, stats.GetAimTurnRate());
        if (_abSteerDir.sqrMagnitude < 0.0001f) _abSteerDir = wantDir;
        _abSteerDir = Vector3.RotateTowards(_abSteerDir.normalized, wantDir, turnRate * Mathf.Deg2Rad * dt, 0f);
        if (_abSteerDir.sqrMagnitude < 0.0001f) _abSteerDir = wantDir;
        _abSteerDir.Normalize();
        aimDir = _abSteerDir;

        Vector3 flatForward = FlatForwardOr(aimDir, _abFacing);
        Vector3 right = Vector3.Cross(Vector3.up, flatForward);

        // 3) 俯仰：鏡頭超出死區的部分才算數（攔截時不套死區，高度直接對準目標）。
        //    沒有推進器（flySpeed = 0）時一律當作水平。
        float pitch = Mathf.Asin(Mathf.Clamp(aimDir.y, -1f, 1f)) * Mathf.Rad2Deg;
        float effectivePitch = intercepting
            ? pitch
            : Mathf.Sign(pitch) * Mathf.Max(0f, Mathf.Abs(pitch) - assaultBoostPitchDeadZone);
        if (stats.flySpeed <= 0f) effectivePitch = 0f;
        float pitchRad = effectivePitch * Mathf.Deg2Rad;

        // 4) 水平：拆成「前進」與「側向」兩個分量各自控制。
        //    前進：目標 dashSpeed × cos(俯仰)，加速度 accelerationSpeed。
        //    側向：目標 = 左右輸入 × sprintSpeed × assaultBoostSideSpeedRatio（放開 = 0），
        //          以 accelerationSpeed 增減。
        //    轉鏡頭時，原本的前進速度有一部分會變成側向速度，再慢慢收回 —— 自然帶一點甩尾的慣性
        Vector3 horizontalRel = new Vector3(vRel.x, 0f, vRel.z);
        float vForward = Vector3.Dot(horizontalRel, flatForward);
        float vSide = Vector3.Dot(horizontalRel, right);

        vForward = Mathf.MoveTowards(vForward, stats.dashSpeed * Mathf.Cos(pitchRad), stats.accelerationSpeed * dt);

        float sideInput = Mathf.Clamp(_moveInput.x, -1f, 1f);
        float targetSide = sideInput * stats.sprintSpeed * assaultBoostSideSpeedRatio;
        vSide = Mathf.MoveTowards(vSide, targetSide, stats.accelerationSpeed * dt);

        horizontalRel = flatForward * vForward + right * vSide;

        // 5) 垂直：dashSpeed × sin(俯仰)，沒有上限（只受鏡頭能轉到的角度限制）
        float targetVy = stats.dashSpeed * Mathf.Sin(pitchRad);

        // 按住飛行鍵：至少以 flySpeed 往上升（能量已經在推進中按每秒 flyEnergyCost 扣，不重複扣）。
        // 用 Max：鏡頭已經往上看、爬得比 flySpeed 快時，按飛行鍵不會反而變慢
        if (Time.time <= _abFlyRequestUntil && stats.flySpeed > 0f)
            targetVy = Mathf.Max(targetVy, stats.flySpeed);

        if (grounded && targetVy < 0f) targetVy = 0f;   // 貼地時不往地面裡推

        // 垂直跟水平用同一個加速度，軌跡才會沿著鏡頭方向走直線
        float vy = Mathf.MoveTowards(vRel.y, targetVy, stats.accelerationSpeed * dt);

        Vector3 newVRel = new Vector3(horizontalRel.x, vy, horizontalRel.z);
        playerRigidbody.linearVelocity = newVRel + platformVel;

        // 撞飛前方的小型敵人
        RamSmallEnemies(newVRel, platformVel, dt);

        // 6) 給 RotateCharacter：角色面向推進方向，移動動畫用實際的水平移動方向（側移時會偏向旁邊）
        _abFacing = flatForward;
        if (horizontalRel.sqrMagnitude > 0.0001f) _abAnimDir = horizontalRel.normalized;
    }

    // ═══════════════════════ 突擊推進踢 ═══════════════════════

    private void BeginAssaultKick()
    {
        _abKicking = true;
        _abKickHitOpen = false;
        _abKickHit.Clear();
        _abKickEndTime = Time.time + assaultKickMaxDuration;

        // 踢擊一開始就把推進的 bool 關掉，只靠 Trigger 進踢擊。
        // 否則 bool 還是 true，Any State → Assault Boost 會在踢擊途中又把狀態搶回去。
        // （推進的邏輯狀態 _assaultBoosting 仍然是 true，要等踢擊結束才真正結束。）
        SetAssaultBoostAnim(false);
        if (!TrySetAssaultKickTrigger())
            Debug.LogWarning($"[PlayerMovement] Animator 沒有 Trigger「{assaultKickAnimTrigger}」，踢擊沒有動畫，" +
                             $"會在 {assaultKickMaxDuration} 秒後自動結束。", this);
    }

    /// <summary>動畫事件：踢擊開始判定。</summary>
    public void OnAssaultKickHitOn()
    {
        if (!_abKicking) return;
        _abKickHitOpen = true;
        AssaultKickSweep(Time.fixedDeltaTime);   // 這一幀就先判一次
    }

    /// <summary>動畫事件：踢擊結束判定。</summary>
    public void OnAssaultKickHitOff()
    {
        if (!_abKicking) return;
        _abKickHitOpen = false;
    }

    /// <summary>動畫事件：踢擊結束 → 突擊推進正式結束。</summary>
    public void OnAssaultKickEnd()
    {
        if (!_abKicking) return;
        EndAssaultBoost();
    }

    /// <summary>踢擊期間每個物理步：不推進、不扣能量，維持當下速度；判定開啟時掃描前方。</summary>
    private void ApplyAssaultKickFixed(float dt)
    {
        if (Time.time >= _abKickEndTime)
        {
            EndAssaultBoost();   // 保險：動畫事件沒來
            return;
        }

        if (_abKickHitOpen) AssaultKickSweep(dt);
    }

    private void AssaultKickSweep(float dt)
    {
        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = playerRigidbody.linearVelocity - platformVel;
        float speed = vRel.magnitude;

        // 掃描方向：在動就沿移動方向，幾乎沒在動就沿面向
        Vector3 dir = (speed > 1f) ? vRel / speed : _abFacing;
        Vector3 start = playerRigidbody.worldCenterOfMass;
        Vector3 end = start + dir * (assaultKickReach + speed * dt);

        int mask = assaultBoostRamLayers.value != 0 ? assaultBoostRamLayers.value : LayerMask.GetMask("Enemy");
        int count = Physics.OverlapCapsuleNonAlloc(start, end, assaultKickRadius, s_RamHits, mask,
                                                   QueryTriggerInteraction.Ignore);
        if (count == 0) return;

        Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
        if (flatDir.sqrMagnitude < 0.0001f) flatDir = _abFacing;
        flatDir.Normalize();

        float weight = (PlayerStats.Instance != null) ? PlayerStats.Instance.GetTotalWeight() : 0f;

        for (int i = 0; i < count; i++)
        {
            Collider col = s_RamHits[i];
            if (col == null) continue;

            var stats = col.GetComponentInParent<ModularEntityStats>();
            if (stats == null || stats.IsDead) continue;
            if (!_abKickHit.Add(stats)) continue;   // 每隻敵人每次踢擊只吃一次

            // ── 傷害 = 撞擊傷害 × 倍率（敵人速度要在踢飛之前讀）──
            Rigidbody enemyRb = col.attachedRigidbody;
            if (enemyRb == null) enemyRb = stats.GetComponentInParent<Rigidbody>();
            float closing = RamClosingSpeed(vRel + platformVel, enemyRb, stats.transform.position, dir, Mathf.Max(speed, 0.01f));
            float damage = assaultBoostRamDamagePerMomentum * weight * closing * assaultKickDamageMultiplier;

            if (damage > 0f)
            {
                var info = new DamageInfo(damage, 0f, 0f, 0f) { kind = DamageKind.Melee };
                stats.TakeDamage(info, gameObject);

                if (Effects != null) Effects.PlayMeleeHitFeedback(col.ClosestPoint(start));
            }

            if (stats.IsDead) continue;
            if (stats.entitySize != EntitySize.Small) continue;   // 中型、大型只吃傷害

            // ── 小型：照撞擊的參數踢飛 ──
            var movement = stats.GetComponent<ModularEntityMovement>();
            if (movement == null) movement = stats.GetComponentInChildren<ModularEntityMovement>();

            Vector3 toEnemy = stats.transform.position - playerRigidbody.position;
            toEnemy.y = 0f;
            Vector3 side = toEnemy - flatDir * Vector3.Dot(toEnemy, flatDir);
            Vector3 knockDir = flatDir;
            if (side.sqrMagnitude > 0.0001f)
                knockDir = (flatDir + side.normalized * assaultBoostRamSideBias).normalized;

            Vector3 knockWorld = knockDir * (speed * assaultBoostRamSpeedMultiplier)
                               + Vector3.up * assaultBoostRamUpwardSpeed
                               + platformVel;

            if (movement != null)
            {
                movement.ApplyKnockback(knockWorld, assaultBoostRamKnockbackDuration);
            }
            else
            {
                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && !rb.isKinematic) rb.linearVelocity = knockWorld;
            }
        }
    }

    private bool TrySetAssaultKickTrigger()
    {
        Animator anim = (playerAnimation != null) ? playerAnimation.anim : null;
        if (!HasAssaultKickTrigger(anim)) return false;
        anim.SetTrigger(_abKickAnimHash);
        return true;
    }

    private void ResetAssaultKickTrigger()
    {
        Animator anim = (playerAnimation != null) ? playerAnimation.anim : null;
        if (HasAssaultKickTrigger(anim)) anim.ResetTrigger(_abKickAnimHash);
    }

    private bool HasAssaultKickTrigger(Animator anim)
    {
        if (anim == null || string.IsNullOrEmpty(assaultKickAnimTrigger)) return false;

        if (_abKickAnimHasParam == null)
        {
            _abKickAnimHash = Animator.StringToHash(assaultKickAnimTrigger);
            _abKickAnimHasParam = false;
            foreach (var p in anim.parameters)
            {
                if (p.nameHash == _abKickAnimHash && p.type == AnimatorControllerParameterType.Trigger)
                {
                    _abKickAnimHasParam = true;
                    break;
                }
            }
        }
        return _abKickAnimHasParam == true;
    }

    /// <summary>
    /// 突擊推進的攔截方向（3D，單位向量）。只在 AutoAim（中鍵鎖定）中有目標時回傳 true。
    /// 用近戰突進同一套攔截解算與防呆，速度換成推進速度；解不出來就直接朝目標的瞄準點。
    /// 目標速度扣掉自己腳下的平台速度（兩者都在船上時，共同的船速不算提前量）。
    /// </summary>
    private bool TryGetAssaultBoostInterceptDir(Vector3 platformVel, float boostSpeed, out Vector3 dir)
    {
        dir = Vector3.zero;
        PlayerAiming aiming = PlayerAiming.Instance;
        if (aiming == null || !aiming.TryGetAutoAimTarget(out _, out Vector3 targetPos, out Rigidbody targetRb))
            return false;

        Vector3 selfPos = playerRigidbody.worldCenterOfMass;
        Vector3 toTarget = targetPos - selfPos;
        if (toTarget.sqrMagnitude < 0.01f) return false;   // 已經貼在目標身上：照鏡頭方向

        Vector3 targetVelRel = (targetRb != null && !targetRb.isKinematic)
            ? targetRb.linearVelocity - platformVel
            : Vector3.zero;

        Vector3 goal = targetPos;
        if (TrySolveIntercept(selfPos, targetPos, toTarget, targetVelRel, out Vector3 intercept, boostSpeed))
            goal = intercept;

        Vector3 d = goal - selfPos;
        if (d.sqrMagnitude < 0.0001f) d = toTarget;
        dir = d.normalized;
        return true;
    }

    /// <summary>
    /// 推進中撞飛前方的小型敵人。在這一步的推進速度算好之後呼叫。
    /// vRel：玩家這一步的相對平台速度；platformVel：平台速度（不在船上時為 0）。
    /// </summary>
    private void RamSmallEnemies(Vector3 vRel, Vector3 platformVel, float dt)
    {
        float speed = vRel.magnitude;
        if (speed < 1f) return;   // 幾乎沒在動（剛啟動）就不撞

        int mask = assaultBoostRamLayers.value != 0 ? assaultBoostRamLayers.value : LayerMask.GetMask("Enemy");

        // 沿這一步的移動方向往前掃一段：兩步的距離 + 一點餘裕，確保在真正碰到之前就先把它撞開
        Vector3 dir = vRel / speed;
        Vector3 start = playerRigidbody.worldCenterOfMass;
        Vector3 end = start + dir * (speed * dt * 2f + 0.5f);

        int count = Physics.OverlapCapsuleNonAlloc(start, end, assaultBoostRamRadius, s_RamHits, mask,
                                                   QueryTriggerInteraction.Ignore);
        if (count == 0) return;

        Vector3 flatDir = new Vector3(dir.x, 0f, dir.z);
        if (flatDir.sqrMagnitude < 0.0001f) flatDir = _abFacing;
        flatDir.Normalize();

        s_RamSeen.Clear();
        for (int i = 0; i < count; i++)
        {
            Collider col = s_RamHits[i];
            if (col == null) continue;

            var stats = col.GetComponentInParent<ModularEntityStats>();
            if (stats == null || stats.IsDead || stats.entitySize != EntitySize.Small) continue;
            if (!s_RamSeen.Add(stats)) continue;   // 同一隻敵人有好幾個碰撞體，只撞一次

            var movement = stats.GetComponent<ModularEntityMovement>();
            if (movement == null) movement = stats.GetComponentInChildren<ModularEntityMovement>();
            if (movement != null && movement.IsKnockedBack) continue;   // 已經在飛了 —— 同一次撞飛只算一次傷害

            // ── 撞擊傷害 ──（敵人速度要在撞飛之前讀，撞飛會改掉它）
            Rigidbody enemyRb = col.attachedRigidbody;
            if (enemyRb == null) enemyRb = stats.GetComponentInParent<Rigidbody>();
            float rammed = RamClosingSpeed(vRel + platformVel, enemyRb, stats.transform.position, dir, speed);
            if (rammed >= assaultBoostRamMinDamageSpeed && assaultBoostRamDamagePerMomentum > 0f
                && !RamDamageOnCooldown(stats))
            {
                _ramDamageTimes[stats] = Time.time;

                float weight = (PlayerStats.Instance != null) ? PlayerStats.Instance.GetTotalWeight() : 0f;
                float damage = assaultBoostRamDamagePerMomentum * weight * rammed;
                if (damage > 0f)
                    stats.TakeDamage(new DamageInfo(damage, 0f, 0f, 0f), gameObject);

                // 撞死了就不用撞飛：它這一幀就會回收，留著擊退狀態可能帶到下一次重用。
                if (stats.IsDead) continue;
            }

            // 方向：推進方向 + 偏離路線的那一側
            Vector3 toEnemy = stats.transform.position - playerRigidbody.position;
            toEnemy.y = 0f;
            Vector3 side = toEnemy - flatDir * Vector3.Dot(toEnemy, flatDir);
            Vector3 knockDir = flatDir;
            if (side.sqrMagnitude > 0.0001f)
                knockDir = (flatDir + side.normalized * assaultBoostRamSideBias).normalized;

            Vector3 knockRel = knockDir * (speed * assaultBoostRamSpeedMultiplier)
                             + Vector3.up * assaultBoostRamUpwardSpeed;
            Vector3 knockWorld = knockRel + platformVel;

            if (movement != null)
            {
                movement.ApplyKnockback(knockWorld, assaultBoostRamKnockbackDuration);
            }
            else
            {
                // 沒有移動腳本的小型實體：直接改 Rigidbody 速度
                Rigidbody rb = col.attachedRigidbody;
                if (rb != null && !rb.isKinematic) rb.linearVelocity = knockWorld;
            }
        }
    }

    private bool RamDamageOnCooldown(ModularEntityStats stats)
    {
        // 順手清掉過期的紀錄（包含已經被回收的敵人），字典不會一直長大
        if (_ramDamageTimes.Count > 32)
        {
            s_RamPrune.Clear();
            foreach (var kv in _ramDamageTimes)
                if (kv.Key == null || Time.time - kv.Value >= assaultBoostRamDamageCooldown)
                    s_RamPrune.Add(kv.Key);
            for (int i = 0; i < s_RamPrune.Count; i++) _ramDamageTimes.Remove(s_RamPrune[i]);
        }

        return _ramDamageTimes.TryGetValue(stats, out float t) && Time.time - t < assaultBoostRamDamageCooldown;
    }

    /// <summary>
    /// 撞擊的相對速度：玩家和敵人沿「玩家 → 敵人」方向互相接近的速度（m/s），
    /// 夾在 0 ~ 玩家推進速度 × assaultBoostRamMaxRelativeSpeedRatio。
    /// playerWorldVel：玩家的世界速度（相對平台速度 + 平台速度），敵人在船上時平台速度會互相抵消。
    /// </summary>
    private float RamClosingSpeed(Vector3 playerWorldVel, Rigidbody enemyRb, Vector3 enemyPos,
                                  Vector3 fallbackDir, float playerSpeed)
    {
        Vector3 enemyVel = (enemyRb != null && !enemyRb.isKinematic) ? enemyRb.linearVelocity : Vector3.zero;

        Vector3 line = enemyPos - playerRigidbody.worldCenterOfMass;
        Vector3 n = (line.sqrMagnitude > 0.0001f) ? line.normalized : fallbackDir;

        float closing = Vector3.Dot(playerWorldVel - enemyVel, n);
        return Mathf.Clamp(closing, 0f, playerSpeed * assaultBoostRamMaxRelativeSpeedRatio);
    }

    private static Vector3 FlatForwardOr(Vector3 dir, Vector3 fallback)
    {
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
        {
            dir = fallback;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) return Vector3.forward;
        }
        return dir.normalized;
    }

    private void SetAssaultBoostAnim(bool on)
    {
        Animator anim = (playerAnimation != null) ? playerAnimation.anim : null;
        if (anim == null || string.IsNullOrEmpty(assaultBoostAnimBool)) return;

        // 第一次用到時檢查 Animator 有沒有這個參數；還沒做動畫時就安靜略過
        if (_abAnimHasParam == null)
        {
            _abAnimHash = Animator.StringToHash(assaultBoostAnimBool);
            _abAnimHasParam = false;
            foreach (var p in anim.parameters)
            {
                if (p.nameHash == _abAnimHash && p.type == AnimatorControllerParameterType.Bool)
                {
                    _abAnimHasParam = true;
                    break;
                }
            }
        }

        if (_abAnimHasParam == true) anim.SetBool(_abAnimHash, on);
    }

    public void FlyAction()
    {
        // 空中且有能量才允許提出飛行請求
        if (grounded) return;
        if (PlayerStats.Instance.currentEnergy <= 0f) return;
        if (PlayerStats.Instance.flyEnergyCost > 0)
        {
            CancelInvoke("ResetEnergyRegenerate");
            canRegenerateEnergy = false;
        }
        // 把「飛行輸入」延長一小段時間，確保 FixedUpdate 一定能吃到
        flyRequestUntil = Time.time + flyInputBuffer;
    }

    private void ResetEnergyRegenerate()
    {
        canRegenerateEnergy = true;
    }

    public float convertJumpHeightToForce(float jumpHeight)
    {
        float g = Mathf.Abs(Physics.gravity.y);
        float v0 = Mathf.Sqrt(2f * g * jumpHeight);
        float mass = playerRigidbody.mass;
        float impulseMagnitude = mass * v0;

        return impulseMagnitude;
    }

    public void EnergyRegenerationCheck()
    {
        if (PlayerStats.Instance.currentEnergy > PlayerStats.Instance.maxEnergy)
        {
            PlayerStats.Instance.currentEnergy = PlayerStats.Instance.maxEnergy;
            canRegenerateEnergy = false;
        }
        else if (PlayerStats.Instance.currentEnergy < PlayerStats.Instance.maxEnergy && canRegenerateEnergy)
        {
            PlayerStats.Instance.currentEnergy = PlayerStats.Instance.currentEnergy + (Time.deltaTime * PlayerStats.Instance.energyRegen);
        }
        else if (PlayerStats.Instance.currentEnergy < PlayerStats.Instance.maxEnergy && !canRegenerateEnergy && PlayerStats.Instance.currentEnergy <= 0)
        {
            Invoke("ResetEnergyRegenerate", 5);
        }
        else if (PlayerStats.Instance.currentEnergy < PlayerStats.Instance.maxEnergy && !canRegenerateEnergy)
        {
            Invoke("ResetEnergyRegenerate", 1);
        }
        else if (PlayerStats.Instance.currentEnergy < 0)
        {
            PlayerStats.Instance.currentEnergy = 0;
        }
    }
    public void GroundCheck()
    {
        // ————————————————————
        // 1) dynamic ground check
        // ————————————————————
        float castDistance = groundCheckDistance + playerRigidbody.linearVelocity.y * Time.fixedDeltaTime;

        // 地面偵測 + 船上偵測共用同一條往下的射線（詳見 ModularEntityMovement.GroundCheck）
        ShipPassenger p = Passenger;
        float lift = p.RayStartOffset;
        Vector3 origin = groundPoint.position + Vector3.up * lift;
        float distance = Mathf.Max(castDistance, p.MaxDistance) + lift;
        int mask = whatIsGround.value | p.PlatformLayers;

        bool hasHit = Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                                      distance, mask, QueryTriggerInteraction.Ignore);

        grounded = hasHit
                   && (whatIsGround.value & (1 << hit.collider.gameObject.layer)) != 0
                   && hit.distance - lift <= castDistance;

        p.ReportGroundHit(hasHit, hit, origin, distance);
        playerAnimation.SetIsOnGround(grounded);
    }
    private void ResetJump()
    {
        readyToJump = true;
    }
    /// <summary>
    /// 權重固定（Multi-Aim 要一直穩住脊椎），只移動代理目標 aimRigTarget：
    /// aimAtCrosshair = true 時看準星，false 時看角色面向的正前方（胸口高度）。
    /// 在 Update 呼叫，Animation Rigging 會在同一幀稍後讀到新位置。
    /// </summary>
    private void UpdateAimRig(bool aimAtCrosshair)
    {
        if (multiAimBody != null) multiAimBody.weight = aimBodyWeight;
        if (multiAimHead != null) multiAimHead.weight = aimHeadWeight;

        if (aimRigTarget == null || characterModel == null) return;

        float goal = aimAtCrosshair ? 1f : 0f;
        _aimRigAimBlend = aimRigBlendSpeed <= 0f
            ? goal
            : Mathf.MoveTowards(_aimRigAimBlend, goal, aimRigBlendSpeed * Time.deltaTime);

        // 以脊椎（Multi-Aim Body 控制的骨頭）為原點，沒有的話退回角色模型
        Transform chest = (multiAimBody != null && multiAimBody.data.constrainedObject != null)
            ? multiAimBody.data.constrainedObject
            : characterModel;
        Vector3 origin = chest.position;

        // 角色正前方（只取水平，避免近戰俯仰影響）
        Vector3 faceDir = characterModel.forward;
        faceDir.y = 0f;
        if (faceDir.sqrMagnitude < 0.0001f) faceDir = transform.forward;
        faceDir.Normalize();

        // 準星方向
        Vector3 aimDir = faceDir;
        Transform aimingPoint = PlayerAiming.Instance != null ? PlayerAiming.Instance.aimingPoint : null;
        if (aimingPoint != null)
        {
            Vector3 d = aimingPoint.position - origin;
            if (d.sqrMagnitude > 0.0001f) aimDir = d.normalized;
        }

        // 內插方向而不是位置：準星可能在 100 公尺外，直接內插位置會繞出奇怪的弧線
        Vector3 dir = Vector3.Slerp(faceDir, aimDir, _aimRigAimBlend);
        aimRigTarget.position = origin + dir * aimRigTargetDistance;
    }

    private void RotateCharacter()
    {
        // 監控 lockOn 狀態變化：剛失去 lockOn 時啟動保留
        bool lockOnNow = (PlayerAiming.Instance != null && PlayerAiming.Instance.lockOn);
        if (_prevLockOn && !lockOnNow)
        {
            StartAimHold(aimHoldAfterLockLost);
        }
        _prevLockOn = lockOnNow;

        // 先處理角色朝向（攻擊優先，其次 lockOn，其次移動）
        Vector3? desiredForward = null;
        bool aimRigWanted = true;


        if (_externalFacing.HasValue)
        {
            // 外部指定的朝向（WeaponBalanceBot 自動測試用），優先於一切
            desiredForward = _externalFacing.Value;
        }
        else if (attackFacingActive)
        {
            desiredForward = attackDesiredForward;
        }
        else if (_assaultBoosting)
        {
            // 突擊推進中面向推進方向（畫面中央的水平方向）。
            // 放在 lockOn 之前：輔助瞄準時 lockOn 也是 true，否則身體會轉去看敵人、跟飛行方向對不上。
            // 開火時仍由上面的 attackFacingActive 轉向目標。
            desiredForward = _abFacing;
        }
        else if (PlayerAiming.Instance.lockOn)
        {
            Vector3 lookDirection = PlayerAiming.Instance.aimingPoint.position - characterModel.position;
            lookDirection.y = 0f;
            if (lookDirection.sqrMagnitude > 0.001f)
                desiredForward = lookDirection.normalized;
        }
        // 新增：剛失去鎖定 or 剛單發射擊後，短時間內仍面向「準星」方向（允許蟹行）
        else if (Time.time < _aimHoldUntil)
        {
            if (TryGetAimForwardFlat(out var aimFwd))
            {
                desiredForward = aimFwd;
                _lastAimHoldForward = aimFwd; // 記住一份 fallback
            }
            else if (_lastAimHoldForward.sqrMagnitude > 0.001f)
            {
                desiredForward = _lastAimHoldForward;
            }
        }
        else
        {
            if (moveDirection.sqrMagnitude > 0.001f)
            {
                Vector3 faceDirection = moveDirection.normalized;
                desiredForward = new Vector3(faceDirection.x, 0, faceDirection.z).normalized;
                // 未瞄準時：只有朝攝影機方向（向前）移動才繼續看準星；
                // 向左、右、後退移動時，身體和頭改看角色自己的正前方。
                Vector3 cameraForward = characterOrientation.forward;
                cameraForward.y = 0f;
                if (cameraForward.sqrMagnitude > 0.0001f)
                {
                    float angle = Vector3.Angle(desiredForward.Value, cameraForward.normalized);
                    aimRigWanted = angle <= aimRigForwardAngle;
                }
            }
        }

        UpdateAimRig(aimRigWanted);

        // 俯仰角：只在近戰攻擊期間朝目標抬頭 / 低頭，其餘時間平滑回 0。
        // 注意膠囊碰撞體不會跟著轉 —— 視覺斜著、碰撞維持直立是標準作法。
        UpdateMeleePitch();

        if (desiredForward.HasValue)
        {
            // 用 Quaternion 而不是直接寫 forward：forward 只能表達方向，
            // 要把 pitch 疊在 yaw 之上必須走旋轉組合。
            Quaternion yawRot = Quaternion.LookRotation(desiredForward.Value, Vector3.up);
            Quaternion targetRot = yawRot * Quaternion.Euler(_meleePitchCurrent, 0f, 0f);

            characterModel.rotation = Quaternion.Slerp(
                characterModel.rotation,
                targetRot,
                Time.deltaTime * attackRotateSpeed
            );
        }
        else if (Mathf.Abs(_meleePitchCurrent) > 0.01f)
        {
            // 沒有轉向需求但 pitch 還沒歸零時，維持當前 yaw 只轉 pitch
            Vector3 flatForward = characterModel.forward;
            flatForward.y = 0f;

            if (flatForward.sqrMagnitude > 0.001f)
            {
                Quaternion yawRot = Quaternion.LookRotation(flatForward.normalized, Vector3.up);
                Quaternion targetRot = yawRot * Quaternion.Euler(_meleePitchCurrent, 0f, 0f);

                characterModel.rotation = Quaternion.Slerp(
                    characterModel.rotation,
                    targetRot,
                    Time.deltaTime * attackRotateSpeed
                );
            }
        }
        // 把世界空間的 moveDirection 轉成「角色本地」方向
        Vector3 localMove = Vector3.zero;
        if (moveDirection.sqrMagnitude > 0.001f)
        {
            // InverseTransformDirection：世界 → 角色本地
            localMove = characterModel.InverseTransformDirection(moveDirection);
            localMove.y = 0f;
            localMove.Normalize();
        }

        // 突擊推進中：沒按方向鍵也在往前衝，移動動畫改用推進方向
        if (_assaultBoosting)
        {
            localMove = characterModel.InverseTransformDirection(_abAnimDir);
            localMove.y = 0f;
            if (localMove.sqrMagnitude > 0.0001f) localMove.Normalize();
        }

        float targetX = localMove.x; // 左右
        float targetY = localMove.z; // 前後

        float targetMagnitude = Mathf.Clamp01(new Vector2(targetX, targetY).magnitude);
        if (targetMagnitude < 0.001f)
        {
            targetX = 0f;
            targetY = 0f;
        }

        animX = Mathf.MoveTowards(animX, targetX, movementBlendSpeed * Time.deltaTime);
        animY = Mathf.MoveTowards(animY, targetY, movementBlendSpeed * Time.deltaTime);

        playerAnimation.SetMovementParameters(animX, animY);
    }
    private void StartAimHold(float seconds)
    {
        if (seconds <= 0f) return;
        _aimHoldUntil = Mathf.Max(_aimHoldUntil, Time.time + seconds);
    }

    private bool TryGetAimForwardFlat(out Vector3 flatForward)
    {
        flatForward = Vector3.zero;

        if (PlayerAiming.Instance == null || characterModel == null) return false;

        Vector3 targetPoint;

        if (PlayerAiming.Instance.lockOn && PlayerAiming.Instance.aimingPoint != null)
            targetPoint = PlayerAiming.Instance.aimingPoint.position;
        else
            targetPoint = PlayerAiming.Instance.GetRay().GetPoint(attackAimRayDistance);

        Vector3 dir = targetPoint - characterModel.position;
        dir.y = 0f;

        if (dir.sqrMagnitude <= 0.001f) return false;

        flatForward = dir.normalized;
        return true;
    }
    // ---- Thruster VFX state (read-only) ----
    public bool IsGrounded => grounded;

    public bool IsFlyingActive => !grounded && Time.time <= flyRequestUntil;

    public bool IsDashActive => Time.time <= dashActiveUntil;
    public bool IsDashAttackBlocked => Time.time <= dashAttackLockUntil;

    // 玩家相對「腳下平台」的水平速度。沒站平台 => 等於世界速度；站在移動的船上不動 => ≈0

    public float HorizontalSpeedRelativeToPlatform
    {
        get
        {
            if (playerRigidbody == null) return 0f;
            Vector3 vRel = playerRigidbody.linearVelocity - GetMobilePlatformVelocity();
            vRel.y = 0f;
            return vRel.magnitude;
        }
    }

    public float VerticalVelocity
    {
        get
        {
            if (playerRigidbody == null) return 0f;
            return playerRigidbody.linearVelocity.y;
        }
    }

    // Mobile Platform carry (velocity-based)
    private bool _onMobilePlatform = false;
    private Rigidbody _mobilePlatformRb = null;
    private Transform _mobilePlatformTf;
    private Quaternion _mobilePlatformLastRot;
    private bool _mobilePlatformRotInit;
    // 對外公開：玩家目前站著的移動平台（沒站平台 = null）。給殘影 parent 用。
    public Transform CurrentPlatform => _onMobilePlatform ? _mobilePlatformTf : null;
    // ── 在不在船上：改讀 ShipPassenger（往下射線），跟敵人共用同一套判斷 ──
    //
    // 舊版用 OnTriggerStay / OnTriggerExit 偵測 "Mobile Platform Hitbox"：
    //   - 船身有多個 hitbox 時，離開其中一個就會被判定下船
    //   - hitbox 被銷毀時收不到 Exit，狀態可能永遠卡住
    //   - 每個 physics step 都重複呼叫 SetSimulationSpaceLandship / SetOnShip / SetPlatform
    // 現在只在「上船 / 下船 / 換船」那一刻各呼叫一次。
    [Header("Mobile Platform")]
    [Tooltip("留空會自動在自己身上找；找不到會在執行時自動加一個（用預設設定）。")]
    [SerializeField] private ShipPassenger shipPassenger;

    private ShipPassenger Passenger
    {
        get
        {
            if (shipPassenger == null)
            {
                shipPassenger = GetComponent<ShipPassenger>();
                if (shipPassenger == null) shipPassenger = gameObject.AddComponent<ShipPassenger>();
            }
            return shipPassenger;
        }
    }

    /// <summary>每個 FixedUpdate 開頭呼叫：狀態有變才觸發上船 / 下船的副作用。</summary>
    private void SyncMobilePlatform()
    {
        ShipPassenger p = Passenger;
        Rigidbody rb = (p != null && p.isOnShip) ? p.PlatformRigidbody : null;

        if (rb == _mobilePlatformRb) return;   // 沒變（包含一直都不在船上）

        if (rb != null) EnterMobilePlatform(rb);
        else ExitMobilePlatform();
    }

    private void EnterMobilePlatform(Rigidbody rb)
    {
        _mobilePlatformRb = rb;
        _mobilePlatformTf = rb.transform;
        _mobilePlatformLastRot = rb.rotation;
        _mobilePlatformRotInit = true;
        _onMobilePlatform = true;

        if (Effects != null) Effects.SetSimulationSpaceLandship();
        attackManager.SetOnShip(rb);

        if (PlayerAiming.Instance != null)
            PlayerAiming.Instance.SetPlatform(_mobilePlatformTf);   // 讓鏡頭跟船的朝向
    }

    private void ExitMobilePlatform()
    {
        _onMobilePlatform = false;
        _mobilePlatformRb = null;
        _mobilePlatformTf = null;
        _mobilePlatformRotInit = false;
        if (Effects != null) Effects.SetSimulationSpaceWorld();
        attackManager.SetOffShip();

        if (PlayerAiming.Instance != null)
            PlayerAiming.Instance.SetPlatform(null);
    }

    private Vector3 GetMobilePlatformVelocity()
    {
        if (!_onMobilePlatform || _mobilePlatformRb == null) return Vector3.zero;
        return _mobilePlatformRb.linearVelocity; // Unity 6
    }
    private void CarryWithPlatformRotation(float dt)
    {
        if (!_onMobilePlatform || _mobilePlatformRb == null || _mobilePlatformTf == null) return;
        if (dt <= 0f) return;

        Quaternion current = _mobilePlatformRb.rotation;

        if (!_mobilePlatformRotInit)
        {
            _mobilePlatformLastRot = current;
            _mobilePlatformRotInit = true;
            return;
        }

        Quaternion delta = current * Quaternion.Inverse(_mobilePlatformLastRot);

        // 讓玩家的位置繞著平台 pivot 旋轉
        Vector3 pivot = _mobilePlatformTf.position;
        Vector3 p = playerRigidbody.position;
        Vector3 newP = pivot + (delta * (p - pivot));
        playerRigidbody.MovePosition(newP);

        // 讓角色模型也吃到同一個 yaw 旋轉
        // 這一步可以避開你 RotateCharacter() 對模型朝向的覆寫感
        Vector3 axis; float angle;
        delta.ToAngleAxis(out angle, out axis);
        if (axis.sqrMagnitude > 0.000001f)
        {
            axis.Normalize();
            float yaw = Vector3.Dot(axis, Vector3.up) * angle;
            if (Mathf.Abs(yaw) > 0.0001f)
            {
                characterModel.Rotate(0f, yaw, 0f, Space.World);

            }
        }

        _mobilePlatformLastRot = current;
    }
    private void HandleDustEffect()
    {
        // 用相對平台速度：站在移動的船上不動 => ≈0 => 不噴
        float speed = HorizontalSpeedRelativeToPlatform;

        if (!grounded || speed < dustMinSpeed) return;

        if (Time.time >= _nextDustTime)
        {
            float t = Mathf.InverseLerp(dustMinSpeed, dustMaxSpeed, speed); // 0~1，自動 clamp
            float interval = Mathf.Lerp(dustIntervalSlow, dustIntervalFast, t);
            _nextDustTime = Time.time + interval;
            if (!_onMobilePlatform)
            {
                if (Effects != null) Effects.DustEffect();
            }
            else
            {
                if (Effects != null) Effects.DustEffect_OnShip();
            }
        }
    }
}