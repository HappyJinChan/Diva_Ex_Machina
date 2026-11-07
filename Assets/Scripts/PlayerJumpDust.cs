using UnityEngine;

/// <summary>
/// 玩家起跳 / 落地時在腳下噴塵土。掛在玩家（PlayerMovement 同一個物件）上。
///
///   起跳：PlayerMovement.JumpAction() 成功時呼叫 PlayJump()。
///   落地：這裡自己偵測「離地 → 著地」，依落地前的最大下落速度決定塵土大小。
///         從飛行、衝刺、高處落下都會觸發；走下小台階（離地很短或下落很慢）不會。
///
/// 顏色：往腳下打一條短射線，依地面自動取色（Terrain 圖層 / 材質顏色），
/// 物件上有 DustSurface 時以它為準。
///
/// 站在船上時塵土會跟著船移動（使用以船為參考的座標系），不會被甩在後面。
/// </summary>
[DisallowMultipleComponent]
public class PlayerJumpDust : MonoBehaviour
{
    [Header("Effect")]
    [Tooltip("Jump Dust Builder 產生的 prefab。")]
    [SerializeField] private JumpDustEffect dustPrefab;

    [Header("Jump")]
    [Tooltip("起跳塵土的強度（0～1）。")]
    [SerializeField, Range(0f, 1f)] private float jumpIntensity = 0.5f;

    [Header("Landing")]
    [Tooltip("落地前的下落速度低於這個值（m/s）就不噴。")]
    [SerializeField] private float minLandingSpeed = 5f;
    [Tooltip("下落速度達到這個值（m/s）時強度 = 1。")]
    [SerializeField] private float maxLandingSpeed = 30f;
    [Tooltip("剛好達到最低下落速度時的強度。")]
    [SerializeField, Range(0f, 1f)] private float minLandingIntensity = 0.25f;
    [Tooltip("離地時間短於這個值（秒）不算落地，避免走過小起伏時一直噴。")]
    [SerializeField] private float minAirTime = 0.15f;

    [Header("Ground Probe")]
    [Tooltip("會揚起塵土的層。預設是 Default、Ground、Mobile Platform、Defence Fortifications、Obstacle。")]
    [SerializeField] private LayerMask groundLayers;
    [Tooltip("船的層。踩在這些層上時，塵土會跟著船移動。")]
    [SerializeField] private LayerMask platformLayers;
    [Tooltip("射線起點在玩家位置上方多高（公尺）。")]
    [SerializeField] private float probeStartHeight = 0.5f;
    [Tooltip("從起點往下找地面的距離（公尺）。")]
    [SerializeField] private float probeDistance = 3f;

    [Header("Colour")]
    [Tooltip("取不到地面顏色時使用，也會跟取到的顏色混合。")]
    [SerializeField] private Color defaultDustColor = new Color(0.78f, 0.70f, 0.56f, 1f);
    [Tooltip("0 = 永遠用預設顏色，1 = 完全用地面顏色。")]
    [SerializeField, Range(0f, 1f)] private float groundColorInfluence = 0.85f;
    [Tooltip("塵土通常比地面灰、比地面淡。這裡往灰色靠多少。")]
    [SerializeField, Range(0f, 1f)] private float desaturate = 0.25f;
    [Tooltip("整體亮度倍率。揚起的塵土受光面積大，通常比地面亮一點。")]
    [SerializeField] private float brightness = 1.15f;

    public LayerMask GroundLayers => groundLayers;
    public LayerMask PlatformLayers => platformLayers;

    private PlayerMovement _movement;
    private Rigidbody _rb;

    private JumpDustEffect _groundInstance;     // 地面用（World 空間）
    private JumpDustEffect _platformInstance;   // 船上用（跟著船的座標系）

    private bool _wasGrounded = true;
    private float _airTime;
    private float _airMinVy;

    private void Reset()
    {
        groundLayers = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Defence Fortifications", "Obstacle");
        platformLayers = LayerMask.GetMask("Mobile Platform");
    }

    private void Awake()
    {
        _movement = GetComponentInParent<PlayerMovement>();
        _rb = GetComponentInParent<Rigidbody>();

        if (groundLayers.value == 0)
            groundLayers = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Defence Fortifications", "Obstacle");
        if (platformLayers.value == 0)
            platformLayers = LayerMask.GetMask("Mobile Platform");
    }

    private void Start()
    {
        GroundColorSampler.PrewarmActiveTerrains();
    }

