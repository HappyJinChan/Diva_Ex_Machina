using UnityEngine;

/// <summary>
/// 貼地高速移動時，地面對 Bradamant 的反應。掛在玩家上（和 PlayerJumpDust 同一個物件）。
///
///   尾流 Wake      ：突擊推進 / Dash / 飛行 / 地面高速移動時，身後拖出往兩側翻開的塵土。
///                    依「移動距離」發射，越快越長；越高越淡、越寬。
///   下洗 Downwash  ：飛行（或離地的突擊推進）時，推進器把正下方的塵土往外推；懸停時最明顯。
///   火花 Sparks    ：在金屬表面（船甲板、DustSurface.kind = Metal）上 Dash / 突擊推進時往後噴。
///   起步 / 急停    ：在地面啟動突擊推進或 Dash 時噴一次起步塵土（沿用 JumpDust）；
///                    從高速急停時，塵土往前推出去（金屬面上加火花）。
///
/// 高度上限以上完全不打射線，幾乎沒有成本。
/// 顏色、船的判斷沿用 PlayerJumpDust（Terrain 圖層 / 材質取色，DustSurface 覆寫）。
/// </summary>
[DisallowMultipleComponent]
public class PlayerGroundWake : MonoBehaviour
{
    [Header("Effect")]
    [Tooltip("Ground Wake Builder 產生的 prefab。")]
    [SerializeField] private GroundWakeEffect wakePrefab;
    [Tooltip("取色、船的判斷、起步塵土都用它。留空會自動找。")]
    [SerializeField] private PlayerJumpDust jumpDust;

    [Header("Triggers")]
    [SerializeField] private bool onAssaultBoost = true;
    [SerializeField] private bool onDash = true;
    [Tooltip("近戰突進也算 Dash。")]
    [SerializeField] private bool includeMeleeDash = true;
    [SerializeField] private bool onFlying = true;
    [SerializeField] private bool onFastGroundMove = true;
    [Tooltip("一般地面移動，相對地面的水平速度超過這個值（m/s）才算高速。")]
    [SerializeField] private float fastGroundSpeed = 18f;

    [Header("Speed / Height")]
    [Tooltip("推進類狀態下，速度低於這個值（m/s）不拖尾流。")]
    [SerializeField] private float minWakeSpeed = 6f;
    [Tooltip("速度達到這個值（m/s）時，速度係數 = 1。")]
    [SerializeField] private float fullSpeed = 40f;
    [Tooltip("離地高於這個值（公尺）就沒有任何地面反應。")]
    [SerializeField] private float maxHeight = 8f;
    [Tooltip("高度衰減的曲線次方。2 = 貼地最強，往上很快變淡。")]
    [SerializeField, Range(0.5f, 4f)] private float heightFalloffPower = 2f;
    [Tooltip("射線起點在玩家位置上方多高（公尺）。")]
    [SerializeField] private float probeStartHeight = 0.5f;

    [Header("Wake")]
    [Tooltip("每移動幾公尺噴一次尾流。")]
    [SerializeField, Min(0.2f)] private float wakeSpacing = 1.2f;
    [Tooltip("左右散開的半寬：x = 貼地，y = 最高處（公尺）。")]
    [SerializeField] private Vector2 wakeSpread = new Vector2(0.6f, 2.5f);
    [Tooltip("地面高速移動（非推進）時的強度倍率。")]
    [SerializeField, Range(0f, 1f)] private float fastGroundIntensity = 0.6f;

    [Header("Downwash")]
    [Tooltip("強度 1 時每秒噴幾團。")]
    [SerializeField] private float downwashRate = 22f;
    [Tooltip("全速時下洗只剩這個比例（推進器的氣流被甩在後面）。")]
    [SerializeField, Range(0f, 1f)] private float downwashAtFullSpeed = 0.35f;
    [Tooltip("懸停（速度係數低於 0.3）且夠低時，每隔幾秒一圈氣流環。")]
    [SerializeField] private float ringInterval = 0.4f;

    [Header("Sparks (Metal)")]
    [Tooltip("離地低於這個值（公尺）才會噴火花。")]
    [SerializeField] private float sparkMaxHeight = 1.2f;
    [Tooltip("每移動幾公尺噴一次火花。")]
    [SerializeField, Min(0.1f)] private float sparkSpacing = 0.6f;
    [SerializeField, Min(1)] private int sparksPerEmit = 5;
    [Tooltip("金屬表面上的塵土量倍率。")]
    [SerializeField, Range(0f, 1f)] private float metalDustAmount = 0.4f;
    [Tooltip("沒有 DustSurface 時，船（Platform Layers）一律當成金屬。")]
    [SerializeField] private bool platformIsMetal = true;

