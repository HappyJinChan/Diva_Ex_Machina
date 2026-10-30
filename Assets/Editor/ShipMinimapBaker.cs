using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Diva Ex Machina > Ship Minimap Baker
///
/// 把貝努鳥號（或任何物件）底下所有網格從正上方投影，烘成一張俯視輪廓圖：
///   · 每個像素取「最高的那一層表面」的高度（相對船的原點）。
///   · 高度每 Level Interval 公尺分一層，每層一個顏色（Height Colors，低 → 高）→ 甲板、建築、艦橋分得出來。
///   · 層與層的交界畫線（Level Line），整艘船的外緣畫外框（Outline）。
///   · 沒有東西的地方是 Background（預設透明）。
///
/// 做法是在 CPU 上直接把三角形投影到 XZ 平面（不經過相機和 Shader），所以結果跟 URP、UTS、後處理都無關，
/// 尺寸是準確的公尺數。
///
/// 座標系：船的位置 + 船的水平朝向（只取 Y 旋轉），不含縮放。貼圖上方 = 船頭。
///   → Minimap 元件的 Ship 必須跟這裡的 Ship Root 是同一個物件（預設都是 SupplyManager 所在的物件）。
///
/// 輸出：Output Folder 底下的 PNG + ShipMinimapIcon，拖進 Minimap 元件的 Ship Outline Data。
/// 船改了就再按一次 Bake，會覆蓋同名檔案，Minimap 不用重設。
///
/// 解析度：Meters Per Pixel。貼圖比畫面上顯示的大太多，縮小時細線會斷成虛線（鋸齒）。
/// 以「最拉近那一級」船在畫面上的長度 × 2 左右為準：1 km 的船在 800 m 級距約 100 px → 約 5 m/px（200 px）。
/// 外框寬度也以那一級為準：2 px × 5 m = 10 m，畫面上約 1 px。
/// </summary>
public class ShipMinimapBaker : EditorWindow
{
    [SerializeField] private Transform shipRoot;
    [SerializeField] private bool includeInactive = false;
    [SerializeField] private LayerMask layers = ~0;

    [SerializeField] private float metersPerPixel = 5f;
    [SerializeField] private int maxTextureSize = 4096;
    [SerializeField] private int paddingPixels = 4;

    [SerializeField] private float levelInterval = 5f;

    [SerializeField] private Gradient heightColors = DefaultGradient();
    [SerializeField] private Color levelLineColor = new Color(0f, 0f, 0f, 0.45f);
    [SerializeField] private Color outlineColor = new Color(0.55f, 0.9f, 1f, 1f);
    [SerializeField] private int outlineWidth = 2;
    [SerializeField] private Color backgroundColor = new Color(0f, 0f, 0f, 0f);

    [SerializeField] private string outputFolder = "Assets/Minimap";
    [SerializeField] private string fileName = "ShipIcon";

    private Vector2 _scroll;

    [MenuItem("Tools/Diva Ex Machina/Ship Minimap Baker")]
    private static void Open() => GetWindow<ShipMinimapBaker>("Ship Minimap Baker");

