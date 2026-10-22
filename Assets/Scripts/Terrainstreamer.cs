using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 依追蹤目標與各 Terrain 之間的水平距離，動態啟用 / 停用 Terrain 物件，
/// 藉此降低遠處地形的渲染與物理負擔。
/// 掛在「Terrain Group」這種父物件上；會自動蒐集其底下所有 Terrain（含一開始為 inactive 的）。
/// 注意：SetActive(false) 只停掉渲染與物理，並不會釋放 TerrainData 佔用的記憶體。
///
/// 追蹤目標可以有很多個，任何一個目標附近的地形都會啟用：
///   · target：主要目標（玩家）。留空時可改用 Camera.main。
///   · extraTargets：在 Inspector 手動指定的其他目標（例如場景裡的母艦）。
///   · TerrainStreamTarget：掛在物件上就會自動登記 / 取消登記。
///     適合執行期才生成的物件（Boss、母艦 prefab），不用回頭改這裡的設定。
/// </summary>
[DisallowMultipleComponent]
public class TerrainStreamer : MonoBehaviour
{
    [Header("追蹤目標")]
    [Tooltip("主要目標（玩家）的 Transform。留空時若下方選項開啟，會自動改用 Camera.main。")]
    public Transform target;

    [Tooltip("target 為空時，是否自動改用主攝影機作為追蹤目標。")]
    public bool useMainCameraIfNull = true;

    [Tooltip("其他要追蹤的目標（例如母艦）。執行期才生成的物件改用 TerrainStreamTarget 元件。")]
    public List<Transform> extraTargets = new List<Transform>();

    [Header("距離設定（水平 XZ，世界單位）")]
    [Tooltip("目標與 Terrain 中心的水平距離小於此值 → 啟用。")]
    public float loadRadius = 3000f;

    [Tooltip("目標與 Terrain 中心的水平距離大於此值 → 停用。\n必須大於 loadRadius，兩者之間為遲滯區間，避免在邊界反覆開關。")]
    public float unloadRadius = 4000f;

    [Header("更新")]
    [Tooltip("每隔幾秒重新計算一次。設 0 代表每幀都算。")]
    public float checkInterval = 0.5f;

    [Header("除錯")]
    [Tooltip("選取此物件時，於 Scene 視窗畫出每個目標的 load / unload 範圍圈。")]
    public bool drawGizmos = true;

    struct Tile
    {
        public GameObject go;
        public Vector3 center;   // 世界座標，快取值（假設地形為靜態，不隨執行期移動）
    }

    // 一次計算用的目標：位置 + 這個目標自己的 load / unload 半徑（平方）
    struct Probe
    {
        public float x, z;
        public float loadSq, unloadSq;
    }

    readonly List<Tile> _tiles = new List<Tile>();
    readonly List<Probe> _probes = new List<Probe>(8);
    float _timer;
    bool _warned;

    // ── TerrainStreamTarget 登記 ────────────────────────────────────────

    static readonly List<TerrainStreamTarget> s_registered = new List<TerrainStreamTarget>();
    static bool s_dirty;   // 有目標新登記 → 下一幀立刻重算（剛生成的 Boss 腳下地形要馬上開，不然會掉下去）

    internal static void Register(TerrainStreamTarget t)
    {
        if (t != null && !s_registered.Contains(t))
        {
            s_registered.Add(t);
            s_dirty = true;
        }
    }

    internal static void Unregister(TerrainStreamTarget t)
    {
        s_registered.Remove(t);
    }

    // ── 生命週期 ─────────────────────────────────────────────────────────

    void Awake()
    {
        RefreshTiles();
    }

    void Start()
    {
        ResolveTarget();
        if (!HasAnyTarget())
        {
            Debug.LogWarning("[TerrainStreamer] 找不到任何追蹤目標，所有 Terrain 將維持原狀。請在 Inspector 指定 target。", this);
            _warned = true;
            return;
        }

        Cull(true);                 // 建立初始狀態
        _timer = checkInterval;
    }

    void Update()
    {
        if (target == null && !_warned) ResolveTarget();

        if (s_dirty)
        {
            s_dirty = false;
            _timer = 0f;
        }

        _timer -= Time.deltaTime;
        if (_timer > 0f) return;

        _timer = Mathf.Max(0f, checkInterval);
        Cull(false);
    }

    /// <summary>重新蒐集底下所有 Terrain（例如執行期新增地形後呼叫）。</summary>
    public void RefreshTiles()
    {
        _tiles.Clear();

        Terrain[] terrains = GetComponentsInChildren<Terrain>(true);
        for (int i = 0; i < terrains.Length; i++)
        {
            Terrain t = terrains[i];
            if (t == null) continue;

            _tiles.Add(new Tile
            {
                go = t.gameObject,
                center = ComputeCenter(t),
            });
        }
    }