    [Header("Start / Stop")]
    [Tooltip("在地面啟動突擊推進時的起步塵土強度（0 = 不噴）。")]
    [SerializeField, Range(0f, 1f)] private float boostStartIntensity = 0.55f;
    [Tooltip("在地面啟動 Dash 時的起步塵土強度（0 = 不噴）。")]
    [SerializeField, Range(0f, 1f)] private float dashStartIntensity = 0.4f;
    [Tooltip("速度曾經高於這個值（m/s）…")]
    [SerializeField] private float skidFromSpeed = 20f;
    [Tooltip("…並在 Skid Window 秒內降到這個值以下 = 急停。")]
    [SerializeField] private float skidToSpeed = 5f;
    [SerializeField] private float skidWindow = 0.3f;

    [Header("Colour")]
    [Tooltip("同一個碰撞體，每隔幾秒重新取一次顏色（Terrain 會隨位置變）。")]
    [SerializeField] private float colorRefreshInterval = 0.25f;

    private PlayerMovement _movement;
    private Rigidbody _rb;
    private GroundWakeEffect _groundInstance;
    private GroundWakeEffect _platformInstance;

    private float _wakeAccum, _sparkAccum, _downwashAccum, _ringTimer;
    private bool _wasBoost, _wasDash;
    private float _lastFastTime = -999f;
    private Vector3 _lastFastDir;
    private bool _skidArmed;

    private Collider _colorCollider;
    private float _colorTime;
    private Color _color;
    private bool _colorEmit;

    private void Awake()
    {
        _movement = GetComponentInParent<PlayerMovement>();
        _rb = GetComponentInParent<Rigidbody>();
        if (jumpDust == null) jumpDust = GetComponent<PlayerJumpDust>();
        if (jumpDust == null) jumpDust = GetComponentInParent<PlayerJumpDust>();
        if (jumpDust == null) jumpDust = GetComponentInChildren<PlayerJumpDust>();
    }

    private void OnDestroy()
    {
        if (_groundInstance != null) Destroy(_groundInstance.gameObject);
        if (_platformInstance != null) Destroy(_platformInstance.gameObject);
    }

