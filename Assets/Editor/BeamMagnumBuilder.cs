#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 建立「光束麥格農」式的蓄力特效與開火特效（跟 Explosion / Thruster Flame Builder 同一套做法）。
///
/// 開啟：Tools > Diva Ex Machina > Beam Magnum Builder
///
/// 產生兩個 prefab（{Output Folder}/）：
///   · BeamMagnum_Charge  蓄力：藍色光暈 + 白色光球 + 旋轉的紫色漩渦 + 後半段出現的虛線光環
///                         + 越來越密的電弧 + 往中心吸入的光點 + 跟著光球變亮的點光源。
///                         整個效果在 Charge Time 秒內從小長到滿（光球變大、電弧變密）。
///   · BeamMagnum_Fire    開火：白色閃光 + 往前擴散的粉紅衝擊環 + 往前噴的火花 + 電弧。
///
/// 用法（恐狼）：
///   1. 把兩個 prefab 拖到砲口（Muzzle）底下，位置歸零（光球中心想往前一點就調 Z）。
///   2. TurretWeapon：Charge Vfx 加入 BeamMagnum_Charge 根物件，Fire Vfx 拖入 BeamMagnum_Fire 根物件。
///   3. Charge Time 要跟 StationaryChargeAttack 的 Charge Time 一樣（預設 15），不一樣就重建。
///
/// 需要 {Output Folder}/Textures 裡的 BM_*.png。材質建在 {Output Folder}/Materials（BM_ 開頭）。
/// 重新建置會覆寫 prefab 與材質。
/// </summary>
public class BeamMagnumBuilder : EditorWindow
{
    private string outputFolder = "Assets/VFX/BeamMagnum";
    private float chargeTime = 15f;
    private float orbSize = 1.6f;   // 光球（白色核心）滿蓄力時的直徑（公尺）

    private Color glowColor = new Color(0.25f, 0.45f, 1f, 0.9f);
    private Color coreColor = new Color(0.95f, 0.93f, 1f, 1f);
    private Color swirlColor = new Color(0.5f, 0.33f, 1f, 0.9f);
    private Color ringColor = new Color(0.4f, 0.8f, 1f, 1f);
    private Color arcColor = new Color(0.35f, 0.6f, 1f, 1f);
    private Color shockColor = new Color(1f, 0.3f, 0.45f, 1f);

    private bool includeLight = true;

    [MenuItem("Tools/Diva Ex Machina/Beam Magnum Builder")]
    private static void Open() => GetWindow<BeamMagnumBuilder>("Beam Magnum");

    private void OnGUI()
    {
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        chargeTime = Mathf.Max(0.5f, EditorGUILayout.FloatField(new GUIContent("Charge Time",
            "蓄力秒數。要跟 StationaryChargeAttack 的 Charge Time 一樣。"), chargeTime));
        orbSize = Mathf.Max(0.1f, EditorGUILayout.FloatField(new GUIContent("Orb Size",
            "滿蓄力時白色光球的直徑（公尺）。其他圖層依比例縮放。"), orbSize));

        EditorGUILayout.Space();
        glowColor = EditorGUILayout.ColorField("Glow", glowColor);
        coreColor = EditorGUILayout.ColorField("Core", coreColor);
        swirlColor = EditorGUILayout.ColorField("Swirl", swirlColor);
        ringColor = EditorGUILayout.ColorField("Ring", ringColor);
        arcColor = EditorGUILayout.ColorField("Arc", arcColor);
        shockColor = EditorGUILayout.ColorField("Fire Shockwave", shockColor);
        includeLight = EditorGUILayout.Toggle("Point Light", includeLight);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build", GUILayout.Height(30))) Build();
    }

    // =====================================================================
    // Build
    // =====================================================================

