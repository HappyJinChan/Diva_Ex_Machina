// HitSparkBuilder.cs — 子彈命中火花特效的產生工具。
// 選單 Tools > VFX > Hit Spark Builder：調整參數後按「建立 / 更新 Prefab」，
// 會產生 Assets/VFX/FX_HitSpark.prefab（以及材質 M_HitSpark.mat），再把它拖到子彈 prefab 的 Hit Effect 欄位。
//
// 特效由三組 Particle System 組成：
//   Sparks：一次爆出一把拉長的火花，受重力與阻力，往「子彈飛來的方向」散開（Bullet 生成時 +Z 朝向來向）。
//   Flash ：命中瞬間的一個亮點，0.06 秒左右就消失，讓命中在畫面上一眼就看得到。
//   Embers：幾顆飛得慢、留得久一點、往下掉的餘燼（可設 0 關掉）。
// 全部用世界空間模擬：Bullet 會把特效掛在被打中的敵人底下，世界空間的火花才不會被敵人拖著走。
// 重新按「建立 / 更新 Prefab」會覆蓋同一個檔案（GUID 不變），已經指定到子彈上的引用不用重設。
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace DivaExMachina.VFX.EditorTools
{
    public class HitSparkBuilder : EditorWindow
    {
        [SerializeField] string prefabPath = "Assets/VFX/FX_HitSpark.prefab";
        [SerializeField] string materialPath = "Assets/VFX/M_HitSpark.mat";
        [SerializeField] Texture2D texture;

        [SerializeField] Color color = new Color(1f, 0.72f, 0.32f);
        [SerializeField, Min(0f)] float intensity = 4f;

        [SerializeField, Min(0)] int sparkCount = 18;
        [SerializeField] Vector2 sparkSpeed = new Vector2(8f, 20f);
        [SerializeField] Vector2 sparkLifetime = new Vector2(0.12f, 0.35f);
        [SerializeField] Vector2 sparkSize = new Vector2(0.03f, 0.07f);
        [SerializeField, Range(0f, 90f)] float spreadAngle = 40f;
        [SerializeField] float gravity = 1.2f;
        [SerializeField, Min(0f)] float drag = 1.5f;
        [SerializeField, Min(0f)] float stretch = 0.04f;
        [SerializeField] bool bounceOnSurfaces = false;

        [SerializeField] bool flash = true;
        [SerializeField, Min(0f)] float flashSize = 0.8f;
        [SerializeField, Min(0.01f)] float flashLifetime = 0.06f;

        [SerializeField, Min(0)] int emberCount = 4;

        [MenuItem("Tools/VFX/Hit Spark Builder")]
        static void Open() => GetWindow<HitSparkBuilder>("Hit Spark");

        void OnEnable()
        {
            if (texture == null) texture = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/VFX/T_HaloGlow.png");
        }

        void OnGUI()
        {
            EditorGUILayout.LabelField("輸出", EditorStyles.boldLabel);
            prefabPath = EditorGUILayout.TextField("Prefab", prefabPath);
            materialPath = EditorGUILayout.TextField("材質", materialPath);
            texture = (Texture2D)EditorGUILayout.ObjectField("粒子貼圖", texture, typeof(Texture2D), false);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("顏色", EditorStyles.boldLabel);
            color = EditorGUILayout.ColorField("火花顏色", color);
            intensity = EditorGUILayout.FloatField(new GUIContent("亮度（HDR）", "搭配 Bloom。越高越亮、泛光越明顯"), intensity);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("火花", EditorStyles.boldLabel);
            sparkCount = EditorGUILayout.IntField("數量", sparkCount);
            sparkSpeed = EditorGUILayout.Vector2Field("速度 最小 / 最大（m/s）", sparkSpeed);
            sparkLifetime = EditorGUILayout.Vector2Field("壽命 最小 / 最大（秒）", sparkLifetime);
            sparkSize = EditorGUILayout.Vector2Field("粗細 最小 / 最大（m）", sparkSize);
            spreadAngle = EditorGUILayout.Slider(new GUIContent("散開角度", "以子彈來向為中心的圓錐半角"), spreadAngle, 0f, 90f);
            gravity = EditorGUILayout.FloatField("重力倍率", gravity);
            drag = EditorGUILayout.FloatField(new GUIContent("空氣阻力", "越大減速越快，火花飛得越短"), drag);
            stretch = EditorGUILayout.FloatField(new GUIContent("拉長程度", "速度越快拉得越長"), stretch);
            bounceOnSurfaces = EditorGUILayout.Toggle(new GUIContent("碰到表面會彈跳", "比較真實，但每顆火花都要做碰撞檢查，敵人多時會比較耗效能"), bounceOnSurfaces);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("閃光 / 餘燼", EditorStyles.boldLabel);
            flash = EditorGUILayout.Toggle("命中閃光", flash);
            using (new EditorGUI.DisabledScope(!flash))
            {
                flashSize = EditorGUILayout.FloatField("閃光大小（m）", flashSize);
                flashLifetime = EditorGUILayout.FloatField("閃光時間（秒）", flashLifetime);
            }
            emberCount = EditorGUILayout.IntField(new GUIContent("餘燼數量", "0 = 不要"), emberCount);

            EditorGUILayout.Space();
            if (GUILayout.Button("建立 / 更新 Prefab", GUILayout.Height(30))) Build();
            using (new EditorGUI.DisabledScope(AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null))
            {
                if (GUILayout.Button("在場景中預覽")) Preview();
            }
            EditorGUILayout.HelpBox("建好之後，把 FX_HitSpark 拖到子彈 prefab 的 Hit Effect 欄位。\n" +
                                    "預覽：會在 Scene 視窗的中心生成一份並選取它（選取時 Scene 視窗才會播放粒子），看完自己刪掉。", MessageType.Info);
        }

        // ======================= 建立 =======================
        void Build()
        {
            var mat = GetOrCreateMaterial();
            if (mat == null) return;

            var root = new GameObject("FX_HitSpark");
            try
            {
                SetupSparks(CreateSystem(root.transform, "Sparks"), mat);
                if (flash) SetupFlash(CreateSystem(root.transform, "Flash"), mat);
                if (emberCount > 0) SetupEmbers(CreateSystem(root.transform, "Embers"), mat);

                EnsureFolder(prefabPath);
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab != null)
                {
                    EditorGUIUtility.PingObject(prefab);
                    Debug.Log($"[HitSpark] 已建立 / 更新 {prefabPath}");
                }
            }
            finally
            {
                DestroyImmediate(root);
            }
        }

        Material GetOrCreateMaterial()
        {
            var shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) { Debug.LogError("[HitSpark] 找不到 URP Particles/Unlit shader"); return null; }

            var mat = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (mat == null)
            {
                EnsureFolder(materialPath);
                mat = new Material(shader) { name = System.IO.Path.GetFileNameWithoutExtension(materialPath) };
                AssetDatabase.CreateAsset(mat, materialPath);
            }
            else mat.shader = shader;

            // 透明 + 疊加（Additive）：火花越疊越亮，不會有黑邊
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", 2f);
            mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)BlendMode.One);
            mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_DstBlendAlpha", (float)BlendMode.One);
            mat.SetFloat("_ZWrite", 0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;

            if (texture != null) mat.SetTexture("_BaseMap", texture);
            mat.SetColor("_BaseColor", new Color(color.r * intensity, color.g * intensity, color.b * intensity, 1f));
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            return mat;
        }

        static ParticleSystem CreateSystem(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = ps.main;
            main.duration = 0.5f;
            main.loop = false;
            main.playOnAwake = true;                                       // 物件池重新啟用時自動播放
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            main.startRotation = 0f;
            main.stopAction = ParticleSystemStopAction.None;               // 回收交給 PrefabPool

            var r = go.GetComponent<ParticleSystemRenderer>();
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            return ps;
        }

        static void Burst(ParticleSystem ps, int count)
        {
            var em = ps.emission;
            em.rateOverTime = 0f;
            em.SetBursts(new[] { new ParticleSystem.Burst(0f, (short)Mathf.Max(1, count)) });
        }

        static Gradient FadeGradient(Color start, Color mid, Color end, float midTime)
        {
            var g = new Gradient();
            g.SetKeys(
                new[] { new GradientColorKey(start, 0f), new GradientColorKey(mid, midTime), new GradientColorKey(end, 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, midTime), new GradientAlphaKey(0f, 1f) });
            return g;
        }

        void SetupSparks(ParticleSystem ps, Material mat)
        {
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(sparkLifetime.x, sparkLifetime.y);
            main.startSpeed = new ParticleSystem.MinMaxCurve(sparkSpeed.x, sparkSpeed.y);
            main.startSize = new ParticleSystem.MinMaxCurve(sparkSize.x, sparkSize.y);
            main.gravityModifier = gravity;
            main.maxParticles = Mathf.Max(1, sparkCount);
            Burst(ps, sparkCount);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;                // 沿 +Z（子彈來向）散開
            shape.angle = spreadAngle;
            shape.radius = 0.02f;

            var lv = ps.limitVelocityOverLifetime;
            lv.enabled = true;
            lv.limit = 1000f;                                              // 不限速，只用阻力
            lv.drag = drag;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(
                FadeGradient(Color.white, new Color(1f, 0.85f, 0.6f), new Color(1f, 0.45f, 0.15f), 0.35f));

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.3f));

            if (bounceOnSurfaces)
            {
                var c = ps.collision;
                c.enabled = true;
                c.type = ParticleSystemCollisionType.World;
                c.mode = ParticleSystemCollisionMode.Collision3D;
                c.quality = ParticleSystemCollisionQuality.Low;
                c.dampen = 0.4f;
                c.bounce = 0.3f;
                c.lifetimeLoss = 0.2f;
            }

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Stretch;
            r.velocityScale = stretch;
            r.lengthScale = 1.5f;
            r.sharedMaterial = mat;
        }

        void SetupFlash(ParticleSystem ps, Material mat)
        {
            var main = ps.main;
            main.startLifetime = flashLifetime;
            main.startSpeed = 0f;
            main.startSize = new ParticleSystem.MinMaxCurve(flashSize * 0.8f, flashSize * 1.1f);
            main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);
            main.maxParticles = 1;
            Burst(ps, 1);

            var shape = ps.shape;
            shape.enabled = false;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(FadeGradient(Color.white, Color.white, new Color(1f, 0.7f, 0.4f), 0.3f));

            var size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 1.2f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = mat;
        }

        void SetupEmbers(ParticleSystem ps, Material mat)
        {
            var main = ps.main;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.4f, 0.8f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 6f);
            main.startSize = new ParticleSystem.MinMaxCurve(sparkSize.y * 0.8f, sparkSize.y * 1.4f);
            main.gravityModifier = gravity * 1.5f;
            main.maxParticles = Mathf.Max(1, emberCount);
            Burst(ps, emberCount);

            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = Mathf.Min(90f, spreadAngle * 1.5f);
            shape.radius = 0.03f;

            var col = ps.colorOverLifetime;
            col.enabled = true;
            col.color = new ParticleSystem.MinMaxGradient(
                FadeGradient(new Color(1f, 0.8f, 0.5f), new Color(1f, 0.5f, 0.2f), new Color(0.8f, 0.25f, 0.05f), 0.5f));

            var r = ps.GetComponent<ParticleSystemRenderer>();
            r.renderMode = ParticleSystemRenderMode.Billboard;
            r.sharedMaterial = mat;
        }

        // ======================= 預覽 =======================
        void Preview()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return;
            var sv = SceneView.lastActiveSceneView;
            Vector3 pos = sv != null ? sv.pivot : Vector3.zero;
            var go = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            go.name = "FX_HitSpark (Preview)";
            go.transform.position = pos;
            Undo.RegisterCreatedObjectUndo(go, "Preview Hit Spark");
            Selection.activeGameObject = go;
            foreach (var ps in go.GetComponentsInChildren<ParticleSystem>()) ps.Play(true);
        }

        static void EnsureFolder(string assetPath)
        {
            EnsureDir(System.IO.Path.GetDirectoryName(assetPath).Replace('\\', '/'));
        }

        static void EnsureDir(string dir)
        {
            if (string.IsNullOrEmpty(dir) || AssetDatabase.IsValidFolder(dir)) return;
            string parent = System.IO.Path.GetDirectoryName(dir).Replace('\\', '/');
            EnsureDir(parent);                                             // 先建立上層
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(dir));
        }
    }
}
