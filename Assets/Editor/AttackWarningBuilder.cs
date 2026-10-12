#if UNITY_EDITOR
using System.IO;
using MoreMountains.Feedbacks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 建立「攻擊預警」特效：敵人出招前，頭部閃出一道十字星芒，讓玩家一眼看出「牠要攻擊了」。
///
/// 組成（全部是加法混合的發光粒子，HDR 強度 > 1 會吃到 Bloom）：
///   Glint  十字星芒（主體）：瞬間放大再收起，帶一點旋轉
///   Core   中心光團：比星芒稍長，讓閃光有「餘韻」
///   Ring   擴散光環：從中心往外擴散後淡出
///   Sparks 火花：少量往外噴射的細長光點
/// 粒子使用 Local 空間，會跟著頭一起移動。
///
/// 開啟：Tools > Diva Ex Machina > Attack Warning Builder
///
/// 貼圖由這個工具自己產生（{Output Folder}/Textures/AW_*.png），不需要外部素材。
/// 重新建置會覆寫貼圖、材質與 prefab，所以可以調整數值再按一次 Build。
///
/// 「Attach to selected enemy」：選取敵人（或在 Prefab 模式裡選取 prefab 根物件）後建置，會
///   1) 在頭骨頭底下放一個特效實例
///   2) 建立（或更新）一個帶 MMF_Player 的「Attack Warning Feedback」物件，用 MMF_Particles 驅動特效
///   3) 把這個 MMF_Player 指定到 ModularEntityEffectManager.attackWarningFeedback
/// </summary>
public class AttackWarningBuilder : EditorWindow
{
    private string outputFolder = "Assets/VFX/AttackWarning";
    private string prefabName = "AttackWarning";

    private Color color = new Color(1f, 0.25f, 0.1f, 1f);
    private float glowIntensity = 6f;
    private float effectScale = 1f;
    private float duration = 0.5f;

    private bool attachToSelected = true;
    private string headBoneName = "Head";
    private float forwardOffset = 0.5f;
    private float upOffset = 0.3f;

    private const string FeedbackObjectName = "Attack Warning Feedback";