    private static Gradient DefaultGradient()
    {
        // 艦體：低處（船殼）深藍灰 → 高處（甲板建築、艦橋）亮灰白
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.16f, 0.22f, 0.30f), 0f),
                new GradientColorKey(new Color(0.30f, 0.38f, 0.46f), 0.4f),
                new GradientColorKey(new Color(0.55f, 0.62f, 0.68f), 0.75f),
                new GradientColorKey(new Color(0.85f, 0.88f, 0.90f), 1f),
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }

    private void OnEnable()
    {
        if (shipRoot == null)
        {
            var sm = Object.FindFirstObjectByType<SupplyManager>();
            if (sm != null) shipRoot = sm.transform;
        }
    }

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
        shipRoot = (Transform)EditorGUILayout.ObjectField(
            new GUIContent("Ship Root", "要烘的船。座標系以它為準，Minimap 的 Ship 要用同一個物件。預設 = SupplyManager 所在的物件。"),
            shipRoot, typeof(Transform), true);
        includeInactive = EditorGUILayout.Toggle(
            new GUIContent("Include Inactive", "包含停用中的物件和關掉的 Renderer。"), includeInactive);
        layers = LayerMaskField(new GUIContent("Layers", "只烘這些 Layer 上的 Renderer。用來排除除錯用的網格、特效等。"), layers);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Resolution", EditorStyles.boldLabel);
        metersPerPixel = EditorGUILayout.FloatField(new GUIContent("Meters Per Pixel", "每個像素幾公尺。越小越精細、貼圖越大。"), Mathf.Max(0.1f, metersPerPixel));
        maxTextureSize = EditorGUILayout.IntPopup("Max Texture Size", maxTextureSize,
            new[] { "1024", "2048", "4096", "8192" }, new[] { 1024, 2048, 4096, 8192 });
        paddingPixels = EditorGUILayout.IntField(new GUIContent("Padding (px)", "四周留白，外框才不會被切掉。"), Mathf.Max(0, paddingPixels));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Levels", EditorStyles.boldLabel);
        levelInterval = EditorGUILayout.FloatField(new GUIContent("Level Interval (m)", "高度每隔幾公尺分一層色塊。設成甲板層高左右，各層甲板就會是不同顏色。"), Mathf.Max(0.25f, levelInterval));

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Colors", EditorStyles.boldLabel);
        heightColors = EditorGUILayout.GradientField(new GUIContent("Height Colors", "低 → 高（船上最低到最高的表面）。"), heightColors);
        levelLineColor = EditorGUILayout.ColorField(new GUIContent("Level Line", "層與層交界的線。Alpha 0 = 不畫。"), levelLineColor);
        outlineColor = EditorGUILayout.ColorField(new GUIContent("Outline", "整艘船外緣的外框。Alpha 0 = 不畫。"), outlineColor);
        outlineWidth = EditorGUILayout.IntSlider(new GUIContent("Outline Width (px)"), outlineWidth, 0, 8);
        backgroundColor = EditorGUILayout.ColorField(new GUIContent("Background", "沒有東西的地方。"), backgroundColor);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        fileName = EditorGUILayout.TextField("File Name", fileName);

        EditorGUILayout.Space();
        List<Renderer> renderers = FindRenderers();
        if (shipRoot == null)
        {
            EditorGUILayout.HelpBox("請指定 Ship Root。", MessageType.Warning);
        }
        else
        {
            string msg = $"「{shipRoot.name}」底下找到 {renderers.Count} 個 Renderer。";
            if (TryEstimateBounds(renderers, out Vector2 min, out Vector2 max))
            {
                Vector2 sz = max - min;
                msg += $"\n範圍約 {sz.x:0} × {sz.y:0} m（寬 × 長）→ 貼圖約 " +
                       $"{Mathf.CeilToInt(sz.x / metersPerPixel) + paddingPixels * 2} × {Mathf.CeilToInt(sz.y / metersPerPixel) + paddingPixels * 2} px。";
                msg += "\n範圍比船大很多的話，表示底下有不該烘的東西（地面、水面、除錯網格），用 Layers 排除。";
            }
            EditorGUILayout.HelpBox(msg, MessageType.Info);
        }

        using (new EditorGUI.DisabledScope(shipRoot == null || renderers.Count == 0))
        {
            if (GUILayout.Button("Bake", GUILayout.Height(32)))
                Bake(renderers);
        }

        EditorGUILayout.EndScrollView();
    }

    // ═══════════════════════ 收集 ═══════════════════════

    private List<Renderer> FindRenderers()
    {
        var list = new List<Renderer>();
        if (shipRoot == null) return list;

        foreach (Renderer r in shipRoot.GetComponentsInChildren<Renderer>(includeInactive))
        {
            if (r == null) continue;
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;   // 粒子、線、拖尾不算
            if (!includeInactive && !r.enabled) continue;
            if ((layers.value & (1 << r.gameObject.layer)) == 0) continue;
            if (r is MeshRenderer)
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
            }
            else if (((SkinnedMeshRenderer)r).sharedMesh == null) continue;
            list.Add(r);
        }
        return list;
    }

    /// <summary>船座標系：位置 + 水平朝向，不含縮放（單位 = 公尺）。</summary>
    private Matrix4x4 WorldToShip()
    {
        return Matrix4x4.TRS(shipRoot.position, Quaternion.Euler(0f, shipRoot.eulerAngles.y, 0f), Vector3.one).inverse;
    }

    // 用 Renderer.bounds 的八個角估範圍（只給 GUI 預覽用，烘焙時用實際頂點）
    private bool TryEstimateBounds(List<Renderer> renderers, out Vector2 min, out Vector2 max)
    {
        min = new Vector2(float.MaxValue, float.MaxValue);
        max = new Vector2(float.MinValue, float.MinValue);
        if (renderers.Count == 0) return false;

        Matrix4x4 w2s = WorldToShip();
        foreach (Renderer r in renderers)
        {
            Bounds b = r.bounds;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = b.center + Vector3.Scale(b.extents,
                    new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = w2s.MultiplyPoint3x4(c);
                min = Vector2.Min(min, new Vector2(p.x, p.z));
                max = Vector2.Max(max, new Vector2(p.x, p.z));
            }
        }
        return true;
    }

    // ═══════════════════════ 烘焙 ═══════════════════════

    private sealed class MeshPart
    {
        public Vector3[] verts;   // 已轉到船座標系
        public List<int[]> tris = new List<int[]>();
    }

    private void Bake(List<Renderer> renderers)
    {
        Matrix4x4 w2s = WorldToShip();
        var parts = new List<MeshPart>();
        int skipped = 0;
        Mesh baked = null;

        try
        {
            // ── 1. 所有頂點轉到船座標系，順便算範圍 ──
            float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;

            for (int ri = 0; ri < renderers.Count; ri++)
            {
                Renderer r = renderers[ri];
                if ((ri & 31) == 0 && EditorUtility.DisplayCancelableProgressBar("Ship Minimap Baker",
                        $"讀取網格 {ri + 1}/{renderers.Count}：{r.name}", (float)ri / renderers.Count * 0.4f))
                {
                    Debug.Log("ShipMinimapBaker: 已取消");
                    return;
                }

                Mesh mesh;
                Matrix4x4 m;
                if (r is SkinnedMeshRenderer smr)
                {
                    // 用目前的姿勢；BakeMesh(useScale: true) 已含縮放，所以矩陣只放位置和旋轉
                    if (baked == null) baked = new Mesh();
                    smr.BakeMesh(baked, true);
                    mesh = baked;
                    m = w2s * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                }
                else
                {
                    mesh = r.GetComponent<MeshFilter>().sharedMesh;
                    m = w2s * r.localToWorldMatrix;
                }

                MeshPart part;
                try
                {
                    Vector3[] src = mesh.vertices;
                    part = new MeshPart { verts = new Vector3[src.Length] };
                    for (int i = 0; i < src.Length; i++)
                    {
                        Vector3 p = m.MultiplyPoint3x4(src[i]);
                        part.verts[i] = p;
                        if (p.x < minX) minX = p.x;
                        if (p.x > maxX) maxX = p.x;
                        if (p.z < minZ) minZ = p.z;
                        if (p.z > maxZ) maxZ = p.z;
                    }
                    for (int s = 0; s < mesh.subMeshCount; s++)
                        if (mesh.GetTopology(s) == MeshTopology.Triangles)
                            part.tris.Add(mesh.GetTriangles(s));
                }
                catch (System.Exception e)
                {
                    skipped++;
                    Debug.LogWarning($"ShipMinimapBaker: 讀不到「{r.name}」的網格（{e.Message}）。" +
                                     "如果是匯入的模型，在 Import Settings 勾選 Read/Write 再試一次。", r);
                    continue;
                }
                parts.Add(part);
            }
            if (parts.Count == 0 || minX > maxX) return;

            // ── 2. 貼圖大小 ──
            float mpp = metersPerPixel;
            int pad = paddingPixels;
            int w = Mathf.CeilToInt((maxX - minX) / mpp) + pad * 2;
            int h = Mathf.CeilToInt((maxZ - minZ) / mpp) + pad * 2;
            if (w > maxTextureSize || h > maxTextureSize)
            {
                mpp = Mathf.Max(maxX - minX, maxZ - minZ) / (maxTextureSize - pad * 2);
                mpp = Mathf.Ceil(mpp * 100f) / 100f;
                w = Mathf.CeilToInt((maxX - minX) / mpp) + pad * 2;
                h = Mathf.CeilToInt((maxZ - minZ) / mpp) + pad * 2;
                Debug.LogWarning($"ShipMinimapBaker: 貼圖會超過 {maxTextureSize}，Meters Per Pixel 自動放大到 {mpp:0.##}");
            }
            float originX = minX - pad * mpp;   // 貼圖左下角（船座標系）
            float originZ = minZ - pad * mpp;

            // ── 3. 投影：每個像素留最高的表面 ──
            var heights = new float[w * h];
            for (int i = 0; i < heights.Length; i++) heights[i] = float.NegativeInfinity;
            float inv = 1f / mpp;

            for (int pi = 0; pi < parts.Count; pi++)
            {
                if ((pi & 31) == 0 && EditorUtility.DisplayCancelableProgressBar("Ship Minimap Baker",
                        $"投影 {pi + 1}/{parts.Count}", 0.4f + 0.4f * pi / parts.Count))
                {
                    Debug.Log("ShipMinimapBaker: 已取消");
                    return;
                }

                MeshPart part = parts[pi];
                Vector3[] v = part.verts;
                foreach (int[] tris in part.tris)
                {
                    for (int t = 0; t + 2 < tris.Length; t += 3)
                    {
                        Vector3 a = v[tris[t]], b = v[tris[t + 1]], c = v[tris[t + 2]];
                        RasterizeTriangle(heights, w, h,
                            (a.x - originX) * inv, (a.z - originZ) * inv, a.y,
                            (b.x - originX) * inv, (b.z - originZ) * inv, b.y,
                            (c.x - originX) * inv, (c.z - originZ) * inv, c.y);
                    }
                }
            }

            // ── 4. 分層、上色、畫線 ──
            EditorUtility.DisplayProgressBar("Ship Minimap Baker", "上色…", 0.85f);

            float minH = float.MaxValue, maxH = float.MinValue;
            for (int i = 0; i < heights.Length; i++)
            {
                float hh = heights[i];
                if (float.IsNegativeInfinity(hh)) continue;
                if (hh < minH) minH = hh;
                if (hh > maxH) maxH = hh;
            }
            if (minH > maxH) { Debug.LogWarning("ShipMinimapBaker: 沒有投影到任何像素。"); return; }

            float interval = Mathf.Max(0.25f, levelInterval);
            int bandMin = Mathf.FloorToInt(minH / interval);
            int bandMax = Mathf.FloorToInt(maxH / interval);
            int bandSpan = Mathf.Max(1, bandMax - bandMin);

            var bandColors = new Color[bandSpan + 1];
            for (int k = 0; k <= bandSpan; k++)
                bandColors[k] = heightColors.Evaluate(Mathf.Clamp01((k + 0.5f) / (bandSpan + 1f)));

            var bands = new int[w * h];
            for (int i = 0; i < heights.Length; i++)
                bands[i] = float.IsNegativeInfinity(heights[i]) ? int.MinValue : Mathf.FloorToInt(heights[i] / interval);

            // 外框：離「沒有東西的像素」outlineWidth 格以內的實心像素
            bool[] outline = BuildOutlineMask(bands, w, h, outlineColor.a > 0f ? outlineWidth : 0);

            var pixels = new Color32[w * h];
            Color lineOpaque = new Color(levelLineColor.r, levelLineColor.g, levelLineColor.b, 1f);
            Color outlineOpaque = new Color(outlineColor.r, outlineColor.g, outlineColor.b, 1f);

            for (int z = 0; z < h; z++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    int b = bands[i];
                    if (b == int.MinValue) { pixels[i] = backgroundColor; continue; }

                    Color c = bandColors[Mathf.Clamp(b - bandMin, 0, bandSpan)];

                    // 層的交界：右邊或上面是比較低的層 → 畫在高的那一側
                    if (levelLineColor.a > 0f && (LowerNeighbor(bands, w, h, x + 1, z, b) || LowerNeighbor(bands, w, h, x, z + 1, b)
                                                  || LowerNeighbor(bands, w, h, x - 1, z, b) || LowerNeighbor(bands, w, h, x, z - 1, b)))
                        c = Color.Lerp(c, lineOpaque, levelLineColor.a);

                    if (outline[i]) c = Color.Lerp(c, outlineOpaque, outlineColor.a);

                    c.a = 1f;
                    pixels[i] = c;
                }
            }

            // ── 5. 存檔 ──
            EditorUtility.DisplayProgressBar("Ship Minimap Baker", "存檔…", 0.95f);
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
                importer.npotScale = TextureImporterNPOTScale.None;   // 保持原尺寸，公尺數才對得上
                importer.mipmapEnabled = true;                        // 小地圖上會縮得很小，需要 Mipmap 才不會閃爍
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.filterMode = FilterMode.Trilinear;          // 縮放級距之間平滑切換 Mipmap，不會跳
                importer.alphaIsTransparency = true;
                importer.maxTextureSize = Mathf.Max(1024, Mathf.NextPowerOfTwo(Mathf.Max(w, h)));
                importer.textureCompression = TextureImporterCompression.CompressedHQ;
                importer.SaveAndReimport();
            }

            string dataPath = $"{outputFolder}/{fileName}.asset";
            var data = AssetDatabase.LoadAssetAtPath<ShipMinimapIcon>(dataPath);
            if (data == null)
            {
                data = CreateInstance<ShipMinimapIcon>();
                AssetDatabase.CreateAsset(data, dataPath);
            }
            data.texture = AssetDatabase.LoadAssetAtPath<Texture2D>(pngPath);
            data.localMin = new Vector2(originX, originZ);
            data.size = new Vector2(w * mpp, h * mpp);   // 對齊像素（含 Padding）
            data.metersPerPixel = mpp;
            EditorUtility.SetDirty(data);
            AssetDatabase.SaveAssets();

            string skip = skipped > 0 ? $"，{skipped} 個網格讀不到（見上方警告）" : "";
            Debug.Log($"ShipMinimapBaker: 完成 {w}×{h}（{mpp:0.##} m/px），範圍 {maxX - minX:0} × {maxZ - minZ:0} m，" +
                      $"高度 {minH:0.#}～{maxH:0.#} m，{bandSpan + 1} 層，{parts.Count} 個網格{skip} → {pngPath}", data);
            EditorGUIUtility.PingObject(data);
        }
        finally
        {
            if (baked != null) DestroyImmediate(baked);
            EditorUtility.ClearProgressBar();
        }
    }

    // 三角形投影到像素格（座標已是像素單位），用像素中心取樣，每格保留最高的高度
    private static void RasterizeTriangle(float[] heights, int w, int h,
        float ax, float az, float ay, float bx, float bz, float by, float cx, float cz, float cy)
    {
        float area = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);

        // 幾乎垂直的面（牆）投影下來是一條線 → 只標頂點，避免除以 0
        if (Mathf.Abs(area) < 1e-6f)
        {
            Plot(heights, w, h, ax, az, ay);
            Plot(heights, w, h, bx, bz, by);
            Plot(heights, w, h, cx, cz, cy);
            return;
        }

        int x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx))));
        int x1 = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))));
        int z0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(az, Mathf.Min(bz, cz))));
        int z1 = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(az, Mathf.Max(bz, cz))));

        // 比一個像素還小的三角形，像素中心可能剛好都不在裡面 → 至少標一下頂點
        if (x1 - x0 <= 1 && z1 - z0 <= 1)
        {
            Plot(heights, w, h, ax, az, ay);
            Plot(heights, w, h, bx, bz, by);
            Plot(heights, w, h, cx, cz, cy);
        }

        float invArea = 1f / area;
        const float eps = -1e-4f;

        for (int z = z0; z <= z1; z++)
        {
            float pz = z + 0.5f;
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f;
                float w0 = ((bx - px) * (cz - pz) - (bz - pz) * (cx - px)) * invArea;
                float w1 = ((cx - px) * (az - pz) - (cz - pz) * (ax - px)) * invArea;
                float w2 = 1f - w0 - w1;
                if (w0 < eps || w1 < eps || w2 < eps) continue;

                float y = w0 * ay + w1 * by + w2 * cy;
                int i = z * w + x;
                if (y > heights[i]) heights[i] = y;
            }
        }
    }

    private static void Plot(float[] heights, int w, int h, float x, float z, float y)
    {
        int ix = Mathf.FloorToInt(x), iz = Mathf.FloorToInt(z);
        if (ix < 0 || iz < 0 || ix >= w || iz >= h) return;
        int i = iz * w + ix;
        if (y > heights[i]) heights[i] = y;
    }

    private static bool LowerNeighbor(int[] bands, int w, int h, int x, int z, int b)
    {
        if (x < 0 || z < 0 || x >= w || z >= h) return false;
        int nb = bands[z * w + x];
        return nb != int.MinValue && nb < b;
    }

    // 實心像素裡，離空像素（或貼圖邊）在 width 格以內的 → true。用兩趟 Chebyshev 距離轉換，O(像素數)。
    private static bool[] BuildOutlineMask(int[] bands, int w, int h, int width)
    {
        var mask = new bool[w * h];
        if (width <= 0) return mask;

        const int far = 1 << 20;
        var dist = new int[w * h];
        for (int i = 0; i < dist.Length; i++) dist[i] = bands[i] == int.MinValue ? 0 : far;

        for (int z = 0; z < h; z++)
            for (int x = 0; x < w; x++)
            {
                int i = z * w + x;
                if (dist[i] == 0) continue;
                int d = far;
                if (x > 0) d = Mathf.Min(d, dist[i - 1] + 1); else d = 1;
                if (z > 0) d = Mathf.Min(d, dist[i - w] + 1); else d = 1;
                if (x > 0 && z > 0) d = Mathf.Min(d, dist[i - w - 1] + 1);
                if (x < w - 1 && z > 0) d = Mathf.Min(d, dist[i - w + 1] + 1);
                dist[i] = Mathf.Min(dist[i], d);
            }
        for (int z = h - 1; z >= 0; z--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = z * w + x;
                if (dist[i] == 0) continue;
                int d = dist[i];
                if (x < w - 1) d = Mathf.Min(d, dist[i + 1] + 1); else d = 1;
                if (z < h - 1) d = Mathf.Min(d, dist[i + w] + 1); else d = 1;
                if (x < w - 1 && z < h - 1) d = Mathf.Min(d, dist[i + w + 1] + 1);
                if (x > 0 && z < h - 1) d = Mathf.Min(d, dist[i + w - 1] + 1);
                dist[i] = Mathf.Min(dist[i], d);
            }

        for (int i = 0; i < mask.Length; i++) mask[i] = dist[i] > 0 && dist[i] <= width;
        return mask;
    }

    private static LayerMask LayerMaskField(GUIContent label, LayerMask mask)
    {
        var names = new List<string>();
        var numbers = new List<int>();
        for (int i = 0; i < 32; i++)
        {
            string n = LayerMask.LayerToName(i);
            if (string.IsNullOrEmpty(n)) continue;
            names.Add(n);
            numbers.Add(i);
        }

        int compact = 0;
        for (int i = 0; i < numbers.Count; i++)
            if ((mask.value & (1 << numbers[i])) != 0) compact |= 1 << i;

        compact = EditorGUILayout.MaskField(label, compact, names.ToArray());

        int result = 0;
        for (int i = 0; i < numbers.Count; i++)
            if ((compact & (1 << i)) != 0) result |= 1 << numbers[i];
        // 「Everything」：保留沒有名字的 Layer 也一起勾
        if (compact == -1) result = ~0;
        mask.value = result;
        return mask;
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
