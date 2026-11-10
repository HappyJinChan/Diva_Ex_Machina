using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Diva Ex Machina > Canyon Terrain Builder
///
/// 產生一條「蜿蜒的峽谷走廊」地形：中間是可以走的平地，四周（含兩端）被非常高的懸崖包圍。
/// 走廊沿 +X 延伸，從 Origin 開始，長度 Length。
///
///   · 中線左右蜿蜒（兩組正弦波疊加，振幅 / 波長可調）。
///   · 平地寬度沿著走廊在 Min ~ Max 之間變化。
///   · 兩端是半圓形的盡頭。
///   · 懸崖邊緣用雜訊打亂，不會是完美的曲線；懸崖頂是起伏的高原。
///
/// 輸出跟 Heightmap → Terrain 一樣：Root 底下的 Tile_x_z + TerrainData 資產，可加 TerrainStreamer。
/// 重新產生：同名 TerrainData 只更新高度（材質、貼圖層保留）。範圍變小時，勾 Remove Unused Tiles 會刪掉多出來的塊。
/// 改參數後可以先按 Preview 看俯視圖（白 = 高）。
/// </summary>
public class CanyonTerrainBuilder : EditorWindow
{
    [SerializeField] private Vector3 origin = Vector3.zero;   // 走廊起點（中線、平地高度）
    [SerializeField] private float length = 15000f;

    [Header("Floor")]
    [SerializeField] private float minFloorWidth = 1500f;
    [SerializeField] private float maxFloorWidth = 3000f;
    [SerializeField] private float widthWavelength = 4000f;
    [SerializeField] private float floorBumps = 3f;            // 平地上的小起伏（公尺）

    [Header("Meander")]
    [SerializeField] private float meanderAmplitude = 900f;
    [SerializeField] private float meanderWavelength = 10000f;
    [SerializeField] private float meanderAmplitude2 = 150f;
    [SerializeField] private float meanderWavelength2 = 2500f;

    [Header("Cliff")]
    [SerializeField] private float cliffHeight = 800f;
    [SerializeField] private float cliffRun = 160f;            // 從崖腳到崖頂的水平距離（越小越陡）
    [SerializeField] private float edgeNoise = 120f;           // 崖邊位置被雜訊推動的幅度（公尺）
    [SerializeField] private float edgeNoiseScale = 600f;      // 崖邊雜訊的尺度（公尺）
    [SerializeField] private float plateauNoise = 120f;        // 崖頂高原的起伏（公尺）
    [SerializeField] private float margin = 1500f;             // 平地外面還要多做多寬的懸崖 / 高原

    [Header("Tiles")]
    [SerializeField] private float tileSize = 1000f;
    [SerializeField] private int resolutionIndex = 2;          // 513
    [SerializeField] private int alphamapResolution = 256;
    [SerializeField] private int seed = 1;
    [SerializeField] private Terrain template;
    [SerializeField] private string rootName = "Canyon Terrain";
    [SerializeField] private string dataFolder = "Assets/Terrain/Canyon";
    [SerializeField] private bool addStreamer = true;
    [SerializeField] private bool removeUnusedTiles = true;

    private static readonly int[] Resolutions = { 129, 257, 513, 1025 };
    private static readonly string[] ResolutionLabels = { "129", "257", "513", "1025" };

    private Texture2D _preview;
    private Vector2 _scroll;
    private float _phase1, _phase2, _phaseW, _noiseOx, _noiseOz;

    [MenuItem("Tools/Diva Ex Machina/Canyon Terrain Builder")]
    private static void Open() => GetWindow<CanyonTerrainBuilder>("Canyon Terrain");

    private void OnDisable()
    {
        if (_preview != null) DestroyImmediate(_preview);
    }