    private void Build()
    {
        AssetDatabase.Refresh();
        EnsureFolder(outputFolder);
        EnsureFolder($"{outputFolder}/Materials");

        Texture2D texGlow = LoadTexture("BM_Glow.png");
        Texture2D texCore = LoadTexture("BM_Core.png");
        Texture2D texSwirl = LoadTexture("BM_Swirl.png");
        Texture2D texRing = LoadTexture("BM_Ring.png");
        Texture2D texArc = LoadTexture("BM_ArcSheet.png");
        Texture2D texSpark = LoadTexture("BM_Spark.png");
        Texture2D texShock = LoadTexture("BM_Shock.png");
        if (!texGlow || !texCore || !texSwirl || !texRing || !texArc || !texSpark || !texShock)
        {
            EditorUtility.DisplayDialog("Beam Magnum Builder",
                $"Missing textures in {outputFolder}/Textures. See the Console.", "OK");
            return;
        }

        var mGlow = MakeMaterial("BM_Glow", texGlow, true, 2f);
        var mCore = MakeMaterial("BM_Core", texCore, true, 3f);
        var mSwirl = MakeMaterial("BM_Swirl", texSwirl, false, 1f);
        var mRing = MakeMaterial("BM_Ring", texRing, true, 2.5f);
        var mArc = MakeMaterial("BM_Arc", texArc, true, 4f);
        var mSpark = MakeMaterial("BM_Spark", texSpark, true, 3f);
        var mShock = MakeMaterial("BM_Shock", texShock, true, 3f);
        var mFlash = MakeMaterial("BM_Flash", texGlow, true, 8f);

        GameObject charge = BuildCharge(mGlow, mCore, mSwirl, mRing, mArc, mSpark);
        GameObject fire = BuildFire(mFlash, mShock, mSpark, mArc);

        Save(charge, "BeamMagnum_Charge");
        Save(fire, "BeamMagnum_Fire");
        AssetDatabase.SaveAssets();
        Debug.Log($"BeamMagnumBuilder: built {outputFolder}/BeamMagnum_Charge.prefab and BeamMagnum_Fire.prefab (charge {chargeTime:0.#}s)");
    }

