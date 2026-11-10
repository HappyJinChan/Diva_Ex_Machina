using UnityEngine;

/// <summary>
/// 貝努鳥號履帶揚起的塵土。每一側履帶放一個（由 Tools > Diva Ex Machina > Track Dust Builder 產生 prefab）。
///
/// 放法：放在船底下，貼著履帶接地的那條線，物件的 Z 軸（藍色箭頭）沿著履帶方向。
///   Track Length 是履帶接地段的長度，塵土會沿著這段隨機噴出（偏向後段，Rear Bias）。
///   左右由「這個物件在船的哪一側」自動判斷，塵土往船外側翻出去。
///
/// 運作：
///   · 速度用這個物件每幀的位移算（不依賴 Rigidbody，kinematic / MovePosition 也行），只看水平速度。
///   · 速度在 Min Speed 以下不噴；到 Full Speed 時是最大量。
///   · 每個噴發點往下打射線找地面；打到船自己的 Collider 會跳過；沒打到地面（懸空、水面）就不噴。
///   · 顏色依地面取色（GroundColorSampler），地面有 DustSurface 時以它為準（Emit Dust 關掉就不噴）。
///   · 粒子是 World 模擬：船開走後塵土留在原地慢慢散開。
/// </summary>
[DisallowMultipleComponent]
public class LandshipTrackDust : MonoBehaviour
{
    [Header("Layers (assigned by TrackDustBuilder)")]
    [SerializeField] private ParticleSystem puff;
    [SerializeField] private ParticleSystem debris;

    [Header("Track")]
    [Tooltip("履帶接地段的長度（公尺，沿這個物件的 Z 軸，以物件為中心）。")]
    [SerializeField, Min(0f)] private float trackLength = 120f;
    [Tooltip("履帶寬度（公尺）。噴發點在這個寬度內隨機。")]
    [SerializeField, Min(0f)] private float trackWidth = 12f;
    [Tooltip("噴發點偏向後段的程度。1 = 平均分布；越大越集中在後端（履帶離地、捲起塵土的地方）。")]
    [SerializeField, Min(1f)] private float rearBias = 2f;
    [Tooltip("船的根物件（判斷左右、排除自己的 Collider）。留空 = 往上找到的 Rigidbody，再沒有就是 transform.root。")]
    [SerializeField] private Transform ship;

    [Header("Speed")]
    [Tooltip("水平速度低於這個值（m/s）不噴。")]
    [SerializeField, Min(0f)] private float minSpeed = 0.3f;
    [Tooltip("達到這個速度（m/s）時噴最多。")]
    [SerializeField, Min(0.01f)] private float fullSpeed = 5f;

    [Header("Puff（主要的塵團）")]
    [Tooltip("全速時每秒幾團。")]
    [SerializeField, Min(0f)] private float puffsPerSecond = 30f;
    [SerializeField] private Vector2 puffSize = new Vector2(6f, 12f);
    [SerializeField] private Vector2 puffLifetime = new Vector2(3f, 5f);
    [Tooltip("往船外側翻出去的速度（m/s）。")]
    [SerializeField] private Vector2 puffOutSpeed = new Vector2(1f, 4f);
    [Tooltip("往上翻的速度（m/s）。")]
    [SerializeField] private Vector2 puffUpSpeed = new Vector2(0.5f, 2.5f);
    [Tooltip("繼承船速度的比例（0 = 噴出來就停在原地；0.3 = 被履帶帶著往前一點）。")]
    [SerializeField, Range(0f, 1f)] private float inheritVelocity = 0.25f;

    [Header("Debris（後端踢起的碎石，可不用）")]
    [SerializeField, Min(0f)] private float debrisPerSecond = 12f;
    [SerializeField] private Vector2 debrisSize = new Vector2(0.3f, 0.8f);
    [SerializeField] private Vector2 debrisUpSpeed = new Vector2(3f, 7f);