    private void FixedUpdate()
    {
        if (_movement == null || _rb == null || jumpDust == null || wakePrefab == null) return;
        float dt = Time.fixedDeltaTime;

        bool grounded = _movement.IsGrounded;
        bool boost = onAssaultBoost && _movement.IsAssaultBoosting;
        bool dash = onDash && (_movement.IsDashActive || (includeMeleeDash && _movement.IsMeleeDashing));
        bool fly = onFlying && _movement.IsFlyingActive;

        bool boostEdge = boost && !_wasBoost;
        bool dashEdge = dash && !_wasDash;
        _wasBoost = boost;
        _wasDash = dash;

        // ── 找地面（高度上限以上不打射線） ──
        Vector3 origin = _rb.position + Vector3.up * probeStartHeight;
        int mask = jumpDust.GroundLayers.value | jumpDust.PlatformLayers.value;
        if (!Physics.Raycast(origin, Vector3.down, out RaycastHit hit, probeStartHeight + maxHeight, mask,
                             QueryTriggerInteraction.Ignore))
        {
            ResetAccumulators();
            return;
        }

        float height = Mathf.Max(0f, hit.distance - probeStartHeight);
        float heightK = Mathf.Pow(1f - Mathf.Clamp01(height / maxHeight), heightFalloffPower);

        // ── 相對地面的速度（船上要扣掉船速） ──
        Transform space = jumpDust.GetPlatformSpace(hit.collider);
        Vector3 groundVel = Vector3.zero;
        if (space != null && hit.collider.attachedRigidbody != null)
            groundVel = hit.collider.attachedRigidbody.GetPointVelocity(hit.point);

        Vector3 rel = Vector3.ProjectOnPlane(_rb.linearVelocity - groundVel, hit.normal);
        float speed = rel.magnitude;
        Vector3 moveDir = speed > 0.01f ? rel / speed : _lastFastDir;
        float speedK = Mathf.InverseLerp(minWakeSpeed, fullSpeed, speed);

        // ── 表面 ──
        if (!SampleSurface(hit, out Color color, out bool metal))
        {
            ResetAccumulators();
            return;   // DustSurface 設定不噴（水面、玻璃）
        }

        GroundWakeEffect fx = GetInstance(space);
        fx.SetSpace(space);

        bool fastGround = onFastGroundMove && grounded && speed >= fastGroundSpeed;
        bool boostState = boost || dash || fly;

        // ── 起步 ──
        if (grounded || height < 1f)
        {
            if (boostEdge && boostStartIntensity > 0f) jumpDust.PlayAt(hit, boostStartIntensity);
            else if (dashEdge && dashStartIntensity > 0f) jumpDust.PlayAt(hit, dashStartIntensity);
        }

        // ── 尾流 ──
        if ((boostState || fastGround) && speedK > 0f && heightK > 0f)
        {
            float intensity = speedK * heightK * (boostState ? 1f : fastGroundIntensity);
            float spread = Mathf.Lerp(wakeSpread.x, wakeSpread.y, height / maxHeight);

            _wakeAccum += speed * dt;
            int n = 0;
            while (_wakeAccum >= wakeSpacing && n++ < 4)
            {
                _wakeAccum -= wakeSpacing;
                fx.EmitWake(hit.point, hit.normal, moveDir, intensity, spread, color, metal ? metalDustAmount : 1f);
            }
            if (_wakeAccum > wakeSpacing) _wakeAccum = 0f;   // 一步跑太遠時不要累積成爆量

            // 火花：金屬表面、Dash / 突擊推進、幾乎貼地
            if (metal && (boost || dash) && height <= sparkMaxHeight)
            {
                _sparkAccum += speed * dt;
                int m = 0;
                while (_sparkAccum >= sparkSpacing && m++ < 4)
                {
                    _sparkAccum -= sparkSpacing;
                    fx.EmitSparks(hit.point, hit.normal, moveDir, Mathf.Max(speedK, 0.3f), sparksPerEmit);
                }
                if (_sparkAccum > sparkSpacing) _sparkAccum = 0f;
            }
            else _sparkAccum = 0f;
        }
        else
        {
            _wakeAccum = 0f;
            _sparkAccum = 0f;
        }

        // ── 下洗 ──
        bool thrustDown = fly || (boost && !grounded);
        if (thrustDown && heightK > 0f)
        {
            float k = heightK * Mathf.Lerp(1f, downwashAtFullSpeed, speedK);
            float spread = Mathf.Lerp(wakeSpread.x, wakeSpread.y, height / maxHeight) * 2f;

            _downwashAccum += downwashRate * k * dt;
            int count = Mathf.FloorToInt(_downwashAccum);
            _downwashAccum -= count;
            if (count > 0) fx.EmitDownwash(hit.point, hit.normal, k, spread, color, Mathf.Min(count, 4));

            _ringTimer -= dt;
            if (speedK < 0.3f && heightK > 0.4f && _ringTimer <= 0f)
            {
                _ringTimer = ringInterval;
                fx.EmitRing(hit.point, hit.normal, k, color);
            }
        }
        else
        {
            _downwashAccum = 0f;
            _ringTimer = 0f;
        }

        // ── 急停 ──
        if (grounded && speed >= skidFromSpeed)
        {
            _lastFastTime = Time.time;
            _lastFastDir = moveDir;
            _skidArmed = true;
        }
        else if (_skidArmed && grounded && speed <= skidToSpeed)
        {
            _skidArmed = false;
            if (Time.time - _lastFastTime <= skidWindow)
                fx.EmitSkid(hit.point, hit.normal, _lastFastDir, 1f, color, metal);
        }
        else if (Time.time - _lastFastTime > skidWindow)
        {
            _skidArmed = false;
        }
    }

    // ── 內部 ─────────────────────────────────────────────────────────────

    private bool SampleSurface(RaycastHit hit, out Color color, out bool metal)
    {
        Collider col = hit.collider;
        DustSurface surface = col.GetComponentInParent<DustSurface>();
        metal = surface != null
            ? surface.kind == DustSurface.SurfaceKind.Metal
            : platformIsMetal && jumpDust.GetPlatformSpace(col) != null;

        if (col != _colorCollider || Time.time - _colorTime >= colorRefreshInterval)
        {
            _colorEmit = jumpDust.TryGetDustColor(hit, out _color);
            _colorCollider = col;
            _colorTime = Time.time;
        }
        color = _color;
        return _colorEmit;
    }

    private void ResetAccumulators()
    {
        _wakeAccum = 0f;
        _sparkAccum = 0f;
        _downwashAccum = 0f;
        _ringTimer = 0f;
    }

    private GroundWakeEffect GetInstance(Transform space)
    {
        if (space != null)
        {
            if (_platformInstance == null) _platformInstance = Spawn("GroundWake (Platform)");
            return _platformInstance;
        }
        if (_groundInstance == null) _groundInstance = Spawn("GroundWake (Ground)");
        return _groundInstance;
    }

    private GroundWakeEffect Spawn(string instanceName)
    {
        GroundWakeEffect fx = Instantiate(wakePrefab, Vector3.zero, Quaternion.identity);   // 不掛在玩家底下
        fx.name = instanceName;
        return fx;
    }
}
