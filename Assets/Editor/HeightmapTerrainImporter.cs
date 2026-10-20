using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// 把一張灰階高度圖（PNG / JPG，8 或 16 位元）轉成一整片切好塊的 Unity Terrain。
/// Tools > Diva Ex Machina > Heightmap → Terrain
///
/// · 圖片代表整張地圖（預設 33 × 33 km），切成 Tiles Per Side × Tiles Per Side 塊。
/// · 黑色最高（Black Is High，預設開）：高度 = (1 − 灰階) × Terrain Height。
///   Terrain Height 510 m、8 位元圖 → 每 1 個灰階剛好 2 m。
/// · 相鄰兩塊從同一張大圖的同一個位置取樣，交界那一排完全一樣 → 不會有接縫。
/// · 圖片下方 = 南（-Z），左方 = 西（-X）。
/// · 第一次：在 Root 底下建立 Tile_x_z 物件 + TerrainData 資產。
///   之後改圖重新匯入：同名的 TerrainData 只更新高度（材質、貼圖層、植被都保留）。
/// · 可以只匯入一個範圍（Tile 範圍），改一小區時比較快。
/// </summary>
public class HeightmapTerrainImporter : EditorWindow
{
    [SerializeField] private Texture2D heightmap;
    [SerializeField] private bool blackIsHigh = true;

    [SerializeField] private Vector3 mapOrigin = Vector3.zero;   // 地圖西南角的世界座標（Y = 灰階最低處的高度）
    [SerializeField] private float mapSize = 33000f;             // 整張圖對應的世界大小（公尺）
    [SerializeField] private float terrainHeight = 510f;         // 灰階 0 → 1 對應的高度差
    [SerializeField] private int tilesPerSide = 33;
    [SerializeField] private int resolutionIndex = 2;            // 513

    [SerializeField] private bool limitRange = false;
    [SerializeField] private Vector2Int rangeMin = Vector2Int.zero;
    [SerializeField] private Vector2Int rangeMax = new Vector2Int(32, 32);

    [SerializeField] private Terrain template;                   // 複製材質、貼圖層、Pixel Error 等設定（可留空）
    [SerializeField] private int alphamapResolution = 256;
    [SerializeField] private string rootName = "Heightmap Terrain";
    [SerializeField] private string dataFolder = "Assets/Terrain/Heightmap";
    [SerializeField] private bool addStreamer = true;

    private static readonly int[] Resolutions = { 129, 257, 513, 1025, 2049 };
    private static readonly string[] ResolutionLabels = { "129", "257", "513", "1025", "2049" };

    private Vector2 _scroll;

    [MenuItem("Tools/Diva Ex Machina/Heightmap → Terrain")]
    private static void Open() => GetWindow<HeightmapTerrainImporter>("Heightmap → Terrain");

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("高度圖", EditorStyles.boldLabel);
        heightmap = (Texture2D)EditorGUILayout.ObjectField("Heightmap", heightmap, typeof(Texture2D), false);
        blackIsHigh = EditorGUILayout.Toggle(new GUIContent("Black Is High", "黑色最高、白色最低。"), blackIsHigh);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("地圖", EditorStyles.boldLabel);
        mapOrigin = EditorGUILayout.Vector3Field(new GUIContent("Map Origin", "地圖西南角（圖片左下角）的世界座標。Y = 最低處的高度。"), mapOrigin);
        mapSize = EditorGUILayout.FloatField(new GUIContent("Map Size (m)", "整張圖對應的世界大小。"), mapSize);
        terrainHeight = EditorGUILayout.FloatField(new GUIContent("Terrain Height (m)", "最低到最高的高度差。510 + 8 位元圖 = 每個灰階 2 m。"), terrainHeight);
        tilesPerSide = EditorGUILayout.IntSlider(new GUIContent("Tiles Per Side", "每邊切幾塊。33 → 每塊 1 km。"), tilesPerSide, 1, 64);
        resolutionIndex = EditorGUILayout.Popup(new GUIContent("Heightmap Resolution", "每塊的高度圖解析度。"), resolutionIndex, ResolutionLabels);

        float tileSize = mapSize / Mathf.Max(1, tilesPerSide);
        int res = Resolutions[resolutionIndex];
        EditorGUILayout.HelpBox(
            $"每塊 {tileSize:0.#} m，高度點間距 {tileSize / (res - 1):0.##} m，共 {tilesPerSide * tilesPerSide} 塊。" +
            (blackIsHigh ? "\n灰階 v（0～255）的高度 = Origin.y + (255 − v) / 255 × Terrain Height。"
                         : "\n灰階 v（0～255）的高度 = Origin.y + v / 255 × Terrain Height。"),
            MessageType.None);

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("範圍", EditorStyles.boldLabel);
        limitRange = EditorGUILayout.Toggle(new GUIContent("Only Some Tiles", "只匯入一個範圍（改一小區時比較快）。"), limitRange);
        using (new EditorGUI.DisabledScope(!limitRange))
        {
            rangeMin = EditorGUILayout.Vector2IntField("Tile Min (x, z)", rangeMin);
            rangeMax = EditorGUILayout.Vector2IntField("Tile Max (x, z)", rangeMax);
        }