    [Header("Ground")]
    [Tooltip("地面的層。Nothing = 所有層（打到船自己的 Collider 會自動跳過）。")]
    [SerializeField] private LayerMask groundMask;
    [Tooltip("射線從噴發點上方多高開始往下打（公尺）。")]
    [SerializeField, Min(0f)] private float rayUp = 4f;
    [Tooltip("往下最多打多遠（公尺）。履帶離地超過這個距離就不噴。")]
    [SerializeField, Min(0.1f)] private float rayDown = 10f;

    [Header("Colour")]
    [SerializeField] private Color defaultDustColor = new Color(0.78f, 0.70f, 0.56f, 1f);
    [SerializeField, Range(0f, 1f)] private float groundColorInfluence = 0.85f;
    [SerializeField, Range(0f, 1f)] private float desaturate = 0.25f;
    [SerializeField, Min(0f)] private float brightness = 1.1f;
    [Tooltip("全速時的透明度；低速時往下降到這個值 × Min Alpha。")]
    [SerializeField, Range(0f, 1f)] private float alpha = 0.8f;
    [SerializeField, Range(0f, 1f)] private float minAlpha = 0.35f;

    [Header("Debug")]
    [SerializeField] private bool drawGizmos = true;

    /// <summary>目前的強度 0～1（給音效之類的用）。</summary>
    public float Intensity { get; private set; }

    private Vector3 _lastPos;
    private bool _hasLastPos;
    private Vector3 _velocity;
    private float _puffAcc, _debrisAcc;
    private float _sideSign = 1f;
    private readonly RaycastHit[] _hits = new RaycastHit[8];

    private void Awake()
    {
        if (ship == null)
        {
            Rigidbody rb = GetComponentInParent<Rigidbody>();
            ship = rb != null ? rb.transform : transform.root;
        }
        float localX = ship != null && ship != transform ? ship.InverseTransformPoint(transform.position).x : 1f;
        _sideSign = localX < 0f ? -1f : 1f;
    }

    private void OnEnable()
    {
        _hasLastPos = false;
        _puffAcc = _debrisAcc = 0f;
    }

    private void LateUpdate()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        Vector3 pos = transform.position;
        if (!_hasLastPos) { _lastPos = pos; _hasLastPos = true; return; }

        Vector3 v = (pos - _lastPos) / dt;
        _lastPos = pos;
        _velocity = Vector3.Lerp(_velocity, v, 1f - Mathf.Exp(-8f * dt));   // 平滑：插值 / 物理步不同步時不會抖

        Vector3 flat = new Vector3(_velocity.x, 0f, _velocity.z);
        float speed = flat.magnitude;
        Intensity = speed <= minSpeed ? 0f : Mathf.Clamp01((speed - minSpeed) / Mathf.Max(0.01f, fullSpeed - minSpeed));
        if (Intensity <= 0f) { _puffAcc = _debrisAcc = 0f; return; }

        Vector3 moveDir = flat / speed;

        if (puff != null)
        {
            _puffAcc += puffsPerSecond * Intensity * dt;
            int n = Mathf.Min((int)_puffAcc, 20);
            _puffAcc -= (int)_puffAcc;
            for (int i = 0; i < n; i++) EmitPuff(moveDir);
        }

