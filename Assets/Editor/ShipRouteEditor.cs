using System.Collections.Generic;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Splines;

// ═══════════════════════════════════════════════════════════════════════
//  ShipRouteGraph：新增起點、從 Spline 匯入、清理
// ═══════════════════════════════════════════════════════════════════════

[CustomEditor(typeof(ShipRouteGraph))]
public class ShipRouteGraphEditor : Editor
{
    SplineContainer _importSpline;
    float _importSpacing = 0f;

    public override void OnInspectorGUI()
    {
        var graph = (ShipRouteGraph)target;

        DrawDefaultInspector();

        List<ShipRouteNode> nodes = graph.Nodes;
        int junctions = 0, deadEnds = 0;
        foreach (var n in nodes)
        {
            if (n.IsJunction) junctions++;
            else if (n.IsDeadEnd) deadEnds++;
        }

        EditorGUILayout.Space();
        EditorGUILayout.HelpBox(
            $"節點 {nodes.Count} 個，分叉點 {junctions} 個，死路 {deadEnds} 個。\n" +
            "選取節點後，Inspector 有「往前延伸」「插入中間點」等按鈕；\n" +
            "選取兩個節點（Ctrl 加選）可以連接它們。",
            MessageType.Info);

        if (GUILayout.Button("新增起點（在 Scene 視窗中央）", GUILayout.Height(26)))
        {
            Vector3 pos = graph.transform.position;
            if (SceneView.lastActiveSceneView != null)
            {
                pos = SceneView.lastActiveSceneView.pivot;
                pos.y = graph.transform.position.y;
            }
            ShipRouteNode n = ShipRouteEditorUtil.CreateNode(graph, pos);
            Selection.activeGameObject = n.gameObject;
        }

        if (GUILayout.Button("清理空連線（刪掉節點後留下的 None）"))
            ShipRouteEditorUtil.CleanNulls(graph);

        // ── 從 Spline 匯入 ──
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("從 Spline 匯入", EditorStyles.boldLabel);
        _importSpline = (SplineContainer)EditorGUILayout.ObjectField("Spline", _importSpline, typeof(SplineContainer), true);
        _importSpacing = EditorGUILayout.FloatField(
            new GUIContent("取樣間距 (m)", "0 = 只用 Spline 的節點（Knot）。\n大於 0 = 沿曲線每隔這麼多公尺取一個點，保留彎曲形狀。"),
            Mathf.Max(0f, _importSpacing));

        using (new EditorGUI.DisabledScope(_importSpline == null))
        {
            if (GUILayout.Button("匯入（新增一條航線，不會刪掉現有節點）"))
                ShipRouteEditorUtil.ImportSpline(graph, _importSpline, _importSpacing);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  ShipRouteNode：延伸、插入、連接、刪除
// ═══════════════════════════════════════════════════════════════════════

[CustomEditor(typeof(ShipRouteNode)), CanEditMultipleObjects]
public class ShipRouteNodeEditor : Editor
{
    const string kExtendPref = "ShipRoute.ExtendDistance";

    public override void OnInspectorGUI()
    {
        // ── 選了兩個節點：連接 ──
        if (targets.Length == 2)
        {
            DrawConnectTwo();
            return;
        }
        if (targets.Length > 2)
        {
            EditorGUILayout.HelpBox("選取兩個節點可以連接它們。", MessageType.Info);
            return;
        }

        var node = (ShipRouteNode)target;
        ShipRouteGraph graph = node.GetComponentInParent<ShipRouteGraph>();

        DrawDefaultInspector();

        if (graph == null)
        {
            EditorGUILayout.HelpBox("這個節點不在 ShipRouteGraph 底下，母艦找不到它。", MessageType.Warning);
            return;
        }

        // ── 往前延伸 ──
        EditorGUILayout.Space();
        float dist = EditorPrefs.GetFloat(kExtendPref, 500f);
        float newDist = EditorGUILayout.FloatField(new GUIContent("延伸距離 (m)", "「往前延伸」新節點離這個點多遠。"), dist);
        if (!Mathf.Approximately(newDist, dist)) EditorPrefs.SetFloat(kExtendPref, Mathf.Max(1f, newDist));

        string extendLabel = node.ExitCount == 0 ? "往前延伸" : "往前延伸（新增一條分支）";
        if (GUILayout.Button(extendLabel, GUILayout.Height(28)))
        {
            ShipRouteNode n = ShipRouteEditorUtil.Extend(graph, node, Mathf.Max(1f, newDist));
            Selection.activeGameObject = n.gameObject;
        }

        // ── 出口 ──
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("出口", EditorStyles.boldLabel);
        if (node.ExitCount == 0)
            EditorGUILayout.HelpBox("死路：母艦會停在這裡。", MessageType.None);

        for (int i = 0; i < node.next.Count; i++)
        {
            ShipRouteNode exit = node.next[i];
            if (exit == null) continue;

            using (new EditorGUILayout.HorizontalScope())
            {
                bool isDefault = node.IsJunction && exit == node.DefaultNext;
                GUILayout.Label((isDefault ? "★ " : "   ") + exit.name, GUILayout.MinWidth(80));

                if (GUILayout.Button("選取", GUILayout.Width(44)))
                    Selection.activeGameObject = exit.gameObject;

                if (GUILayout.Button("插入中間點", GUILayout.Width(80)))
                {
                    ShipRouteNode mid = ShipRouteEditorUtil.InsertBetween(graph, node, exit);
                    Selection.activeGameObject = mid.gameObject;
                    GUIUtility.ExitGUI();
                }

                using (new EditorGUI.DisabledScope(!node.IsJunction || isDefault))
                {
                    if (GUILayout.Button("設為預設", GUILayout.Width(64)))
                    {
                        Undo.RecordObject(node, "Set Default Branch");
                        node.defaultBranch = i;
                        EditorUtility.SetDirty(node);
                    }
                }

                if (GUILayout.Button("斷開", GUILayout.Width(44)))
                {
                    Undo.RecordObject(node, "Disconnect Route");
                    node.next.RemoveAt(i);
                    if (node.defaultBranch >= node.next.Count) node.defaultBranch = 0;
                    EditorUtility.SetDirty(node);
                    GUIUtility.ExitGUI();
                }
            }
        }

        // ── 入口 ──
        List<ShipRouteNode> incoming = ShipRouteEditorUtil.Incoming(graph, node);
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("入口（哪些點會來到這裡）", EditorStyles.boldLabel);
        if (incoming.Count == 0)
            EditorGUILayout.HelpBox("沒有入口：這是起點。", MessageType.None);
        foreach (var p in incoming)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("   " + p.name);
                if (GUILayout.Button("選取", GUILayout.Width(44)))
                    Selection.activeGameObject = p.gameObject;
            }
        }

        // ── 刪除 ──
        EditorGUILayout.Space();
        if (GUILayout.Button("刪除這個節點（前後自動接起來）"))
        {
            ShipRouteEditorUtil.DeleteAndBridge(graph, node);
            GUIUtility.ExitGUI();
        }
    }

    void DrawConnectTwo()
    {
        // Selection.activeObject 是最後點選的那個 → 當作 B
        var t0 = (ShipRouteNode)targets[0];
        var t1 = (ShipRouteNode)targets[1];
        ShipRouteNode b = (Selection.activeGameObject == t0.gameObject) ? t0 : t1;
        ShipRouteNode a = (b == t0) ? t1 : t0;

        EditorGUILayout.HelpBox("先選的是 A，最後點選的是 B。", MessageType.Info);

        DrawConnectButton(a, b);
        DrawConnectButton(b, a);
    }

    static void DrawConnectButton(ShipRouteNode from, ShipRouteNode to)
    {
        bool exists = from.HasExit(to);
        string label = exists ? $"斷開 {from.name} → {to.name}" : $"連接 {from.name} → {to.name}";
        if (GUILayout.Button(label, GUILayout.Height(26)))
        {
            Undo.RecordObject(from, exists ? "Disconnect Route" : "Connect Route");
            if (exists) from.next.Remove(to);
            else from.next.Add(to);
            if (from.defaultBranch >= from.next.Count) from.defaultBranch = 0;
            EditorUtility.SetDirty(from);
        }
    }
}

// ═══════════════════════════════════════════════════════════════════════
//  共用操作（都支援 Undo）
// ═══════════════════════════════════════════════════════════════════════

public static class ShipRouteEditorUtil
{
    public static ShipRouteNode CreateNode(ShipRouteGraph graph, Vector3 worldPos)
    {
        var go = new GameObject(NextName(graph));
        Undo.RegisterCreatedObjectUndo(go, "Create Route Node");
        go.transform.SetParent(graph.transform, false);
        go.transform.position = worldPos;
        return Undo.AddComponent<ShipRouteNode>(go);
    }

