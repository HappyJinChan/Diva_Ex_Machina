// CityNightSetup.cs — 夜間窗戶燈火：用 3 個發光版本的牆面材質，定稿時每棟建築隨機分配一個。
//
// 選單 Tools > City > 4. Create Night Wall Materials：
//   以 M_City_Wall 為底，建立 M_City_Wall_Night_A / B / C，
//   各自套上 Textures/T_Wall_Night_A/B/C.png 當自發光貼圖（亮的窗戶各不相同，青色線條三種都會亮）。
//   只是 URP/Lit 開 Emission，不用自訂 shader；同一棟樓用同一個材質，所以 GPU Resident Drawer 照樣有效。
//   亮度要調整：直接改這三個材質的 Emission Color（HDR 強度），或改下面的 DefaultIntensity 再執行一次選單。
//   重新執行選單會用 M_City_Wall 目前的設定覆蓋這三個材質（再套回發光貼圖）。
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace DivaExMachina.City.EditorTools
{
    public static class CityNightSetup
    {
        public static readonly string[] Variants = { "A", "B", "C" };
        const float DefaultIntensity = 3f;   // HDR 倍率，搭配 Bloom

        [MenuItem("Tools/City/4. Create Night Wall Materials")]
        public static void CreateNightMaterials()
        {
            string root = CityImportSetup.FindRoot();
            if (root == null) return;
            var wall = AssetDatabase.LoadAssetAtPath<Material>(root + "/Materials/M_City_Wall.mat");
            if (wall == null) { Debug.LogError("[City] 找不到 M_City_Wall，請先執行 Tools > City > 1"); return; }

            int made = 0;
            foreach (var v in Variants)
            {
                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>($"{root}/Textures/T_Wall_Night_{v}.png");
                if (tex == null) { Debug.LogWarning($"[City] 找不到 T_Wall_Night_{v}.png"); continue; }

                string path = $"{root}/Materials/M_City_Wall_Night_{v}.mat";
                var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (mat == null)
                {
                    mat = new Material(wall) { name = $"M_City_Wall_Night_{v}" };
                    AssetDatabase.CreateAsset(mat, path);
                }
                else mat.CopyPropertiesFromMaterial(wall);

                mat.SetTexture("_EmissionMap", tex);
                mat.SetColor("_EmissionColor", Color.white * DefaultIntensity);
                mat.EnableKeyword("_EMISSION");
                mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;   // 全即時光，不參與烘焙
                EditorUtility.SetDirty(mat);
                made++;
            }
            AssetDatabase.SaveAssets();
            Debug.Log($"[City] 夜間牆面材質完成：{made} 個（{root}/Materials/M_City_Wall_Night_*）");
        }

        /// <summary>原本的牆面材質。</summary>
        public static Material LoadDayWall()
        {
            string root = CityImportSetup.FindRoot();
            return root == null ? null : AssetDatabase.LoadAssetAtPath<Material>(root + "/Materials/M_City_Wall.mat");
        }

        /// <summary>已經建立的夜間牆面材質（缺的會略過）。</summary>
        public static List<Material> LoadNightWalls()
        {
            var list = new List<Material>();
            string root = CityImportSetup.FindRoot();
            if (root == null) return list;
            foreach (var v in Variants)
            {
                var m = AssetDatabase.LoadAssetAtPath<Material>($"{root}/Materials/M_City_Wall_Night_{v}.mat");
                if (m != null) list.Add(m);
            }
            return list;
        }

        /// <summary>固定的字串雜湊（FNV-1a），同一個 seed 與群組名稱每次都得到同一個結果。</summary>
        public static int StableHash(int seed, string s)
        {
            unchecked
            {
                uint h = 2166136261u ^ (uint)seed;
                foreach (char c in s) { h ^= c; h *= 16777619u; }
                return (int)(h & 0x7fffffff);
            }
        }
    }
}