    [MenuItem("Tools/Diva Ex Machina/Attack Warning Builder")]
    private static void Open()
    {
        GetWindow<AttackWarningBuilder>("Attack Warning");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        prefabName = EditorGUILayout.TextField("Prefab Name", prefabName);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Look", EditorStyles.boldLabel);
        color = EditorGUILayout.ColorField(new GUIContent("Color", "主色。紅、橘色最容易被讀成「危險」。中心會自動偏白。"), color);
        glowIntensity = EditorGUILayout.Slider(new GUIContent("Glow Intensity",
            "HDR 發光強度。越高 Bloom 越明顯；場景很亮時可以調高。"), glowIntensity, 1f, 20f);
        effectScale = EditorGUILayout.Slider(new GUIContent("Effect Scale",
            "整體大小。1 = 適合約 2 公尺高的敵人。遠距離也要看得到的話可以放大。"), effectScale, 0.25f, 5f);
        duration = EditorGUILayout.Slider(new GUIContent("Duration",
            "閃光持續時間（秒）。建議略短於攻擊的預備時間。"), duration, 0.2f, 1.5f);

        EditorGUILayout.Space();
        attachToSelected = EditorGUILayout.ToggleLeft(
            "Attach to selected enemy（放到頭上，並用 MMF_Player 接到 ModularEntityEffectManager）", attachToSelected);
        using (new EditorGUI.DisabledScope(!attachToSelected))
        {
            headBoneName = EditorGUILayout.TextField(new GUIContent("Head Bone Name", "特效要掛在哪根骨頭底下。"), headBoneName);
            forwardOffset = EditorGUILayout.FloatField(new GUIContent("Forward Offset", "往身體正前方偏移（公尺），讓光點出現在臉前面。"), forwardOffset);
            upOffset = EditorGUILayout.FloatField(new GUIContent("Up Offset", "往上偏移（公尺）。"), upOffset);
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Build", GUILayout.Height(32)))
            Build();

        EditorGUILayout.HelpBox(
            "播放：ModularEntityEffectManager.PlayAttackWarning()。\n" +
            "近戰出招時會自動播放（每招的 Show Warning 可以關掉）；\n" +
            "也可以在攻擊動畫加 Animation Event「MeleeWarning」，在指定的幀播放。",
            MessageType.Info);
    }

    // =====================================================================
    // Build
    // =====================================================================

    private void Build()
    {
        EnsureFolder(outputFolder);
        EnsureFolder($"{outputFolder}/Textures");
        EnsureFolder($"{outputFolder}/Materials");

        // ---- 貼圖（程式產生） ----
        Texture2D texGlow = WriteTexture("AW_Glow.png", 128, (u, v) =>
        {
            float r = Mathf.Sqrt(u * u + v * v);
            return Mathf.Pow(Mathf.Clamp01(1f - r), 2.2f);
        });
        Texture2D texStar = WriteTexture("AW_Star.png", 256, (u, v) =>
        {
            float r = Mathf.Sqrt(u * u + v * v);
            float h = Mathf.Exp(-(v * v) / (2f * 0.018f * 0.018f)) * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(u)), 1.6f);
            float w = Mathf.Exp(-(u * u) / (2f * 0.018f * 0.018f)) * Mathf.Pow(Mathf.Clamp01(1f - Mathf.Abs(v)), 1.6f);
            float d1 = (u - v) * 0.7071f, d2 = (u + v) * 0.7071f;   // 細一點、短一點的斜線
            float diag = 0.45f * (Mathf.Exp(-(d1 * d1) / (2f * 0.012f * 0.012f)) + Mathf.Exp(-(d2 * d2) / (2f * 0.012f * 0.012f)))
                         * Mathf.Pow(Mathf.Clamp01(1f - r * 1.6f), 2f);
            float core = Mathf.Exp(-(r * r) / (2f * 0.08f * 0.08f));
            return Mathf.Max(Mathf.Max(h, w), Mathf.Max(diag, core));
        });
        Texture2D texRing = WriteTexture("AW_Ring.png", 256, (u, v) =>
        {
            float r = Mathf.Sqrt(u * u + v * v);
            float d = r - 0.82f;
            return r > 1f ? 0f : Mathf.Exp(-(d * d) / (2f * 0.045f * 0.045f));
        });

        // ---- 材質（加法混合，HDR 強度吃 Bloom） ----
        Material matStar = MakeAdditiveMaterial("AW_Star", texStar, glowIntensity);
        Material matCore = MakeAdditiveMaterial("AW_Core", texGlow, glowIntensity * 0.7f);
        Material matRing = MakeAdditiveMaterial("AW_Ring", texRing, glowIntensity * 0.45f);
        Material matSpark = MakeAdditiveMaterial("AW_Spark", texGlow, glowIntensity * 0.6f);

        float S = effectScale;
        float D = duration;
        Color hot = Color.Lerp(color, Color.white, 0.55f);   // 中心偏白，邊緣是主色

        // ---- Prefab ----
        var root = new GameObject(prefabName);
        var rootPs = root.AddComponent<ParticleSystem>();
        {
            // 根粒子系統不發射，只當「一起播放」的開關（MMF_Particles 播放它時會連子物件一起播）
            var main = rootPs.main;
            main.loop = false;
            main.duration = D;
            main.playOnAwake = false;
            main.startLifetime = 0.1f;
            main.maxParticles = 1;
            var e = rootPs.emission;
            e.enabled = false;
            var sh = rootPs.shape;
            sh.enabled = false;
            root.GetComponent<ParticleSystemRenderer>().enabled = false;
        }

        // Glint：十字星芒
        ParticleSystem glint = CreateLayer("Glint", root.transform, matStar, D);
        {
            var main = glint.main;
            main.startLifetime = D;
            main.startSize = 2.4f * S;
            main.startColor = hot;
            main.startRotation = 0f;
            Burst(glint, 1);
            SetSizeOverLife(glint, (0f, 0.05f), (0.12f, 1f), (0.35f, 0.75f), (1f, 0f));
            SetAlpha(glint, (1f, 0f), (1f, 0.6f), (0f, 1f));
            var rot = glint.rotationOverLifetime;
            rot.enabled = true;
            rot.z = 45f * Mathf.Deg2Rad;                       // 慢慢轉 45°，看起來在閃
        }

