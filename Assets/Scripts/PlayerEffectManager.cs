using System.Collections;
using MoreMountains.Feedbacks;
using UnityEngine;

/// <summary>
/// 玩家的特效（MMF 回饋、粒子、推進器火焰）。從 PlayerAnimation 分離出來 —— PlayerAnimation 只管 Animator。
///
/// ★ 必須跟 PlayerAnimation 掛在同一個物件（有 Animator 的那個）上：
///   · 攻擊動畫的 Animation Event（AnimEvent_SwordSwing、AnimEvent_Reload、AnimEvent_MeleeImpact、
///     PlayWalkFeedback、DustEffect…）只會呼叫 Animator 所在物件上的方法。
///     這些方法名稱維持原樣搬過來，動畫片段裡的事件不用改。
///   · OnCollisionEnter 也要在同一個物件上才收得到，跟原本的行為一致。
///
/// 推進器火焰：這裡只負責「依裝備生成 / 清除 / 擺位置」；
/// 什麼時候開哪一種火焰由火焰 prefab 上的 FlameEffect 決定。
/// </summary>
[DisallowMultipleComponent]
public class PlayerEffectManager : MonoBehaviour
{
    [Header("References")]
    [Tooltip("用來判斷撞擊速度。留空會往父物件找 Rigidbody。")]
    [SerializeField] private Rigidbody playerRigidbody;

    [Header("MMF Feedbacks")]
    public MMF_Player leftAttackFeedback;    // 左手遠程武器開火（槍口）
    public MMF_Player rightAttackFeedback;   // 右手遠程武器開火（槍口）
    public MMF_Player leftShoulderAttackFeedback;    // 左肩武器開火（槍口發光 + 音效）
    public MMF_Player rightShoulderAttackFeedback;   // 右肩武器開火（槍口發光 + 音效）
    public MMF_Player meleeAttackFeedback;   // 近戰命中 / 高速撞到敵人
    public MMF_Player swordSwingFeedback;    // 揮刀
    public MMF_Player reloadFeedback;        // 換彈
    public MMF_Player walkFeedback;          // 走路
    public MMF_Player dustFeedback;          // 地面塵土
    public MMF_Player dustFeedback_OnShip;   // 船上塵土

    [Tooltip("被支援艙命中（換裝）時的全身閃光。用 MMF_Property 動畫 EntityHitFlash.FlashAmount（0 → 1 → 0），\n" +
             "跟敵人的 Hit Flash Feedback 同一種做法。Bradamant 根物件上要有 EntityHitFlash（勾 Dynamic Renderers）。")]
    public MMF_Player supplyHitFlashFeedback;

    [Header("Thruster Sound（突擊推進 / Dash）")]
    [Tooltip("突擊推進開始的一次性回饋（例如 SFX_Thruster_Ignite）。")]
    public MMF_Player assaultBoostStartFeedback;
    [Tooltip("突擊推進期間的循環回饋（例如 SFX_Thruster_Boost_Loop，音效要勾 Loop）。\n" +
             "推進開始時 PlayFeedbacks，結束時 StopFeedbacks。\n" +
             "用 MMF_MMSoundManagerSound 的話，要勾 Stop Sound On Feedback Stop，StopFeedbacks 才會停掉聲音。")]
    public MMF_Player assaultBoostLoopFeedback;
    [Tooltip("突擊推進結束的一次性回饋（例如 SFX_Thruster_Stop）。")]
    public MMF_Player assaultBoostStopFeedback;
    [Tooltip("Dash 開始的一次性回饋。")]
    public MMF_Player dashFeedback;
    [Tooltip("向上飛行開始的一次性回饋（可留空）。")]
    public MMF_Player flyStartFeedback;
    [Tooltip("向上飛行期間的循環回饋（例如 SFX_Thruster_Loop，要設成循環）。開始時 PlayFeedbacks，結束時 StopFeedbacks。\n" +
             "突擊推進期間不播（突擊推進有自己的循環聲）。")]
    public MMF_Player flyLoopFeedback;
    [Tooltip("向上飛行結束的一次性回饋（可留空）。")]
    public MMF_Player flyStopFeedback;
    [Tooltip("放開飛行後再等這麼久才算結束（秒）。點按飛行鍵時聲音不會一直開開關關。")]
    [SerializeField, Min(0f)] private float flyStopDelay = 0.15f;
    [Tooltip("用來讀取突擊推進 / Dash 狀態。留空會往父物件找。")]
    [SerializeField] private PlayerMovement playerMovement;

