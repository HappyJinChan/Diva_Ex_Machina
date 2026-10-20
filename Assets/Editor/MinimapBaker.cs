using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Diva Ex Machina > Minimap Baker
///
/// 把目前場景裡所有 Terrain（包括停用中的，TerrainStreamer 會把遠的關掉）的高度烘成一張俯視圖：
///   · 平面色塊：高度每 Contour Interval 公尺分一層，每一層一個顏色（Height Colors 漸層，低 → 高）。
///   · 輪廓線：層與層的交界畫線；每 Major Every 層畫一條比較深的主線。
///   · （可選）Slope Shade：陡的地方稍微變暗，讓山脊看得出來。0 = 純平面。
///   · 沒有地形的地方是 Background（預設透明）。
///
/// 輸出：Output Folder 底下的 PNG + MinimapData（記錄這張圖對應的世界範圍），拖進 Minimap 元件的 Data。
/// 地形改了就再按一次 Bake，會覆蓋同名檔案，Minimap 不用重設。
///
/// 解析度：Meters Per Pixel。小地圖顯示半徑 500 m、畫面上直徑約 200 px 時，約 5 m/px 就夠；
/// 預設 4。貼圖任一邊超過 Max Texture Size 時會自動放大 Meters Per Pixel。
/// </summary>
public class MinimapBaker : EditorWindow
{
    [SerializeField] private Transform sourceRoot;
    [SerializeField] private bool includeInactive = true;

    [SerializeField] private float metersPerPixel = 4f;
    [SerializeField] private int maxTextureSize = 8192;

    [SerializeField] private float contourInterval = 20f;
    [SerializeField] private int majorEvery = 5;

    [SerializeField] private Gradient heightColors = DefaultGradient();
    [SerializeField] private Color lineColor = new Color(0f, 0f, 0f, 0.35f);
    [SerializeField] private Color majorLineColor = new Color(0f, 0f, 0f, 0.65f);
    [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0f);
    [SerializeField, Range(0f, 1f)] private float slopeShade = 0f;

    [SerializeField] private string outputFolder = "Assets/Minimap";
    [SerializeField] private string fileName = "Minimap";

    private Vector2 _scroll;

    [MenuItem("Tools/Diva Ex Machina/Minimap Baker")]
    private static void Open() => GetWindow<MinimapBaker>("Minimap Baker");