        EditorGUILayout.Space(6);
        EditorGUILayout.LabelField("輸出", EditorStyles.boldLabel);
        template = (Terrain)EditorGUILayout.ObjectField(new GUIContent("Template Terrain", "從這塊 Terrain 複製材質、貼圖層、Layer、Pixel Error 等設定。可留空。"), template, typeof(Terrain), true);
        alphamapResolution = EditorGUILayout.IntPopup(new GUIContent("Alphamap Resolution", "貼圖層的解析度。越大檔案越大。"), alphamapResolution,
            new[] { new GUIContent("64"), new GUIContent("128"), new GUIContent("256"), new GUIContent("512"), new GUIContent("1024") },
            new[] { 64, 128, 256, 512, 1024 });
        rootName = EditorGUILayout.TextField(new GUIContent("Root Name", "所有 Tile 放在這個物件底下。"), rootName);
        dataFolder = EditorGUILayout.TextField(new GUIContent("Data Folder", "TerrainData 資產存放的資料夾（Assets 底下）。"), dataFolder);
        addStreamer = EditorGUILayout.Toggle(new GUIContent("Add TerrainStreamer", "在 Root 上加 TerrainStreamer（依距離開關 Tile）。"), addStreamer);

        EditorGUILayout.Space(10);
        using (new EditorGUI.DisabledScope(heightmap == null))
        {
            if (GUILayout.Button("匯入 / 更新地形", GUILayout.Height(32)))
                Import();
        }