    private bool _wasAssaultBoosting;
    private bool _wasDashing;
    private bool _flySoundOn;
    private float _flyLastActiveTime = float.NegativeInfinity;

    [Header("Particles（上下船時切換模擬空間）")]
    [SerializeField] private ParticleSystem[] leftMuzzle;
    [SerializeField] private ParticleSystem[] rightMuzzle;
    [SerializeField] private ParticleSystem[] leftShoulderMuzzle;
    [SerializeField] private ParticleSystem[] rightShoulderMuzzle;

    [Header("Shoulder Muzzle")]
    [Tooltip("開火前把肩武器的 MMF_Player 物件移到當下肩武器的槍口（位置 + 朝向）。\n" +
             "肩武器可以換，槍口位置會變，所以發光粒子要放在 MMF_Player 物件底下，跟著它一起移動。\n" +
             "關掉 = MMF_Player 留在原位（例如你自己把它掛在固定的肩部掛點上）。")]
    [SerializeField] private bool moveShoulderFeedbackToMuzzle = true;
    [SerializeField] private ParticleSystem dustParticle;
    [SerializeField] private Transform landshipTransform;

    [Header("Thruster Flame")]
    public Transform thrusterFlamePointL;
    public Transform thrusterFlamePointR;

    [Header("Collision Feedback")]
    [Tooltip("撞到敵人時，速度超過這個值（m/s）才播 meleeAttackFeedback。")]
    [SerializeField] private float collisionFeedbackMinSpeed = 8f;

    // 依目前裝備的推進器生成的火焰實例（FlameEffect 負責開關）
    public GameObject normalThrusterFlameL { get; private set; }
    public GameObject normalThrusterFlameR { get; private set; }
    public GameObject boostedThrusterFlameL { get; private set; }
    public GameObject boostedThrusterFlameR { get; private set; }
    public GameObject meleeThrusterFlameL { get; private set; }
    public GameObject meleeThrusterFlameR { get; private set; }

    private Coroutine _initRoutine;
    private bool _subscribed;

    // ═══════════════════════ 初始化 ═══════════════════════

    private void Awake()
    {
        if (playerRigidbody == null) playerRigidbody = GetComponentInParent<Rigidbody>();
        if (playerMovement == null) playerMovement = GetComponentInParent<PlayerMovement>();
    }

    private void OnEnable()
    {
        _initRoutine = StartCoroutine(InitWhenReady());
    }

    private IEnumerator InitWhenReady()
    {
        while (PlayerStats.Instance == null)
            yield return null;

        PlayerStats.Instance.OnThrusterVisualChanged += ApplyThrusterVfxChange;
        PlayerStats.Instance.OnThrusterFlameOffsetChanged += ApplyThrusterFlameTransformChange;
        _subscribed = true;

        // 初始同步（推進器的火焰 + 噴口位移）
        ApplyThrusterVfxChange(PlayerStats.Instance.CurrentThruster);
        ApplyThrusterFlameTransformChange(PlayerStats.Instance.CurrentThrusterFlameOffset);
    }

    private void OnDisable()
    {
        if (_initRoutine != null) StopCoroutine(_initRoutine);

        if (_subscribed && PlayerStats.Instance != null)
        {
            PlayerStats.Instance.OnThrusterVisualChanged -= ApplyThrusterVfxChange;
            PlayerStats.Instance.OnThrusterFlameOffsetChanged -= ApplyThrusterFlameTransformChange;
        }
        _subscribed = false;

        // 停用時正在推進 → 循環音效不要留著一直響
        if (_wasAssaultBoosting) assaultBoostLoopFeedback?.StopFeedbacks();
        if (_flySoundOn) flyLoopFeedback?.StopFeedbacks();
        _wasAssaultBoosting = false;
        _wasDashing = false;
        _flySoundOn = false;
    }

