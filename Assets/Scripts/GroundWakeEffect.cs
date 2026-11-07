using UnityEngine;

/// <summary>
/// 貼地高速移動時的地面尾流特效（由 Tools > Diva Ex Machina > Ground Wake Builder 產生的 prefab 根物件）。
/// PlayerGroundWake 決定「什麼時候、在哪裡、多強」，這裡只負責把粒子噴出來。
///
/// 圖層（子物件上的 ParticleSystem，平常不發射，只靠 Emit 噴出；位置和速度都由這裡逐顆指定）：
///   Wake     - 身後往兩側翻開的塵團（主體，V 形尾流）
///   Grit     - 被踢起、受重力落下的碎石
///   Streaks  - 貼地往後射的速度線（強度高才出現）
///   Downwash - 推進器往下壓、從正下方往外推開的塵團
///   Ring     - 懸停時地面上擴散的氣流環
///   Sparks   - 金屬表面上往後噴的火花
///
/// 上升：用負的 Gravity Modifier 做，不用 Velocity over Lifetime，
/// 這樣在 World 和 Custom（船）兩種模擬空間下行為一樣。
/// </summary>
[DisallowMultipleComponent]
public class GroundWakeEffect : MonoBehaviour
{
    [Header("Layers (assigned by GroundWakeBuilder)")]
    [SerializeField] private ParticleSystem wake;
    [SerializeField] private ParticleSystem grit;
    [SerializeField] private ParticleSystem streaks;
    [SerializeField] private ParticleSystem downwash;
    [SerializeField] private ParticleSystem ring;
    [SerializeField] private ParticleSystem sparks;

    [Header("Wake")]
    [Tooltip("每次噴發，左右各幾團（強度 1 時）。")]
    [SerializeField, Min(1)] private int wakePuffsPerSide = 2;
    [Tooltip("往兩側推開的速度範圍（m/s，強度 1 時）。")]
    [SerializeField] private Vector2 wakeSideSpeed = new Vector2(3f, 7f);
    [Tooltip("往後拖的速度範圍（m/s，強度 1 時）。")]
    [SerializeField] private Vector2 wakeBackSpeed = new Vector2(1f, 5f);
    [Tooltip("往上翻的速度範圍（m/s）。")]
    [SerializeField] private Vector2 wakeUpSpeed = new Vector2(0.4f, 1.6f);
    [Tooltip("塵團大小範圍（公尺，強度 1 時）。")]
    [SerializeField] private Vector2 wakeSize = new Vector2(0.9f, 1.6f);
    [Tooltip("塵團壽命範圍（秒）。")]
    [SerializeField] private Vector2 wakeLifetime = new Vector2(0.8f, 1.4f);

    [Header("Grit / Streaks")]
    [Tooltip("強度高於這個值才噴碎石。")]
    [SerializeField, Range(0f, 1f)] private float gritMinIntensity = 0.45f;
    [SerializeField, Min(0)] private int gritCount = 2;
    [Tooltip("強度高於這個值才出現速度線。")]
    [SerializeField, Range(0f, 1f)] private float streakMinIntensity = 0.6f;
    [SerializeField, Min(0)] private int streakCount = 2;

    [Header("Downwash")]
    [Tooltip("往外推開的速度範圍（m/s，強度 1 時）。")]
    [SerializeField] private Vector2 downwashSpeed = new Vector2(5f, 10f);
    [SerializeField] private Vector2 downwashSize = new Vector2(1.0f, 1.8f);

    [Header("Sparks")]
    [Tooltip("往後噴的速度範圍（m/s，強度 1 時）。")]
    [SerializeField] private Vector2 sparkBackSpeed = new Vector2(6f, 14f);
    [SerializeField] private Vector2 sparkUpSpeed = new Vector2(1.5f, 5f);
    [SerializeField] private float sparkSideSpeed = 4f;
    [SerializeField] private Color sparkColorA = new Color(1f, 0.85f, 0.55f, 1f);
    [SerializeField] private Color sparkColorB = new Color(1f, 0.55f, 0.2f, 1f);

    [Header("Colour")]
    [Tooltip("強度 0 時的透明度倍率（高空、低速時塵土很淡）。")]
    [SerializeField, Range(0f, 1f)] private float minAlpha = 0.3f;
    [Tooltip("同一次噴發裡，顏色在「顏色 × 這個值」到「顏色」之間隨機。")]
    [SerializeField, Range(0.5f, 1f)] private float colorVariation = 0.88f;
    [SerializeField] private Color gritTint = new Color(0.55f, 0.52f, 0.48f, 1f);
    [SerializeField] private Color streakTint = new Color(1.1f, 1.1f, 1.1f, 0.8f);
    [SerializeField] private Color ringTint = new Color(1.1f, 1.1f, 1.1f, 0.6f);

    private ParticleSystem[] _layers;
    private Transform _space;
    private bool _spaceInitialized;