        // Core：中心光團
        ParticleSystem core = CreateLayer("Core", root.transform, matCore, D);
        {
            var main = core.main;
            main.startLifetime = D * 1.15f;
            main.startSize = 1.1f * S;
            main.startColor = Color.Lerp(color, Color.white, 0.25f);
            Burst(core, 1);
            SetSizeOverLife(core, (0f, 0.2f), (0.15f, 1f), (1f, 0.4f));
            SetAlpha(core, (1f, 0f), (0.8f, 0.5f), (0f, 1f));
        }

        // Ring：擴散光環
        ParticleSystem ring = CreateLayer("Ring", root.transform, matRing, D);
        {
            var main = ring.main;
            main.startLifetime = D * 0.7f;
            main.startSize = 2.6f * S;
            main.startColor = color;
            Burst(ring, 1);
            SetSizeOverLife(ring, (0f, 0.15f), (1f, 1f));
            SetAlpha(ring, (1f, 0f), (0.7f, 0.4f), (0f, 1f));
        }

        // Sparks：火花
        ParticleSystem sparks = CreateLayer("Sparks", root.transform, matSpark, D);
        {
            var main = sparks.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(D * 0.35f, D * 0.6f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(3f * S, 6f * S);
            main.startSize = new ParticleSystem.MinMaxCurve(0.06f * S, 0.12f * S);
            main.startColor = hot;
            Burst(sparks, 10);
            var sh = sparks.shape;
            sh.enabled = true;
            sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = 0.05f * S;
            var limit = sparks.limitVelocityOverLifetime;
            limit.enabled = true;
            limit.limit = 0f;
            limit.dampen = 0.12f;                              // 噴出去之後很快慢下來
            SetAlpha(sparks, (1f, 0f), (1f, 0.5f), (0f, 1f));
            var r = sparks.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.06f;
            r.lengthScale = 1.5f;
        }

        string prefabPath = $"{outputFolder}/{prefabName}.prefab";
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
        DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        Debug.Log($"[AttackWarningBuilder] Saved {prefabPath}", prefab);

        if (attachToSelected)
            AttachToSelected(prefab);
        else
            Selection.activeObject = prefab;
    }

    // =====================================================================
    // 掛到敵人身上
    // =====================================================================