        if (debris != null && debrisPerSecond > 0f)
        {
            _debrisAcc += debrisPerSecond * Intensity * dt;
            int n = Mathf.Min((int)_debrisAcc, 10);
            _debrisAcc -= (int)_debrisAcc;
            for (int i = 0; i < n; i++) EmitDebris(moveDir);
        }
    }

    // ── 噴發 ─────────────────────────────────────────────────────────────

    private void EmitPuff(Vector3 moveDir)
    {
        if (!TryGroundPoint(moveDir, rearOnly: false, out RaycastHit hit, out Color color)) return;

        Vector3 outDir = OutwardDir(hit.normal);
        Vector3 vel = outDir * Rand(puffOutSpeed)
                    + hit.normal * Rand(puffUpSpeed)
                    + _velocity * inheritVelocity;

        color.a = alpha * Mathf.Lerp(minAlpha, 1f, Intensity);
        float size = Rand(puffSize) * Mathf.Lerp(0.7f, 1f, Intensity);
        Emit(puff, hit.point + hit.normal * (size * 0.3f), vel, size, Rand(puffLifetime), color);
    }

    private void EmitDebris(Vector3 moveDir)
    {
        if (!TryGroundPoint(moveDir, rearOnly: true, out RaycastHit hit, out Color color)) return;

        Vector3 vel = OutwardDir(hit.normal) * Random.Range(0.5f, 2f)
                    - moveDir * Random.Range(1f, 4f)
                    + hit.normal * Rand(debrisUpSpeed)
                    + _velocity * inheritVelocity;

        color = new Color(color.r * 0.6f, color.g * 0.58f, color.b * 0.55f, 1f);
        Emit(debris, hit.point + hit.normal * 0.3f, vel, Rand(debrisSize), Random.Range(1.2f, 2f), color);
    }

    /// <summary>沿履帶取一個點，往下找地面。rearOnly = 只取最後端 10%。</summary>
    private bool TryGroundPoint(Vector3 moveDir, bool rearOnly, out RaycastHit hit, out Color color)
    {
        hit = default;
        color = defaultDustColor;

        // 後端 = 跟移動方向相反的那一端（倒車時自動換邊）
        float rearSign = Vector3.Dot(transform.forward, moveDir) >= 0f ? -1f : 1f;
        float t = rearOnly ? Random.Range(0.9f, 1f) : Mathf.Pow(Random.value, 1f / rearBias);   // 0 = 前端、1 = 後端
        float z = rearSign * (t - 0.5f) * trackLength;
        float x = Random.Range(-0.5f, 0.5f) * trackWidth;

        Vector3 origin = transform.TransformPoint(new Vector3(x, 0f, z)) + Vector3.up * rayUp;
        int mask = groundMask.value != 0 ? groundMask.value : Physics.DefaultRaycastLayers;
        int count = Physics.RaycastNonAlloc(origin, Vector3.down, _hits, rayUp + rayDown, mask, QueryTriggerInteraction.Ignore);

        float best = float.MaxValue;
        bool found = false;
        for (int i = 0; i < count; i++)
        {
            RaycastHit h = _hits[i];
            if (ship != null && h.collider.transform.IsChildOf(ship)) continue;   // 船自己
            if (h.distance < best) { best = h.distance; hit = h; found = true; }
        }
        if (!found) return false;

        return TryGetDustColor(hit, out color);
    }

    private bool TryGetDustColor(RaycastHit hit, out Color color)
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
        color = new Color(Mathf.Clamp01(color.r * brightness), Mathf.Clamp01(color.g * brightness),
                          Mathf.Clamp01(color.b * brightness), 1f);
        return true;
    }

    /// <summary>往船外側（貼著地面）。</summary>
    private Vector3 OutwardDir(Vector3 normal)
    {
        Vector3 side = (ship != null ? ship.right : transform.right) * _sideSign;
        side = Vector3.ProjectOnPlane(side, normal).normalized;
        // 稍微散開，不要整排往同一個方向
        return Quaternion.AngleAxis(Random.Range(-25f, 25f), normal) * side;
    }

    private static void Emit(ParticleSystem ps, Vector3 pos, Vector3 vel, float size, float lifetime, Color color)
    {
        var p = new ParticleSystem.EmitParams
        {
            position = pos,
            velocity = vel,
            startSize = size,
            startLifetime = lifetime,
            startColor = color,
            rotation = Random.Range(0f, 360f),
            applyShapeToPosition = false,
        };
        ps.Emit(p, 1);
    }

    private static float Rand(Vector2 r) => Random.Range(r.x, r.y);

    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;
        Gizmos.color = new Color(0.9f, 0.75f, 0.4f, 0.8f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawWireCube(Vector3.zero, new Vector3(trackWidth, 0.2f, trackLength));
        Gizmos.DrawLine(Vector3.zero, Vector3.down * rayDown);
    }
}
