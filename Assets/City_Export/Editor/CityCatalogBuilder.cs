// CityCatalogBuilder.cs — 依 manifest.json 與匯入的 FBX 建立 CityModuleCatalog，並自動校正座標軸。
// 選單：Tools > City > 3. Build Module Catalog（請先完成步驟 1、2）
using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DivaExMachina.City.EditorTools
{
    public static class CityCatalogBuilder
    {
        [Serializable]
        class Manifest
        {
            public float cell_size_m;
            public float unit_height_m;
            public ManifestModule[] modules;
        }

        [Serializable]
        class ManifestModule
        {
            public string name;
            public string file;
            public string profile;
            public string category;
            public int height_units;
            public string[] gate_open_at_rot0;
            public float[] bounds_min;   // Blender 座標
            public float[] bounds_max;
        }

        [MenuItem("Tools/City/3. Build Module Catalog")]
        public static void Build()
        {
            string root = CityImportSetup.FindRoot();
            if (root == null) return;

            string manifestPath = Path.Combine(Directory.GetCurrentDirectory(), root, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                Debug.LogError($"[City] 找不到 {manifestPath}");
                return;
            }
            var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.modules == null || manifest.modules.Length == 0)
            {
                Debug.LogError("[City] manifest.json 讀取失敗或沒有模組");
                return;
            }

            string catPath = root + "/CityModuleCatalog.asset";
            var cat = AssetDatabase.LoadAssetAtPath<CityModuleCatalog>(catPath);
            if (cat == null)
            {
                cat = ScriptableObject.CreateInstance<CityModuleCatalog>();
                AssetDatabase.CreateAsset(cat, catPath);
            }
            cat.cellSize = manifest.cell_size_m > 0 ? manifest.cell_size_m : 100f;
            cat.unitHeight = manifest.unit_height_m > 0 ? manifest.unit_height_m : 24.5f;
            cat.entries.Clear();

            int missing = 0, verticalBad = 0;
            var blenderBounds = new System.Collections.Generic.List<(Vector3 bmin, Vector3 bmax, Bounds unity)>();

            foreach (var m in manifest.modules)
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{root}/{m.file}");
                var e = new CityModuleCatalog.Entry
                {
                    name = m.name,
                    prefab = prefab,
                    heightUnits = m.height_units,
                    profile = m.profile ?? "",
                    category = m.category ?? "",
                    gateOpenBlender = m.gate_open_at_rot0 ?? new string[0],
                };
                if (prefab == null)
                {
                    missing++;
                    Debug.LogWarning($"[City] 找不到模組 {m.file}");
                }
                else if (LocalBounds(prefab, out var b))
                {
                    e.boundsMin = b.min;
                    e.boundsMax = b.max;
                    var bmin = new Vector3(m.bounds_min[0], m.bounds_min[1], m.bounds_min[2]);
                    var bmax = new Vector3(m.bounds_max[0], m.bounds_max[1], m.bounds_max[2]);
                    blenderBounds.Add((bmin, bmax, b));
                    // 垂直方向：Unity Y 應等於 Blender Z
                    if (Mathf.Abs(b.min.y - bmin.z) > 0.5f || Mathf.Abs(b.max.y - bmax.z) > 0.5f)
                    {
                        verticalBad++;
                        Debug.LogWarning($"[City] {m.name} 垂直範圍不符（Unity {b.min.y:F1}~{b.max.y:F1}，Blender {bmin.z:F1}~{bmax.z:F1}）。" +
                                         "請確認 FBX 匯入設定已勾選 Bake Axis Conversion、Convert Units，並 Reimport。");
                    }
                }
                cat.entries.Add(e);
            }

            // ---- 自動校正：找出 Blender (x, y) → Unity (X, Z) 的對應 ----
            Vector2Int bestX = new Vector2Int(-1, 0), bestY = new Vector2Int(0, -1);
            float bestErr = float.MaxValue;
            for (int swap = 0; swap < 2; swap++)
            for (int sx = -1; sx <= 1; sx += 2)
            for (int sy = -1; sy <= 1; sy += 2)
            {
                var mx = swap == 0 ? new Vector2Int(sx, 0) : new Vector2Int(0, sx);
                var my = swap == 0 ? new Vector2Int(0, sy) : new Vector2Int(sy, 0);
                float err = 0f;
                foreach (var (bmin, bmax, ub) in blenderBounds)
                {
                    float gxMin = float.MaxValue, gxMax = float.MinValue, gzMin = float.MaxValue, gzMax = float.MinValue;
                    for (int i = 0; i < 4; i++)
                    {
                        float bx = (i & 1) == 0 ? bmin.x : bmax.x;
                        float by = (i & 2) == 0 ? bmin.y : bmax.y;
                        float gx = mx.x * bx + my.x * by;
                        float gz = mx.y * bx + my.y * by;
                        gxMin = Mathf.Min(gxMin, gx); gxMax = Mathf.Max(gxMax, gx);
                        gzMin = Mathf.Min(gzMin, gz); gzMax = Mathf.Max(gzMax, gz);
                    }
                    err += Mathf.Abs(gxMin - ub.min.x) + Mathf.Abs(gxMax - ub.max.x)
                         + Mathf.Abs(gzMin - ub.min.z) + Mathf.Abs(gzMax - ub.max.z);
                }
                if (err < bestErr) { bestErr = err; bestX = mx; bestY = my; }
            }
            cat.blenderXToGrid = bestX;
            cat.blenderYToGrid = bestY;
            cat.calibrationError = blenderBounds.Count > 0 ? bestErr / (blenderBounds.Count * 4) : -1f;

            EditorUtility.SetDirty(cat);
            AssetDatabase.SaveAssets();
            Selection.activeObject = cat;

            string msg = $"[City] 模組目錄完成：{cat.entries.Count} 個模組（缺 {missing} 個）。" +
                         $"軸向：Blender +X → 格子 {bestX}，Blender +Y → 格子 {bestY}，平均誤差 {cat.calibrationError:F2} m。";
            if (cat.calibrationError > 0.5f || verticalBad > 0)
                Debug.LogError(msg + " 誤差偏大，請檢查 FBX 匯入設定。");
            else
                Debug.Log(msg);
        }

        /// <summary>prefab 根節點座標系下的網格範圍。</summary>
        static bool LocalBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            bool any = false;
            var root = go.transform;
            foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                var mesh = mf.sharedMesh;
                if (mesh == null) continue;
                Matrix4x4 m = root.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                var mb = mesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    var corner = mb.center + Vector3.Scale(mb.extents, new Vector3(
                        (i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    var p = m.MultiplyPoint3x4(corner);
                    if (!any) { bounds = new Bounds(p, Vector3.zero); any = true; }
                    else bounds.Encapsulate(p);
                }
            }
            return any;
        }
    }
}