    // ═══════════════════════ 推進器音效（突擊推進 / Dash）═══════════════════════

    // 每幀比較狀態，只在「開始 / 結束」的那一幀觸發（跟 FlameEffect 的做法一樣）
    private void Update()
    {
        if (playerMovement == null) return;

        bool ab = playerMovement.IsAssaultBoosting;
        if (ab != _wasAssaultBoosting)
        {
            if (ab)
            {
                assaultBoostStartFeedback?.PlayFeedbacks(transform.position);
                assaultBoostLoopFeedback?.PlayFeedbacks(transform.position);
            }
            else
            {
                assaultBoostLoopFeedback?.StopFeedbacks();
                assaultBoostStopFeedback?.PlayFeedbacks(transform.position);
            }
            _wasAssaultBoosting = ab;
        }

        // 向上飛行：突擊推進優先（跟 FlameEffect 的優先順序一致），放開後延遲 flyStopDelay 才關
        if (playerMovement.IsFlyingActive && !ab) _flyLastActiveTime = Time.time;
        bool fly = !ab && Time.time - _flyLastActiveTime <= flyStopDelay;
        if (fly != _flySoundOn)
        {
            if (fly)
            {
                flyStartFeedback?.PlayFeedbacks(transform.position);
                flyLoopFeedback?.PlayFeedbacks(transform.position);
            }
            else
            {
                flyLoopFeedback?.StopFeedbacks();
                if (!ab) flyStopFeedback?.PlayFeedbacks(transform.position);   // 轉成突擊推進時不播關閉聲（點火聲接手）
            }
            _flySoundOn = fly;
        }

        // 只有一般 Dash 播推進音效；近戰突進不播（太吵）
        bool dash = playerMovement.IsDashActive;
        if (dash && !_wasDashing)
            dashFeedback?.PlayFeedbacks(transform.position);
        _wasDashing = dash;
    }

    // ═══════════════════════ 遠程武器 ═══════════════════════

    /// <summary>左手武器開火的槍口回饋。開槍動畫由 PlayerAnimation.TriggerFireLeft() 另外觸發。</summary>
    public void PlayLeftMuzzleFeedback()
    {
        leftAttackFeedback?.PlayFeedbacks(transform.position);
    }

    /// <summary>右手武器開火的槍口回饋。開槍動畫由 PlayerAnimation.TriggerFireRight() 另外觸發。</summary>
    public void PlayRightMuzzleFeedback()
    {
        rightAttackFeedback?.PlayFeedbacks(transform.position);
    }

    /// <summary>左肩武器開火的槍口回饋。開火動畫由 PlayerAnimation.FireShoulderAndWait 另外觸發。</summary>
    public void PlayLeftShoulderMuzzleFeedback(Transform muzzle)
    {
        PlayShoulderMuzzle(leftShoulderAttackFeedback, muzzle);
    }

    /// <summary>右肩武器開火的槍口回饋。</summary>
    public void PlayRightShoulderMuzzleFeedback(Transform muzzle)
    {
        PlayShoulderMuzzle(rightShoulderAttackFeedback, muzzle);
    }

    private void PlayShoulderMuzzle(MMF_Player feedback, Transform muzzle)
    {
        if (feedback == null) return;
        if (moveShoulderFeedbackToMuzzle && muzzle != null)
            feedback.transform.SetPositionAndRotation(muzzle.position, muzzle.rotation);
        feedback.PlayFeedbacks(muzzle != null ? muzzle.position : transform.position);
    }

    // ═══════════════════════ 換裝 ═══════════════════════