    private void Awake()
    {
        _layers = new[] { wake, grit, streaks, downwash, ring, sparks };
    }

    // ── 座標系 ───────────────────────────────────────────────────────────

    /// <summary>站在船上時傳入船的 Transform，粒子會跟著船移動；地面傳 null。</summary>
    public void SetSpace(Transform space)
    {
        if (_layers == null) Awake();
        if (_spaceInitialized && space == _space) return;
        _space = space;
        _spaceInitialized = true;

        foreach (ParticleSystem ps in _layers)
        {
            if (ps == null) continue;
            var main = ps.main;
            if (space != null)
            {
                main.simulationSpace = ParticleSystemSimulationSpace.Custom;
                main.customSimulationSpace = space;
            }
            else
            {
                main.simulationSpace = ParticleSystemSimulationSpace.World;
            }
        }
    }

    private Vector3 P(Vector3 world) => _space != null ? _space.InverseTransformPoint(world) : world;
    private Vector3 V(Vector3 world) => _space != null ? _space.InverseTransformVector(world) : world;

    // ── 對外 API ─────────────────────────────────────────────────────────

    /// <summary>
    /// 身後的 V 形尾流。
    /// </summary>
    /// <param name="point">地面接觸點（玩家正下方）。</param>
    /// <param name="normal">地面法線。</param>
    /// <param name="moveDir">相對地面的移動方向（已投影到地面，單位向量）。</param>
    /// <param name="intensity">0～1，速度 × 高度。</param>
    /// <param name="spread">左右散開的半寬（公尺）。越高越寬。</param>
    /// <param name="color">地面塵土顏色。</param>
    /// <param name="amount">數量倍率（金屬表面會調低）。</param>
    public void EmitWake(Vector3 point, Vector3 normal, Vector3 moveDir, float intensity,
                         float spread, Color color, float amount = 1f)
    {
        if (_layers == null) Awake();
        float k = Mathf.Clamp01(intensity);
        Vector3 back = -moveDir;
        Vector3 side = Vector3.Cross(normal, moveDir).normalized;
        float speedMul = Mathf.Lerp(0.5f, 1.3f, k);
        float sizeMul = Mathf.Lerp(0.7f, 1.3f, k);
        Color c = Fade(color, k);

        if (wake != null)
        {
            int perSide = Mathf.Max(1, Mathf.RoundToInt(wakePuffsPerSide * amount * Mathf.Lerp(0.5f, 1f, k)));
            for (int s = -1; s <= 1; s += 2)
            {
                for (int i = 0; i < perSide; i++)
                {
                    Vector3 pos = point + normal * 0.15f
                                + side * (s * Random.Range(0.2f, 1f) * spread)
                                + back * Random.Range(0f, 0.6f);
                    Vector3 vel = (side * (s * Rand(wakeSideSpeed)) + back * Rand(wakeBackSpeed)) * speedMul
                                + normal * Rand(wakeUpSpeed);
                    Emit(wake, pos, vel, Rand(wakeSize) * sizeMul, Rand(wakeLifetime) * Mathf.Lerp(0.8f, 1.2f, k), Vary(c));
                }
            }
        }

        if (grit != null && k >= gritMinIntensity && amount >= 0.99f)
        {
            for (int i = 0; i < gritCount; i++)
            {
                Vector3 vel = back * Random.Range(2f, 5f) + side * Random.Range(-2.5f, 2.5f) + normal * Random.Range(3f, 6f);
                Emit(grit, point + normal * 0.1f, vel * speedMul, Random.Range(0.05f, 0.12f), Random.Range(0.45f, 0.8f),
                     Mul(color, gritTint));
            }
        }

        if (streaks != null && k >= streakMinIntensity)
        {
            for (int i = 0; i < streakCount; i++)
            {
                Vector3 vel = back * Random.Range(9f, 15f) + side * Random.Range(-3f, 3f) + normal * Random.Range(0f, 0.6f);
                Vector3 pos = point + normal * 0.1f + side * Random.Range(-spread, spread) * 0.5f;
                Emit(streaks, pos, vel, Random.Range(0.07f, 0.12f), Random.Range(0.15f, 0.25f), Mul(color, streakTint));
            }
        }
    }