    private void OnGUI()
    {
        var so = new SerializedObject(this);
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("走廊", EditorStyles.boldLabel);
        EditorGUILayout.PropertyField(so.FindProperty("origin"), new GUIContent("Origin", "走廊起點（中線上、平地的高度）。走廊往 +X 延伸。"));
        EditorGUILayout.PropertyField(so.FindProperty("length"), new GUIContent("Length (m)", "平地從頭到尾的長度。"));

        DrawProps(so, "minFloorWidth", "maxFloorWidth", "widthWavelength", "floorBumps",
                      "meanderAmplitude", "meanderWavelength", "meanderAmplitude2", "meanderWavelength2",
                      "cliffHeight", "cliffRun", "edgeNoise", "edgeNoiseScale", "plateauNoise", "margin",
                      "tileSize");
        var resProp = so.FindProperty("resolutionIndex");
        resProp.intValue = EditorGUILayout.Popup("Heightmap Resolution", resProp.intValue, ResolutionLabels);
        DrawProps(so, "alphamapResolution", "seed", "template", "rootName", "dataFolder", "addStreamer", "removeUnusedTiles");
        so.ApplyModifiedProperties();

        GetBounds(out Vector2 min, out Vector2 max);
        int nx = Mathf.CeilToInt((max.x - min.x) / tileSize), nz = Mathf.CeilToInt((max.y - min.y) / tileSize);
        int res = Resolutions[Mathf.Clamp(resolutionIndex, 0, Resolutions.Length - 1)];
        float slope = Mathf.Atan2(cliffHeight, Mathf.Max(1f, cliffRun)) * Mathf.Rad2Deg;
        EditorGUILayout.HelpBox(
            $"地圖 {(max.x - min.x) / 1000f:0.0} × {(max.y - min.y) / 1000f:0.0} km，{nx} × {nz} = {nx * nz} 塊，" +
            $"高度點間距 {tileSize / (res - 1):0.##} m。懸崖平均坡度約 {slope:0}°。\n" +
            "懸崖要陡到爬不上去，Cliff Run 要遠小於 Cliff Height；高度點間距太大時，陡坡會變成鋸齒。",
            MessageType.None);

        EditorGUILayout.Space(6);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Preview", GUILayout.Height(28))) BuildPreview();
            if (GUILayout.Button("Build Terrain", GUILayout.Height(28))) Build();
        }

        if (_preview != null)
        {
            float w = EditorGUIUtility.currentViewWidth - 30f;
            float h = w * _preview.height / _preview.width;
            Rect r = GUILayoutUtility.GetRect(w, h, GUILayout.ExpandWidth(false));
            GUI.DrawTexture(r, _preview, ScaleMode.ScaleToFit);
        }