    /// <summary>在 node 前方建立新節點並連線（node → 新節點）。</summary>
    public static ShipRouteNode Extend(ShipRouteGraph graph, ShipRouteNode node, float distance)
    {
        // 方向：沿著「從入口來的方向」繼續；沒有入口就沿著第一個出口的反方向…都沒有就朝 +Z
        Vector3 dir = Vector3.forward;
        List<ShipRouteNode> inc = Incoming(graph, node);
        if (inc.Count > 0) dir = node.Position - inc[0].Position;
        else if (node.DefaultNext != null) dir = node.DefaultNext.Position - node.Position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f) dir = Vector3.forward;
        dir.Normalize();

        // 已經有出口（新增分支）：往旁邊偏 35°，不要跟原本的出口疊在一起
        if (node.ExitCount > 0) dir = Quaternion.Euler(0f, 35f, 0f) * dir;

        ShipRouteNode n = CreateNode(graph, node.Position + dir * distance);
        n.transform.SetSiblingIndex(node.transform.GetSiblingIndex() + 1);

        Undo.RecordObject(node, "Extend Route");
        node.next.Add(n);
        EditorUtility.SetDirty(node);
        return n;
    }

    /// <summary>在 a → b 中間插入新節點：a → mid → b。</summary>
    public static ShipRouteNode InsertBetween(ShipRouteGraph graph, ShipRouteNode a, ShipRouteNode b)
    {
        ShipRouteNode mid = CreateNode(graph, (a.Position + b.Position) * 0.5f);
        mid.transform.SetSiblingIndex(a.transform.GetSiblingIndex() + 1);

        Undo.RecordObject(a, "Insert Route Node");
        int i = a.next.IndexOf(b);
        if (i >= 0) a.next[i] = mid;   // 同一個位置 → 預設分支不變
        else a.next.Add(mid);
        EditorUtility.SetDirty(a);

        mid.next.Add(b);
        return mid;
    }

