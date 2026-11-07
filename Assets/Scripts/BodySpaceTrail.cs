using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 記在「角色座標系」裡的武器 / 踢擊拖尾。
///
/// 一般的 TrailRenderer、粒子 Trails 都把拖尾記在世界座標：角色高速移動時，
/// 拖尾畫出的是「腳在世界中走過的路」，幾乎都是往前衝的位移，看起來會落後、被拉長。
/// 這裡把每個取樣點存成 Space（例如 Body）的本地座標，所以只留下「腳相對身體」的弧線，
/// 不管角色跑多快，拖尾都黏在身上。
///
/// 形狀：在 Base 和 Tip 兩點之間掃出一片帶子（傳統的劍光做法）。
///   踢擊：Tip = 腳尖附近，Base = 小腿中段（各放一個空物件在腿骨底下，位置自己調）。
/// 取樣之間用 Catmull-Rom 補點，動作很快、幀率低時弧線也不會變成折線。
///
/// 立體感（都可以個別關掉）：
///   · Cross Plane：再加一片和主帶子垂直的面（十字截面），側面看也不會變成一條線。
///   · Core：貼著 Tip 邊的一條細亮芯，用自己的材質和顏色。亮芯 + 外層光暈 = 讀起來像「發光的刃」。
///   · UV：Tile 模式依長度重複貼圖；Scroll Speed 讓貼圖沿拖尾往後流。
///     （貼圖的 Wrap Mode 要是 Repeat，捲動才看得到。）
///
/// 開關：
///   · Min Emit Speed > 0：Tip 相對身體的速度超過這個值才噴 —— 走路時腳慢不噴，踢出去才噴。
///   · 也可以用 Play() / Stop()（MMF 的 Events、動畫事件、程式）手動控制。
/// 停止後已經畫出的拖尾會照壽命淡出。
///
/// 繪製：Graphics.RenderMesh，不建立 Renderer 物件，也不會進 GPU Resident Drawer。
/// 顏色寫在頂點色（Particles 類 Shader 會乘上它）。
/// UV：u 沿拖尾（0 = 最新），v 橫跨帶子（主帶子：0 = 內緣、1 = Tip）。
/// </summary>
[DisallowMultipleComponent]
public class BodySpaceTrail : MonoBehaviour
{
    public enum UVMode { Stretch, Tile }

    [Header("Points")]
    [Tooltip("拖尾的座標系：會跟著角色移動、轉向的物件（例如 Body）。\n" +
             "留空 = 執行時自動找（武器這類執行時才裝上的 prefab 用）：見 Auto Space Name。")]
    [SerializeField] private Transform space;
    [Tooltip("Space 留空時，往上找到角色（PlayerAnimation 所在的模型根物件），再在它底下找這個名字的物件當 Space。\n" +
             "找不到這個名字時，直接用角色的模型根物件（一樣會跟著角色移動、轉向）。")]
    [SerializeField] private string autoSpaceName = "Body";
    [Tooltip("帶子的外緣（例如腳尖）。")]
    [SerializeField] private Transform tip;
    [Tooltip("帶子的內緣（例如小腿中段）。留空 = 只用 Tip，往身體中心方向長出 Fallback Width 的寬度。")]
    [SerializeField] private Transform basePoint;
    [Tooltip("沒有 Base Point 時的帶子寬度（公尺）。有 Base Point 時不會用到。")]
    [SerializeField] private float fallbackWidth = 0.4f;

    [Header("Shape")]
    [Tooltip("拖尾長度：每個取樣點活多久（秒）。")]
    [SerializeField, Min(0.01f)] private float lifetime = 0.2f;
    [Tooltip("Tip 相對上一個取樣點移動超過這個距離（公尺）才新增一個點。")]
    [SerializeField, Min(0.001f)] private float minSampleDistance = 0.03f;
    [Tooltip("每兩個取樣點之間補幾個點（Catmull-Rom）。")]
    [SerializeField, Range(0, 8)] private int smoothing = 4;
    [Tooltip("最多保留幾個取樣點。")]
    [SerializeField, Range(4, 128)] private int maxSamples = 48;