    /// <summary>被支援艙命中時的全身閃光。由 SupplyBurst 在換上新套裝之後呼叫（新裝甲才會一起閃）。</summary>
    public void PlaySupplyHitFlash()
    {
        if (supplyHitFlashFeedback != null)
            supplyHitFlashFeedback.PlayFeedbacks(transform.position);
    }

    // ═══════════════════════ 近戰 ═══════════════════════

    /// <summary>
    /// 近戰命中的打擊回饋。由 MeleeAttackController 在 hitbox 實際造成傷害時呼叫，
    /// 所以只有真的打到才會播 —— 揮空不會。
    /// </summary>
    public void PlayMeleeHitFeedback(Vector3 position)
    {
        meleeAttackFeedback?.PlayFeedbacks(position);
    }

    // Animation Event（名稱不能改，動畫片段裡是用名稱呼叫的）
    public void AnimEvent_MeleeImpact()
    {
        PlayMeleeHitFeedback(transform.position);
    }

    public void AnimEvent_SwordSwing()
    {
        swordSwingFeedback?.PlayFeedbacks(transform.position);
    }

    public void AnimEvent_Reload()
    {
        reloadFeedback?.PlayFeedbacks(transform.position);
    }

    /// <summary>高速撞到敵人時播命中回饋。</summary>
    private void OnCollisionEnter(Collision collision)
    {
        if (meleeAttackFeedback == null || playerRigidbody == null) return;
        if (!collision.gameObject.CompareTag("Enemy")) return;
        if (playerRigidbody.linearVelocity.magnitude <= collisionFeedbackMinSpeed) return;

        meleeAttackFeedback.PlayFeedbacks(transform.position);
    }

    // ═══════════════════════ 走路 / 塵土 ═══════════════════════

    [Header("Footstep")]
    [Tooltip("Blend Tree 混合時，每個片段的動畫事件都會觸發。只採用權重至少這麼高的片段的事件，\n" +
             "同一步才不會因為走路 / 跑步片段的落地時間略有差異而響兩次。")]
    [SerializeField, Range(0f, 1f)] private float footstepMinClipWeight = 0.5f;
    [Tooltip("兩次腳步聲之間的最短間隔（秒）。權重剛好各半、或兩個片段的事件只差幾幀時的保險。\n" +
             "要比最快的步伐間隔短（跑步一步大約 0.25–0.35 秒），否則會吃掉真正的腳步。")]
    [SerializeField, Min(0f)] private float footstepMinInterval = 0.12f;
    private float _lastFootstepTime = float.NegativeInfinity;

    // Animation Event 也可能呼叫這幾個（名稱維持原樣）
    // 參數改成 AnimationEvent：Unity 會自動把事件資訊傳進來，動畫片段裡的事件設定不用改。
    public void PlayWalkFeedback(AnimationEvent e)
    {
        if (e != null && e.animatorClipInfo.clip != null && e.animatorClipInfo.weight < footstepMinClipWeight) return;
        if (Time.time - _lastFootstepTime < footstepMinInterval) return;
        _lastFootstepTime = Time.time;
        walkFeedback?.PlayFeedbacks(transform.position);
    }

    public void StopWalkFeedback()
    {
        walkFeedback?.StopFeedbacks();
    }

    public void DustEffect()
    {
        dustFeedback?.PlayFeedbacks(transform.position);
    }

    public void DustEffect_OnShip()
    {
        dustFeedback_OnShip?.PlayFeedbacks(transform.position);
    }

    public void StopDustEffect()
    {
        dustFeedback?.StopFeedbacks();
        dustFeedback_OnShip?.StopFeedbacks();
    }

    // ═══════════════════════ 上下船：粒子的模擬空間 ═══════════════════════

    /// <summary>上船：塵土與槍口粒子改用船的座標模擬，跟著船走。</summary>
    public void SetSimulationSpaceLandship()
    {
        if (dustParticle == null) return;
        var main = dustParticle.main;
        main.simulationSpace = ParticleSystemSimulationSpace.Custom;
        main.customSimulationSpace = landshipTransform;

        SetMuzzleSimulationSpace(leftMuzzle, ParticleSystemSimulationSpace.Custom);
        SetMuzzleSimulationSpace(rightMuzzle, ParticleSystemSimulationSpace.Custom);
        SetMuzzleSimulationSpace(leftShoulderMuzzle, ParticleSystemSimulationSpace.Custom);
        SetMuzzleSimulationSpace(rightShoulderMuzzle, ParticleSystemSimulationSpace.Custom);
    }