    /// <summary>由外部（例如切換操控時）改變主要追蹤目標，並立即重建狀態。</summary>
    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
        _warned = false;
        if (target != null) Cull(true);
    }

    void ResolveTarget()
    {
        if (target != null) return;
        if (useMainCameraIfNull && Camera.main != null)
            target = Camera.main.transform;
    }

    bool HasAnyTarget()
    {
        if (target != null) return true;
        for (int i = 0; i < extraTargets.Count; i++)
            if (extraTargets[i] != null) return true;
        for (int i = 0; i < s_registered.Count; i++)
            if (s_registered[i] != null) return true;
        return false;
    }

    // ── 計算 ─────────────────────────────────────────────────────────────

    void CollectProbes()
    {
        _probes.Clear();

        float loadSq = loadRadius * loadRadius;
        float unloadSq = unloadRadius * unloadRadius;

        if (target != null) AddProbe(target.position, loadSq, unloadSq);

        for (int i = 0; i < extraTargets.Count; i++)
        {
            Transform t = extraTargets[i];
            if (t != null && t != target) AddProbe(t.position, loadSq, unloadSq);
        }

        for (int i = s_registered.Count - 1; i >= 0; i--)
        {
            TerrainStreamTarget st = s_registered[i];
            if (st == null) { s_registered.RemoveAt(i); continue; }

            if (st.LoadRadiusOverride > 0f)
            {
                // 自訂半徑：遲滯區間沿用 streamer 的（unloadRadius - loadRadius）
                float l = st.LoadRadiusOverride;
                float u = l + Mathf.Max(0f, unloadRadius - loadRadius);
                AddProbe(st.transform.position, l * l, u * u);
            }
            else
            {
                AddProbe(st.transform.position, loadSq, unloadSq);
            }
        }
    }

    void AddProbe(Vector3 p, float loadSq, float unloadSq)
    {
        _probes.Add(new Probe { x = p.x, z = p.z, loadSq = loadSq, unloadSq = unloadSq });
    }

    void Cull(bool initial)
    {
        CollectProbes();
        if (_probes.Count == 0) return;   // 沒有目標：維持原狀（跟原本 target 為空時一樣）

        for (int i = 0; i < _tiles.Count; i++)
        {
            GameObject go = _tiles[i].go;
            if (go == null) continue;

            Vector3 c = _tiles[i].center;
            bool active = go.activeSelf;

            // 初始化 / 已停用：任一目標進入 loadRadius 才開
            // 已啟用：所有目標都超過 unloadRadius 才關
            bool useUnload = !initial && active;
            bool shouldBeActive = false;

            for (int k = 0; k < _probes.Count; k++)
            {
                Probe pr = _probes[k];
                float dx = c.x - pr.x;
                float dz = c.z - pr.z;
                float distSq = dx * dx + dz * dz;   // 只算水平距離，母艦拉高時不會誤關腳下地形

                if (distSq <= (useUnload ? pr.unloadSq : pr.loadSq))
                {
                    shouldBeActive = true;
                    break;
                }
            }

            if (shouldBeActive != active)
                go.SetActive(shouldBeActive);
        }
    }

    static Vector3 ComputeCenter(Terrain t)
    {
        // Unity Terrain 的 transform.position 是「左下角」，加上一半尺寸才是中心
        if (t.terrainData != null)
        {
            Vector3 size = t.terrainData.size;
            return t.transform.position + new Vector3(size.x * 0.5f, 0f, size.z * 0.5f);
        }
        return t.transform.position;
    }

    [ContextMenu("Refresh Tiles")]
    void ContextRefresh() => RefreshTiles();

    // ── 除錯 ─────────────────────────────────────────────────────────────

    void OnDrawGizmosSelected()
    {
        if (!drawGizmos) return;

        Transform main = target != null
            ? target
            : (useMainCameraIfNull && Camera.main != null ? Camera.main.transform : null);
        if (main != null) DrawRings(main.position, loadRadius, unloadRadius);

        for (int i = 0; i < extraTargets.Count; i++)
            if (extraTargets[i] != null && extraTargets[i] != main)
                DrawRings(extraTargets[i].position, loadRadius, unloadRadius);

        for (int i = 0; i < s_registered.Count; i++)
        {
            TerrainStreamTarget st = s_registered[i];
            if (st == null) continue;
            float l = st.LoadRadiusOverride > 0f ? st.LoadRadiusOverride : loadRadius;
            float u = l + Mathf.Max(0f, unloadRadius - loadRadius);
            DrawRings(st.transform.position, l, u);
        }
    }

    static void DrawRings(Vector3 c, float load, float unload)
    {
        Gizmos.color = new Color(0.3f, 1f, 0.4f, 1f);
        DrawRingXZ(c, load);
        Gizmos.color = new Color(1f, 0.4f, 0.3f, 1f);
        DrawRingXZ(c, unload);
    }

    static void DrawRingXZ(Vector3 center, float radius, int segments = 64)
    {
        if (radius <= 0f) return;

        Vector3 prev = center + new Vector3(radius, 0f, 0f);
        for (int i = 1; i <= segments; i++)
        {
            float a = (i / (float)segments) * Mathf.PI * 2f;
            Vector3 next = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            Gizmos.DrawLine(prev, next);
            prev = next;
        }
    }
}
