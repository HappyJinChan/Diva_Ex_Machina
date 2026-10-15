using UnityEngine;
using Unity.Cinemachine;

/// <summary>
/// 近戰突進時的 FOV 收縮。掛在 CinemachineCamera 的 GameObject 上即可。
/// 另外也負責突擊推進（Assault Boost）的 FOV 拉寬與持續細微震動（Cinemachine Noise）。
///
/// 設計要點：
/// 1) 讀 PlayerMovement.IsMeleeDashing 這個「狀態」，而不是掛 AnimEvent。
///    突進有五種結束路徑（Brake / StepEnd / NextComboStep / DashCancel / ReachedStopDistance），
///    但全部匯流到 StopMeleeDashInternal 的 _meleeDashing = false。
///    跟著狀態走，FOV 就不可能卡在縮小狀態回不來。
///    ——特別是 OnBrake() 有 `if (!_attacking) return;`，Dash 取消時那個 event 根本不會執行。
///
/// 2) 寫 vcam.Lens.FieldOfView，不寫 Camera.main.fieldOfView。
///    後者每幀都會被 CinemachineBrain 覆蓋掉。
///
/// 3) 用「目標值 + 指數阻尼」而不是 Coroutine。
///    連段中 DashStart/Brake 會密集重觸發，阻尼寫法天然可重入，沒有狀態要清。
/// </summary>
[DisallowMultipleComponent]
public class MeleeDashFovKick : MonoBehaviour
{
    [Header("References")]
    [Tooltip("留空會自動抓同一個 GameObject 上的 CinemachineCamera。")]
    [SerializeField] private CinemachineCamera virtualCamera;

    [Tooltip("留空會自動從 PlayerController.Instance 身上找。")]
    [SerializeField] private PlayerMovement playerMovement;

    [Header("Kick")]
    [Tooltip("突進時的 FOV 偏移量。負值 = 縮小視野（拉近、速度感）；正值 = 廣角（衝刺感）。")]
    [SerializeField] private float fovDelta = -10f;

    [Tooltip("收縮的時間常數（秒）。越小越急促。0.04~0.08 打擊感最好。")]
    [SerializeField] private float attackTime = 0.06f;

    [Tooltip("回復的時間常數（秒）。刻意比收縮慢，才會有殘留的餘韻。")]
    [SerializeField] private float releaseTime = 0.25f;

    [Header("Assault Boost")]
    [Tooltip("突擊推進也套用鏡頭效果（FOV 拉寬 + 持續震動）。")]
    [SerializeField] private bool includeAssaultBoost = true;

    [Tooltip("突擊推進時的 FOV 偏移量。正值 = 廣角，畫面邊緣往外拉，速度感來源。跟近戰突進的偏移相加。")]
    [SerializeField] private float assaultBoostFovDelta = 12f;

    [Tooltip("進入突擊推進效果的時間常數（秒）。越小越猛。")]
    [SerializeField] private float assaultBoostAttackTime = 0.1f;

    [Tooltip("結束後回復的時間常數（秒）。")]
    [SerializeField] private float assaultBoostReleaseTime = 0.35f;

    [Header("Assault Boost Shake (Cinemachine Noise)")]
    [Tooltip("CinemachineCamera 上的 Basic Multi Channel Perlin。留空會自動抓同一個 GameObject 上的。\n" +
             "沒有這個元件、或它沒有指定 Noise Profile 時，就不會震。")]
    [SerializeField] private CinemachineBasicMultiChannelPerlin noise;

    [Tooltip("突擊推進時「加上去」的震幅（Amplitude Gain）。持續的細微抖動，建議 0.3～1。0 = 不震。")]
    [SerializeField] private float assaultBoostNoiseAmplitude = 0.5f;

    [Tooltip("突擊推進時的震動頻率（Frequency Gain）。越大越像高頻的機體震動。0 = 維持原本的頻率。")]
    [SerializeField] private float assaultBoostNoiseFrequency = 3f;

    [Header("Safety")]
    [Tooltip("保險絲：突進狀態持續超過這個秒數就強制回復。0 = 關閉。")]
    [SerializeField] private float maxHoldSeconds = 2f;

    [Tooltip("使用 unscaledDeltaTime。若命中時有 hitstop（時間凍結），關閉此項會讓 FOV 一起凍住 —— 通常那才是想要的效果。")]
    [SerializeField] private bool useUnscaledTime = false;

    // 突進中的每一幀都會被套用的偏移量
    private float _baseFov;
    private float _current;
    private float _holdTimer;

