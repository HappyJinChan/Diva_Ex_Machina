// CityDistrictEditor.cs — CityDistrict 的 Inspector：對齊格子、檢查地形、生成、清除。
// 生成 = 預覽狀態；定稿 = 剔除被包住的 Fill、加方塊碰撞體、靜態旗標（見 CityFinalizer.cs）。
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DivaExMachina.City.EditorTools
{
    [CustomEditor(typeof(CityDistrict))]
    public class CityDistrictEditor : Editor
    {
        const string GeneratedName = "Generated_City";

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();
            var d = (CityDistrict)target;

            EditorGUILayout.Space();
            if (d.catalog == null)
            {
                EditorGUILayout.HelpBox("還沒有指定模組目錄。請先執行 Tools > City > 3. Build Module Catalog，或按下方按鈕自動尋找。", MessageType.Warning);
                if (GUILayout.Button("自動尋找模組目錄")) FindCatalog(d);
            }
            if (d.unevenCells.Count > 0)
                EditorGUILayout.HelpBox($"有 {d.unevenCells.Count} 格地形高度與地面高度 {d.groundY} m 不符" +
                                        $"（其中 {d.unevenCells.Intersect(d.builtCells).Count()} 格有建築，紅色）。" +
                                        "選取這個物件時，場景中會標出位置。", MessageType.Warning);

            using (new EditorGUI.DisabledScope(d.catalog == null))
            {
                if (GUILayout.Button("對齊 100 m 格子")) Snap(d);
                if (GUILayout.Button("檢查地形")) { CheckTerrain(d); SceneView.RepaintAll(); }
                if (GUILayout.Button("生成（Generate）", GUILayout.Height(28))) Generate(d);
                if (GUILayout.Button("清除")) Clear(d);

                var gen = d.transform.Find(GeneratedName);
                EditorGUILayout.Space();
                if (gen != null && CityFinalizer.IsFinalized(gen))
                    EditorGUILayout.HelpBox("已定稿。要修改請按「生成」回到預覽狀態（同一個 seed 結果相同）。", MessageType.Info);
                using (new EditorGUI.DisabledScope(gen == null))
                {
                    if (GUILayout.Button("定稿（剔除 Fill、碰撞體、靜態旗標）", GUILayout.Height(28))) CityFinalizer.FinalizeDistrict(d, gen);
                    if (GUILayout.Button("存成 Prefab")) CityFinalizer.SaveAsPrefab(d, gen);
                }
            }
        }

        static void FindCatalog(CityDistrict d)
        {
            string guid = AssetDatabase.FindAssets("t:CityModuleCatalog").FirstOrDefault();
            if (guid == null) { Debug.LogWarning("[City] 專案中找不到 CityModuleCatalog"); return; }
            Undo.RecordObject(d, "Assign City Catalog");
            d.catalog = AssetDatabase.LoadAssetAtPath<CityModuleCatalog>(AssetDatabase.GUIDToAssetPath(guid));
            EditorUtility.SetDirty(d);
        }

        static void Snap(CityDistrict d)
        {
            float c = d.CellSize;
            var p = d.transform.position;
            Undo.RecordObject(d.transform, "Snap City District");
            d.transform.position = new Vector3(Mathf.Round(p.x / c) * c, d.groundY, Mathf.Round(p.z / c) * c);
            if (d.transform.rotation != Quaternion.identity)
            {
                d.transform.rotation = Quaternion.identity;
                Debug.LogWarning("[City] 第一階段只支援不旋轉的街區，已把旋轉歸零。");
            }
            d.transform.localScale = Vector3.one;
        }

        static void CheckTerrain(CityDistrict d)
        {
            float c = d.CellSize;
            var o = d.transform.position;
            Undo.RecordObject(d, "Check City Terrain");
            d.unevenCells.Clear();
            int noTerrain = 0;
            var samples = new[] { new Vector2(0.5f, 0.5f), new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.05f),
                                  new Vector2(0.05f, 0.95f), new Vector2(0.95f, 0.95f) };
            for (int x = 0; x < d.sizeX; x++)
                for (int y = 0; y < d.sizeZ; y++)
                {
                    foreach (var s in samples)
                    {
                        var wp = o + new Vector3((x + s.x) * c, 0, (y + s.y) * c);
                        float? h = SampleTerrain(wp);
                        if (!h.HasValue) { noTerrain++; break; }
                        if (Mathf.Abs(h.Value - d.groundY) > d.terrainTolerance)
                        {
                            d.unevenCells.Add(new Vector2Int(x, y));
                            break;
                        }
                    }
                }
            EditorUtility.SetDirty(d);
            if (noTerrain > 0) Debug.LogWarning($"[City] 有 {noTerrain} 格底下沒有 Terrain。");
            Debug.Log($"[City] 地形檢查：{d.unevenCells.Count} 格高度與 {d.groundY} m 相差超過 {d.terrainTolerance} m。");
        }

        static float? SampleTerrain(Vector3 p)
        {
            foreach (var t in Terrain.activeTerrains)
            {
                var tp = t.transform.position;
                var size = t.terrainData.size;
                if (p.x >= tp.x && p.x <= tp.x + size.x && p.z >= tp.z && p.z <= tp.z + size.z)
                    return t.SampleHeight(p) + tp.y;
            }
            return null;
        }

        static void Clear(CityDistrict d)
        {
            var old = d.transform.Find(GeneratedName);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);
            Undo.RecordObject(d, "Clear City");
            d.builtCells.Clear();
            EditorUtility.SetDirty(d);
        }

        static void Generate(CityDistrict d)
        {
            if (d.catalog == null || d.catalog.entries.Count == 0)
            {
                Debug.LogError("[City] 模組目錄是空的，請先執行 Tools > City > 3. Build Module Catalog");
                return;
            }
            Undo.SetCurrentGroupName("Generate City");
            int group = Undo.GetCurrentGroup();

            Snap(d);
            Clear(d);

            var gen = new CityGenerator(d);
            List<CityPlacement> placements;
            try { placements = gen.Generate(); }
            catch (System.Exception ex)
            {
                Debug.LogException(ex);
                return;
            }

            float c = d.CellSize, u = d.catalog.unitHeight;
            var root = new GameObject(GeneratedName);
            root.transform.SetParent(d.transform, false);
            var groups = new Dictionary<string, Transform>();
            int missing = 0;
            var missingNames = new HashSet<string>();
            foreach (var p in placements)
            {
                var e = d.catalog.Get(p.module);
                if (e == null || e.prefab == null) { missing++; missingNames.Add(p.module); continue; }
                if (!groups.TryGetValue(p.group, out var parent))
                {
                    var g = new GameObject(p.group);
                    g.transform.SetParent(root.transform, false);
                    groups[p.group] = parent = g.transform;
                }
                var go = (GameObject)PrefabUtility.InstantiatePrefab(e.prefab, parent);
                go.transform.localPosition = new Vector3((p.cell.x + 0.5f) * c, p.unit * u, (p.cell.y + 0.5f) * c);
                go.transform.localRotation = Quaternion.Euler(0f, 90f * p.quarterTurns, 0f);
                go.name = $"{p.module}_{p.cell.x}_{p.cell.y}_{p.unit:00}";
            }
            // 每個群組的原點移到街區中心（地面高度），方便單獨移動
            foreach (var g in groups.Values)
            {
                if (g.childCount == 0) continue;
                Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
                foreach (Transform ch in g) { min = Vector3.Min(min, ch.localPosition); max = Vector3.Max(max, ch.localPosition); }
                var pivot = new Vector3((min.x + max.x) * 0.5f, 0f, (min.z + max.z) * 0.5f);
                g.localPosition = pivot;
                foreach (Transform ch in g) ch.localPosition -= pivot;
            }
            Undo.RegisterCreatedObjectUndo(root, "Generate City");

            Undo.RecordObject(d, "Generate City");
            d.builtCells = placements.Select(p => p.cell).Distinct().ToList();
            EditorUtility.SetDirty(d);
            CheckTerrain(d);
            Undo.CollapseUndoOperations(group);

            foreach (var line in gen.log) Debug.Log("[City] " + line);
            if (missing > 0) Debug.LogWarning($"[City] {missing} 件模組沒有 prefab：{string.Join(", ", missingNames)}");
            var built = new HashSet<Vector2Int>(d.builtCells);
            int bad = d.unevenCells.Count(cell => built.Contains(cell));
            if (bad > 0) Debug.LogWarning($"[City] 有 {bad} 格建築下方的地形不平（場景中紅色標示），請修改高度圖。");
        }
    }
}