    private void AttachToSelected(GameObject prefab)
    {
        GameObject sel = Selection.activeGameObject;
        if (sel == null)
        {
            EditorUtility.DisplayDialog("Attack Warning Builder",
                "Prefab 已建立，但沒有選取敵人，所以沒有掛上去。\n選取敵人（或在 Prefab 模式選 prefab 根物件）後再按一次 Build。", "OK");
            Selection.activeObject = prefab;
            return;
        }

        Transform root = FindEntityRoot(sel.transform);
        var effects = root.GetComponentInChildren<ModularEntityEffectManager>(true);
        if (effects == null)
        {
            EditorUtility.DisplayDialog("Attack Warning Builder", $"在 {root.name} 底下找不到 ModularEntityEffectManager。", "OK");
            return;
        }

        Transform head = FindDeep(root, headBoneName);
        if (head == null)
        {
            EditorUtility.DisplayDialog("Attack Warning Builder", $"在 {root.name} 底下找不到名為 '{headBoneName}' 的骨頭。", "OK");
            return;
        }

        // 身體正前方：有 Animator 的物件（獵犬是 Wolf Mesh）的 forward
        Animator anim = root.GetComponentInChildren<Animator>(true);
        Vector3 bodyForward = anim != null ? anim.transform.forward : root.forward;
        bodyForward.y = 0f;
        bodyForward = bodyForward.sqrMagnitude > 0.0001f ? bodyForward.normalized : Vector3.forward;

        // 1) 頭上的特效實例（同名的舊實例先刪掉）
        Transform old = head.Find(prefab.name);
        if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

        var inst = (GameObject)PrefabUtility.InstantiatePrefab(prefab, head);
        Undo.RegisterCreatedObjectUndo(inst, "Attach Attack Warning");
        inst.transform.position = head.position + bodyForward * forwardOffset + Vector3.up * upOffset;
        inst.transform.rotation = Quaternion.identity;
        inst.transform.localScale = Vector3.one;

        // 2) MMF_Player（放在 EffectManager 所在物件底下，跟 Damage Feedback 同一層）
        Transform fbT = effects.transform.Find(FeedbackObjectName);
        GameObject fbGo;
        if (fbT == null)
        {
            fbGo = new GameObject(FeedbackObjectName);
            Undo.RegisterCreatedObjectUndo(fbGo, "Attach Attack Warning");
            fbGo.transform.SetParent(effects.transform, false);
        }
        else
        {
            fbGo = fbT.gameObject;
        }

        var player = fbGo.GetComponent<MMF_Player>();
        if (player == null) player = Undo.AddComponent<MMF_Player>(fbGo);
        Undo.RecordObject(player, "Attach Attack Warning");

        // 只保留一個 MMF_Particles（重建時換成新的特效）
        if (player.FeedbacksList != null)
            player.FeedbacksList.RemoveAll(f => f is MMF_Particles);
        var particles = (MMF_Particles)player.AddFeedback(typeof(MMF_Particles));
        particles.Label = "Attack Warning Particles";
        particles.Mode = MMF_Particles.Modes.Play;
        particles.BoundParticleSystem = inst.GetComponent<ParticleSystem>();
        particles.StopSystemOnInit = true;
        EditorUtility.SetDirty(player);

        // 3) 指定到 EffectManager
        Undo.RecordObject(effects, "Attach Attack Warning");
        effects.attackWarningFeedback = player;
        EditorUtility.SetDirty(effects);

        if (PrefabUtility.IsPartOfPrefabInstance(effects))
            PrefabUtility.RecordPrefabInstancePropertyModifications(effects);

        var stage = PrefabStageUtility.GetCurrentPrefabStage();
        if (stage != null) EditorSceneManager.MarkSceneDirty(stage.scene);
        else EditorSceneManager.MarkSceneDirty(root.gameObject.scene);

        Selection.activeGameObject = inst;
        Debug.Log($"[AttackWarningBuilder] 已掛到 {root.name} 的 {head.name}，並指定給 {effects.name}.attackWarningFeedback", inst);
    }

    private static Transform FindEntityRoot(Transform t)
    {
        var brain = t.GetComponentInParent<ModularEntityBrain>();
        if (brain != null) return brain.transform;
        var effects = t.GetComponentInParent<ModularEntityEffectManager>();
        if (effects != null) return effects.transform;
        return t.root;
    }