    // 突擊推進效果的權重 0～1（FOV 和震動共用同一條曲線）
    private float _abWeight;
    private float _baseNoiseAmplitude;
    private float _baseNoiseFrequency;

    private void Awake()
    {
        if (virtualCamera == null)
            virtualCamera = GetComponent<CinemachineCamera>();

        if (playerMovement == null && PlayerController.Instance != null)
            playerMovement = PlayerController.Instance.GetComponentInChildren<PlayerMovement>();

        if (virtualCamera != null)
            _baseFov = virtualCamera.Lens.FieldOfView;

        if (noise == null)
            noise = GetComponent<CinemachineBasicMultiChannelPerlin>();

        if (noise != null)
        {
            // 原本的 Noise 設定當基準，突擊推進的震動疊在上面，結束後回到原狀
            _baseNoiseAmplitude = noise.AmplitudeGain;
            _baseNoiseFrequency = noise.FrequencyGain;
            if (includeAssaultBoost && assaultBoostNoiseAmplitude > 0f && noise.NoiseProfile == null)
                Debug.LogWarning($"[MeleeDashFovKick] '{name}' 的 Basic Multi Channel Perlin 沒有指定 Noise Profile，突擊推進不會震。", this);
        }
        else if (includeAssaultBoost && assaultBoostNoiseAmplitude > 0f)
        {
            Debug.LogWarning($"[MeleeDashFovKick] '{name}' 上沒有 CinemachineBasicMultiChannelPerlin（Noise），突擊推進不會震。", this);
        }
    }

    private void Update()
    {
        if (virtualCamera == null || playerMovement == null) return;

        float dt = useUnscaledTime ? Time.unscaledDeltaTime : Time.deltaTime;

        bool dashing = playerMovement.IsMeleeDashing;

        // 保險絲。理論上不會觸發，但 FOV 卡住是玩家一定會發現的 bug，
        // 值得多一道防線。
        if (dashing)
        {
            _holdTimer += dt;
            if (maxHoldSeconds > 0f && _holdTimer > maxHoldSeconds)
                dashing = false;
        }
        else
        {
            _holdTimer = 0f;
        }

        float target = dashing ? fovDelta : 0f;
        float tau = dashing ? attackTime : releaseTime;

        // 幀率無關的指數逼近。用 Lerp(a,b,dt*k) 會隨幀率改變手感。
        _current = (tau <= 0.0001f)
            ? target
            : Mathf.Lerp(_current, target, 1f - Mathf.Exp(-dt / tau));

        // 收斂後直接吸附，避免永遠殘留 0.001 的偏移一直寫 Lens
        if (Mathf.Abs(_current - target) < 0.01f)
            _current = target;

        // 突擊推進：權重 0～1。踢擊期間 IsAssaultBoosting 也是 true，會一路維持到推進結束。
        // 突擊推進可能持續很久，所以不套用上面的保險絲。
        bool assaultBoost = includeAssaultBoost && playerMovement.IsAssaultBoosting;
        float abTarget = assaultBoost ? 1f : 0f;
        float abTau = assaultBoost ? assaultBoostAttackTime : assaultBoostReleaseTime;
        _abWeight = (abTau <= 0.0001f)
            ? abTarget
            : Mathf.Lerp(_abWeight, abTarget, 1f - Mathf.Exp(-dt / abTau));
        if (Mathf.Abs(_abWeight - abTarget) < 0.001f)
            _abWeight = abTarget;

        var lens = virtualCamera.Lens;
        lens.FieldOfView = Mathf.Clamp(_baseFov + _current + assaultBoostFovDelta * _abWeight, 1f, 179f);
        virtualCamera.Lens = lens;

        ApplyAssaultBoostNoise();
    }

    private void ApplyAssaultBoostNoise()
    {
        if (noise == null) return;

        noise.AmplitudeGain = _baseNoiseAmplitude + assaultBoostNoiseAmplitude * _abWeight;
        noise.FrequencyGain = (assaultBoostNoiseFrequency > 0f)
            ? Mathf.Lerp(_baseNoiseFrequency, assaultBoostNoiseFrequency, _abWeight)
            : _baseNoiseFrequency;
    }

    /// <summary>
    /// 若之後有其他系統（瞄準縮放、劇情鏡頭）要改基準 FOV，透過這裡改，
    /// 不要直接寫 vcam.Lens —— 會被這個元件每幀覆蓋掉。
    /// </summary>
    public void SetBaseFov(float fov) => _baseFov = fov;

    /// <summary>基準 FOV。給其他系統讀。</summary>
    public float BaseFov => _baseFov;
}
