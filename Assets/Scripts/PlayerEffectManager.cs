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
    public MMF_Player meleeAttackFeedback;   // 近戰命中 / 高速撞到敵人
    public MMF_Player swordSwingFeedback;    // 揮刀
    public MMF_Player reloadFeedback;        // 換彈
    public MMF_Player walkFeedback;          // 走路
    public MMF_Player dustFeedback;          // 地面塵土
    public MMF_Player dustFeedback_OnShip;   // 船上塵土

    [Header("Particles（上下船時切換模擬空間）")]
    [SerializeField] private ParticleSystem[] leftMuzzle;
    [SerializeField] private ParticleSystem[] rightMuzzle;
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

    // Animation Event 也可能呼叫這幾個（名稱維持原樣）
    public void PlayWalkFeedback()
    {
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
    }

    /// <summary>下船：改回世界座標模擬。</summary>
    public void SetSimulationSpaceWorld()
    {
        if (dustParticle == null) return;
        var main = dustParticle.main;
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        SetMuzzleSimulationSpace(leftMuzzle, ParticleSystemSimulationSpace.World);
        SetMuzzleSimulationSpace(rightMuzzle, ParticleSystemSimulationSpace.World);
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
