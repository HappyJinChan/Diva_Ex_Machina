#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Tools > Diva Ex Machina > Ground Wake Builder
///
/// 產生地面尾流特效 prefab（GroundWakeEffect），給 PlayerGroundWake 用。
/// 材質沿用 Jump Dust（Assets/VFX/JumpDust/Materials）和 Hit Spark（Assets/VFX/M_HitSpark.mat），
/// 所以要先跑過 Jump Dust Builder 和 Hit Spark Builder。
///
/// 重新建置會覆寫 prefab，可以改數值再按一次 Build。
/// 勾選「Attach to selected Player」時，會在選取的玩家上加 PlayerGroundWake 並指定這個 prefab。
/// </summary>
public class GroundWakeBuilder : EditorWindow
{
    private string outputFolder = "Assets/VFX/GroundWake";
    private string prefabName = "FX_GroundWake";
    private string jumpDustMaterials = "Assets/VFX/JumpDust/Materials";
    private string sparkMaterialPath = "Assets/VFX/M_HitSpark.mat";
    private bool attachToPlayer = true;

    [MenuItem("Tools/Diva Ex Machina/Ground Wake Builder")]
    private static void Open()
    {
        GetWindow<GroundWakeBuilder>("Ground Wake");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        prefabName = EditorGUILayout.TextField("Prefab Name", prefabName);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Materials", EditorStyles.boldLabel);
        jumpDustMaterials = EditorGUILayout.TextField("Jump Dust Materials", jumpDustMaterials);
        sparkMaterialPath = EditorGUILayout.TextField("Spark Material", sparkMaterialPath);

        EditorGUILayout.Space();
        attachToPlayer = EditorGUILayout.ToggleLeft(
            "Attach to selected Player（在選取的玩家上加 PlayerGroundWake 並指定 prefab）", attachToPlayer);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build Prefab", GUILayout.Height(32)))
            Build();

        EditorGUILayout.HelpBox(
            "尾流：突擊推進 / Dash / 飛行 / 地面高速移動，離地 8 m 以內。\n" +
            "下洗：飛行時從正下方往外推的塵土，懸停時有氣流環。\n" +
            "火花：船甲板或 DustSurface.kind = Metal 的表面。\n" +
            "PlayerGroundWake 要和 PlayerJumpDust 放在同一個玩家上（取色、船判斷共用）。",
            MessageType.Info);
    }

