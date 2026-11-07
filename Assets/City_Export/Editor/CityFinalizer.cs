// CityFinalizer.cs — 第二階段「定稿」：把預覽狀態的 Generated_City 整理成可以放進遊戲的版本。
// 不合併網格（交給 GPU Resident Drawer 用 instancing 畫同一種模組），只做三件事：
//   1. 拿掉被完全包住的 POD_Fill（四周和上方都有平台板塊，完全看不到）
//   2. 每個模組實例加 MeshCollider，直接用模組自己的網格。形狀完全吻合（圓角、角柱、鰭片、凸塊、
//      U 形橋、門洞隧道）；同一種網格的碰撞資料由 Unity 共用，31 種模組只烘焙 31 份。
//      注意：開放網格（Silo_Mid、BRG_Span、BASE_W_GateL、POD_Gate_L/T）的破洞處沒有碰撞。
//   3. 靜態旗標（Occluder / Occludee / Reflection Probe）。不勾 Batching Static，會和 GPU Resident Drawer 衝突。
//   4. 夜間窗戶（CityDistrict.nightWindows）：每個群組的牆面隨機換成 M_City_Wall_Night_A/B/C 其中一個（見 CityNightSetup.cs）。
// 剔除由場景裡現有的模組實例推算，所以手動刪改過的實例也會反映進來。
// 回到預覽狀態：用同一個 seed 再按一次「生成」。
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DivaExMachina.City.EditorTools
{
    public static class CityFinalizer
    {
        const StaticEditorFlags Flags = StaticEditorFlags.OccluderStatic
                                      | StaticEditorFlags.OccludeeStatic
                                      | StaticEditorFlags.ReflectionProbeStatic;

        static readonly Vector2Int[] Dirs =
            { new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(-1, 0), new Vector2Int(0, -1) };

        class Piece
        {
            public GameObject go;
            public CityModuleCatalog.Entry e;
            public Vector2Int cell;
            public int unit;
        }

        public static bool IsFinalized(Transform root) => root.GetComponentInChildren<MeshCollider>(true) != null;

        // ======================= 定稿 =======================
        public static void FinalizeDistrict(CityDistrict d, Transform root)
        {
            var cat = d.catalog;
            if (cat == null) { Debug.LogError("[City] 沒有模組目錄"); return; }
            if (IsFinalized(root))
            {
                Debug.LogWarning("[City] 這個街區已經定稿過了。要重做請先按「生成」回到預覽狀態，再定稿一次。");
                return;
            }

            var pieces = Collect(root, cat, out var unknown);
            if (unknown.Count > 0)
                Debug.LogWarning($"[City] {unknown.Count} 個物件認不出是哪種模組，不參與剔除判斷：{string.Join(", ", unknown.Take(10))}");

            // 平台板塊佔用的 (格子 x, 格子 y, 層)。坡道不算填滿。
            var solid = new HashSet<Vector3Int>();
            foreach (var p in pieces)
                if (IsPodiumSolid(p.e))
                    for (int k = 0; k < Mathf.Max(1, p.e.heightUnits); k++)
                        solid.Add(new Vector3Int(p.cell.x, p.cell.y, p.unit + k));

            Undo.SetCurrentGroupName("Finalize City");
            int undoGroup = Undo.GetCurrentGroup();

            // 1. 剔除被完全包住的 Fill：四周同一層與正上方都是平台板塊（包在裡面，不需要碰撞）
            int fills = 0, culled = 0;
            foreach (var p in pieces)
            {
                if (p.e.name != "BLD_POD_Fill") continue;
                fills++;
                int z = p.unit;
                bool hidden = solid.Contains(new Vector3Int(p.cell.x, p.cell.y, z + 1))
                              && Dirs.All(dd => solid.Contains(new Vector3Int(p.cell.x + dd.x, p.cell.y + dd.y, z)));
                if (!hidden) continue;
                Undo.DestroyObjectImmediate(p.go);
                culled++;
            }

            // 2. 網格碰撞體
            int colliders = 0;
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.GetComponent<Collider>() != null) continue;
                var mc = Undo.AddComponent<MeshCollider>(mf.gameObject);
                mc.sharedMesh = mf.sharedMesh;
                mc.convex = false;
                colliders++;
            }

            // 3. 靜態旗標
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                Undo.RecordObject(t.gameObject, "Finalize City");
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, Flags);
            }

            // 4. 夜間窗戶：每棟建築（群組）隨機換成一種夜間牆面材質
            int lit = d.nightWindows ? AssignNightWalls(d, root) : 0;

            Undo.CollapseUndoOperations(undoGroup);
            int renderers = root.GetComponentsInChildren<MeshRenderer>(true).Length;
            Debug.Log($"[City] 定稿完成：剔除被包住的 Fill {culled}/{fills} 件；MeshCollider {colliders} 個；" +
                      $"剩下 {renderers} 個 MeshRenderer；夜間牆面 {lit} 個群組。");
        }

        static int AssignNightWalls(CityDistrict d, Transform root)
        {
            var day = CityNightSetup.LoadDayWall();
            var nights = CityNightSetup.LoadNightWalls();
            if (day == null || nights.Count == 0)
            {
                Debug.LogWarning("[City] 沒有夜間牆面材質，略過窗戶燈火。請先執行 Tools > City > 4. Create Night Wall Materials");
                return 0;
            }

            int groups = 0;
            foreach (Transform g in root)
            {
                var night = nights[CityNightSetup.StableHash(d.seed, g.name) % nights.Count];
                bool any = false;
                foreach (var r in g.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var mats = r.sharedMaterials;
                    bool changed = false;
                    for (int i = 0; i < mats.Length; i++)
                        if (mats[i] == day) { mats[i] = night; changed = true; }
                    if (!changed) continue;
                    Undo.RecordObject(r, "Finalize City");
                    r.sharedMaterials = mats;
                    any = true;
                }
                if (any) groups++;
            }
            return groups;
        }

        // ======================= 存成 Prefab =======================
        public static void SaveAsPrefab(CityDistrict d, Transform root)
        {
            if (!IsFinalized(root) &&
                !EditorUtility.DisplayDialog("存成 Prefab", "這個街區還沒定稿（沒有碰撞體）。仍然要存嗎？", "存", "取消"))
                return;

            string baseDir = CityImportSetup.FindRoot();
            if (baseDir == null) return;
            string dir = baseDir + "/Baked";
            if (!AssetDatabase.IsValidFolder(dir)) AssetDatabase.CreateFolder(baseDir, "Baked");

            string scene = string.IsNullOrEmpty(d.gameObject.scene.name) ? "Untitled" : d.gameObject.scene.name;
            string file = Sanitize($"{scene}_{d.name}");
            string path = $"{dir}/{file}.prefab";
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) != null &&
                !EditorUtility.DisplayDialog("存成 Prefab", $"{path} 已經存在，要覆蓋嗎？", "覆蓋", "取消"))
                return;

            string keepName = root.name;                                         // 存檔會把物件改名成檔名；
            var prefab = PrefabUtility.SaveAsPrefabAssetAndConnect(root.gameObject, path, InteractionMode.UserAction);
            if (root.name != keepName) root.name = keepName;                     // 改回來，「生成／清除」才找得到它
            if (prefab != null) Debug.Log($"[City] 已存成 Prefab：{path}", prefab);
            else Debug.LogError($"[City] 存 Prefab 失敗：{path}");
        }

        // ======================= 收集模組實例 =======================
        static List<Piece> Collect(Transform root, CityModuleCatalog cat, out List<string> unknown)
        {
            unknown = new List<string>();
            var list = new List<Piece>();
            float c = cat.cellSize, u = cat.unitHeight;
            foreach (Transform g in root)
                foreach (Transform t in g)
                {
                    var e = Identify(t.gameObject, cat);
                    if (e == null) { unknown.Add(t.name); continue; }
                    var lp = root.InverseTransformPoint(t.position);
                    list.Add(new Piece
                    {
                        go = t.gameObject,
                        e = e,
                        cell = new Vector2Int(Mathf.RoundToInt(lp.x / c - 0.5f), Mathf.RoundToInt(lp.z / c - 0.5f)),
                        unit = Mathf.RoundToInt(lp.y / u),
                    });
                }
            return list;
        }

        static CityModuleCatalog.Entry Identify(GameObject go, CityModuleCatalog cat)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromSource(go);
            if (src != null)
            {
                var e = cat.Get(src.name);
                if (e != null) return e;
            }
            // 後備：用物件名稱（生成時命名為 模組_x_y_層）
            return cat.entries.Where(x => x != null && !string.IsNullOrEmpty(x.name) && go.name.StartsWith(x.name + "_"))
                              .OrderByDescending(x => x.name.Length).FirstOrDefault();
        }

        static bool IsPodiumSolid(CityModuleCatalog.Entry e) => e.name.StartsWith("BLD_POD_") && e.name != "BLD_POD_Ramp";

        static string Sanitize(string s)
        {
            foreach (char ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
            return s;
        }
    }
}
