// CityImportSetup.cs — 城市模組（City_Export）的 Unity URP 匯入設定
// 放在 City_Export/Editor/ 底下即可（Editor 資料夾內的腳本只在編輯器中編譯）。
//
// 1. CityModelPostprocessor：City_Export/Models/ 裡的 FBX 匯入時自動套用設定
//    （縮放 1、轉換單位、Bake Axis Conversion、不產生碰撞體、不匯入攝影機/燈光/動畫）。
// 2. 選單 Tools > City > 1. Create URP Materials：在 City_Export/Materials/ 建立 7 個 URP/Lit 材質並指定貼圖。
// 3. 選單 Tools > City > 2. Remap FBX Materials：把每個 FBX 的材質槽依名稱對應到上面的材質。
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace DivaExMachina.City.EditorTools
{
    public class CityModelPostprocessor : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            string p = assetPath.Replace('\\', '/');
            if (!p.Contains("/City_Export/Models/")) return;

            var mi = (ModelImporter)assetImporter;
            mi.globalScale = 1f;
            mi.useFileScale = true;              // Convert Units
            mi.bakeAxisConversion = true;        // 根節點旋轉歸零
            mi.addCollider = false;              // 碰撞體之後用簡單方塊另外加
            mi.importCameras = false;
            mi.importLights = false;
            mi.importBlendShapes = false;
            mi.importAnimation = false;
            mi.animationType = ModelImporterAnimationType.None;
            mi.importNormals = ModelImporterNormals.Import;
            mi.meshCompression = ModelImporterMeshCompression.Off;
            mi.isReadable = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
        }
    }

    public static class CityImportSetup
    {
        // 材質名稱必須和 Blender 裡的材質名稱一致（FBX 的材質槽靠名稱對應）
        static readonly (string mat, string tex, float smoothness, float metallic)[] Materials =
        {
            ("M_City_Wall",          "T_Wall",          0.45f, 0.15f),
            ("M_City_Roof",          "T_Roof",          0.30f, 0.15f),
            ("M_City_Structure",     "T_Structure",     0.55f, 0.60f),
            ("M_City_FuelTank",      "T_FuelTank",      0.50f, 0.30f),
            ("M_City_Tunnel",        "T_Tunnel",        0.20f, 0.05f),
            ("M_City_TunnelCeiling", "T_TunnelCeiling", 0.20f, 0.05f),
            ("M_City_Core",          "T_Accent",        0.50f, 0.20f),
        };

        internal static string FindRoot()
        {
            const string preferred = "Assets/City_Export";
            if (AssetDatabase.IsValidFolder(preferred)) return preferred;
            string found = AssetDatabase.GetAllAssetPaths()
                .FirstOrDefault(p => p.EndsWith("/City_Export") && AssetDatabase.IsValidFolder(p));
            if (found == null) Debug.LogError("[City] 找不到 City_Export 資料夾，請把整個資料夾放進 Assets 底下。");
            return found;
        }

        [MenuItem("Tools/City/1. Create URP Materials")]
        public static void CreateMaterials()
        {
            string root = FindRoot();
            if (root == null) return;

            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("[City] 找不到 Universal Render Pipeline/Lit，請確認專案使用 URP。");
                return;
            }

            string matDir = root + "/Materials";
            if (!AssetDatabase.IsValidFolder(matDir)) AssetDatabase.CreateFolder(root, "Materials");

            int made = 0;
            foreach (var m in Materials)
            {
                string path = $"{matDir}/{m.mat}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(shader) { name = m.mat };
                    AssetDatabase.CreateAsset(mat, path);
                    made++;
                }
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{root}/Textures/{m.tex}.png");
                if (tex == null) Debug.LogWarning($"[City] 找不到貼圖 {m.tex}.png");
                mat.SetTexture("_BaseMap", tex);
                mat.SetColor("_BaseColor", Color.white);
                mat.SetFloat("_Smoothness", m.smoothness);
                mat.SetFloat("_Metallic", m.metallic);
                EditorUtility.SetDirty(mat);
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[City] 材質完成：新建 {made} 個，共 {Materials.Length} 個（{matDir}）");
        }

        [MenuItem("Tools/City/2. Remap FBX Materials")]
        public static void RemapMaterials()
        {
            string root = FindRoot();
            if (root == null) return;
            string matDir = root + "/Materials";

            int files = 0, slots = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Model", new[] { root + "/Models" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) continue;

                bool changed = false;
                foreach (var src in AssetDatabase.LoadAllAssetsAtPath(path).OfType<Material>())
                {
                    var target = AssetDatabase.LoadAssetAtPath<Material>($"{matDir}/{src.name}.mat");
                    if (target == null)
                    {
                        Debug.LogWarning($"[City] {Path.GetFileName(path)}：找不到材質 {src.name}，請先執行步驟 1。");
                        continue;
                    }
                    importer.AddRemap(new AssetImporter.SourceAssetIdentifier(src), target);
                    changed = true; slots++;
                }
                if (changed) { importer.SaveAndReimport(); files++; }
            }
            Debug.Log($"[City] 材質對應完成：{files} 個 FBX，{slots} 個材質槽。");
        }
    }
}