    private void Build()
    {
        Material matPuff = AssetDatabase.LoadAssetAtPath<Material>($"{jumpDustMaterials}/JD_Puff.mat");
        Material matDebris = AssetDatabase.LoadAssetAtPath<Material>($"{jumpDustMaterials}/JD_Debris.mat");
        Material matRing = AssetDatabase.LoadAssetAtPath<Material>($"{jumpDustMaterials}/JD_Ring.mat");
        Material matStreak = AssetDatabase.LoadAssetAtPath<Material>($"{jumpDustMaterials}/JD_Streak.mat");
        Material matSpark = AssetDatabase.LoadAssetAtPath<Material>(sparkMaterialPath);

        if (matPuff == null || matDebris == null || matRing == null || matStreak == null || matSpark == null)
        {
            EditorUtility.DisplayDialog("Ground Wake Builder",
                "找不到材質。請先執行 Jump Dust Builder（JD_Puff / JD_Debris / JD_Ring / JD_Streak）" +
                "和 Hit Spark Builder（M_HitSpark）。", "OK");
            return;
        }

        EnsureFolder(outputFolder);
        var root = new GameObject(prefabName);

        // ---- Wake：身後往兩側翻開的塵團 ----
        ParticleSystem wake = CreateLayer("Wake", root.transform, matPuff, 400);
        SetDrag(wake, 3.5f);
        SetGravity(wake, -0.15f);                       // 停下來之後慢慢上浮
        SetSpin(wake, 0.45f);
        SetSizeOverLife(wake, (0f, 0.55f), (0.28f, 1.25f), (1f, 2.0f));
        SetAlphaSmooth(wake, (0f, 0f), (0.95f, 0.06f), (0.85f, 0.4f), (0.35f, 0.75f), (0f, 1f));
        SetSheet(wake, 2, 2);

        // ---- Grit：踢起的碎石 ----
        ParticleSystem grit = CreateLayer("Grit", root.transform, matDebris, 120);
        SetGravity(grit, 2.2f);
        SetSpin(grit, 6f);
        SetAlphaSmooth(grit, (1f, 0f), (1f, 0.75f), (0f, 1f));
        SetSheet(grit, 2, 2);
        grit.GetComponent<ParticleSystemRenderer>().sortingFudge = -5f;

        // ---- Streaks：貼地往後射的速度線 ----
        ParticleSystem streaks = CreateLayer("Streaks", root.transform, matStreak, 100);
        SetDrag(streaks, 6f);
        SetAlphaSmooth(streaks, (0.8f, 0f), (0.5f, 0.5f), (0f, 1f));
        {
            var r = streaks.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.lengthScale = 1f;
            r.velocityScale = 0.06f;
            r.cameraVelocityScale = 0f;
            r.sortingFudge = -2f;
        }

        // ---- Downwash：推進器往下壓、往外推開的塵團 ----
        ParticleSystem downwash = CreateLayer("Downwash", root.transform, matPuff, 200);
        SetDrag(downwash, 3f);
        SetGravity(downwash, -0.12f);
        SetSpin(downwash, 0.35f);
        SetSizeOverLife(downwash, (0f, 0.6f), (0.3f, 1.3f), (1f, 2.2f));
        SetAlphaSmooth(downwash, (0f, 0f), (0.8f, 0.08f), (0.6f, 0.45f), (0f, 1f));
        SetSheet(downwash, 2, 2);
        downwash.GetComponent<ParticleSystemRenderer>().sortingFudge = 5f;

        // ---- Ring：懸停時地面的氣流環（平躺在地面） ----
        ParticleSystem ring = CreateLayer("Ring", root.transform, matRing, 12);
        SetSizeOverLife(ring, (0f, 0.45f), (0.3f, 2.2f), (1f, 3.3f));
        SetAlphaSmooth(ring, (0f, 0f), (0.7f, 0.06f), (0.45f, 0.45f), (0f, 1f));
        ring.GetComponent<ParticleSystemRenderer>().renderMode = ParticleSystemRenderMode.HorizontalBillboard;

        // ---- Sparks：金屬表面的火花 ----
        ParticleSystem sparks = CreateLayer("Sparks", root.transform, matSpark, 240);
        SetGravity(sparks, 1.5f);
        SetDrag(sparks, 1f);
        SetSizeOverLife(sparks, (0f, 1f), (0.7f, 0.8f), (1f, 0f));
        SetAlphaSmooth(sparks, (1f, 0f), (1f, 0.6f), (0f, 1f));
        {
            var r = sparks.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.lengthScale = 1.5f;
            r.velocityScale = 0.04f;
            r.cameraVelocityScale = 0f;
            r.sortingFudge = -6f;
        }

        var fx = root.AddComponent<GroundWakeEffect>();
        var so = new SerializedObject(fx);
        so.FindProperty("wake").objectReferenceValue = wake;
        so.FindProperty("grit").objectReferenceValue = grit;
        so.FindProperty("streaks").objectReferenceValue = streaks;
        so.FindProperty("downwash").objectReferenceValue = downwash;
        so.FindProperty("ring").objectReferenceValue = ring;
        so.FindProperty("sparks").objectReferenceValue = sparks;
        so.ApplyModifiedPropertiesWithoutUndo();

        string path = $"{outputFolder}/{prefabName}.prefab";
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        DestroyImmediate(root);
        AssetDatabase.SaveAssets();

        if (prefab == null)
        {
            Debug.LogError($"GroundWakeBuilder: failed to save {path}");
            return;
        }

        if (attachToPlayer) AttachToSelectedPlayer(prefab.GetComponent<GroundWakeEffect>());

        EditorGUIUtility.PingObject(prefab);
        Debug.Log($"GroundWakeBuilder: saved {path}");
    }