    /// <summary>下船：改回世界座標模擬。</summary>
    public void SetSimulationSpaceWorld()
    {
        if (dustParticle == null) return;
        var main = dustParticle.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        SetMuzzleSimulationSpace(leftMuzzle, ParticleSystemSimulationSpace.World);
        SetMuzzleSimulationSpace(rightMuzzle, ParticleSystemSimulationSpace.World);
        SetMuzzleSimulationSpace(leftShoulderMuzzle, ParticleSystemSimulationSpace.World);
        SetMuzzleSimulationSpace(rightShoulderMuzzle, ParticleSystemSimulationSpace.World);
    }

    private void SetMuzzleSimulationSpace(ParticleSystem[] systems, ParticleSystemSimulationSpace space)
    {
        if (systems == null) return;
        for (int i = 0; i < systems.Length; i++)
        {
            if (systems[i] == null) continue;
            var main = systems[i].main;
            main.simulationSpace = space;
            if (space == ParticleSystemSimulationSpace.Custom)
                main.customSimulationSpace = landshipTransform;
        }
    }

    // ═══════════════════════ 推進器火焰 ═══════════════════════

    /// <summary>噴口位移。右邊的 X 軸鏡像。</summary>
    private void ApplyThrusterFlameTransformChange(Vector3 offset)
    {
        if (thrusterFlamePointL != null) thrusterFlamePointL.localPosition = offset;
        if (thrusterFlamePointR != null)
        {
            offset.x = -offset.x;   // 右邊的 X 軸鏡像
            thrusterFlamePointR.localPosition = offset;
        }
    }

    /// <summary>換推進器：清掉舊火焰，依新推進器生成 Normal / Boosted / Melee 三種火焰（開關交給 FlameEffect）。</summary>
    private void ApplyThrusterVfxChange(Thruster thr)
    {
        // 1) 先清掉舊的（避免重複堆疊）
        ClearThrusterVfxInstances();
        // 2) 沒裝 thruster（或卸下）→ 清空後直接結束
        if (thr == null) return;

        normalThrusterFlameL = SpawnFlame(thr.normalThrusterFlame, thrusterFlamePointL);
        normalThrusterFlameR = SpawnFlame(thr.normalThrusterFlame, thrusterFlamePointR);
        boostedThrusterFlameL = SpawnFlame(thr.boostedThrusterFlame, thrusterFlamePointL);
        boostedThrusterFlameR = SpawnFlame(thr.boostedThrusterFlame, thrusterFlamePointR);
        meleeThrusterFlameL = SpawnFlame(thr.meleeThrusterFlame, thrusterFlamePointL);
        meleeThrusterFlameR = SpawnFlame(thr.meleeThrusterFlame, thrusterFlamePointR);
    }

    private static GameObject SpawnFlame(GameObject prefab, Transform point)
    {
        if (prefab == null || point == null) return null;
        return Instantiate(prefab, point, false);
    }

    private void ClearThrusterVfxInstances()
    {
        DestroyIfAny(normalThrusterFlameL);
        DestroyIfAny(normalThrusterFlameR);
        DestroyIfAny(boostedThrusterFlameL);
        DestroyIfAny(boostedThrusterFlameR);
        DestroyIfAny(meleeThrusterFlameL);
        DestroyIfAny(meleeThrusterFlameR);

        normalThrusterFlameL = null;
        normalThrusterFlameR = null;
        boostedThrusterFlameL = null;
        boostedThrusterFlameR = null;
        meleeThrusterFlameL = null;
        meleeThrusterFlameR = null;
    }

    private static void DestroyIfAny(GameObject go)
    {
        if (go != null) Destroy(go);
    }
}
