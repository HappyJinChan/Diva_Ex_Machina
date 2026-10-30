using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 敵人陰影的 LOD：只有鏡頭附近的敵人開即時陰影，其他的不投影。
///
/// 為什麼：即時陰影要把每個投影的物體在每一層陰影級聯裡再畫一次（含蒙皮），
/// 170 隻敵人全開時大約多花 10 ms。遠處的敵人在畫面上很小，少了影子也不容易察覺，
/// 所以只讓「最近的 maxRealShadows 隻、而且在 realShadowDistance 內」的敵人開真陰影。
/// （遠處要假影子的話，可以另外接 Decal 之類的做法，開關點在 SetReal。）
///
/// 用法：
///   1. 場景裡放一個空物件，掛上這個元件。
///   2. 敵人 Prefab 的 Cast Shadows 維持什麼都可以 —— 敵人第一次被看到時，這裡會先把它設成 Off，
///      之後由這個元件決定開關。
///
/// 敵人清單讀 GameManager.Instance.enemies，物件池重生的敵人也會自動套用。
/// </summary>
public class EnemyShadowLOD : MonoBehaviour
{
    [Tooltip("用來算距離的相機。留空會用 Camera.main。")]
    [SerializeField] private Camera targetCamera;

    [Tooltip("同時開即時陰影的敵人上限（取最近的幾隻）。")]
    [SerializeField, Min(0)] private int maxRealShadows = 30;

    [Tooltip("離相機這個距離內的敵人才有資格開即時陰影（公尺）。")]
    [SerializeField, Min(0f)] private float realShadowDistance = 40f;

    [Tooltip("已經開著的敵人，要超過 realShadowDistance + 這個值才會關掉。避免敵人在邊界來回走時影子閃爍。")]
    [SerializeField, Min(0f)] private float hysteresis = 5f;

    [Tooltip("多久重新決定一次（秒）。")]
    [SerializeField, Min(0.02f)] private float updateInterval = 0.25f;

    [Tooltip("開即時陰影時用的模式。")]
    [SerializeField] private ShadowCastingMode realShadowMode = ShadowCastingMode.On;

    [Tooltip("勾選 = MeshRenderer（裝甲、武器、掛件）也一起投影。不勾 = 只有 SkinnedMeshRenderer 投影。")]
    [SerializeField] private bool includeMeshRenderers = true;

    [Header("Debug（唯讀）")]
    [Tooltip("目前開著即時陰影的敵人數量。")]
    [SerializeField] private int activeRealShadows;

    private sealed class Entry
    {
        public Renderer[] casters;
        public bool real;
        public int seenTick;
    }

    private readonly Dictionary<GameObject, Entry> _entries = new Dictionary<GameObject, Entry>();
    private readonly List<KeyValuePair<float, Entry>> _candidates = new List<KeyValuePair<float, Entry>>(256);
    private readonly HashSet<Entry> _chosen = new HashSet<Entry>();
    private readonly List<Renderer> _tmpRenderers = new List<Renderer>(16);

    private static readonly System.Comparison<KeyValuePair<float, Entry>> ByDistance = (a, b) => a.Key.CompareTo(b.Key);

    private float _timer;
    private int _tick;

    private void Update()
    {
        _timer -= Time.deltaTime;
        if (_timer > 0f) return;
        _timer = updateInterval;
        Refresh();
    }

    private void OnDisable()
    {
        // 元件被關掉時全部關掉即時陰影，不要留一堆開著的
        foreach (var kv in _entries)
            if (kv.Key != null) SetReal(kv.Value, false);
        activeRealShadows = 0;
    }

    private void Refresh()
    {
        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        List<GameObject> enemies = GameManager.Instance != null ? GameManager.Instance.enemies : null;
        if (cam == null || enemies == null) return;

        _tick++;
        Vector3 camPos = cam.transform.position;
        float enter = realShadowDistance * realShadowDistance;
        float exitDist = realShadowDistance + hysteresis;
        float exit = exitDist * exitDist;

        // 1. 收集有資格的敵人（開著的用比較寬的距離，避免閃爍）
        _candidates.Clear();
        for (int i = 0; i < enemies.Count; i++)
        {
            GameObject go = enemies[i];
            if (go == null || !go.activeInHierarchy) continue;

            Entry e = GetEntry(go);
            e.seenTick = _tick;

            float sqr = (go.transform.position - camPos).sqrMagnitude;
            if (sqr <= (e.real ? exit : enter))
                _candidates.Add(new KeyValuePair<float, Entry>(sqr, e));
        }

        // 2. 取最近的 maxRealShadows 隻
        _candidates.Sort(ByDistance);
        _chosen.Clear();
        int n = Mathf.Min(maxRealShadows, _candidates.Count);
        for (int i = 0; i < n; i++) _chosen.Add(_candidates[i].Value);

        // 3. 套用。這一輪沒出現在清單裡的（死掉、回收、停用）一律關掉
        int active = 0;
        foreach (var kv in _entries)
        {
            if (kv.Key == null) continue;
            Entry e = kv.Value;
            bool want = e.seenTick == _tick && _chosen.Contains(e);
            if (want != e.real) SetReal(e, want);
            if (e.real) active++;
        }
        activeRealShadows = active;
    }

    private Entry GetEntry(GameObject go)
    {
        if (_entries.TryGetValue(go, out Entry e)) return e;

        // 投影的 Renderer：SkinnedMeshRenderer，必要時加上 MeshRenderer
        _tmpRenderers.Clear();
        _tmpRenderers.AddRange(go.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        if (includeMeshRenderers)
            _tmpRenderers.AddRange(go.GetComponentsInChildren<MeshRenderer>(true));

        e = new Entry
        {
            casters = _tmpRenderers.ToArray(),
            real = true,   // 先當作開著，下一行強制關掉 → 不管 Prefab 怎麼設，起點一律是 Off
        };
        SetReal(e, false);
        _entries[go] = e;
        return e;
    }

    private void SetReal(Entry e, bool real)
    {
        e.real = real;
        ShadowCastingMode mode = real ? realShadowMode : ShadowCastingMode.Off;
        for (int i = 0; i < e.casters.Length; i++)
            if (e.casters[i] != null) e.casters[i].shadowCastingMode = mode;
    }
}
