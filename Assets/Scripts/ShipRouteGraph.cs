using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// 母艦航線圖：底下的 ShipRouteNode 子物件用單向連線串成路網，可以分叉、匯合、繞圈。
/// 母艦用 ShipRouteFollower 沿著它走。
///
/// 編輯（Scene 視窗 + Inspector）：
///   · 選取這個物件 → Inspector「新增起點」建立第一個節點。
///   · 選取節點 → 「往前延伸」：在前方建立新節點、自動連線、並選取它，連續按就能一路畫下去。
///   · 選取兩個節點（先選起點，Ctrl 加選終點）→ 「連接 A → B」做出分叉或匯合。
///   · 選取節點 → 「插入中間點」在它和下一個點之間加一個點，方便彎得更圓。
///   · 移動節點就用一般的移動工具。航線只看 X、Z。
///   · 已經有 Spline 的話，Inspector 的「從 Spline 匯入」可以把節點轉過來。
///
/// Scene 視窗：箭頭 = 行進方向；紅點 = 分叉點；橘點 = 死路（母艦會停在那裡）。
/// </summary>
[DisallowMultipleComponent]
public class ShipRouteGraph : MonoBehaviour
{
    [Header("Gizmos")]
    public Color lineColor = new Color(0.55f, 1f, 0.45f, 1f);
    public Color nodeColor = new Color(0.55f, 1f, 0.45f, 1f);
    public Color junctionColor = new Color(1f, 0.35f, 0.45f, 1f);
    public Color deadEndColor = new Color(1f, 0.6f, 0.1f, 1f);
    [Tooltip("線的粗細（像素）。")]
    public float lineWidth = 4f;
    [Tooltip("節點、箭頭在畫面上的大小（跟縮放無關）。")]
    public float handleScale = 1f;
    [Tooltip("每隔多少公尺在線上畫一個方向箭頭。0 = 只在中間畫一個。")]
    public float arrowSpacing = 500f;
    public bool showLabels = true;

    readonly List<ShipRouteNode> _nodes = new List<ShipRouteNode>();

    /// <summary>底下所有節點（含停用中的）。每次呼叫都重新蒐集，節點數量不多，開銷可以忽略。</summary>
    public List<ShipRouteNode> Nodes
    {
        get
        {
            _nodes.Clear();
            GetComponentsInChildren(true, _nodes);
            return _nodes;
        }
    }

    /// <summary>
    /// 找最接近 worldPos 的一段航線（只看 XZ）。
    /// 回傳該段的起點 from、終點 to、以及在段上的比例 t（0 = from，1 = to）。
    /// </summary>
    public bool FindNearestEdge(Vector3 worldPos, out ShipRouteNode from, out ShipRouteNode to, out float t)
    {
        from = to = null;
        t = 0f;
        float best = float.MaxValue;

        List<ShipRouteNode> nodes = Nodes;
        for (int i = 0; i < nodes.Count; i++)
        {
            ShipRouteNode a = nodes[i];
            if (a == null) continue;
            for (int k = 0; k < a.next.Count; k++)
            {
                ShipRouteNode b = a.next[k];
                if (b == null || b == a) continue;

                float s = ProjectXZ(worldPos, a.Position, b.Position);
                Vector3 p = Vector3.Lerp(a.Position, b.Position, s);
                float dx = p.x - worldPos.x, dz = p.z - worldPos.z;
                float d = dx * dx + dz * dz;
                if (d < best)
                {
                    best = d;
                    from = a;
                    to = b;
                    t = s;
                }
            }
        }
        return from != null;
    }

    /// <summary>p 投影到線段 a→b 上的比例（只看 XZ，夾在 0～1）。</summary>
    public static float ProjectXZ(Vector3 p, Vector3 a, Vector3 b)
    {
        float abx = b.x - a.x, abz = b.z - a.z;
        float len2 = abx * abx + abz * abz;
        if (len2 < 0.0001f) return 1f;
        float s = ((p.x - a.x) * abx + (p.z - a.z) * abz) / len2;
        return Mathf.Clamp01(s);
    }

    public static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x, dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

#if UNITY_EDITOR
    // ── Scene 視窗繪製 ──────────────────────────────────────────────────
    // 用 Handles 畫，大小跟著畫面縮放（地圖 33 km，用世界單位的 Gizmo 拉遠就看不到了）。

    void OnDrawGizmos()
    {
        List<ShipRouteNode> nodes = Nodes;
        Color oldColor = Handles.color;
        var labelStyle = new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = Color.white } };

        for (int i = 0; i < nodes.Count; i++)
        {
            ShipRouteNode a = nodes[i];
            if (a == null) continue;
            Vector3 pa = a.Position;

            // 線 + 箭頭
            Handles.color = lineColor;
            for (int k = 0; k < a.next.Count; k++)
            {
                ShipRouteNode b = a.next[k];
                if (b == null || b == a) continue;
                Vector3 pb = b.Position;

                Handles.DrawAAPolyLine(lineWidth, pa, pb);
                DrawArrows(pa, pb);

                // 分叉點的預設出口：在出口附近畫一個小圈標示
                if (a.IsJunction && b == a.DefaultNext)
                {
                    Vector3 mark = Vector3.Lerp(pa, pb, Mathf.Min(0.15f, 200f / Mathf.Max(1f, DistanceXZ(pa, pb))));
                    float ms = HandleUtility.GetHandleSize(mark) * 0.12f * handleScale;
                    Handles.DrawWireDisc(mark, Vector3.up, ms);
                }
            }

            // 節點
            Handles.color = a.IsJunction ? junctionColor : (a.IsDeadEnd ? deadEndColor : nodeColor);
            float size = HandleUtility.GetHandleSize(pa) * 0.12f * handleScale;
            if (a.IsJunction || a.IsDeadEnd) size *= 1.5f;
            Handles.DrawSolidDisc(pa, Vector3.up, size);

            if (showLabels)
                Handles.Label(pa + Vector3.up * size * 2f, a.name, labelStyle);
        }

        Handles.color = oldColor;
    }

    void DrawArrows(Vector3 a, Vector3 b)
    {
        float len = DistanceXZ(a, b);
        if (len < 0.01f) return;

        int count = (arrowSpacing > 0f) ? Mathf.Max(1, Mathf.FloorToInt(len / arrowSpacing)) : 1;
        Vector3 dir = new Vector3(b.x - a.x, 0f, b.z - a.z) / len;
        Vector3 side = new Vector3(-dir.z, 0f, dir.x);

        for (int i = 0; i < count; i++)
        {
            float s = (i + 0.5f) / count;
            Vector3 tip = Vector3.Lerp(a, b, s);
            float hs = HandleUtility.GetHandleSize(tip) * 0.15f * handleScale;
            Vector3 back = tip - dir * hs;
            Handles.DrawAAPolyLine(lineWidth, back + side * hs * 0.6f, tip, back - side * hs * 0.6f);
        }
    }
#endif
}
