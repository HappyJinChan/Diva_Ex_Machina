using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 近戰的劍風特效（半月形斬擊，例如 Free Slash VFX 的 Slash VFX）。掛在角色模型上（PlayerAnimation 同一個物件）。
///
/// MeleeAttackController 在 HitboxOn 時呼叫 Play()，依這一段（MeleeAttackStep）的設定擺放：
///   · 角度 / 位置 / 大小 / 反向 / 延遲：MeleeAttackStep 的 Slash VFX 區塊（用 Tools > Diva Ex Machina > Slash Angle Editor 調）
///   · 大小預設依這一段的 Arc Range 自動縮放：劍風的半徑 = 扇形判定的距離（看到的範圍 = 打得到的範圍）
///   · 右手：沒有勾 Right Hand Override 時，自動把左手的設定左右鏡像
///
/// 劍風生成在角色模型底下，粒子的 Simulation Space 改成 Local —— 高速突進時才不會被甩在身後。
/// 名字在 World Space Children 裡的子物件（預設 Flying 光點）維持 World，留下一點空間感；
/// 名字在 Remove Children 裡的子物件（預設 Impact）在生成時直接移除（命中特效由命中系統負責，空揮不該爆）。
///
/// 每種 prefab 各自一個物件池，不會每一刀都 Instantiate。
/// </summary>
[DisallowMultipleComponent]
public class MeleeSlashVfx : MonoBehaviour
{
    [Tooltip("劍風的座標系（角色模型）。留空 = 這個物件。")]
    [SerializeField] private Transform model;

    [Tooltip("這些名字的子物件維持 World 模擬（留在原地），其他一律改成 Local（跟著角色）。")]
    [SerializeField] private string[] worldSpaceChildren = { "Flying" };

    [Tooltip("這些名字的子物件在生成時移除。")]
    [SerializeField] private string[] removeChildren = { "Impact" };

    [Tooltip("播放後幾秒收回物件池（要比劍風最長的粒子壽命長）。")]
    [SerializeField, Min(0.1f)] private float releaseAfter = 1.5f;

    private sealed class Instance
    {
        public GameObject go;
        public ParticleSystem[] roots;
        public float refRadius;
        public float releaseAt;
    }

    private struct Pending
    {
        public GameObject prefab;
        public MeleeAttackStep step;
        public bool isLeft;
        public float at;
    }

    private readonly Dictionary<GameObject, Stack<Instance>> _pool = new Dictionary<GameObject, Stack<Instance>>();
    private readonly List<KeyValuePair<GameObject, Instance>> _active = new List<KeyValuePair<GameObject, Instance>>();
    private readonly List<Pending> _pending = new List<Pending>();

    private Transform Model => model != null ? model : transform;

    // ═══════════════════════ 對外 ═══════════════════════

    /// <summary>播放這一段的劍風。prefab 為 null 時什麼都不做。</summary>
    public void Play(GameObject prefab, MeleeAttackStep step, bool isLeft)
    {
        if (prefab == null || step == null) return;

        if (step.slashDelay > 0f)
            _pending.Add(new Pending { prefab = prefab, step = step, isLeft = isLeft, at = Time.time + step.slashDelay });
        else
            Spawn(prefab, step, isLeft);
    }

    /// <summary>取消還沒播的延遲劍風（例如攻擊被 Dash 取消）。已經播出去的不動。</summary>
    public void CancelPending() => _pending.Clear();

    private void Update()
    {
        float now = Time.time;

        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (now < _pending[i].at) continue;
            Pending p = _pending[i];
            _pending.RemoveAt(i);
            Spawn(p.prefab, p.step, p.isLeft);
        }

        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var kv = _active[i];
            if (kv.Value.go == null) { _active.RemoveAt(i); continue; }
            if (now < kv.Value.releaseAt) continue;