    [Header("Look")]
    [SerializeField] private Material material;
    [Tooltip("顏色隨拖尾長度變化：左 = 最新（腳的位置），右 = 最舊（尾端）。")]
    [SerializeField] private Gradient colorOverLength = DefaultGradient();
    [Tooltip("寬度隨拖尾長度變化（0～1，乘在 Base→Tip 上；從 Base 那一側往 Tip 收窄）。左 = 腳邊。")]
    [SerializeField] private AnimationCurve widthOverLength = AnimationCurve.Linear(0f, 1f, 1f, 0.3f);
    [Tooltip("繪製用的 Layer。")]
    [SerializeField] private int renderLayer = 0;

    [Header("Cross Plane（十字截面）")]
    [Tooltip("再加一片和主帶子垂直的面，從側面看也有厚度。")]
    [SerializeField] private bool crossPlane = true;
    [Tooltip("垂直那片的寬度，是主帶子寬度的幾倍。")]
    [SerializeField, Range(0.05f, 1.5f)] private float crossWidth = 0.5f;
    [Tooltip("垂直那片的中心放在主帶子的哪裡：0 = 內緣，1 = Tip。")]
    [SerializeField, Range(0f, 1f)] private float crossCenter = 0.8f;
    [Tooltip("垂直那片的透明度倍率（兩片疊在一起時避免過亮）。")]
    [SerializeField, Range(0f, 1f)] private float crossAlpha = 0.6f;

    [Header("Core（亮芯）")]
    [Tooltip("亮芯的材質。留空 = 不畫亮芯。通常用 Additive、白色、邊緣柔和的拖尾貼圖。")]
    [SerializeField] private Material coreMaterial;
    [Tooltip("亮芯的顏色（左 = 腳邊）。通常接近白色，比外層短、淡得快。")]
    [SerializeField] private Gradient coreColorOverLength = DefaultCoreGradient();
    [Tooltip("亮芯寬度，是主帶子寬度的幾倍（貼著 Tip 那一側）。")]
    [SerializeField, Range(0.02f, 1f)] private float coreWidth = 0.18f;

    [Header("UV")]
    [Tooltip("Stretch：整張貼圖拉滿整條拖尾。Tile：每 Tile Length 公尺重複一次。")]
    [SerializeField] private UVMode uvMode = UVMode.Stretch;
    [Tooltip("Tile 模式：貼圖重複一次的長度（公尺，沿 Tip 走過的路徑）。")]
    [SerializeField, Min(0.01f)] private float tileLength = 0.6f;
    [Tooltip("貼圖沿拖尾往尾端流動的速度（每秒幾張貼圖）。0 = 不動。")]
    [SerializeField] private float scrollSpeed = 0f;

    [Header("Emission")]
    [SerializeField] private bool emitting = true;
    [Tooltip("Tip 相對身體的速度（m/s）超過這個值才噴。0 = 只看 Emitting。")]
    [SerializeField, Min(0f)] private float minEmitSpeed = 6f;
    [Tooltip("速度掉到門檻以下後，再多噴幾秒（避免踢到最高點速度歸零時拖尾斷掉）。")]
    [SerializeField, Min(0f)] private float emitHoldTime = 0.08f;

    private struct Sample
    {
        public Vector3 b, t;   // space 本地座標
        public float time;
    }

    private struct Frame
    {
        public Vector3 inner, t, perp;   // perp：垂直主帶子的單位向量
        public float age, u;
    }

    private readonly List<Sample> _samples = new List<Sample>(64);
    private readonly List<Frame> _frames = new List<Frame>(512);
    private readonly List<Vector3> _verts = new List<Vector3>(2048);
    private readonly List<Color> _colors = new List<Color>(2048);
    private readonly List<Vector2> _uvs = new List<Vector2>(2048);
    private readonly List<int> _tris0 = new List<int>(4096);
    private readonly List<int> _tris1 = new List<int>(4096);