        EditorGUILayout.EndScrollView();
    }

    // ── 匯入 ─────────────────────────────────────────────

    private void Import()
    {
        string texPath = AssetDatabase.GetAssetPath(heightmap);
        if (string.IsNullOrEmpty(texPath) || !File.Exists(texPath))
        {
            EditorUtility.DisplayDialog("Heightmap → Terrain", "找不到高度圖檔案。", "OK");
            return;
        }
        if (!dataFolder.StartsWith("Assets"))
        {
            EditorUtility.DisplayDialog("Heightmap → Terrain", "Data Folder 必須在 Assets 底下。", "OK");
            return;
        }

        int res = Resolutions[resolutionIndex];
        int tiles = Mathf.Max(1, tilesPerSide);
        float tileSize = mapSize / tiles;

        int x0 = 0, z0 = 0, x1 = tiles - 1, z1 = tiles - 1;
        if (limitRange)
        {
            x0 = Mathf.Clamp(rangeMin.x, 0, tiles - 1); z0 = Mathf.Clamp(rangeMin.y, 0, tiles - 1);
            x1 = Mathf.Clamp(rangeMax.x, x0, tiles - 1); z1 = Mathf.Clamp(rangeMax.y, z0, tiles - 1);
        }

        // 1. 讀圖（直接讀檔案，不需要在 Import Settings 開 Read/Write）
        ushort[] gray;
        int imgW, imgH;
        try
        {
            EditorUtility.DisplayProgressBar("Heightmap → Terrain", "讀取高度圖…", 0f);
            if (!ReadGray(texPath, out gray, out imgW, out imgH)) return;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        EnsureFolder(dataFolder);
        Transform root = FindOrCreateRoot();

        int total = (x1 - x0 + 1) * (z1 - z0 + 1);
        int done = 0;
        var heights = new float[res, res];
        bool cancelled = false;

        try
        {
            for (int tz = z0; tz <= z1 && !cancelled; tz++)
            {
                for (int tx = x0; tx <= x1; tx++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("Heightmap → Terrain",
                            $"Tile {tx},{tz}（{done + 1} / {total}）", done / (float)total))
                    {
                        cancelled = true;
                        break;
                    }

                    FillHeights(gray, imgW, imgH, tx, tz, tiles, res, heights);
                    BuildTile(root, tx, tz, tileSize, res, heights);
                    done++;
                }
            }
        }
        finally
        {
            AssetDatabase.SaveAssets();
            EditorUtility.ClearProgressBar();
        }

        if (addStreamer && root.GetComponent<TerrainStreamer>() == null)
            Undo.AddComponent<TerrainStreamer>(root.gameObject);
        var streamer = root.GetComponent<TerrainStreamer>();
        if (streamer != null) streamer.RefreshTiles();

        EditorSceneMarkDirty(root.gameObject);
        Debug.Log($"[Heightmap → Terrain] 完成 {done} / {total} 塊{(cancelled ? "（已取消）" : "")}。圖片 {imgW} × {imgH}，每塊 {res} × {res}。");
    }

    // 讀檔 → 每個像素一個 0～65535 的灰階值（y = 0 是圖片最下面一排）
    private static bool ReadGray(string path, out ushort[] gray, out int w, out int h)
    {
        gray = null; w = h = 0;
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
        try
        {
            if (!tex.LoadImage(File.ReadAllBytes(path), false))
            {
                EditorUtility.DisplayDialog("Heightmap → Terrain", "無法讀取圖片（支援 PNG / JPG）。", "OK");
                return false;
            }
            w = tex.width; h = tex.height;
            gray = new ushort[w * h];
            for (int y = 0; y < h; y++)
            {
                Color[] row = tex.GetPixels(0, y, w, 1);
                int o = y * w;
                for (int x = 0; x < w; x++)
                {
                    // 只用紅色通道：灰階圖 R = G = B；16 位元灰階 PNG 載入成單通道格式時只有 R 有值
                    float g = row[x].r;
                    gray[o + x] = (ushort)Mathf.Clamp(Mathf.RoundToInt(g * 65535f), 0, 65535);
                }
                if ((y & 255) == 0)
                    EditorUtility.DisplayProgressBar("Heightmap → Terrain", "讀取高度圖…", y / (float)h);
            }
            return true;
        }
        finally
        {
            DestroyImmediate(tex);
        }
    }

    // 從整張圖取樣這一塊的高度（雙線性）。用整張地圖的全域座標取樣，所以相鄰兩塊的交界完全一樣。
    private void FillHeights(ushort[] gray, int w, int h, int tx, int tz, int tiles, int res, float[,] heights)
    {
        int globalSamples = tiles * (res - 1);   // 整張地圖每邊的高度點間隔數
        for (int j = 0; j < res; j++)
        {
            float v = (tz * (res - 1) + j) / (float)globalSamples;    // 0 = 南
            float py = v * (h - 1);
            int y0 = Mathf.Clamp((int)py, 0, h - 1), y1 = Mathf.Min(y0 + 1, h - 1);
            float fy = py - y0;

            for (int i = 0; i < res; i++)
            {
                float u = (tx * (res - 1) + i) / (float)globalSamples; // 0 = 西
                float px = u * (w - 1);
                int xa = Mathf.Clamp((int)px, 0, w - 1), xb = Mathf.Min(xa + 1, w - 1);
                float fx = px - xa;

                float a = Mathf.Lerp(gray[y0 * w + xa], gray[y0 * w + xb], fx);
                float b = Mathf.Lerp(gray[y1 * w + xa], gray[y1 * w + xb], fx);
                float g = Mathf.Lerp(a, b, fy) / 65535f;

                heights[j, i] = blackIsHigh ? 1f - g : g;              // [z, x]
            }
        }
    }

    private void BuildTile(Transform root, int tx, int tz, float tileSize, int res, float[,] heights)
    {
        string name = $"Tile_{tx}_{tz}";
        string assetPath = $"{dataFolder.TrimEnd('/')}/{name}.asset";

        TerrainData data = AssetDatabase.LoadAssetAtPath<TerrainData>(assetPath);
        bool isNew = data == null;
        if (isNew)
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
        else
        {
            if (data.heightmapResolution != res) data.heightmapResolution = res;
            data.size = new Vector3(tileSize, terrainHeight, tileSize);   // 改解析度會重設 size，所以每次都設
        }

        data.SetHeights(0, 0, heights);
        EditorUtility.SetDirty(data);

        // 場景物件
        Transform t = root.Find(name);
        Terrain terrain;
        if (t == null)
        {
            GameObject go = Terrain.CreateTerrainGameObject(data);
            go.name = name;
            Undo.RegisterCreatedObjectUndo(go, "Import Heightmap");
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
        terrain.detailObjectDistance = template.detailObjectDistance;
        terrain.treeDistance = template.treeDistance;
        terrain.gameObject.layer = template.gameObject.layer;
        terrain.gameObject.tag = template.gameObject.tag;
    }

    private Transform FindOrCreateRoot()
    {
        GameObject root = GameObject.Find(rootName);
        if (root == null)
        {
            root = new GameObject(rootName);
            Undo.RegisterCreatedObjectUndo(root, "Import Heightmap");
        }
        return root.transform;
    }

    private static void EditorSceneMarkDirty(GameObject go)
    {
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(go.scene);
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