    private static Transform FindDeep(Transform parent, string name)
    {
        foreach (Transform t in parent.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    // =====================================================================
    // 粒子工具
    // =====================================================================

    private static ParticleSystem CreateLayer(string name, Transform parent, Material material, float duration)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = false;
        main.duration = duration;
        main.playOnAwake = false;
        main.prewarm = false;
        main.gravityModifier = 0f;
        main.startSpeed = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;   // 跟著頭走
        main.scalingMode = ParticleSystemScalingMode.Local;      // 不受骨頭（父物件）縮放影響，大小只看 Effect Scale
        main.maxParticles = 32;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;

        var e = ps.emission;
        e.enabled = true;
        e.rateOverTime = 0f;
        e.rateOverDistance = 0f;

        var sh = ps.shape;
        sh.enabled = false;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = material;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortMode = ParticleSystemSortMode.None;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;
        r.minParticleSize = 0f;
        r.maxParticleSize = 5f;                          // 預設 0.5（螢幕比例）會把近距離的星芒截小

        return ps;
    }

    private static void Burst(ParticleSystem ps, int count)
    {
        var e = ps.emission;
        e.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)count) });
    }

    private static void SetSizeOverLife(ParticleSystem ps, params (float time, float value)[] keys)
    {
        var curve = new AnimationCurve();
        foreach (var k in keys) curve.AddKey(new Keyframe(k.time, k.value));
        for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);
        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, curve);
    }

    private static void SetAlpha(ParticleSystem ps, params (float alpha, float time)[] keys)
    {
        var g = new Gradient();
        var a = new GradientAlphaKey[keys.Length];
        for (int i = 0; i < keys.Length; i++) a[i] = new GradientAlphaKey(keys[i].alpha, keys[i].time);
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, a);
        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = g;
    }

    // =====================================================================
    // 貼圖與材質
    // =====================================================================

    /// <summary>產生白色、以 alpha 表示形狀的貼圖（座標 u, v ∈ [-1, 1]）。</summary>
    private Texture2D WriteTexture(string fileName, int size, System.Func<float, float, float> alpha)
    {
        string path = $"{outputFolder}/Textures/{fileName}";
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var px = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            float v = (y + 0.5f) / size * 2f - 1f;
            for (int x = 0; x < size; x++)
            {
                float u = (x + 0.5f) / size * 2f - 1f;
                byte a = (byte)Mathf.RoundToInt(Mathf.Clamp01(alpha(u, v)) * 255f);
                px[y * size + x] = new Color32(255, 255, 255, a);
            }
        }
        tex.SetPixels32(px);
        tex.Apply();
        File.WriteAllBytes(path, tex.EncodeToPNG());
        DestroyImmediate(tex);

        AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceUpdate);
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.sRGBTexture = true;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>URP Particles/Unlit 加法混合。intensity > 1 會吃到 Bloom。</summary>
    private Material MakeAdditiveMaterial(string name, Texture texture, float intensity)
    {
        string path = $"{outputFolder}/Materials/{name}.mat";

        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
        {
            Debug.LogWarning("AttackWarningBuilder: URP Particles/Unlit not found, falling back to built-in particles.");
            shader = Shader.Find("Legacy Shaders/Particles/Additive");
        }

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        else
        {
            mat.shader = shader;
        }

        SetTextureIfExists(mat, "_BaseMap", texture);
        SetTextureIfExists(mat, "_MainTex", texture);

        // SetVector（不是 SetColor）：HDR 強度不會被再做一次 Gamma 轉換
        if (mat.HasProperty("_BaseColor")) mat.SetVector("_BaseColor", new Vector4(intensity, intensity, intensity, 1f));

        SetFloatIfExists(mat, "_Surface", 1f);
        SetFloatIfExists(mat, "_Blend", 2f);
        SetFloatIfExists(mat, "_SrcBlend", (float)BlendMode.SrcAlpha);
        SetFloatIfExists(mat, "_DstBlend", (float)BlendMode.One);
        SetFloatIfExists(mat, "_SrcBlendAlpha", (float)BlendMode.One);
        SetFloatIfExists(mat, "_DstBlendAlpha", (float)BlendMode.One);
        SetFloatIfExists(mat, "_ZWrite", 0f);
        SetFloatIfExists(mat, "_Cull", (float)CullMode.Off);
        SetFloatIfExists(mat, "_ColorMode", 0f);
        SetFloatIfExists(mat, "_SoftParticlesEnabled", 0f);
        mat.DisableKeyword("_SOFTPARTICLES_ON");

        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.DisableKeyword("_ALPHAMODULATE_ON");
        mat.renderQueue = (int)RenderQueue.Transparent + 10;   // 畫在一般半透明（煙、塵）之後
        mat.SetShaderPassEnabled("ShadowCaster", false);
        mat.SetShaderPassEnabled("DepthOnly", false);

        EditorUtility.SetDirty(mat);
        return mat;
    }

    private static void SetTextureIfExists(Material m, string prop, Texture t)
    {
        if (m.HasProperty(prop)) m.SetTexture(prop, t);
    }

    private static void SetFloatIfExists(Material m, string prop, float v)
    {
        if (m.HasProperty(prop)) m.SetFloat(prop, v);
    }

    private static void EnsureFolder(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(path)) return;
        string parent = Path.GetDirectoryName(path).Replace('\\', '/');
        EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
    }
}
#endif