    private static Gradient DefaultGradient()
    {
        // 月面：低處偏暗的藍灰，高處偏亮的沙色
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.13f, 0.17f, 0.22f), 0f),
                new GradientColorKey(new Color(0.24f, 0.30f, 0.34f), 0.4f),
                new GradientColorKey(new Color(0.45f, 0.45f, 0.40f), 0.75f),
                new GradientColorKey(new Color(0.70f, 0.66f, 0.56f), 1f),
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
        sourceRoot = (Transform)EditorGUILayout.ObjectField(
            new GUIContent("Root", "只烘這個物件底下的 Terrain。留空 = 整個場景。"),
            sourceRoot, typeof(Transform), true);
        includeInactive = EditorGUILayout.Toggle(
            new GUIContent("Include Inactive", "包含停用中的 Terrain。TerrainStreamer 會把遠處的地塊關掉，用 Root 篩選時建議保持勾選。"),
            includeInactive);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Resolution", EditorStyles.boldLabel);
        metersPerPixel = EditorGUILayout.FloatField(new GUIContent("Meters Per Pixel", "每個像素幾公尺。越小越精細、貼圖越大。"), Mathf.Max(0.25f, metersPerPixel));
        maxTextureSize = EditorGUILayout.IntPopup("Max Texture Size", maxTextureSize,
            new[] { "2048", "4096", "8192", "16384" }, new[] { 2048, 4096, 8192, 16384 });

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Contours", EditorStyles.boldLabel);
        contourInterval = EditorGUILayout.FloatField(new GUIContent("Contour Interval (m)", "高度每隔幾公尺分一層色塊、畫一條線。"), Mathf.Max(0.5f, contourInterval));
        majorEvery = EditorGUILayout.IntField(new GUIContent("Major Every", "每幾層畫一條比較深的主線。0 = 不畫主線。"), Mathf.Max(0, majorEvery));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Colors", EditorStyles.boldLabel);
        heightColors = EditorGUILayout.GradientField(new GUIContent("Height Colors", "低 → 高。每一層取該層中間高度的顏色。"), heightColors);
        lineColor = EditorGUILayout.ColorField("Line", lineColor);
        majorLineColor = EditorGUILayout.ColorField("Major Line", majorLineColor);
        backgroundColor = EditorGUILayout.ColorField(new GUIContent("Background", "沒有地形的地方。"), backgroundColor);
        slopeShade = EditorGUILayout.Slider(new GUIContent("Slope Shade", "陡的地方變暗多少。0 = 純平面色塊。"), slopeShade, 0f, 1f);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        fileName = EditorGUILayout.TextField("File Name", fileName);

        EditorGUILayout.Space();
        Terrain[] terrains = FindTerrains();
        string scope = sourceRoot != null ? $"「{sourceRoot.name}」底下" : "場景裡";
        string inactive = includeInactive ? "含停用中的" : "只算啟用中的";
        EditorGUILayout.HelpBox($"{scope}找到 {terrains.Length} 塊 Terrain（{inactive}）。", MessageType.Info);

        using (new EditorGUI.DisabledScope(terrains.Length == 0))
        {
            if (GUILayout.Button("Bake", GUILayout.Height(32)))
                Bake(terrains);
        }

        EditorGUILayout.EndScrollView();
    }

    private Terrain[] FindTerrains()
    {
        if (sourceRoot != null)
            return sourceRoot.GetComponentsInChildren<Terrain>(includeInactive);
        return Object.FindObjectsByType<Terrain>(
            includeInactive ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
            FindObjectsSortMode.None);
    }

    // ═══════════════════════ 烘焙 ═══════════════════════

    private void Bake(Terrain[] terrains)
    {
        // ── 1. 世界範圍 ──
        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        float minH = float.MaxValue, maxH = float.MinValue;
        var valid = new List<Terrain>();

        foreach (Terrain t in terrains)
        {
            if (t == null || t.terrainData == null) continue;
            Vector3 pos = t.GetPosition();
            Vector3 size = t.terrainData.size;
            minX = Mathf.Min(minX, pos.x); minZ = Mathf.Min(minZ, pos.z);
            maxX = Mathf.Max(maxX, pos.x + size.x); maxZ = Mathf.Max(maxZ, pos.z + size.z);
            valid.Add(t);
        }
        if (valid.Count == 0) return;

        float mpp = metersPerPixel;
        int w = Mathf.CeilToInt((maxX - minX) / mpp);
        int h = Mathf.CeilToInt((maxZ - minZ) / mpp);
        if (w > maxTextureSize || h > maxTextureSize)
        {
            mpp = Mathf.Max((maxX - minX), (maxZ - minZ)) / maxTextureSize;
            mpp = Mathf.Ceil(mpp * 100f) / 100f;
            w = Mathf.CeilToInt((maxX - minX) / mpp);
            h = Mathf.CeilToInt((maxZ - minZ) / mpp);
            Debug.LogWarning($"MinimapBaker: 貼圖會超過 {maxTextureSize}，Meters Per Pixel 自動放大到 {mpp:0.##}");
        }

        var heights = new float[w * h];
        for (int i = 0; i < heights.Length; i++) heights[i] = float.NaN;

        // ── 2. 每塊地形：讀一次高度陣列，填它涵蓋的像素 ──
        try
        {
            for (int ti = 0; ti < valid.Count; ti++)
            {
                Terrain t = valid[ti];
                if (EditorUtility.DisplayCancelableProgressBar("Minimap Baker",
                        $"讀取地形 {ti + 1}/{valid.Count}：{t.name}", (float)ti / valid.Count * 0.7f))
                {
                    Debug.Log("MinimapBaker: 已取消");
                    return;
                }

                TerrainData td = t.terrainData;
                int res = td.heightmapResolution;
                float[,] hm = td.GetHeights(0, 0, res, res);   // [z, x]，0～1
                Vector3 pos = t.GetPosition();
                Vector3 size = td.size;

                int px0 = Mathf.Max(0, Mathf.FloorToInt((pos.x - minX) / mpp));
                int px1 = Mathf.Min(w - 1, Mathf.CeilToInt((pos.x + size.x - minX) / mpp));
                int pz0 = Mathf.Max(0, Mathf.FloorToInt((pos.z - minZ) / mpp));
                int pz1 = Mathf.Min(h - 1, Mathf.CeilToInt((pos.z + size.z - minZ) / mpp));

                for (int pz = pz0; pz <= pz1; pz++)
                {
                    float wz = minZ + (pz + 0.5f) * mpp;
                    float v = (wz - pos.z) / size.z;
                    if (v < 0f || v > 1f) continue;

                    for (int px = px0; px <= px1; px++)
                    {
                        float wx = minX + (px + 0.5f) * mpp;
                        float u = (wx - pos.x) / size.x;
                        if (u < 0f || u > 1f) continue;

                        float hh = pos.y + Bilinear(hm, res, u, v) * size.y;
                        heights[pz * w + px] = hh;
                        if (hh < minH) minH = hh;
                        if (hh > maxH) maxH = hh;
                    }
                }
            }

            // ── 3. 分層、上色、畫線 ──

            float interval = Mathf.Max(0.5f, contourInterval);
            int bandMin = Mathf.FloorToInt(minH / interval);
            int bandMax = Mathf.FloorToInt(maxH / interval);
            int bandSpan = Mathf.Max(1, bandMax - bandMin);

            // 每一層的顏色先算好（Gradient.Evaluate 不便宜，像素有幾千萬個）
            var bandColors = new Color[bandSpan + 1];
            for (int k = 0; k <= bandSpan; k++)
                bandColors[k] = heightColors.Evaluate(Mathf.Clamp01((k + 0.5f) / (bandSpan + 1f)));

            var bands = new int[w * h];
            for (int i = 0; i < heights.Length; i++)
                bands[i] = float.IsNaN(heights[i]) ? int.MinValue : Mathf.FloorToInt(heights[i] / interval);

            var pixels = new Color32[w * h];
            for (int z = 0; z < h; z++)
            {
                if ((z & 255) == 0 && EditorUtility.DisplayCancelableProgressBar("Minimap Baker",
                        "上色、畫等高線…", 0.7f + 0.25f * z / h))
                {
                    Debug.Log("MinimapBaker: 已取消");
                    return;
                }

                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    int b = bands[i];
                    if (b == int.MinValue) { pixels[i] = backgroundColor; continue; }

                    // 平面色塊：取這一層的顏色
                    Color c = bandColors[Mathf.Clamp(b - bandMin, 0, bandSpan)];

                    // 坡度：陡的地方變暗
                    if (slopeShade > 0f)
                    {
                        float hx = Sample(heights, w, h, x + 1, z, heights[i]) - Sample(heights, w, h, x - 1, z, heights[i]);
                        float hz = Sample(heights, w, h, x, z + 1, heights[i]) - Sample(heights, w, h, x, z - 1, heights[i]);
                        float slope = Mathf.Clamp01(Mathf.Sqrt(hx * hx + hz * hz) / (2f * mpp));   // tan(坡度)，1 = 45°
                        c = Color.Lerp(c, c * 0.5f, slope * slopeShade);
                        c.a = 1f;
                    }

                    // 輪廓線：右邊或上面的層不一樣 → 交界
                    int line = 0;   // 0 = 沒有，1 = 一般，2 = 主線
                    line = Mathf.Max(line, Boundary(bands, w, h, x + 1, z, b));
                    line = Mathf.Max(line, Boundary(bands, w, h, x, z + 1, b));
                    if (line > 0)
                    {
                        Color lc = line == 2 ? majorLineColor : lineColor;
                        c = Color.Lerp(c, new Color(lc.r, lc.g, lc.b, 1f), lc.a);
                    }

                    pixels[i] = c;
                }
            }

            // ── 4. 存檔 ──
            EditorUtility.DisplayProgressBar("Minimap Baker", "存檔…", 0.95f);
            if (!AssetDatabase.IsValidFolder(outputFolder))
                CreateFolders(outputFolder);

            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.SetPixels32(pixels);
            tex.Apply();
            string pngPath = $"{outputFolder}/{fileName}.png";
            File.WriteAllBytes(pngPath, tex.EncodeToPNG());
            DestroyImmediate(tex);

            AssetDatabase.ImportAsset(pngPath, ImportAssetOptions.ForceUpdate);
            var importer = (TextureImporter)AssetImporter.GetAtPath(pngPath);
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.npotScale = TextureImporterNPOTScale.None;   // 保持原尺寸，世界範圍才對得上
                importer.mipmapEnabled = true;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Bilinear;
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = Mathf.Max(2048, Mathf.NextPowerOfTwo(Mathf.Max(w, h)));
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            string dataPath = $"{outputFolder}/{fileName}Data.asset";
            var data = AssetDatabase.LoadAssetAtPath<MinimapData>(dataPath);
            if (data == null)
            {
                data = CreateInstance<MinimapData>();
                AssetDatabase.CreateAsset(data, dataPath);
            }
            data.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
            data.worldMin = new Vector2(minX, minZ);
            data.worldSize = new Vector2(w * mpp, h * mpp);   // 對齊像素（不是地形的實際範圍）
            data.metersPerPixel = mpp;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            Debug.Log($"MinimapBaker: 完成 {w}×{h}（{mpp:0.##} m/px），{valid.Count} 塊地形，" +
                      $"高度 {minH:0}～{maxH:0} m，{bandSpan + 1} 層 → {pngPath}", data);
            EditorGUIUtility.PingObject(data);
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static float Bilinear(float[,] hm, int res, float u, float v)
    {
        float fx = Mathf.Clamp01(u) * (res - 1);
        float fz = Mathf.Clamp01(v) * (res - 1);
        int x0 = Mathf.FloorToInt(fx), z0 = Mathf.FloorToInt(fz);
        int x1 = Mathf.Min(x0 + 1, res - 1), z1 = Mathf.Min(z0 + 1, res - 1);
        float tx = fx - x0, tz = fz - z0;
        float a = Mathf.Lerp(hm[z0, x0], hm[z0, x1], tx);
        float b = Mathf.Lerp(hm[z1, x0], hm[z1, x1], tx);
        return Mathf.Lerp(a, b, tz);
    }

    private static float Sample(float[] heights, int w, int h, int x, int z, float fallback)
    {
        if (x < 0 || z < 0 || x >= w || z >= h) return fallback;
        float v = heights[z * w + x];
        return float.IsNaN(v) ? fallback : v;
    }

    // 跟鄰居不同層 → 1；跨過的是主線那一層 → 2
    private int Boundary(int[] bands, int w, int h, int x, int z, int b)
    {
        if (x < 0 || z < 0 || x >= w || z >= h) return 0;
        int nb = bands[z * w + x];
        if (nb == int.MinValue || nb == b) return 0;

        if (majorEvery > 0)
        {
            int lo = Mathf.Min(b, nb) + 1, hi = Mathf.Max(b, nb);   // 跨過的層界：lo～hi
            for (int k = lo; k <= hi; k++)
                if (((k % majorEvery) + majorEvery) % majorEvery == 0) return 2;
        }
        return 1;
    }

    private static void CreateFolders(string path)
    {
        string[] parts = path.Split('/');
        string cur = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = $"{cur}/{parts[i]}";
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(cur, parts[i]);
            cur = next;
        }
    }
}
