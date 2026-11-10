#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Tools > Diva Ex Machina > Track Dust Builder
///
/// 產生貝努鳥號履帶塵土的 prefab（LandshipTrackDust）。
/// 材質沿用 Jump Dust（Assets/VFX/JumpDust/Materials 的 JD_Puff、JD_Debris），要先跑過 Jump Dust Builder。
///
/// 重新建置會覆寫 prefab，可以改數值再按一次 Build。
/// 勾選「Add Left / Right to selected ship」時，會在選取的船底下放左右兩個實例，
/// 位置依船的 Renderer 範圍粗估（貼著兩側、底部），之後再自己對齊履帶。
/// </summary>
public class TrackDustBuilder : EditorWindow
{
    private string outputFolder = "Assets/VFX/TrackDust";
    private string prefabName = "FX_TrackDust";
    private string jumpDustMaterials = "Assets/VFX/JumpDust/Materials";
    private bool addToSelectedShip = true;
    private int maxPuffs = 1500;

    [MenuItem("Tools/Diva Ex Machina/Track Dust Builder")]
    private static void Open() => GetWindow<TrackDustBuilder>("Track Dust");

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
        outputFolder = EditorGUILayout.TextField("Output Folder", outputFolder);
        prefabName = EditorGUILayout.TextField("Prefab Name", prefabName);
        jumpDustMaterials = EditorGUILayout.TextField("Jump Dust Materials", jumpDustMaterials);
        maxPuffs = EditorGUILayout.IntField("Max Puffs (per side)", maxPuffs);

        EditorGUILayout.Space();
        addToSelectedShip = EditorGUILayout.ToggleLeft(
            "Add Left / Right to selected ship（在選取的船底下放左右兩個）", addToSelectedShip);

        EditorGUILayout.Space();
        if (GUILayout.Button("Build Prefab", GUILayout.Height(32)))
            Build();

