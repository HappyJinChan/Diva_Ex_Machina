// ShipHullOutline.cs — 貝努鳥號的船身輪廓與登船口接近點，給 PathFinder 繞船用。
//
// 取代原本掛在船上、會一直重新挖洞（NavMesh Carving）的 17 個 NavMeshObstacle：
// 地面 NavMesh 上不再有船，PathFinder 算完地面路徑後，用這裡的輪廓做 2D 檢查，
// 路徑穿過船身時改成沿著輪廓頂點繞過去。所有點都是船的子物件，存成船的本地座標，
// 每次讀取時再投影回世界座標，所以船開多快都不會落後。
//
// 擺放方式：
//   1. 這個元件掛在「真正的船」的 root（LandshipNavigation.realShip）上。
//   2. corners：沿著船身外側繞一圈的頂點（順時針或逆時針都可以），離船身留 5–10 m，
//      敵人會沿著這些點繞船。凸出的地方（側翼、登船口兩邊）要多放幾個點把形狀描出來。
//      可以先用右鍵選單「建立預設輪廓」產生一個長方形，再自己調整、增加。
//   3. approachPoints：每個登船口外側的接近點，順序要和 LandshipNavigation.dockingPoints 一致，
//      而且要放在輪廓「外面」。可以用右鍵選單「建立接近點」自動產生再微調。
//   選取這個物件時，場景中會畫出輪廓（青色）、接近點（黃色）和接近點到登船口的連線。
using System.Collections.Generic;
using UnityEngine;

public class ShipHullOutline : MonoBehaviour
{
    public static ShipHullOutline Instance { get; private set; }

    [Tooltip("船身外圍輪廓的頂點，依順序繞一圈。放在船身外側 5–10 m，必須是船的子物件。")]
    public Transform[] corners;

    [Tooltip("每個登船口外側的接近點，順序和 LandshipNavigation.dockingPoints 一致，要放在輪廓外面。")]
    public Transform[] approachPoints;

    [Header("右鍵選單自動建立用")]
    [Tooltip("「建立接近點」時，接近點離登船口多遠（往船外方向）。")]
    [Min(1f)] public float approachDistance = 25f;
    [Tooltip("「建立預設輪廓」時，輪廓離船身碰撞體範圍多遠。")]
    [Min(0f)] public float outlineMargin = 8f;

    void OnEnable() { Instance = this; }
    void OnDisable() { if (Instance == this) Instance = null; }

    public bool IsValid
    {
        get
        {
            if (corners == null || corners.Length < 3) return false;
            for (int i = 0; i < corners.Length; i++) if (corners[i] == null) return false;
            return true;
        }
    }

    public Transform GetApproach(int dockIndex)
    {
        if (approachPoints == null || dockIndex < 0 || dockIndex >= approachPoints.Length) return null;
        return approachPoints[dockIndex];
    }

    /// <summary>頂點 i 在 shipRoot 本地座標的位置。</summary>
    public Vector3 CornerLocal(Transform shipRoot, int i) => shipRoot.InverseTransformPoint(corners[i].position);

    /// <summary>把輪廓投影到 shipRoot 本地的 XZ 平面，寫進 buffer（不夠大時重新配置）。回傳頂點數。</summary>
    public int FillLocalPolygon(Transform shipRoot, ref Vector2[] buffer)
    {
        int n = corners.Length;
        if (buffer == null || buffer.Length < n) buffer = new Vector2[n];
        for (int i = 0; i < n; i++)
        {
            Vector3 p = shipRoot.InverseTransformPoint(corners[i].position);
            buffer[i] = new Vector2(p.x, p.z);
        }
        return n;
    }

    // ======================= 2D 幾何（船本地 XZ） =======================