    private Mesh _mesh;
    private Vector3 _lastTipLocal;
    private bool _hasLast;
    private float _emitUntil = -1f;
    private bool _manualStop;

    public bool Emitting
    {
        get => emitting;
        set => emitting = value;
    }

    /// <summary>開始噴（MMF Events / 動畫事件用）。</summary>
    public void Play()
    {
        emitting = true;
        _manualStop = false;
    }

    /// <summary>停止噴。已經畫出的部分會照壽命淡出。</summary>
    public void Stop()
    {
        emitting = false;
        _manualStop = true;
    }

    /// <summary>立刻清掉整條拖尾。</summary>
    public void Clear()
    {
        _samples.Clear();
        _hasLast = false;
    }

    private void OnEnable()
    {
        if (space == null) ResolveSpace();
        if (_mesh == null)
        {
            _mesh = new Mesh { name = "BodySpaceTrail" };
            _mesh.MarkDynamic();
        }
        Clear();
    }

    private void OnDisable() => Clear();

    private void OnDestroy()
    {
        if (_mesh != null) Destroy(_mesh);
    }

    // 動畫、IK 都在 LateUpdate 之前算完，這裡讀到的是這一幀最終的腳位置
    private void LateUpdate()
    {
        if (space == null && !ResolveSpace()) return;
        if (tip == null || material == null) return;

        float now = Time.time;
        float dt = Time.deltaTime;

        // ── 取樣 ──
        Vector3 tipL = space.InverseTransformPoint(tip.position);
        Vector3 baseL = basePoint != null
            ? space.InverseTransformPoint(basePoint.position)
            : FallbackBase(tipL);

        float speed = (_hasLast && dt > 0f) ? (tipL - _lastTipLocal).magnitude / dt : 0f;
        if (minEmitSpeed <= 0f || speed >= minEmitSpeed) _emitUntil = now + emitHoldTime;
        bool emitNow = emitting && !_manualStop && (minEmitSpeed <= 0f || now <= _emitUntil);

        if (emitNow)
        {
            var s = new Sample { b = baseL, t = tipL, time = now };
            if (_samples.Count == 0 || (tipL - _samples[_samples.Count - 1].t).sqrMagnitude >= minSampleDistance * minSampleDistance)
            {
                _samples.Add(s);
                if (_samples.Count > maxSamples) _samples.RemoveAt(0);
            }
            else
            {
                // 移動太少：只更新最新那一點的位置（時間不變），頭部永遠貼著腳
                _samples[_samples.Count - 1] = new Sample { b = baseL, t = tipL, time = _samples[_samples.Count - 1].time };
            }
        }

        _lastTipLocal = tipL;
        _hasLast = true;

        // ── 移除過期的點 ──
        int expired = 0;
        while (expired < _samples.Count && now - _samples[expired].time > lifetime) expired++;
        if (expired > 0) _samples.RemoveRange(0, expired);

        if (_samples.Count < 2) return;

        BuildMesh(now);

        Matrix4x4 m = space.localToWorldMatrix;
        var bounds = new Bounds(space.position, Vector3.one * 50f);

        var rp = new RenderParams(material)
        {
            layer = renderLayer,
            shadowCastingMode = ShadowCastingMode.Off,
            receiveShadows = false,
            motionVectorMode = MotionVectorGenerationMode.ForceNoMotion,
            worldBounds = bounds,
        };
        Graphics.RenderMesh(rp, _mesh, 0, m);

        if (coreMaterial != null && _tris1.Count > 0)
        {
            var rpCore = rp;
            rpCore.material = coreMaterial;
            Graphics.RenderMesh(rpCore, _mesh, 1, m);
        }
    }

    // ═══════════════════════ 自動找 Space ═══════════════════════

