// NavMeshTileGridEditor.cs — NavMeshTileGrid 的 Inspector：自動切格、輪流烘焙、產生接縫 Link。
// 流程：1. 依地形範圍自動設定 → 2. 建立格子 → 3. 烘焙尚未烘焙的格子 → 4. 產生接縫 Link
// 烘焙一次只做一格，每格烘完立刻存檔並釋放記憶體，中途可以取消，下次「烘焙尚未烘焙的格子」會接著做。
// NavMesh 資料存在場景旁邊的 <場景名>_NavMeshTiles/ 資料夾。
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Debug = UnityEngine.Debug;

namespace DivaExMachina.Navigation.EditorTools
{
    [CustomEditor(typeof(NavMeshTileGrid))]
    public class NavMeshTileGridEditor : Editor
    {
        const string LinksName = "Seam_Links";

        public override void OnInspectorGUI()
        {
            var g = (NavMeshTileGrid)target;
            DrawDefaultInspector();
            AgentTypePopup(g);

            var tiles = Tiles(g);
            int baked = tiles.Count(t => t.s.navMeshData != null);
            var linksRoot = g.transform.Find(LinksName);
            int links = linksRoot != null ? linksRoot.GetComponentsInChildren<NavMeshLink>(true).Length : 0;
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox($"格子 {g.countX} × {g.countZ}（每格 {g.tileSize:0} m）\n" +
                                    $"已建立 {tiles.Count} 格，已烘焙 {baked} 格；接縫 Link {links} 個", MessageType.None);

            if (GUILayout.Button("1. 依地形範圍自動設定")) FitToTerrain(g);
            if (GUILayout.Button("2. 建立格子（更新設定）")) CreateTiles(g);
            using (new EditorGUI.DisabledScope(tiles.Count == 0))
            {
                if (GUILayout.Button("3. 烘焙尚未烘焙的格子", GUILayout.Height(26))) Bake(g, tiles.Where(t => t.s.navMeshData == null).ToList());
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("烘焙選取的格子"))
                    {
                        var sel = new HashSet<GameObject>(Selection.gameObjects);
                        var pick = tiles.Where(t => sel.Contains(t.s.gameObject)).ToList();
                        if (Bake(g, pick)) GenerateLinks(g, new HashSet<(int, int)>(pick.Select(t => (t.x, t.z))));
                    }
                    if (GUILayout.Button("全部重新烘焙") &&
                        EditorUtility.DisplayDialog("全部重新烘焙", $"要重新烘焙全部 {tiles.Count} 格嗎？可能要很久。", "烘焙", "取消"))
                        Bake(g, tiles);
                }
                if (GUILayout.Button("4. 產生接縫 Link", GUILayout.Height(26))) GenerateLinks(g);
                if (GUILayout.Button("清除接縫 Link")) ClearLinks(g);
            }
            EditorGUILayout.HelpBox("加了建築之後：選取那些建築，執行 Tools > NavMesh Tiles > 重新烘焙選取物件所在的格子" +
                                    "（Ctrl+Shift+Alt+B），只會重烘碰到的格子和它們的接縫。", MessageType.Info);
        }

        // ======================= 工具 =======================
        struct Tile
        {
            public int x, z;
            public NavMeshSurface s;
        }

        static List<Tile> Tiles(NavMeshTileGrid g)
        {
            var list = new List<Tile>();
            foreach (Transform t in g.transform)
            {
                var s = t.GetComponent<NavMeshSurface>();
                if (s == null || !TryParse(t.name, out int x, out int z)) continue;
                list.Add(new Tile { x = x, z = z, s = s });
            }
            return list;
        }

        static bool TryParse(string name, out int x, out int z)
        {
            x = z = 0;
            var parts = name.Split('_');
            return parts.Length == 3 && parts[0] == "Tile" && int.TryParse(parts[1], out x) && int.TryParse(parts[2], out z);
        }

        static void AgentTypePopup(NavMeshTileGrid g)
        {
            int count = NavMesh.GetSettingsCount();
            var ids = new int[count];
            var names = new string[count];
            int index = 0;
            for (int i = 0; i < count; i++)
            {
                ids[i] = NavMesh.GetSettingsByIndex(i).agentTypeID;
                names[i] = NavMesh.GetSettingsNameFromID(ids[i]);
                if (ids[i] == g.agentTypeID) index = i;
            }
            int next = EditorGUILayout.Popup("Agent Type", index, names);
            if (count > 0 && ids[next] != g.agentTypeID)
            {
                Undo.RecordObject(g, "Change Agent Type");
                g.agentTypeID = ids[next];
                EditorUtility.SetDirty(g);
            }
        }

        static void Configure(NavMeshTileGrid g, NavMeshSurface s)
        {
            s.agentTypeID = g.agentTypeID;
            s.collectObjects = CollectObjects.Volume;
            s.center = new Vector3(0f, (g.minY + g.maxY) * 0.5f, 0f);
            s.size = new Vector3(g.tileSize + g.overlap, Mathf.Max(1f, g.maxY - g.minY), g.tileSize + g.overlap);
            s.layerMask = g.layerMask;
            s.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            s.defaultArea = 0;
            s.ignoreNavMeshAgent = true;
            s.ignoreNavMeshObstacle = true;
            s.overrideVoxelSize = g.overrideVoxelSize;
            s.voxelSize = g.voxelSize;
            s.overrideTileSize = g.overrideTileSize;
            s.tileSize = g.navTileSize;
            s.minRegionArea = g.minRegionArea;
            s.buildHeightMesh = false;
        }

        // ======================= 1. 依地形範圍自動設定 =======================
        static void FitToTerrain(NavMeshTileGrid g)
        {
            var terrains = Terrain.activeTerrains;
            if (terrains.Length == 0) { Debug.LogWarning("[NavTiles] 場景中沒有啟用中的 Terrain"); return; }
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
            foreach (var t in terrains)
            {
                var p = t.transform.position;
                var size = t.terrainData.size;
                minX = Mathf.Min(minX, p.x); minZ = Mathf.Min(minZ, p.z);
                maxX = Mathf.Max(maxX, p.x + size.x); maxZ = Mathf.Max(maxZ, p.z + size.z);
            }
            float T = g.tileSize;
            float x0 = Mathf.Floor(minX / T + 0.001f) * T, z0 = Mathf.Floor(minZ / T + 0.001f) * T;

            Undo.RecordObject(g.transform, "Fit NavMesh Tiles");
            Undo.RecordObject(g, "Fit NavMesh Tiles");
            g.transform.SetPositionAndRotation(new Vector3(x0, 0f, z0), Quaternion.identity);
            g.transform.localScale = Vector3.one;
            g.countX = Mathf.Max(1, Mathf.CeilToInt((maxX - x0) / T - 0.001f));
            g.countZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - z0) / T - 0.001f));
            EditorUtility.SetDirty(g);
            Debug.Log($"[NavTiles] 地形範圍 X {minX:0}–{maxX:0}、Z {minZ:0}–{maxZ:0} → {g.countX} × {g.countZ} 格");
        }

        // ======================= 2. 建立格子 =======================
        static void CreateTiles(NavMeshTileGrid g)
        {
            Physics.SyncTransforms();
            Undo.SetCurrentGroupName("Create NavMesh Tiles");
            int undoGroup = Undo.GetCurrentGroup();

            var existing = Tiles(g).ToDictionary(t => (t.x, t.z), t => t.s);
            int made = 0, updated = 0, empty = 0, removed = 0;
            var half = new Vector3((g.tileSize + g.overlap) * 0.5f, Mathf.Max(1f, g.maxY - g.minY) * 0.5f, (g.tileSize + g.overlap) * 0.5f);

            for (int x = 0; x < g.countX; x++)
                for (int z = 0; z < g.countZ; z++)
                {
                    var local = g.TileCenterLocal(x, z);
                    var center = g.transform.TransformPoint(local + Vector3.up * ((g.minY + g.maxY) * 0.5f));
                    existing.TryGetValue((x, z), out var s);
                    if (s == null && !Physics.CheckBox(center, half, Quaternion.identity, g.layerMask, QueryTriggerInteraction.Ignore))
                    {
                        empty++;                                                   // 範圍內沒有任何碰撞體（地圖外）
                        continue;
                    }
                    if (s == null)
                    {
                        var go = new GameObject(NavMeshTileGrid.TileName(x, z));
                        go.transform.SetParent(g.transform, false);
                        go.transform.localPosition = local;
                        Undo.RegisterCreatedObjectUndo(go, "Create NavMesh Tiles");
                        s = Undo.AddComponent<NavMeshSurface>(go);
                        made++;
                    }
                    else
                    {
                        Undo.RecordObject(s.transform, "Create NavMesh Tiles");
                        s.transform.localPosition = local;
                        updated++;
                    }
                    Undo.RecordObject(s, "Create NavMesh Tiles");
                    Configure(g, s);
                    EditorUtility.SetDirty(s);
                }

            foreach (var kv in existing)                                           // 超出格數的舊格子
                if (kv.Key.x >= g.countX || kv.Key.z >= g.countZ)
                {
                    Undo.DestroyObjectImmediate(kv.Value.gameObject);
                    removed++;
                }

            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log($"[NavTiles] 建立 {made} 格、更新 {updated} 格、略過 {empty} 格（沒有碰撞體）、移除 {removed} 格。" +
                      "改了格子設定後，已烘焙的格子要重新烘焙才會生效。");
        }

        // ======================= 3. 輪流烘焙 =======================
        // ======================= 只重烘選取物件所在的格子 =======================
        // 在 Hierarchy 或 Scene 選取剛加的建築（或格子本身），執行這個選單：
        // 找出範圍（含重疊區）碰到它們的格子，只烘焙那幾格，再重做那幾格的接縫 Link。
        [MenuItem("Tools/NavMesh Tiles/重新烘焙選取物件所在的格子 %#&b")]
        static void BakeTilesUnderSelection()
        {
            var sel = Selection.gameObjects;
            if (sel.Length == 0) { Debug.LogWarning("[NavTiles] 請先選取物件"); return; }
            var grids = Object.FindObjectsByType<NavMeshTileGrid>(FindObjectsSortMode.None);
            if (grids.Length == 0) { Debug.LogWarning("[NavTiles] 場景中沒有 NavMeshTileGrid"); return; }

            foreach (var g in grids)
            {
                var hit = TilesTouching(g, sel);
                if (hit.Count == 0) continue;
                Debug.Log($"[NavTiles] 重新烘焙 {hit.Count} 格：{string.Join(", ", hit.Select(t => t.s.name))}");
                if (Bake(g, hit)) GenerateLinks(g, new HashSet<(int, int)>(hit.Select(t => (t.x, t.z))));
            }
        }

        static List<Tile> TilesTouching(NavMeshTileGrid g, GameObject[] sel)
        {
            var bounds = new List<Bounds>();
            var direct = new HashSet<GameObject>();
            foreach (var go in sel)
            {
                if (go.transform.parent == g.transform && go.GetComponent<NavMeshSurface>() != null) { direct.Add(go); continue; }
                bool any = false;
                var b = new Bounds(go.transform.position, Vector3.zero);
                foreach (var c in go.GetComponentsInChildren<Collider>()) { if (any) b.Encapsulate(c.bounds); else { b = c.bounds; any = true; } }
                foreach (var r in go.GetComponentsInChildren<Renderer>()) { if (any) b.Encapsulate(r.bounds); else { b = r.bounds; any = true; } }
                bounds.Add(b);
            }
            return Tiles(g).Where(t =>
            {
                if (direct.Contains(t.s.gameObject)) return true;
                var vol = new Bounds(t.s.transform.TransformPoint(t.s.center), t.s.size);
                return bounds.Any(b => vol.Intersects(b));
            }).ToList();
        }

        static bool Bake(NavMeshTileGrid g, List<Tile> tiles)
        {
            if (tiles.Count == 0) { Debug.Log("[NavTiles] 沒有要烘焙的格子"); return false; }
            string dir = DataFolder(g);
            if (dir == null) return false;

            var sw = Stopwatch.StartNew();
            int done = 0, failed = 0;
            bool cancelled = false;
            try
            {
                for (int i = 0; i < tiles.Count; i++)
                {
                    var s = tiles[i].s;
                    float eta = done > 0 ? (float)sw.Elapsed.TotalSeconds / done * (tiles.Count - i) : 0f;
                    if (EditorUtility.DisplayCancelableProgressBar("烘焙 NavMesh",
                            $"{s.name}（{i + 1}/{tiles.Count}）  剩餘約 {eta / 60f:0.0} 分鐘", (float)i / tiles.Count))
                    {
                        cancelled = true;
                        break;
                    }

                    Configure(g, s);
                    var old = s.navMeshData;
                    string oldPath = old != null ? AssetDatabase.GetAssetPath(old) : null;
                    s.BuildNavMesh();
                    if (s.navMeshData == null || s.navMeshData == old) { failed++; continue; }

                    string path = $"{dir}/{s.name}.asset";
                    if (!string.IsNullOrEmpty(oldPath)) AssetDatabase.DeleteAsset(oldPath);
                    if (AssetDatabase.LoadMainAssetAtPath(path) != null) AssetDatabase.DeleteAsset(path);
                    AssetDatabase.CreateAsset(s.navMeshData, path);
                    EditorUtility.SetDirty(s);
                    done++;

                    AssetDatabase.SaveAssets();                                     // 每格存檔並釋放記憶體
                    EditorUtility.UnloadUnusedAssetsImmediate();
                    System.GC.Collect();
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                EditorSceneManager.MarkSceneDirty(g.gameObject.scene);
            }
            Debug.Log($"[NavTiles] 烘焙完成 {done} 格{(failed > 0 ? $"、失敗 {failed} 格" : "")}{(cancelled ? "（已取消）" : "")}，" +
                      $"用時 {sw.Elapsed.TotalMinutes:0.0} 分鐘。資料在 {dir}。記得存場景。");
            return done > 0;
        }

        static string DataFolder(NavMeshTileGrid g)
        {
            var scene = g.gameObject.scene;
            if (string.IsNullOrEmpty(scene.path))
            {
                Debug.LogError("[NavTiles] 請先存場景，NavMesh 資料要存在場景旁邊");
                return null;
            }
            string parent = Path.GetDirectoryName(scene.path).Replace('\\', '/');
            string folder = scene.name + "_NavMeshTiles";
            string dir = parent + "/" + folder;
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(parent, folder);
            return dir;
        }

        // ======================= 4. 接縫 Link =======================
        static void ClearLinks(NavMeshTileGrid g)
        {
            var old = g.transform.Find(LinksName);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
        }

        static string SeamName(int x, int z, bool alongX) => $"Seam_{x:00}_{z:00}_{(alongX ? "X" : "Z")}";

        // only == null：全部接縫重做；否則只重做碰到這些格子的接縫
        static void GenerateLinks(NavMeshTileGrid g, HashSet<(int, int)> only = null)
        {
            Undo.SetCurrentGroupName("Generate NavMesh Seam Links");
            int undoGroup = Undo.GetCurrentGroup();
            if (only == null) ClearLinks(g);
            Physics.SyncTransforms();

            var tiles = Tiles(g).Where(t => t.s.navMeshData != null && t.s.isActiveAndEnabled)
                                .ToDictionary(t => (t.x, t.z), t => t.s);
            var rootT = g.transform.Find(LinksName);
            bool newRoot = rootT == null;
            var root = newRoot ? new GameObject(LinksName) : rootT.gameObject;
            if (newRoot) root.transform.SetParent(g.transform, false);

            var filter = new NavMeshQueryFilter { agentTypeID = g.agentTypeID, areaMask = NavMesh.AllAreas };
            float T = g.tileSize;
            int n = Mathf.Max(1, Mathf.FloorToInt(T / g.linkSpacing));
            float width = Mathf.Min(g.linkWidth, T / n);
            float reach = g.overlap * 0.5f + g.linkSampleRadius + 1f;              // 端點要落在只有一格 NavMesh 的地方
            float top = g.transform.position.y + g.maxY + 1f;
            float depth = g.maxY - g.minY + 2f;

            var seams = new List<(int x, int z, bool alongX)>();
            foreach (var key in tiles.Keys)
            {
                if (tiles.ContainsKey((key.x + 1, key.z)) &&
                    (only == null || only.Contains(key) || only.Contains((key.x + 1, key.z)))) seams.Add((key.x, key.z, true));
                if (tiles.ContainsKey((key.x, key.z + 1)) &&
                    (only == null || only.Contains(key) || only.Contains((key.x, key.z + 1)))) seams.Add((key.x, key.z, false));
            }
            if (only != null)                                                      // 先拿掉這些接縫的舊 Link
                foreach (var (x, z, alongX) in seams)
                {
                    var old = root.transform.Find(SeamName(x, z, alongX));
                    if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
                }

            int links = 0;
            bool cancelled = false;
            try
            {
                for (int si = 0; si < seams.Count; si++)
                {
                    var (x, z, alongX) = seams[si];
                    if (si % 10 == 0 && EditorUtility.DisplayCancelableProgressBar("產生接縫 Link",
                            $"接縫 {si + 1}/{seams.Count}，已放 {links} 個", (float)si / seams.Count))
                    {
                        cancelled = true;
                        break;
                    }

                    // 接縫線：alongX = 格子 (x,z) 與 (x+1,z) 之間（x 固定），否則 (x,z) 與 (x,z+1) 之間
                    Vector3 p0 = alongX ? new Vector3((x + 1) * T, 0f, z * T) : new Vector3(x * T, 0f, (z + 1) * T);
                    Vector3 tangent = alongX ? Vector3.forward : Vector3.right;
                    Vector3 normal = g.transform.TransformDirection(alongX ? Vector3.right : Vector3.forward);

                    GameObject seamGo = null;
                    for (int k = 0; k < n; k++)
                    {
                        var w = g.transform.TransformPoint(p0 + tangent * ((k + 0.5f) * T / n));
                        var hits = Physics.RaycastAll(new Vector3(w.x, top, w.z), Vector3.down, depth, g.layerMask, QueryTriggerInteraction.Ignore);
                        float lastY = float.MaxValue;
                        foreach (var h in hits.OrderByDescending(r => r.point.y))
                        {
                            if (lastY - h.point.y < 2f) continue;                  // 同一個表面的重複命中
                            lastY = h.point.y;

                            if (!NavMesh.SamplePosition(h.point - normal * reach, out var a, g.linkSampleRadius, filter)) continue;
                            if (!NavMesh.SamplePosition(h.point + normal * reach, out var b, g.linkSampleRadius, filter)) continue;
                            if (Mathf.Abs(a.position.y - b.position.y) > g.linkMaxHeightDiff) continue;
                            if (Vector3.Dot(a.position - h.point, normal) >= 0f || Vector3.Dot(b.position - h.point, normal) <= 0f) continue;

                            if (seamGo == null)
                            {
                                seamGo = new GameObject(SeamName(x, z, alongX));
                                seamGo.transform.SetParent(root.transform, false);
                            }
                            var link = seamGo.AddComponent<NavMeshLink>();
                            link.agentTypeID = g.agentTypeID;
                            link.startPoint = seamGo.transform.InverseTransformPoint(a.position);
                            link.endPoint = seamGo.transform.InverseTransformPoint(b.position);
                            link.width = width;
                            link.bidirectional = true;
                            link.autoUpdate = false;
                            link.area = 0;
                            link.UpdateLink();
                            links++;
                        }
                    }
                    if (seamGo != null && !newRoot) Undo.RegisterCreatedObjectUndo(seamGo, "Generate NavMesh Seam Links");
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (newRoot) Undo.RegisterCreatedObjectUndo(root, "Generate NavMesh Seam Links");
            Undo.CollapseUndoOperations(undoGroup);
            EditorSceneManager.MarkSceneDirty(g.gameObject.scene);
            Debug.Log($"[NavTiles] 接縫 {seams.Count} 條，放了 {links} 個 Link{(cancelled ? "（已取消，只做了一部分）" : "")}。");
        }
    }
}