    public static bool PointInPolygon(Vector2[] poly, int n, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            Vector2 a = poly[i], b = poly[j];
            if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x)
                inside = !inside;
        }
        return inside;
    }

    static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

    static bool SegmentsCross(Vector2 p, Vector2 q, Vector2 c, Vector2 d)
    {
        float d1 = Cross(p, q, c), d2 = Cross(p, q, d), d3 = Cross(c, d, p), d4 = Cross(c, d, q);
        return d1 * d2 < 0f && d3 * d4 < 0f;
    }

    /// <summary>
    /// 線段 p→q 是否穿過輪廓內部。skipA / skipB 是 p、q 本身所在的頂點（-1 = 不是頂點），
    /// 和它們相連的邊不檢查。
    /// </summary>
    public static bool Blocked(Vector2[] poly, int n, Vector2 p, Vector2 q, int skipA = -1, int skipB = -1)
    {
        for (int j = 0; j < n; j++)
        {
            int k = (j + 1) % n;
            if (j == skipA || k == skipA || j == skipB || k == skipB) continue;
            if (SegmentsCross(p, q, poly[j], poly[k])) return true;
        }
        return PointInPolygon(poly, n, (p + q) * 0.5f);
    }

    static float[] s_dist = new float[0];
    static int[] s_prev = new int[0];
    static bool[] s_done = new bool[0];

    /// <summary>
    /// 從 a 到 g、不穿過輪廓內部的最短折線（沿輪廓頂點）。route 依序填入要經過的頂點索引（不含 a、g）。
    /// 頂點只有十幾個，直接在可視圖上跑 Dijkstra。找不到路線回傳 false。
    /// </summary>
    public static bool ShortestRoute(Vector2[] poly, int n, Vector2 a, Vector2 g, List<int> route)
    {
        route.Clear();
        int A = n, G = n + 1, N = n + 2;
        if (s_dist.Length < N) { s_dist = new float[N]; s_prev = new int[N]; s_done = new bool[N]; }
        for (int i = 0; i < N; i++) { s_dist[i] = float.MaxValue; s_prev[i] = -1; s_done[i] = false; }
        s_dist[A] = 0f;

        Vector2 Pos(int i) => i == A ? a : i == G ? g : poly[i];

        bool Visible(int u, int v)
        {
            if (u < n && v < n)
            {
                int diff = Mathf.Abs(u - v);
                if (diff == 1 || diff == n - 1) return true;                // 相鄰頂點：輪廓的邊
                return !Blocked(poly, n, poly[u], poly[v], u, v);
            }
            int su = u < n ? u : -1, sv = v < n ? v : -1;
            return !Blocked(poly, n, Pos(u), Pos(v), su, sv);
        }

        for (int iter = 0; iter < N; iter++)
        {
            int u = -1;
            float best = float.MaxValue;
            for (int i = 0; i < N; i++)
                if (!s_done[i] && s_dist[i] < best) { best = s_dist[i]; u = i; }
            if (u < 0) break;
            if (u == G) break;
            s_done[u] = true;
            for (int v = 0; v < N; v++)
            {
                if (s_done[v] || v == u || v == A) continue;
                float nd = s_dist[u] + Vector2.Distance(Pos(u), Pos(v));
                if (nd >= s_dist[v]) continue;
                if (!Visible(u, v)) continue;
                s_dist[v] = nd;
                s_prev[v] = u;
            }
        }
        if (s_prev[G] < 0) return false;

        for (int v = s_prev[G]; v != A && v >= 0; v = s_prev[v]) route.Add(v);
        route.Reverse();
        return true;
    }

    // ======================= 編輯器輔助 =======================