        EditorGUILayout.EndScrollView();
    }

    private static void DrawProps(SerializedObject so, params string[] names)
    {
        foreach (string n in names) EditorGUILayout.PropertyField(so.FindProperty(n), true);
    }

    // ── 形狀 ─────────────────────────────────────────────

    private void InitNoise()
    {
        var rng = new System.Random(seed);
        _phase1 = (float)rng.NextDouble() * Mathf.PI * 2f;
        _phase2 = (float)rng.NextDouble() * Mathf.PI * 2f;
        _phaseW = (float)rng.NextDouble() * Mathf.PI * 2f;
        _noiseOx = (float)rng.NextDouble() * 10000f;
        _noiseOz = (float)rng.NextDouble() * 10000f;
    }

    /// <summary>中線的 Z 偏移（相對 origin），u = 沿走廊的距離。</summary>
    private float CenterZ(float u)
    {
        float a = meanderWavelength > 1f ? meanderAmplitude * Mathf.Sin(u / meanderWavelength * Mathf.PI * 2f + _phase1) : 0f;
        float b = meanderWavelength2 > 1f ? meanderAmplitude2 * Mathf.Sin(u / meanderWavelength2 * Mathf.PI * 2f + _phase2) : 0f;
        return a + b;
    }

    /// <summary>平地半寬，兩端收成半圓。</summary>
    private float HalfWidth(float u)
    {
        float t = widthWavelength > 1f ? 0.5f + 0.5f * Mathf.Sin(u / widthWavelength * Mathf.PI * 2f + _phaseW) : 0.5f;
        float hw = Mathf.Lerp(minFloorWidth, maxFloorWidth, t) * 0.5f;

        // 盡頭：半徑 = 當地半寬的半圓
        float r = Mathf.Max(1f, Mathf.Min(hw, length * 0.5f));
        float e = Mathf.Min(u, length - u);                       // 離最近那一端的距離
        if (e < r)
        {
            float k = 1f - Mathf.Clamp01(e / r);
            hw *= Mathf.Sqrt(Mathf.Max(0f, 1f - k * k));          // 半圓
        }
        return hw;
    }

    private float Fbm(float x, float z, float scale)
    {
        float sum = 0f, amp = 1f, norm = 0f, f = 1f / Mathf.Max(1f, scale);
        for (int o = 0; o < 4; o++)
        {
            sum += (Mathf.PerlinNoise((x + _noiseOx) * f, (z + _noiseOz) * f) * 2f - 1f) * amp;
            norm += amp; amp *= 0.5f; f *= 2f;
        }
        return sum / norm;   // -1 ~ 1
    }

    /// <summary>世界座標 → 相對 origin 的高度（公尺）。</summary>
    private float HeightAt(float wx, float wz)
    {
        float u = wx - origin.x;
        float v = wz - origin.z;

        // 到平地邊緣的距離（> 0 = 平地外）。兩端外面用到端點的距離，所以盡頭是圓的
        float uc = Mathf.Clamp(u, 0f, length);
        float side = Mathf.Abs(v - CenterZ(uc)) - HalfWidth(uc);
        float along = Mathf.Abs(u - uc);
        float sideOut = Mathf.Max(0f, side);
        float d = along > 0f ? Mathf.Sqrt(along * along + sideOut * sideOut) : side;

        d += Fbm(wx, wz, edgeNoiseScale) * edgeNoise;             // 崖邊不規則

        if (d <= 0f)
            return Fbm(wx * 3f, wz * 3f, 200f) * floorBumps;      // 平地

        float t = Mathf.Clamp01(d / Mathf.Max(1f, cliffRun));
        float rise = 1f - (1f - t) * (1f - t) * (1f - t);          // 崖腳急升、崖頂收平
        float plateau = (Fbm(wx, wz, 1500f) * 0.5f + 0.5f) * plateauNoise * t;
        return rise * cliffHeight + plateau;
    }

    private void GetBounds(out Vector2 min, out Vector2 max)
    {
        InitNoise();
        float zMin = float.MaxValue, zMax = float.MinValue;
        for (float u = 0f; u <= length; u += 50f)
        {
            float c = CenterZ(u), hw = HalfWidth(u);
            zMin = Mathf.Min(zMin, c - hw); zMax = Mathf.Max(zMax, c + hw);
        }
        float m = margin + cliffRun + edgeNoise;
        min = new Vector2(origin.x - m, origin.z + zMin - m);
        max = new Vector2(origin.x + length + m, origin.z + zMax + m);
    }

    // ── Preview ──────────────────────────────────────────

    private void BuildPreview()
    {
        GetBounds(out Vector2 min, out Vector2 max);
        int pw = 512;
        int ph = Mathf.Clamp(Mathf.RoundToInt(pw * (max.y - min.y) / (max.x - min.x)), 16, 512);
        if (_preview != null) DestroyImmediate(_preview);
        _preview = new Texture2D(pw, ph, TextureFormat.RGB24, false) { filterMode = FilterMode.Bilinear };
        float top = cliffHeight + plateauNoise;
        var px = new Color[pw * ph];
        for (int j = 0; j < ph; j++)
            for (int i = 0; i < pw; i++)
            {
                float wx = Mathf.Lerp(min.x, max.x, i / (pw - 1f));
                float wz = Mathf.Lerp(min.y, max.y, j / (ph - 1f));
                float g = Mathf.Clamp01(HeightAt(wx, wz) / Mathf.Max(1f, top));
                px[j * pw + i] = g < 0.02f ? new Color(0.55f, 0.48f, 0.36f) : new Color(g, g, g);   // 平地上色
            }
        _preview.SetPixels(px);
        _preview.Apply();
    }

    // ── Build ────────────────────────────────────────────

    private void Build()
    {
        if (!dataFolder.StartsWith("Assets"))
        {
            EditorUtility.DisplayDialog("Canyon Terrain", "Data Folder 必須在 Assets 底下。", "OK");
            return;
        }

        GetBounds(out Vector2 min, out Vector2 max);
        int nx = Mathf.CeilToInt((max.x - min.x) / tileSize);
        int nz = Mathf.CeilToInt((max.y - min.y) / tileSize);
        int res = Resolutions[Mathf.Clamp(resolutionIndex, 0, Resolutions.Length - 1)];

        // 高度範圍：平地的小起伏會低於 0，所以整體往上墊 floorBumps
        float baseOffset = floorBumps + 1f;
        float terrainHeight = cliffHeight + plateauNoise + baseOffset + 10f;
        Vector3 mapOrigin = new Vector3(min.x, origin.y - baseOffset, min.y);

        EnsureFolder(dataFolder);
        Transform root = FindOrCreateRoot();

        var heights = new float[res, res];
        int total = nx * nz, done = 0;
        bool cancelled = false;
        try
        {
            for (int tz = 0; tz < nz && !cancelled; tz++)
                for (int tx = 0; tx < nx; tx++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Canyon Terrain", $"Tile {tx},{tz}（{done + 1} / {total}）", done / (float)total))
                    { cancelled = true; break; }

                    for (int j = 0; j < res; j++)
                        for (int i = 0; i < res; i++)
                        {
                            // 用全域座標取樣：相鄰兩塊的交界完全一樣，不會有接縫
                            float wx = mapOrigin.x + (tx + i / (res - 1f)) * tileSize;
                            float wz = mapOrigin.z + (tz + j / (res - 1f)) * tileSize;
                            heights[j, i] = Mathf.Clamp01((HeightAt(wx, wz) + baseOffset) / terrainHeight);
                        }

                    BuildTile(root, tx, tz, res, terrainHeight, mapOrigin, heights);
                    done++;
                }
        }
        finally
        {
            AssetDatabase.SaveAssets();
            EditorUtility.ClearProgressBar();
        }

        if (removeUnusedTiles && !cancelled)
        {
            for (int i = root.childCount - 1; i >= 0; i--)
            {
                Transform c = root.GetChild(i);
                if (!c.name.StartsWith("Tile_")) continue;
                string[] p = c.name.Split('_');
                if (p.Length == 3 && int.TryParse(p[1], out int x) && int.TryParse(p[2], out int z) && (x >= nx || z >= nz))
                    Undo.DestroyObjectImmediate(c.gameObject);
            }
        }

        if (addStreamer && root.GetComponent<TerrainStreamer>() == null)
            Undo.AddComponent<TerrainStreamer>(root.gameObject);
        var streamer = root.GetComponent<TerrainStreamer>();
        if (streamer != null) streamer.RefreshTiles();

        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(root.gameObject.scene);
        Debug.Log($"[Canyon Terrain] 完成 {done} / {total} 塊{(cancelled ? "（已取消）" : "")}。" +
                  $"範圍 X {min.x:0} ~ {max.x:0}、Z {min.y:0} ~ {max.y:0}，平地高度 Y = {origin.y:0}。");
    }

    private void BuildTile(Transform root, int tx, int tz, int res, float terrainHeight, Vector3 mapOrigin, float[,] heights)
    {
        string name = $"Tile_{tx}_{tz}";
        string assetPath = $"{dataFolder.TrimEnd('/')}/{name}.asset";

        TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath);
        if (data == null)
        {
            data = new TerrainData();
            data.heightmapResolution = res;
            data.alphamapResolution = alphamapResolution;
            data.baseMapResolution = Mathf.Min(1024, alphamapResolution * 2);
            data.size = new Vector3(tileSize, terrainHeight, tileSize);
            if (template != null && template.terrainData != null)
                data.terrainLayers = template.terrainData.terrainLayers;
            AssetDatabase.CreateAsset(data, assetPath);
        }
        else if (data.heightmapResolution != res) data.heightmapResolution = res;
        data.size = new Vector3(tileSize, terrainHeight, tileSize);   // 改解析度會重設 size，所以每次都設

        data.SetHeights(0, 0, heights);
        EditorUtility.SetDirty(data);

        Transform t = root.Find(name);
        Terrain terrain;
        if (t == null)
        {
            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Build Canyon Terrain");
            go.transform.SetParent(root, false);
            terrain = go.GetComponent<Terrain>();
            ApplyTemplate(terrain);
        }
        else
        {
            terrain = t.GetComponent<Terrain>();
            if (terrain == null) terrain = t.gameObject.AddComponent<Terrain>();
            if (terrain.terrainData != data) terrain.terrainData = data;
            var col = t.GetComponent<TerrainCollider>();
            if (col == null) col = t.gameObject.AddComponent<TerrainCollider>();
            col.terrainData = data;
        }

        terrain.transform.position = mapOrigin + new Vector3(tx * tileSize, 0f, tz * tileSize);
        terrain.allowAutoConnect = true;
        terrain.groupingID = 0;
    }

    private void ApplyTemplate(Terrain terrain)
    {
        if (template == null) return;
        terrain.materialTemplate = template.materialTemplate;
        terrain.heightmapPixelError = template.heightmapPixelError;
        terrain.basemapDistance = template.basemapDistance;
        terrain.drawInstanced = template.drawInstanced;
        terrain.shadowCastingMode = template.shadowCastingMode;
        terrain.gameObject.layer = template.gameObject.layer;
        terrain.gameObject.tag = template.gameObject.tag;
    }

    private Transform FindOrCreateRoot()
    {
        GameObject root = GameObject.Find(rootName);
        if (root == null)
        {
            root = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(root, "Build Canyon Terrain");
        }
        return root.transform;
    }

    private static void EnsureFolder(string folder)
    {
        folder = folder.TrimEnd('/');
        if (AssetDatabase.IsValidFolder(folder)) return;
        string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
    }
}