    private static void AttachToSelectedPlayer(GroundWakeEffect prefabEffect)
    {
        GameObject sel = Selection.activeGameObject;
        if (sel == null) return;

        PlayerJumpDust jd = sel.GetComponentInParent<PlayerJumpDust>() ?? sel.GetComponentInChildren<PlayerJumpDust>();
        GameObject host = jd != null ? jd.gameObject : sel.GetComponentInParent<PlayerMovement>()?.gameObject;
        if (host == null)
        {
            Debug.LogWarning("GroundWakeBuilder: 選取的物件不是玩家（找不到 PlayerJumpDust 或 PlayerMovement），沒有自動掛上。");
            return;
        }
        if (jd == null)
            Debug.LogWarning("GroundWakeBuilder: 玩家身上沒有 PlayerJumpDust。PlayerGroundWake 需要它來取色和判斷船。");

        var wake = host.GetComponent<PlayerGroundWake>();
        if (wake == null) wake = Undo.AddComponent<PlayerGroundWake>(host);

        var so = new SerializedObject(wake);
        so.FindProperty("wakePrefab").objectReferenceValue = prefabEffect;
        if (jd != null) so.FindProperty("jumpDust").objectReferenceValue = jd;
        so.ApplyModifiedProperties();
        EditorUtility.SetDirty(wake);
    }

    // =====================================================================
    // Particle helpers（和 JumpDustBuilder 相同的設定方式）
    // =====================================================================

    private static ParticleSystem CreateLayer(string name, Transform parent, Material material, int maxParticles)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.loop = true;              // 一直在「播放」但不發射，只靠 Emit() 噴出
        main.duration = 1f;
        main.playOnAwake = true;
        main.prewarm = false;
        main.gravityModifier = 0f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = maxParticles;
        main.cullingMode = ParticleSystemCullingMode.AlwaysSimulate;
        main.startColor = Color.white;
        main.startSize = 1f;
        main.startSpeed = 0f;
        main.startLifetime = 1f;

        var e = ps.emission;
        e.enabled = true;
        e.rateOverTime = 0f;
        e.rateOverDistance = 0f;

        var s = ps.shape;
        s.enabled = false;             // 位置、速度都由 GroundWakeEffect 逐顆指定

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = material;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortMode = ParticleSystemSortMode.Distance;
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;

        return ps;
    }

    private static void SetGravity(ParticleSystem ps, float g)
    {
        var main = ps.main;
        main.gravityModifier = g;
    }

    private static void SetDrag(ParticleSystem ps, float drag)
    {
        var lv = ps.limitVelocityOverLifetime;
        lv.enabled = true;
        lv.limit = 1000f;
        lv.drag = drag;
        lv.multiplyDragByParticleSize = false;
        lv.multiplyDragByParticleVelocity = false;
    }

    private static void SetSizeOverLife(ParticleSystem ps, params (float time, float value)[] keys)
    {
        var curve = new AnimationCurve();
        foreach (var k in keys) curve.AddKey(k.time, k.value);
        for (int i = 0; i < curve.length; i++) curve.SmoothTangents(i, 0f);

        var sol = ps.sizeOverLifetime;
        sol.enabled = true;
        sol.size = new ParticleSystem.MinMaxCurve(1f, curve);
    }

    private static void SetSpin(ParticleSystem ps, float radiansPerSecond)
    {
        var rol = ps.rotationOverLifetime;
        rol.enabled = true;
        rol.z = new ParticleSystem.MinMaxCurve(-radiansPerSecond, radiansPerSecond);
    }

    private static void SetSheet(ParticleSystem ps, int cols, int rows)
    {
        var tsa = ps.textureSheetAnimation;
        tsa.enabled = true;
        tsa.mode = ParticleSystemAnimationMode.Grid;
        tsa.numTilesX = cols;
        tsa.numTilesY = rows;
        tsa.animation = ParticleSystemAnimationType.WholeSheet;
        tsa.frameOverTime = new ParticleSystem.MinMaxCurve(0f, 0.999f);
        tsa.cycleCount = 1;
    }

    private static void SetAlphaSmooth(ParticleSystem ps, params (float alpha, float time)[] keys)
    {
        var alphaKeys = new GradientAlphaKey[keys.Length];
        for (int i = 0; i < keys.Length; i++)
            alphaKeys[i] = new GradientAlphaKey(keys[i].alpha, keys[i].time);

        var g = new Gradient { mode = GradientMode.Blend };
        g.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            alphaKeys);

        var col = ps.colorOverLifetime;
        col.enabled = true;
        col.color = new ParticleSystem.MinMaxGradient(g);
    }

    private static void EnsureFolder(string path)
    {
        path = path.Replace('\\', '/').TrimEnd('/');
        if (AssetDatabase.IsValidFolder(path)) return;

        string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
        string leaf = Path.GetFileName(path);
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            EnsureFolder(parent);

        AssetDatabase.CreateFolder(parent, leaf);
    }
}

#endif