            kv.Value.go.SetActive(false);
            _pool[kv.Key].Push(kv.Value);
            _active.RemoveAt(i);
        }
    }

    private void OnDisable()
    {
        _pending.Clear();
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var kv = _active[i];
            if (kv.Value.go != null)
            {
                kv.Value.go.SetActive(false);
                _pool[kv.Key].Push(kv.Value);
            }
        }
        _active.Clear();
    }

    private void Spawn(GameObject prefab, MeleeAttackStep step, bool isLeft)
    {
        Instance inst = Get(prefab);

        ComputeLocalPose(step, isLeft, inst.refRadius, out Vector3 pos, out Quaternion rot, out Vector3 scale);
        Transform t = inst.go.transform;
        t.SetParent(Model, false);
        t.localPosition = pos;
        t.localRotation = rot;
        t.localScale = scale;

        inst.go.SetActive(true);
        foreach (ParticleSystem ps in inst.roots)
        {
            ps.Clear(true);
            ps.Play(true);
        }

        inst.releaseAt = Time.time + releaseAfter;
        _active.Add(new KeyValuePair<GameObject, Instance>(prefab, inst));
    }

    private Instance Get(GameObject prefab)
    {
        if (!_pool.TryGetValue(prefab, out var stack))
        {
            stack = new Stack<Instance>();
            _pool[prefab] = stack;
        }

        while (stack.Count > 0)
        {
            Instance pooled = stack.Pop();
            if (pooled.go != null) return pooled;
        }

        GameObject go = Instantiate(prefab, Model, false);
        go.name = prefab.name + " (Slash)";
        go.SetActive(false);
        Prepare(go, worldSpaceChildren, removeChildren, immediate: false);  // 執行時是從動畫事件（HitboxOn）呼叫的，不能 DestroyImmediate

        return new Instance
        {
            go = go,
            roots = FindRootSystems(go),
            refRadius = MeasureReferenceRadius(go),
        };
    }

    // ═══════════════════════ 共用（Slash Angle Editor 也用） ═══════════════════════

    /// <summary>
    /// 整理一個劍風實例：移除不要的子物件、模擬空間改成 Local（名單內的維持 World）、關掉 Play On Awake。
    /// immediate = true 時用 DestroyImmediate（只限編輯器預覽）。
    /// 執行時用 false：動畫事件、物理回呼裡不能 DestroyImmediate。Destroy 要到這一格結束才生效，
    /// 所以先把子物件關掉並移出階層，這一格的 Play(true) 和後面的搜尋才不會再碰到它。
    /// </summary>
    public static void Prepare(GameObject go, string[] worldSpaceNames, string[] removeNames, bool immediate)
    {
        if (removeNames != null)
        {
            foreach (Transform child in go.GetComponentsInChildren<Transform>(true))
            {
                if (child == null) continue;   // 父物件剛被移除，連帶一起沒了
                if (child == go.transform || !NameIn(child.name, removeNames)) continue;
                if (immediate)
                {
                    DestroyImmediate(child.gameObject);
                }
                else
                {
                    child.gameObject.SetActive(false);
                    child.SetParent(null, false);
                    Destroy(child.gameObject);
                }
            }
        }

        foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            if (ps == null) continue;
            var main = ps.main;
            main.simulationSpace = NameIn(ps.name, worldSpaceNames)
                ? ParticleSystemSimulationSpace.World
                : ParticleSystemSimulationSpace.Local;
            main.playOnAwake = false;
            main.stopAction = ParticleSystemStopAction.None;
        }
    }

    /// <summary>
    /// 劍風網格在 localScale = 1 時的半徑（公尺）：主粒子系統的網格包圍盒離原點最遠的距離 × Start Size。
    /// Arc Range 自動縮放用：localScale = Arc Range ÷ 這個值。
    /// </summary>
    public static float MeasureReferenceRadius(GameObject go)
    {
        ParticleSystem ps = go.GetComponent<ParticleSystem>();
        if (ps == null) ps = go.GetComponentInChildren<ParticleSystem>(true);
        if (ps == null) return 1f;

        var main = ps.main;
        float size = main.startSize3D ? main.startSizeXMultiplier : main.startSizeMultiplier;

        var r = ps.GetComponent<ParticleSystemRenderer>();
        Mesh mesh = (r != null && r.renderMode == ParticleSystemRenderMode.Mesh) ? r.mesh : null;
        if (mesh == null) return Mathf.Max(0.01f, size * 0.5f);

        Bounds b = mesh.bounds;
        float ext = 0f;
        for (int i = 0; i < 3; i++)
            ext = Mathf.Max(ext, Mathf.Abs(b.min[i]), Mathf.Abs(b.max[i]));
        return Mathf.Max(0.01f, ext * size);
    }

    /// <summary>
    /// 這一段劍風在角色模型座標系裡的位置 / 旋轉 / 縮放。
    /// 右手沒有覆寫時，把左手的設定對角色的左右（模型的 X 軸）鏡像。
    /// </summary>
    public static void ComputeLocalPose(MeleeAttackStep step, bool isLeft, float refRadius,
                                        out Vector3 pos, out Quaternion rot, out Vector3 scale)
    {
        bool useRight = !isLeft && step.slashRightHandOverride;

        Vector3 p = useRight ? step.slashOffsetRight : step.slashOffset;
        Quaternion q = Quaternion.Euler(useRight ? step.slashRotationRight : step.slashRotation);
        bool flip = useRight ? step.slashFlipRight : step.slashFlip;

        if (!isLeft && !useRight)
        {
            // 對模型的 YZ 平面鏡像：位置 X 取反、旋轉取鏡像、揮擊方向反過來
            p.x = -p.x;
            q = MirrorX(q);
            flip = !flip;
        }

        float s = step.slashScaleMode == MeleeSlashScaleMode.ArcRange
            ? step.slashScale * Mathf.Max(0.01f, step.arcRange) / Mathf.Max(0.01f, refRadius)
            : step.slashScale;

        pos = p;
        rot = q;
        scale = new Vector3(flip ? -s : s, s, s);
    }

    /// <summary>對 YZ 平面（X 取反）鏡像一個旋轉。</summary>
    public static Quaternion MirrorX(Quaternion q) => new Quaternion(q.x, -q.y, -q.z, q.w);

    /// <summary>最上層的粒子系統（上面沒有其他粒子系統）。對它們 Play(true) 就會帶動全部。</summary>
    public static ParticleSystem[] FindRootSystems(GameObject go)
    {
        var list = new List<ParticleSystem>();
        foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
        {
            bool isChild = false;
            if (ps.transform != go.transform)
            {
                for (Transform p = ps.transform.parent; p != null; p = p.parent)
                {
                    if (p.GetComponent<ParticleSystem>() != null) { isChild = true; break; }
                    if (p == go.transform) break;
                }
            }
            if (!isChild) list.Add(ps);
        }
        return list.ToArray();
    }

    private static bool NameIn(string name, string[] names)
    {
        if (names == null) return false;
        foreach (string n in names)
            if (!string.IsNullOrEmpty(n) && name == n) return true;
        return false;
    }
}