    /// <summary>推進器下洗氣流：從正下方往四周推開的塵團。</summary>
    public void EmitDownwash(Vector3 point, Vector3 normal, float intensity, float spread, Color color, int count)
    {
        if (_layers == null) Awake();
        if (downwash == null || count <= 0) return;

        float k = Mathf.Clamp01(intensity);
        Quaternion toPlane = Quaternion.FromToRotation(Vector3.up, normal);
        Color c = Fade(color, k);

        for (int i = 0; i < count; i++)
        {
            float a = Random.Range(0f, Mathf.PI * 2f);
            Vector3 dir = toPlane * new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a));
            Vector3 pos = point + normal * 0.15f + dir * Random.Range(0.2f, Mathf.Max(0.3f, spread * 0.5f));
            Vector3 vel = dir * Rand(downwashSpeed) * Mathf.Lerp(0.6f, 1.2f, k) + normal * Random.Range(0.2f, 0.8f);
            Emit(downwash, pos, vel, Rand(downwashSize) * Mathf.Lerp(0.8f, 1.2f, k), Random.Range(0.9f, 1.5f), Vary(c));
        }
    }

    /// <summary>懸停時地面上擴散的氣流環。</summary>
    public void EmitRing(Vector3 point, Vector3 normal, float intensity, Color color)
    {
        if (_layers == null) Awake();
        if (ring == null) return;
        float k = Mathf.Clamp01(intensity);
        Emit(ring, point + normal * 0.05f, Vector3.zero, Mathf.Lerp(1.5f, 3f, k), Mathf.Lerp(0.5f, 0.8f, k),
             Fade(Mul(color, ringTint), k));
    }

    /// <summary>金屬表面的火花。moveDir = 移動方向；火花往反方向噴。</summary>
    public void EmitSparks(Vector3 point, Vector3 normal, Vector3 moveDir, float intensity, int count)
    {
        EmitSparksToward(point, normal, -moveDir, intensity, count);
    }

    /// <summary>
    /// 急停：塵土往「前方」（原本的移動方向）推出去，金屬表面另外往前噴火花。
    /// </summary>
    public void EmitSkid(Vector3 point, Vector3 normal, Vector3 moveDir, float intensity, Color color, bool metal)
    {
        if (_layers == null) Awake();
        float k = Mathf.Clamp01(intensity);
        Vector3 side = Vector3.Cross(normal, moveDir).normalized;
        Color c = Fade(color, k);

        if (wake != null)
        {
            int n = Mathf.RoundToInt(Mathf.Lerp(4f, 9f, k) * (metal ? 0.5f : 1f));
            for (int i = 0; i < n; i++)
            {
                Vector3 pos = point + normal * 0.15f + side * Random.Range(-0.8f, 0.8f) + moveDir * Random.Range(0f, 0.8f);
                Vector3 vel = moveDir * Random.Range(4f, 9f) * Mathf.Lerp(0.6f, 1.2f, k)
                            + side * Random.Range(-3f, 3f) + normal * Rand(wakeUpSpeed);
                Emit(wake, pos, vel, Rand(wakeSize) * Mathf.Lerp(0.8f, 1.3f, k), Rand(wakeLifetime), Vary(c));
            }
        }

        if (metal) EmitSparksToward(point, normal, moveDir, k, Mathf.RoundToInt(Mathf.Lerp(6f, 16f, k)));
    }

    // ── 內部 ─────────────────────────────────────────────────────────────

    private void EmitSparksToward(Vector3 point, Vector3 normal, Vector3 dir, float intensity, int count)
    {
        if (_layers == null) Awake();
        if (sparks == null || count <= 0) return;

        float k = Mathf.Clamp01(intensity);
        Vector3 side = Vector3.Cross(normal, dir).normalized;
        float speedMul = Mathf.Lerp(0.6f, 1.2f, k);

        for (int i = 0; i < count; i++)
        {
            Vector3 vel = (dir * Rand(sparkBackSpeed) + side * Random.Range(-sparkSideSpeed, sparkSideSpeed)) * speedMul
                        + normal * Rand(sparkUpSpeed);
            Color c = Color.Lerp(sparkColorA, sparkColorB, Random.value);
            Emit(sparks, point + normal * 0.05f, vel, Random.Range(0.04f, 0.09f), Random.Range(0.18f, 0.4f), c);
        }
    }

    private void Emit(ParticleSystem ps, Vector3 worldPos, Vector3 worldVel, float size, float lifetime, Color color)
    {
        var p = new ParticleSystem.EmitParams
        {
            position = P(worldPos),
            velocity = V(worldVel),
            startSize = size,
            startLifetime = lifetime,
            startColor = Clamp(color),
            rotation = Random.Range(0f, 360f),
            applyShapeToPosition = false,
        };
        ps.Emit(p, 1);
    }

    private Color Fade(Color c, float k)
    {
        c.a *= Mathf.Lerp(minAlpha, 1f, k);
        return c;
    }

    private Color Vary(Color c)
    {
        float m = Random.Range(colorVariation, 1f);
        return new Color(c.r * m, c.g * m, c.b * m, c.a);
    }

    private static Color Mul(Color a, Color b) => new Color(a.r * b.r, a.g * b.g, a.b * b.b, a.a * b.a);

    private static Color Clamp(Color c) =>
        new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), Mathf.Clamp01(c.a));

    private static float Rand(Vector2 range) => Random.Range(range.x, range.y);
}