    /// <summary>刪除節點；所有入口改接到它的所有出口。</summary>
    public static void DeleteAndBridge(ShipRouteGraph graph, ShipRouteNode node)
    {
        List<ShipRouteNode> inc = Incoming(graph, node);
        foreach (var p in inc)
        {
            Undo.RecordObject(p, "Delete Route Node");
            int idx = p.next.IndexOf(node);
            p.next.RemoveAll(x => x == node);
            foreach (var exit in node.next)
            {
                if (exit == null || exit == p || exit == node || p.next.Contains(exit)) continue;
                if (idx >= 0 && idx <= p.next.Count) p.next.Insert(idx++, exit);
                else p.next.Add(exit);
            }
            if (p.defaultBranch >= p.next.Count) p.defaultBranch = 0;
            EditorUtility.SetDirty(p);
        }

        GameObject select = inc.Count > 0 ? inc[0].gameObject : graph.gameObject;
        Undo.DestroyObjectImmediate(node.gameObject);
        Selection.activeGameObject = select;
    }

    public static List<ShipRouteNode> Incoming(ShipRouteGraph graph, ShipRouteNode node)
    {
        var list = new List<ShipRouteNode>();
        foreach (var n in graph.Nodes)
            if (n != node && n.next.Contains(node)) list.Add(n);
        return list;
    }

    public static void CleanNulls(ShipRouteGraph graph)
    {
        int removed = 0;
        foreach (var n in graph.Nodes)
        {
            int before = n.next.Count;
            if (n.next.Exists(x => x == null))
            {
                Undo.RecordObject(n, "Clean Route");
                n.next.RemoveAll(x => x == null);
                if (n.defaultBranch >= n.next.Count) n.defaultBranch = 0;
                EditorUtility.SetDirty(n);
            }
            removed += before - n.next.Count;
        }
        Debug.Log($"[ShipRoute] 清掉 {removed} 條空連線。", graph);
    }

    /// <summary>把 SplineContainer 裡每一條 Spline 轉成一串節點（封閉的會接回起點）。</summary>
    public static void ImportSpline(ShipRouteGraph graph, SplineContainer container, float spacing)
    {
        int group = Undo.GetCurrentGroup();
        Matrix4x4 m = container.transform.localToWorldMatrix;
        int total = 0;

        for (int s = 0; s < container.Splines.Count; s++)
        {
            Spline spline = container.Splines[s];
            if (spline == null || spline.Count < 2) continue;

            var points = new List<Vector3>();
            if (spacing <= 0f)
            {
                for (int k = 0; k < spline.Count; k++)
                    points.Add(m.MultiplyPoint3x4((Vector3)spline[k].Position));
            }
            else
            {
                float len = SplineUtility.CalculateLength(spline, m);
                int n = Mathf.Max(2, Mathf.CeilToInt(len / spacing));
                int last = spline.Closed ? n - 1 : n;   // 封閉：最後一點等於第一點，不重複
                for (int i = 0; i <= last; i++)
                {
                    float3 p = SplineUtility.EvaluatePosition(spline, i / (float)n);
                    points.Add(m.MultiplyPoint3x4((Vector3)p));
                }
            }

            ShipRouteNode first = null, prev = null;
            foreach (Vector3 p in points)
            {
                ShipRouteNode node = CreateNode(graph, p);
                if (first == null) first = node;
                if (prev != null) prev.next.Add(node);
                prev = node;
                total++;
            }
            if (spline.Closed && prev != null && first != null && prev != first)
                prev.next.Add(first);
        }

        Undo.CollapseUndoOperations(group);
        Debug.Log($"[ShipRoute] 從 {container.name} 匯入 {total} 個節點。", graph);
    }

    static string NextName(ShipRouteGraph graph)
    {
        int max = 0;
        foreach (Transform c in graph.transform)
        {
            string n = c.name;
            if (n.StartsWith("Node ") && int.TryParse(n.Substring(5), out int v) && v > max) max = v;
        }
        return $"Node {max + 1:000}";
    }
}