    /// <summary>
    /// Space 留空時自動找：角色的模型根物件（PlayerAnimation，沒有的話用 Animator）底下名叫 Auto Space Name 的物件；
    /// 找不到這個名字就用模型根物件本身。
    /// 武器是裝備時才生成到手上的，所以 OnEnable 找不到（還沒掛到角色身上）時，LateUpdate 會再找，直到找到為止。
    /// </summary>
    private bool ResolveSpace()
    {
        Transform root = null;
        var pa = GetComponentInParent<PlayerAnimation>();
        if (pa != null) root = pa.transform;
        if (root == null)
        {
            var animator = GetComponentInParent<Animator>();
            if (animator != null) root = animator.transform;
        }
        if (root == null) return false;   // 還沒裝到角色身上

        Transform found = null;
        if (!string.IsNullOrEmpty(autoSpaceName))
            found = FindChildByName(root, autoSpaceName);

        space = found != null ? found : root;
        Clear();
        return true;
    }

    private static Transform FindChildByName(Transform root, string childName)
    {
        if (root.name == childName) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform r = FindChildByName(root.GetChild(i), childName);
            if (r != null) return r;
        }
        return null;
    }

    private Vector3 FallbackBase(Vector3 tipL)
    {
        // 往身體中心（space 原點附近，略高）收 fallbackWidth
        Vector3 toCenter = new Vector3(0f, 1f, 0f) - tipL;
        return tipL + (toCenter.sqrMagnitude > 0.0001f ? toCenter.normalized : Vector3.up) * fallbackWidth;
    }

    // ═══════════════════════ Mesh ═══════════════════════

    private void BuildMesh(float now)
    {
        BuildFrames(now);

        _verts.Clear(); _colors.Clear(); _uvs.Clear(); _tris0.Clear(); _tris1.Clear();
        int f = _frames.Count;
        if (f < 2) { _mesh.Clear(); return; }

        // 主帶子：內緣 → Tip
        int start = _verts.Count;
        for (int i = 0; i < f; i++)
        {
            Frame fr = _frames[i];
            Color c = colorOverLength.Evaluate(fr.age);
            AddVert(fr.inner, c, fr.u, 0f);
            AddVert(fr.t, c, fr.u, 1f);
        }
        AddStrip(start, f, _tris0);

        // 十字截面：垂直主帶子
        if (crossPlane)
        {
            start = _verts.Count;
            for (int i = 0; i < f; i++)
            {
                Frame fr = _frames[i];
                Vector3 center = Vector3.Lerp(fr.inner, fr.t, crossCenter);
                Vector3 half = fr.perp * ((fr.t - fr.inner).magnitude * crossWidth * 0.5f);
                Color c = colorOverLength.Evaluate(fr.age);
                c.a *= crossAlpha;
                AddVert(center - half, c, fr.u, 0f);
                AddVert(center + half, c, fr.u, 1f);
            }
            AddStrip(start, f, _tris0);
        }

        // 亮芯：貼著 Tip 的細帶子（有十字截面時也加一片垂直的）
        if (coreMaterial != null)
        {
            start = _verts.Count;
            for (int i = 0; i < f; i++)
            {
                Frame fr = _frames[i];
                Vector3 coreInner = Vector3.Lerp(fr.t, fr.inner, coreWidth);
                Color c = coreColorOverLength.Evaluate(fr.age);
                AddVert(coreInner, c, fr.u, 0f);
                AddVert(fr.t, c, fr.u, 1f);
            }
            AddStrip(start, f, _tris1);

            if (crossPlane)
            {
                start = _verts.Count;
                for (int i = 0; i < f; i++)
                {
                    Frame fr = _frames[i];
                    Vector3 coreInner = Vector3.Lerp(fr.t, fr.inner, coreWidth);
                    Vector3 center = Vector3.Lerp(coreInner, fr.t, 0.5f);
                    Vector3 half = fr.perp * ((fr.t - coreInner).magnitude * 0.5f);
                    Color c = coreColorOverLength.Evaluate(fr.age);
                    c.a *= crossAlpha;
                    AddVert(center - half, c, fr.u, 0f);
                    AddVert(center + half, c, fr.u, 1f);
                }
                AddStrip(start, f, _tris1);
            }
        }

        _mesh.Clear();
        _mesh.subMeshCount = 2;
        _mesh.SetVertices(_verts);
        _mesh.SetColors(_colors);
        _mesh.SetUVs(0, _uvs);
        _mesh.SetTriangles(_tris0, 0, false);
        _mesh.SetTriangles(_tris1, 1, false);
        _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 20f);
    }

    /// <summary>取樣點 → 補點後的 Frame（由新到舊）。</summary>
    private void BuildFrames(float now)
    {
        _frames.Clear();
        int n = _samples.Count;
        int steps = smoothing + 1;

        for (int i = n - 1; i >= 1; i--)
        {
            Sample p0 = _samples[Mathf.Min(i + 1, n - 1)];
            Sample p1 = _samples[i];
            Sample p2 = _samples[i - 1];
            Sample p3 = _samples[Mathf.Max(i - 2, 0)];

            int last = (i == 1) ? steps : steps - 1;   // 最後一段要包含終點
            for (int k = 0; k <= last; k++)
            {
                float f = k / (float)steps;
                Vector3 t = CatmullRom(p0.t, p1.t, p2.t, p3.t, f);
                Vector3 b = CatmullRom(p0.b, p1.b, p2.b, p3.b, f);
                float age = Mathf.Clamp01((now - Mathf.Lerp(p1.time, p2.time, f)) / lifetime);
                float w = Mathf.Clamp01(widthOverLength.Evaluate(age));
                _frames.Add(new Frame { inner = Vector3.Lerp(t, b, w), t = t, age = age });
            }
        }

        // 垂直方向、UV
        int count = _frames.Count;
        float dist = 0f;
        float scroll = scrollSpeed * Time.time;
        for (int i = 0; i < count; i++)
        {
            Frame fr = _frames[i];

            Vector3 along = _frames[Mathf.Min(i + 1, count - 1)].t - _frames[Mathf.Max(i - 1, 0)].t;
            Vector3 across = fr.t - fr.inner;
            Vector3 perp = Vector3.Cross(across, along);
            if (perp.sqrMagnitude < 1e-10f) perp = i > 0 ? _frames[i - 1].perp : Vector3.up;
            fr.perp = perp.normalized;

            if (i > 0) dist += (fr.t - _frames[i - 1].t).magnitude;
            float u = uvMode == UVMode.Stretch ? fr.age : dist / tileLength;
            fr.u = u - scroll;

            _frames[i] = fr;
        }
    }

    private void AddVert(Vector3 p, Color c, float u, float v)
    {
        _verts.Add(p);
        _colors.Add(c);
        _uvs.Add(new Vector2(u, v));
    }

    private static void AddStrip(int start, int frames, List<int> tris)
    {
        for (int i = 0; i < frames - 1; i++)
        {
            int a = start + i * 2;
            tris.Add(a); tris.Add(a + 1); tris.Add(a + 2);
            tris.Add(a + 2); tris.Add(a + 1); tris.Add(a + 3);
        }
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t +
                       (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 +
                       (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // 外層：腳邊接近白色的淺藍 → 深藍 → 淡出（熱 → 冷）
    private static Gradient DefaultGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.75f, 0.92f, 1f), 0f),
                new GradientColorKey(new Color(0.3f, 0.6f, 1f), 0.3f),
                new GradientColorKey(new Color(0.1f, 0.25f, 0.9f), 1f),
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.6f, 0.35f), new GradientAlphaKey(0f, 1f) });
        return g;
    }

    // 亮芯：白色，比外層淡得快
    private static Gradient DefaultCoreGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.7f, 0.9f, 1f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.5f, 0.2f), new GradientAlphaKey(0f, 0.6f) });
        return g;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (tip == null) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(tip.position, 0.05f);
        if (basePoint != null)
        {
            Gizmos.DrawWireSphere(basePoint.position, 0.05f);
            Gizmos.DrawLine(basePoint.position, tip.position);
        }
    }
#endif
}