    private void Save(GameObject root, string name)
    {
        string path = $"{outputFolder}/{name}.prefab";
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path, out bool ok);
        DestroyImmediate(root);
        if (!ok || prefab == null) { Debug.LogError($"BeamMagnumBuilder: failed to save {path}"); return; }
        EditorGUIUtility.PingObject(prefab);
    }

    // ---------------------------------------------------------------------
    // Charge
    // ---------------------------------------------------------------------

    private GameObject BuildCharge(Material mGlow, Material mCore, Material mSwirl, Material mRing, Material mArc, Material mSpark)
    {
        float s = orbSize;
        float duration = chargeTime + 1f;        // 系統時間軸（射速、尺寸曲線都依這個走）
        float life = duration + 1f;              // 單顆圖層（光暈、光球、漩渦）的壽命，蓋過整個蓄力
        float fullAt = Mathf.Clamp01(chargeTime * 0.9f / life);   // 長到滿的時間點（壽命比例）

        AnimationCurve grow = new AnimationCurve(
            new Keyframe(0f, 0.12f), new Keyframe(fullAt * 0.5f, 0.55f), new Keyframe(fullAt, 1f), new Keyframe(1f, 1f));

        // 1. 光暈（根物件）
        var root = new GameObject("BeamMagnum_Charge");
        ParticleSystem glow = AddSystem(root, mGlow, 0);
        SetupSingle(glow, duration, life, s * 2.3f, glowColor);
        SizeOverLife(glow, grow);
        // 微微呼吸
        var glowNoise = glow.noise; glowNoise.enabled = true; glowNoise.strength = 0f;
        glowNoise.sizeAmount = 0.12f; glowNoise.frequency = 0.8f; glowNoise.scrollSpeed = 1.5f;

        // 2. 白色光球
        ParticleSystem core = AddSystem(Child(root, "Core"), mCore, 1);
        SetupSingle(core, duration, life, s, coreColor);
        SizeOverLife(core, grow);

        // 3. 紫色漩渦（Alpha 混合，疊在白球上才看得到紫色）
        ParticleSystem swirl = AddSystem(Child(root, "Swirl"), mSwirl, 2);
        SetupSingle(swirl, duration, life, s * 0.95f, swirlColor);
        SizeOverLife(swirl, grow);
        var swirlMain = swirl.main;
        swirlMain.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        var swirlRot = swirl.rotationOverLifetime; swirlRot.enabled = true;
        swirlRot.z = new ParticleSystem.MinMaxCurve(-3.5f);   // 弧度/秒（約 200°/s，順時針）

        // 4. 虛線光環：蓄力到 40% 才出現
        ParticleSystem ring = AddSystem(Child(root, "Ring"), mRing, 3);
        float ringDelay = chargeTime * 0.4f;
        SetupSingle(ring, duration, life - ringDelay, s * 1.5f, ringColor);
        var ringMain = ring.main; ringMain.startDelay = ringDelay;
        var ringRot = ring.rotationOverLifetime; ringRot.enabled = true; ringRot.z = new ParticleSystem.MinMaxCurve(0.6f);
        ColorFadeIn(ring, 0.08f);
        SizeOverLife(ring, new AnimationCurve(new Keyframe(0f, 1.4f), new Keyframe(0.06f, 1f), new Keyframe(1f, 1f)));

        // 5. 電弧：越來越密、越來越大
        ParticleSystem arcs = AddSystem(Child(root, "Arcs"), mArc, 4);
        {
            var m = arcs.main;
            m.duration = duration; m.loop = false; m.playOnAwake = false;
            m.simulationSpace = ParticleSystemSimulationSpace.Local;
            m.scalingMode = ParticleSystemScalingMode.Hierarchy;
            m.startLifetime = new ParticleSystem.MinMaxCurve(0.06f, 0.14f);
            m.startSpeed = 0f;
            m.startSize = new ParticleSystem.MinMaxCurve(s * 1.2f,
                new AnimationCurve(new Keyframe(0f, 0.3f), new Keyframe(1f, 1f)),
                new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(1f, 1.6f)));
            m.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            m.startColor = arcColor;
            m.maxParticles = 64;

            var e = arcs.emission; e.enabled = true;
            e.rateOverTime = new ParticleSystem.MinMaxCurve(30f, new AnimationCurve(
                new Keyframe(0f, 0.07f), new Keyframe(0.6f, 0.35f), new Keyframe(1f, 1f)));

            var sh = arcs.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = s * 0.45f; sh.radiusThickness = 0f;

            var tsa = arcs.textureSheetAnimation; tsa.enabled = true;
            tsa.numTilesX = 4; tsa.numTilesY = 4;
            tsa.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
            tsa.startFrame = new ParticleSystem.MinMaxCurve(0f, 15f);
            tsa.cycleCount = 1;
        }

        // 6. 往中心吸入的光點
        ParticleSystem inflow = AddSystem(Child(root, "Inflow"), mSpark, 4);
        {
            float radius = s * 2.2f, lt = 0.45f;
            var m = inflow.main;
            m.duration = duration; m.loop = false; m.playOnAwake = false;
            m.simulationSpace = ParticleSystemSimulationSpace.Local;
            m.scalingMode = ParticleSystemScalingMode.Hierarchy;
            m.startLifetime = lt;
            m.startSpeed = 0f;
            m.startSize = new ParticleSystem.MinMaxCurve(s * 0.06f, s * 0.12f);
            m.startColor = Color.Lerp(arcColor, Color.white, 0.5f);
            m.maxParticles = 200;

            var e = inflow.emission; e.enabled = true;
            e.rateOverTime = new ParticleSystem.MinMaxCurve(80f, new AnimationCurve(
                new Keyframe(0f, 0.08f), new Keyframe(1f, 1f)));

            var sh = inflow.shape; sh.enabled = true; sh.shapeType = ParticleSystemShapeType.Sphere;
            sh.radius = radius; sh.radiusThickness = 0f;

            var v = inflow.velocityOverLifetime; v.enabled = true;
            v.space = ParticleSystemSimulationSpace.Local;
            v.radial = new ParticleSystem.MinMaxCurve(-(radius - s * 0.4f) / lt);

            ColorFadeIn(inflow, 0.3f);

            var r = inflow.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = 0.06f; r.lengthScale = 1f;
        }

        // 7. 點光源：跟著光暈粒子（顏色、大小）
        if (includeLight)
        {
            var lightGo = Child(root, "LightTemplate");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 1f; light.intensity = 1f; light.shadows = LightShadows.None;
            lightGo.SetActive(false);   // 只當模板，由 Lights module 生成

            var lm = glow.lights; lm.enabled = true;
            lm.light = light; lm.ratio = 1f; lm.maxLights = 1;
            lm.useParticleColor = true; lm.sizeAffectsRange = true; lm.alphaAffectsIntensity = true;
            lm.rangeMultiplier = 3f; lm.intensityMultiplier = 6f;
        }

        return root;
    }

    // ---------------------------------------------------------------------
    // Fire
    // ---------------------------------------------------------------------

    private GameObject BuildFire(Material mFlash, Material mShock, Material mSpark, Material mArc)
    {
        float s = orbSize;
        var root = new GameObject("BeamMagnum_Fire");

        // 1. 白色閃光（根物件）
        ParticleSystem flash = AddSystem(root, mFlash, 5);
        Burst(flash, 1, 0.3f, s * 3f, Color.white);
        SizeOverLife(flash, new AnimationCurve(new Keyframe(0f, 0.6f), new Keyframe(0.15f, 1.3f), new Keyframe(1f, 0.8f)));
        ColorFadeOut(flash, Color.white, Color.Lerp(coreColor, glowColor, 0.5f));

        // 2. 粉紅衝擊環（面向砲口前方，往外擴散）
        ParticleSystem shock = AddSystem(Child(root, "Shockwave"), mShock, 4);
        Burst(shock, 2, 0.4f, s * 1.2f, shockColor);
        var shockMain = shock.main;
        shockMain.startLifetime = new ParticleSystem.MinMaxCurve(0.3f, 0.45f);
        var sr = shock.GetComponent<ParticleSystemRenderer>();
        sr.alignment = ParticleSystemRenderSpace.Local;   // 平貼在砲口的 XY 平面 → 環面朝前
        SizeOverLife(shock, new AnimationCurve(new Keyframe(0f, 0.4f), new Keyframe(1f, 4.5f)));
        ColorFadeOut(shock, shockColor, shockColor);
        var shockVel = shock.velocityOverLifetime; shockVel.enabled = true;
        shockVel.space = ParticleSystemSimulationSpace.Local;
        // x / y / z 必須是同一種模式（這裡都是 Random Between Two Constants），否則 Unity 會一直報錯
        shockVel.x = new ParticleSystem.MinMaxCurve(0f, 0f);
        shockVel.y = new ParticleSystem.MinMaxCurve(0f, 0f);
        shockVel.z = new ParticleSystem.MinMaxCurve(s * 6f, s * 12f);   // 往前推出去

        // 3. 往前噴的火花
        ParticleSystem sparks = AddSystem(Child(root, "Sparks"), mSpark, 4);
        Burst(sparks, 40, 0.4f, s * 0.1f, Color.Lerp(arcColor, Color.white, 0.5f));
        var spMain = sparks.main;
        spMain.startLifetime = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
        spMain.startSpeed = new ParticleSystem.MinMaxCurve(s * 15f, s * 35f);
        spMain.startSize = new ParticleSystem.MinMaxCurve(s * 0.05f, s * 0.12f);
        var spShape = sparks.shape; spShape.enabled = true; spShape.shapeType = ParticleSystemShapeType.Cone;
        spShape.angle = 30f; spShape.radius = s * 0.3f;
        var spLimit = sparks.limitVelocityOverLifetime; spLimit.enabled = true; spLimit.drag = 4f;
        ColorFadeOut(sparks, Color.white, arcColor);
        var spr = sparks.GetComponent<ParticleSystemRenderer>();
        spr.renderMode = ParticleSystemRenderMode.Stretch; spr.velocityScale = 0.04f; spr.lengthScale = 1.5f;

        // 4. 電弧
        ParticleSystem arcs = AddSystem(Child(root, "Arcs"), mArc, 5);
        Burst(arcs, 10, 0.15f, s * 2f, arcColor);
        var aMain = arcs.main;
        aMain.startLifetime = new ParticleSystem.MinMaxCurve(0.08f, 0.2f);
        aMain.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
        aMain.startSize = new ParticleSystem.MinMaxCurve(s * 1.2f, s * 2.5f);
        var aShape = arcs.shape; aShape.enabled = true; aShape.shapeType = ParticleSystemShapeType.Sphere;
        aShape.radius = s * 0.8f; aShape.radiusThickness = 0f;
        var tsa = arcs.textureSheetAnimation; tsa.enabled = true;
        tsa.numTilesX = 4; tsa.numTilesY = 4;
        tsa.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
        tsa.startFrame = new ParticleSystem.MinMaxCurve(0f, 15f);

        return root;
    }

    // =====================================================================
    // Particle helpers
    // =====================================================================

    private static GameObject Child(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    private static ParticleSystem AddSystem(GameObject go, Material mat, int sortingOrder)
    {
        var ps = go.AddComponent<ParticleSystem>();
        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = mat;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortingOrder = sortingOrder;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.minParticleSize = 0f;
        r.maxParticleSize = 10f;   // 靠近鏡頭時不要被螢幕比例上限切掉

        var e = ps.emission; e.enabled = false;
        var sh = ps.shape; sh.enabled = false;
        var m = ps.main; m.playOnAwake = false;
        return ps;
    }

    /// <summary>整個蓄力期間只有一顆粒子的圖層（光暈、光球、漩渦、光環）。</summary>
    private static void SetupSingle(ParticleSystem ps, float duration, float life, float size, Color color)
    {
        var m = ps.main;
        m.duration = duration; m.loop = false; m.playOnAwake = false;
        m.simulationSpace = ParticleSystemSimulationSpace.Local;
        m.scalingMode = ParticleSystemScalingMode.Hierarchy;
        m.startLifetime = life;
        m.startSpeed = 0f;
        m.startSize = size;
        m.startColor = color;
        m.maxParticles = 1;

        var e = ps.emission; e.enabled = true;
        e.rateOverTime = 0f;
        e.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
    }

    private static void Burst(ParticleSystem ps, int count, float life, float size, Color color)
    {
        var m = ps.main;
        m.duration = 1f; m.loop = false; m.playOnAwake = false;
        m.simulationSpace = ParticleSystemSimulationSpace.World;   // 開火特效留在原地
        m.scalingMode = ParticleSystemScalingMode.Hierarchy;
        m.startLifetime = life;
        m.startSpeed = 0f;
        m.startSize = size;
        m.startColor = color;
        m.maxParticles = Mathf.Max(count, 1);

        var e = ps.emission; e.enabled = true;
        e.rateOverTime = 0f;
        e.SetBursts(new[] { new ParticleSystem.Burst(0f, count) });
    }

    private static void SizeOverLife(ParticleSystem ps, AnimationCurve curve)
    {
        var sol = ps.sizeOverLifetime; sol.enabled = true;
        float max = 0f;
        foreach (Keyframe k in curve.keys) max = Mathf.Max(max, k.value);
        if (max <= 0f) max = 1f;
        var norm = new AnimationCurve();
        foreach (Keyframe k in curve.keys) norm.AddKey(new Keyframe(k.time, k.value / max));
        sol.size = new ParticleSystem.MinMaxCurve(max, norm);
    }

    private static void ColorFadeIn(ParticleSystem ps, float fadeTime)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, Mathf.Clamp(fadeTime, 0.01f, 0.99f)), new GradientAlphaKey(1f, 1f) });
        col.color = g;
    }

    private static void ColorFadeOut(ParticleSystem ps, Color from, Color to)
    {
        var col = ps.colorOverLifetime; col.enabled = true;
        var g = new Gradient();
        g.SetKeys(
            new[] { new GradientColorKey(from, 0f), new GradientColorKey(to, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0.8f, 0.4f), new GradientAlphaKey(0f, 1f) });
        col.color = g;
    }

    // =====================================================================
    // Asset helpers（跟 ExplosionBuilder 相同）
    // =====================================================================

    private Texture2D LoadTexture(string fileName)
    {
        string path = $"{outputFolder}/Textures/{fileName}";
        var importer = AssetImporter.GetAtPath(path) as TextureImporter;
        if (importer == null)
        {
            Debug.LogError($"BeamMagnumBuilder: texture not found at {path}");
            return null;
        }

        bool dirty =
            importer.textureType != TextureImporterType.Default ||
            !importer.alphaIsTransparency ||
            importer.wrapMode != TextureWrapMode.Clamp ||
            !importer.mipmapEnabled ||
            !importer.sRGBTexture ||
            importer.alphaSource != TextureImporterAlphaSource.FromInput;

        if (dirty)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.mipmapEnabled = true;
            importer.sRGBTexture = true;
            importer.SaveAndReimport();
        }
        return AssetDatabase.LoadAssetAtPath<Texture2D>(path);
    }

    /// <summary>URP Particles/Unlit。additive = 發光（加法混合）；否則為一般半透明（Alpha 混合）。</summary>
    private Material MakeMaterial(string name, Texture texture, bool additive, float intensity)
    {
        string path = $"{outputFolder}/Materials/{name}.mat";

        var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
        if (shader == null)
            shader = Shader.Find(additive ? "Legacy Shaders/Particles/Additive" : "Legacy Shaders/Particles/Alpha Blended");

        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat == null)
        {
            mat = new Material(shader) { name = name };
            AssetDatabase.CreateAsset(mat, path);
        }
        else mat.shader = shader;

        SetTextureIfExists(mat, "_BaseMap", texture);
        SetTextureIfExists(mat, "_MainTex", texture);
        if (mat.HasProperty("_BaseColor")) mat.SetVector("_BaseColor", new Vector4(intensity, intensity, intensity, 1f));

        SetFloatIfExists(mat, "_Surface", 1f);
        SetFloatIfExists(mat, "_Blend", additive ? 2f : 0f);
        SetFloatIfExists(mat, "_SrcBlend", (float)BlendMode.SrcAlpha);
        SetFloatIfExists(mat, "_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
        SetFloatIfExists(mat, "_SrcBlendAlpha", (float)BlendMode.One);
        SetFloatIfExists(mat, "_DstBlendAlpha", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
        SetFloatIfExists(mat, "_ZWrite", 0f);
        SetFloatIfExists(mat, "_Cull", (float)CullMode.Off);
        SetFloatIfExists(mat, "_ColorMode", 0f);
        SetFloatIfExists(mat, "_SoftParticlesEnabled", 0f);
        mat.DisableKeyword("_SOFTPARTICLES_ON");

        mat.SetOverrideTag("RenderType", "Transparent");
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.DisableKeyword("_ALPHAMODULATE_ON");
        mat.renderQueue = (int)RenderQueue.Transparent;
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
        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}
#endif