        EditorGUILayout.HelpBox(
            "放好之後：選左右兩個物件，在 Scene 視窗看黃色框（Gizmo）是不是貼著履帶接地的那一段，\n" +
            "調整位置、Track Length、Track Width。Z 軸（藍色箭頭）要沿著履帶方向。\n" +
            "塵團大小、數量都在 LandshipTrackDust 上調，不用重建。",
            MessageType.Info);
    }

    private void Build()
    {
        Material matPuff = AssetDatabase.LoadAssetAtPath<Material>($"{jumpDustMaterials}/JD_Puff.mat");
        Material matDebris = AssetDatabase.LoadAssetAtPath<Material>($"{jumpDustMaterials}/JD_Debris.mat");
        if (matPuff == null || matDebris == null)
        {
            EditorUtility.DisplayDialog("Track Dust Builder",
                "找不到材質。請先執行 Jump Dust Builder（JD_Puff / JD_Debris）。", "OK");
            return;
        }

        EnsureFolder(outputFolder);
        var root = new GameObject(prefabName);

        // ---- Puff：大塊、慢慢膨脹散開的塵團 ----
        ParticleSystem puff = CreateLayer("Puff", root.transform, matPuff, Mathf.Max(100, maxPuffs));
        SetDrag(puff, 0.8f);
        SetGravity(puff, -0.02f);                     // 很慢地往上飄
        SetSpin(puff, 0.15f);
        SetSizeOverLife(puff, (0f, 0.5f), (0.25f, 1.1f), (1f, 2.2f));
        SetAlphaSmooth(puff, (0f, 0f), (1f, 0.08f), (0.75f, 0.45f), (0f, 1f));
        SetSheet(puff, 2, 2);

        // ---- Debris：後端踢起、落下的碎石 ----
        ParticleSystem debris = CreateLayer("Debris", root.transform, matDebris, 300);
        SetGravity(debris, 1.5f);
        SetSpin(debris, 5f);
        SetAlphaSmooth(debris, (1f, 0f), (1f, 0.8f), (0f, 1f));
        SetSheet(debris, 2, 2);
        debris.GetComponent<ParticleSystemRenderer>().sortingFudge = -5f;

        var fx = root.AddComponent<LandshipTrackDust>();
        var so = new SerializedObject(fx);
        so.FindProperty("puff").objectReferenceValue = puff;
        so.FindProperty("debris").objectReferenceValue = debris;
        so.ApplyModifiedPropertiesWithoutUndo();

        string path = $"{outputFolder}/{prefabName}.prefab";
        GameObject prefab = PrefabUtility.SaveAsPrefabAsset(root, path);
        DestroyImmediate(root);
        AssetDatabase.SaveAssets();
        if (prefab == null) { Debug.LogError($"TrackDustBuilder: failed to save {path}"); return; }

        if (addToSelectedShip) AddToSelectedShip(prefab);

        EditorGUIUtility.PingObject(prefab);
        Debug.Log($"TrackDustBuilder: saved {path}");
    }

    private static void AddToSelectedShip(GameObject prefab)
    {
        GameObject ship = Selection.activeGameObject;
        if (ship == null) { Debug.Log("TrackDustBuilder: 沒有選取船，只建了 prefab。"); return; }

        // 船的範圍（本地座標）：用所有 Renderer 的 bounds 粗估
        var renderers = ship.GetComponentsInChildren<Renderer>();
        Bounds local = new Bounds(Vector3.zero, Vector3.zero);
        bool any = false;
        foreach (Renderer r in renderers)
        {
            if (r is ParticleSystemRenderer || r is TrailRenderer || r is LineRenderer) continue;
            Bounds b = r.bounds;
            Vector3 c = b.center, e = b.extents;
            for (int i = 0; i < 8; i++)
            {
                Vector3 corner = c + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
                Vector3 p = ship.transform.InverseTransformPoint(corner);
                if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                else local.Encapsulate(p);
            }
        }
        if (!any) local = new Bounds(Vector3.zero, new Vector3(40f, 10f, 120f));

        for (int s = -1; s <= 1; s += 2)
        {
            string name = s < 0 ? "Track Dust L" : "Track Dust R";
            Transform existing = ship.transform.Find(name);
            if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);

            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab, ship.transform);
            go.name = name;
            go.transform.localPosition = new Vector3(local.center.x + s * local.extents.x * 0.9f, local.min.y, local.center.z);
            go.transform.localRotation = Quaternion.identity;
            Undo.RegisterCreatedObjectUndo(go, "Add Track Dust");

            var so = new SerializedObject(go.GetComponent<LandshipTrackDust>());
            so.FindProperty("trackLength").floatValue = local.size.z * 0.8f;
            so.ApplyModifiedProperties();
        }
        Debug.Log("TrackDustBuilder: 已在船底下放好 Track Dust L / R，請對齊履帶的位置。");
    }

    // =====================================================================
    // Particle helpers（和 GroundWakeBuilder 相同）
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
        s.enabled = false;

        var r = go.GetComponent<ParticleSystemRenderer>();
        r.sharedMaterial = material;
        r.renderMode = ParticleSystemRenderMode.Billboard;
        r.sortMode = ParticleSystemSortMode.Distance;
        r.maxParticleSize = 5f;        // 預設 0.5（佔畫面一半）：大塵團靠近鏡頭時會被縮小，放寬
        r.shadowCastingMode = ShadowCastingMode.Off;
        r.receiveShadows = false;
        r.lightProbeUsage = LightProbeUsage.Off;
        r.reflectionProbeUsage = ReflectionProbeUsage.Off;

        return ps;
    }

    private static void SetGravity(ParticleSystem ps, float g) { var m = ps.main; m.gravityModifier = g; }

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
        for (int i = 0; i < keys.Length; i++) alphaKeys[i] = new GradientAlphaKey(keys[i].alpha, keys[i].time);
        var g = new Gradient { mode = GradientMode.Blend };
        g.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) }, alphaKeys);
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
        if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
        AssetDatabase.CreateFolder(parent, leaf);
    }
}

#endif