#if UNITY_EDITOR
    [ContextMenu("建立預設輪廓（依碰撞體範圍）")]
    void CreateDefaultOutline()
    {
        bool any = false;
        Bounds b = default;
        foreach (var c in GetComponentsInChildren<Collider>())
        {
            if (c.isTrigger) continue;
            var lb = c.bounds;
            // 世界範圍的 8 個角轉回本地，求本地範圍
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? lb.min.x : lb.max.x, (i & 2) == 0 ? lb.min.y : lb.max.y, (i & 4) == 0 ? lb.min.z : lb.max.z);
                var lp = transform.InverseTransformPoint(corner);
                if (!any) { b = new Bounds(lp, Vector3.zero); any = true; } else b.Encapsulate(lp);
            }
        }
        if (!any) { Debug.LogWarning("[ShipHull] 船底下沒有碰撞體，無法建立預設輪廓"); return; }

        float m = outlineMargin, y = b.min.y;
        var pts = new[]
        {
            new Vector3(b.min.x - m, y, b.min.z - m), new Vector3(b.min.x - m, y, b.max.z + m),
            new Vector3(b.max.x + m, y, b.max.z + m), new Vector3(b.max.x + m, y, b.min.z - m),
        };
        var root = FindOrCreateChild("HullOutline");
        var list = new List<Transform>();
        for (int i = 0; i < pts.Length; i++)
        {
            var go = new GameObject($"Corner_{i:00}");
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Create Hull Outline");
            go.transform.SetParent(root, false);
            go.transform.localPosition = root.InverseTransformPoint(transform.TransformPoint(pts[i]));
            list.Add(go.transform);
        }
        UnityEditor.Undo.RecordObject(this, "Create Hull Outline");
        corners = list.ToArray();
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log("[ShipHull] 已建立 4 個頂點的長方形輪廓。請沿著船身形狀調整、增加頂點（選取頂點物件後複製再移動）。");
    }

    [ContextMenu("建立接近點（依登船口）")]
    void CreateApproachPoints()
    {
        var nav = FindAnyObjectByType<LandshipNavigation>();
        if (nav == null || nav.dockingPoints == null || nav.dockingPoints.Length == 0)
        {
            Debug.LogWarning("[ShipHull] 找不到 LandshipNavigation 或它沒有 dockingPoints");
            return;
        }
        var root = FindOrCreateChild("ApproachPoints");
        var list = new Transform[nav.dockingPoints.Length];
        for (int i = 0; i < nav.dockingPoints.Length; i++)
        {
            var dock = nav.dockingPoints[i];
            if (dock == null) continue;
            Vector3 outward = dock.position - transform.position;
            outward.y = 0f;
            outward = outward.sqrMagnitude > 0.01f ? outward.normalized : transform.right;
            var go = new GameObject($"Approach_{i}_{dock.name}");
            UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Create Approach Points");
            go.transform.SetParent(root, false);
            go.transform.position = dock.position + outward * approachDistance;
            list[i] = go.transform;
        }
        UnityEditor.Undo.RecordObject(this, "Create Approach Points");
        approachPoints = list;
        UnityEditor.EditorUtility.SetDirty(this);
        Debug.Log("[ShipHull] 已依登船口建立接近點（方向是「從船中心往登船口」）。船尾的登船口請手動確認方向，並確認都在輪廓外面。");
    }

    Transform FindOrCreateChild(string name)
    {
        var t = transform.Find(name);
        if (t != null) return t;
        var go = new GameObject(name);
        UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Create " + name);
        go.transform.SetParent(transform, false);
        return go.transform;
    }

    void OnDrawGizmosSelected()
    {
        if (IsValid)
        {
            Gizmos.color = Color.cyan;
            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 a = corners[i].position, b = corners[(i + 1) % corners.Length].position;
                Gizmos.DrawLine(a, b);
                Gizmos.DrawSphere(a, 2f);
            }
        }
        if (approachPoints == null) return;
        var nav = FindAnyObjectByType<LandshipNavigation>();
        for (int i = 0; i < approachPoints.Length; i++)
        {
            if (approachPoints[i] == null) continue;
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(approachPoints[i].position, 2.5f);
            if (nav != null && nav.dockingPoints != null && i < nav.dockingPoints.Length && nav.dockingPoints[i] != null)
                Gizmos.DrawLine(approachPoints[i].position, nav.dockingPoints[i].position);
        }
    }
#endif
}