    private void OnDestroy()
    {
        if (_groundInstance != null) Destroy(_groundInstance.gameObject);
        if (_platformInstance != null) Destroy(_platformInstance.gameObject);
    }

    // ── 落地偵測 ─────────────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (_movement == null) return;

        bool grounded = _movement.IsGrounded;

        if (!grounded)
        {
            _airTime += Time.fixedDeltaTime;
            if (_rb != null) _airMinVy = Mathf.Min(_airMinVy, _rb.linearVelocity.y);
        }
        else if (!_wasGrounded)
        {
            float fallSpeed = -_airMinVy;
            if (_airTime >= minAirTime && fallSpeed >= minLandingSpeed)
                PlayLand(fallSpeed);
        }

        if (grounded)
        {
            _airTime = 0f;
            _airMinVy = 0f;
        }
        _wasGrounded = grounded;
    }

    // ── 對外 API ─────────────────────────────────────────────────────────

    /// <summary>起跳時呼叫（PlayerMovement.JumpAction）。</summary>
    public void PlayJump()
    {
        Play(jumpIntensity);
    }

    /// <summary>落地時呼叫。fallSpeed = 落地前的下落速度（正值，m/s）。</summary>
    public void PlayLand(float fallSpeed)
    {
        float t = Mathf.InverseLerp(minLandingSpeed, maxLandingSpeed, fallSpeed);
        Play(Mathf.Lerp(minLandingIntensity, 1f, t));
    }

    /// <summary>在腳下噴一次指定強度（0～1）的塵土。</summary>
    public void Play(float intensity)
    {
        if (dustPrefab == null) return;

        Vector3 origin = transform.position + Vector3.up * probeStartHeight;
        int mask = groundLayers.value | platformLayers.value;
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                             probeStartHeight + probeDistance, mask, QueryTriggerInteraction.Ignore))
            return;   // 腳下沒有地面（例如在空中觸發）→ 不噴

        PlayAt(hit, intensity);
    }

    /// <summary>在指定的地面接觸點噴一次塵土（給 PlayerGroundWake 之類已經打過射線的呼叫端用）。</summary>
    public void PlayAt(RaycastHit hit, float intensity)
    {
        if (dustPrefab == null) return;
        if (!TryGetDustColor(hit, out Color color)) return;   // DustSurface 設定不噴

        Transform space = GetPlatformSpace(hit.collider);
        JumpDustEffect fx = GetInstance(space != null);
        fx.Play(hit.point, hit.normal, intensity, color, space);
    }

    // ── 內部 ─────────────────────────────────────────────────────────────

    /// <summary>依地面取塵土顏色。DustSurface 設定不噴時回傳 false。</summary>
    public bool TryGetDustColor(RaycastHit hit, out Color color)
    {
        DustSurface surface = hit.collider.GetComponentInParent<DustSurface>();
        if (surface != null)
        {
            color = surface.dustColor;
            return surface.emitDust;
        }

        color = defaultDustColor;
        if (GroundColorSampler.TrySample(hit, out Color ground))
            color = Color.Lerp(defaultDustColor, ground, groundColorInfluence);

        float lum = color.grayscale;
        color = Color.Lerp(color, new Color(lum, lum, lum, 1f), desaturate);
        color = new Color(
            Mathf.Clamp01(color.r * brightness),
            Mathf.Clamp01(color.g * brightness),
            Mathf.Clamp01(color.b * brightness),
            1f);
        return true;
    }

    /// <summary>打到的是船 → 回傳船的 Transform（塵土用它當座標系）；地面回傳 null。</summary>
    public Transform GetPlatformSpace(Collider col)
    {
        Rigidbody rb = col.attachedRigidbody;
        bool isPlatform = (platformLayers.value & (1 << col.gameObject.layer)) != 0 ||
                          (rb != null && (platformLayers.value & (1 << rb.gameObject.layer)) != 0);
        if (!isPlatform) return null;
        return rb != null ? rb.transform : col.transform;
    }

    private JumpDustEffect GetInstance(bool onPlatform)
    {
        if (onPlatform)
        {
            if (_platformInstance == null) _platformInstance = Spawn("JumpDust (Platform)");
            return _platformInstance;
        }

        if (_groundInstance == null) _groundInstance = Spawn("JumpDust (Ground)");
        return _groundInstance;
    }

    private JumpDustEffect Spawn(string instanceName)
    {
        JumpDustEffect fx = Instantiate(dustPrefab);   // 不掛在玩家底下：粒子要留在原地，不能跟著玩家跑
        fx.name = instanceName;
        return fx;
    }
}
