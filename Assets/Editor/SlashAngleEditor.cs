#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

/// <summary>
/// Tools > Diva Ex Machina > Slash Angle Editor
///
/// 調整每一段近戰攻擊的劍風（MeleeAttackStep 的 Slash VFX 區塊）：
///   1. 選角色（PlayerAnimation）、MeleeComboLibrary、武器類型 / 握法 / 第幾段、左右手
///   2. 「擺到 HitboxOn 的姿勢」：用 AnimationMode 把角色擺成這一段攻擊動畫在 HitboxOn 那一格的樣子（不用進 Play 模式）
///   3. Scene 視窗裡會出現劍風的預覽，用 移動(W) / 旋轉(E) / 縮放(R) 把手拖到跟刀的軌跡對齊，數值直接寫回 MeleeComboLibrary（可 Undo）
///   4. 「劍風時間」滑桿可以看劍風播放到不同時間的樣子；橘色弧線是這一段的扇形判定（Arc Range / Arc Angle）
///
/// 右手沒有勾 Right Hand Override 時，在右手上拖動會改到左手的設定（右手是左手的鏡像）。
/// 預覽物件不存檔、不放進角色的階層，關掉視窗或進 Play 模式時自動清掉並還原姿勢。
/// </summary>
public class SlashAngleEditor : EditorWindow
{
    [SerializeField] private PlayerAnimation player;
    [SerializeField] private MeleeComboLibrary library;
    [SerializeField] private MeleeWeaponClass weaponClass = MeleeWeaponClass.Sword;
    [SerializeField] private MeleeGrip grip = MeleeGrip.OneHanded;
    [SerializeField] private int stepIndex;
    [SerializeField] private bool rightHand;
    [SerializeField] private GameObject previewPrefab;
    [SerializeField] private string meleeLayerName = "One_Hand_Melee_Attack";
    [SerializeField] private float animTime;
    [SerializeField] private float slashTime = 0.1f;
    [SerializeField] private bool showArc = true;

    private static readonly string[] WorldSpaceNames = { "Flying" };
    private static readonly string[] RemoveNames = { "Impact" };

    private GameObject _preview;
    private GameObject _previewSource;
    private ParticleSystem[] _previewRoots;
    private float _refRadius = 1f;
    private AnimationClip _clip;
    private string _clipInfo = "";
    private float _hitboxTime = -1f;
    private bool _posed;
    private Vector2 _scroll;

    [MenuItem("Tools/Diva Ex Machina/Slash Angle Editor")]
    private static void Open() => GetWindow<SlashAngleEditor>("Slash Angle");

    private void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
        AutoFind();
    }

    private void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        Cleanup();
    }

    private void OnPlayModeChanged(PlayModeStateChange s)
    {
        if (s == PlayModeStateChange.ExitingEditMode) Cleanup();
    }

    private void AutoFind()
    {
        if (player == null) player = FindFirstObjectByType<PlayerAnimation>();
        if (library == null)
        {
            var ctrl = FindFirstObjectByType<MeleeAttackController>();
            if (ctrl != null)
                library = new SerializedObject(ctrl).FindProperty("comboLibrary")?.objectReferenceValue as MeleeComboLibrary;
        }
    }

    private void Cleanup()
    {
        if (_preview != null) DestroyImmediate(_preview);
        _preview = null;
        _previewSource = null;
        _previewRoots = null;
        if (AnimationMode.InAnimationMode()) AnimationMode.StopAnimationMode();
        _posed = false;
        SceneView.RepaintAll();
    }

    // ═══════════════════════ 資料存取 ═══════════════════════

    private MeleeAttackStep CurrentStep =>
        library != null ? library.GetStep(weaponClass, grip, stepIndex) : null;

    /// <summary>這一段在 library 裡的 SerializedProperty（用來寫值，支援 Undo）。</summary>
    private SerializedProperty FindStepProperty(SerializedObject so)
    {
        SerializedProperty movesets = so.FindProperty("movesets");
        if (movesets == null || library.movesets == null) return null;
        for (int i = 0; i < library.movesets.Count && i < movesets.arraySize; i++)
        {
            if (library.movesets[i] == null || library.movesets[i].weaponClass != weaponClass) continue;

            SerializedProperty seq = movesets.GetArrayElementAtIndex(i)
                .FindPropertyRelative(grip == MeleeGrip.OneHanded ? "oneHanded" : "twoHanded");
            SerializedProperty steps = seq?.FindPropertyRelative("steps");
            if (steps == null || stepIndex < 0 || stepIndex >= steps.arraySize) return null;
            return steps.GetArrayElementAtIndex(stepIndex);
        }
        return null;
    }

    // ═══════════════════════ GUI ═══════════════════════

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        EditorGUILayout.LabelField("Target", EditorStyles.boldLabel);
        player = (PlayerAnimation)EditorGUILayout.ObjectField("Player", player, typeof(PlayerAnimation), true);
        library = (MeleeComboLibrary)EditorGUILayout.ObjectField("Combo Library", library, typeof(MeleeComboLibrary), false);
        if (player == null || library == null)
        {
            if (GUILayout.Button("自動尋找")) AutoFind();
            EditorGUILayout.HelpBox("需要場景裡的角色（PlayerAnimation）和 MeleeComboLibrary。", MessageType.Info);
            EditorGUILayout.EndScrollView();
            return;
        }

        EditorGUI.BeginChangeCheck();
        weaponClass = (MeleeWeaponClass)EditorGUILayout.EnumPopup("Weapon Class", weaponClass);
        grip = (MeleeGrip)EditorGUILayout.EnumPopup("Grip", grip);

        int count = library.GetStepCount(weaponClass, grip);
        if (count <= 0)
        {
            EditorGUILayout.HelpBox("這個武器類型 × 握法沒有連段資料。", MessageType.Warning);
            EditorGUILayout.EndScrollView();
            return;
        }
        stepIndex = Mathf.Clamp(stepIndex, 0, count - 1);
        var labels = new string[count];
        for (int i = 0; i < count; i++)
        {
            var s = library.GetStep(weaponClass, grip, i);
            labels[i] = $"{i}: {(s != null ? s.GetStateName(!rightHand) : "?")}";
        }
        stepIndex = EditorGUILayout.Popup("Step", stepIndex, labels);
        rightHand = GUILayout.Toolbar(rightHand ? 1 : 0, new[] { "左手", "右手" }) == 1;
        bool targetChanged = EditorGUI.EndChangeCheck();

        MeleeAttackStep step = CurrentStep;
        if (step == null) { EditorGUILayout.EndScrollView(); return; }

        if (targetChanged) { ResolveClip(step); _posed = false; }
        if (_clip == null && string.IsNullOrEmpty(_clipInfo)) ResolveClip(step);

        // ── 姿勢 ──
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Pose", EditorStyles.boldLabel);
        EditorGUILayout.LabelField(_clipInfo, EditorStyles.wordWrappedMiniLabel);
        using (new EditorGUI.DisabledScope(_clip == null))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(_hitboxTime >= 0f ? $"擺到 HitboxOn 的姿勢（{_hitboxTime:0.00}s）" : "擺到動畫開頭"))
                {
                    animTime = Mathf.Max(0f, _hitboxTime);
                    Pose();
                }
                if (GUILayout.Button("還原姿勢", GUILayout.Width(80))) Cleanup();
            }
            if (_clip != null)
            {
                EditorGUI.BeginChangeCheck();
                animTime = EditorGUILayout.Slider("動畫時間", animTime, 0f, _clip.length);
                if (EditorGUI.EndChangeCheck()) Pose();
            }
        }

        // ── 劍風預覽 ──
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Slash Preview", EditorStyles.boldLabel);
        GameObject src = step.slashVfxOverride != null ? step.slashVfxOverride : previewPrefab;
        if (step.slashVfxOverride == null)
            previewPrefab = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Preview Prefab",
                "這一段沒有 Slash Vfx Override 時，預覽用哪個劍風（執行時用的是武器的 swordSlash）。"), previewPrefab, typeof(GameObject), false);
        else
            EditorGUILayout.ObjectField("Slash (Override)", step.slashVfxOverride, typeof(GameObject), false);

        EditorGUI.BeginChangeCheck();
        slashTime = EditorGUILayout.Slider("劍風時間", slashTime, 0f, 0.6f);
        showArc = EditorGUILayout.Toggle("顯示扇形判定", showArc);
        bool previewChanged = EditorGUI.EndChangeCheck();

        EnsurePreview(src);
        if (_preview == null)
            EditorGUILayout.HelpBox("指定一個劍風 prefab 才能預覽（例如 Free Slash VFX/Prefabs/Slash VFX）。", MessageType.Info);
        else
            EditorGUILayout.LabelField($"劍風網格半徑（scale 1）：{_refRadius:0.00} m", EditorStyles.miniLabel);

        // ── 數值（直接改 library，支援 Undo） ──
        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Slash VFX（這一段）", EditorStyles.boldLabel);
        var so = new SerializedObject(library);
        so.Update();
        SerializedProperty sp = FindStepProperty(so);
        if (sp != null)
        {
            EditorGUI.BeginChangeCheck();
            bool editRight = rightHand && step.slashRightHandOverride;
            if (rightHand && !step.slashRightHandOverride)
                EditorGUILayout.HelpBox("右手目前是左手的鏡像：在這裡或 Scene 視窗調整，會改到左手的設定。", MessageType.None);

            EditorGUILayout.PropertyField(sp.FindPropertyRelative(editRight ? "slashRotationRight" : "slashRotation"), new GUIContent("Rotation"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative(editRight ? "slashOffsetRight" : "slashOffset"), new GUIContent("Offset"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative(editRight ? "slashFlipRight" : "slashFlip"), new GUIContent("Flip"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("slashScaleMode"), new GUIContent("Scale Mode"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("slashScale"), new GUIContent("Scale"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("slashDelay"), new GUIContent("Delay"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("slashRightHandOverride"), new GUIContent("Right Hand Override"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("arcRange"), new GUIContent("Arc Range（判定）"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("arcAngle"), new GUIContent("Arc Angle（判定）"));
            EditorGUILayout.PropertyField(sp.FindPropertyRelative("arcPitchAngle"), new GUIContent("Arc Pitch Angle（判定）"));
            if (EditorGUI.EndChangeCheck()) { so.ApplyModifiedProperties(); previewChanged = true; }

            if (rightHand && step.slashRightHandOverride && GUILayout.Button("用左手的鏡像填入右手"))
            {
                Undo.RecordObject(library, "Mirror Slash To Right");
                MeleeSlashVfx.ComputeLocalPose(CloneWithoutOverride(step), false, 1f, out Vector3 p, out Quaternion q, out Vector3 sc);
                step.slashOffsetRight = p;
                step.slashRotationRight = q.eulerAngles;
                step.slashFlipRight = sc.x < 0f;
                EditorUtility.SetDirty(library);
                previewChanged = true;
            }
        }

        if (previewChanged || targetChanged) SceneView.RepaintAll();

        EditorGUILayout.HelpBox(
            "Scene 視窗：W 移動、E 旋轉、R 縮放（拖中間的方塊）。\n" +
            "Rotation 的 Z 通常就是劍風的傾斜：0 = 水平橫掃，±90 = 垂直劈。Flip 反轉揮擊方向。",
            MessageType.None);

        EditorGUILayout.EndScrollView();
    }

    private static MeleeAttackStep CloneWithoutOverride(MeleeAttackStep s)
    {
        var c = new MeleeAttackStep();
        c.slashOffset = s.slashOffset;
        c.slashRotation = s.slashRotation;
        c.slashFlip = s.slashFlip;
        c.slashScaleMode = MeleeSlashScaleMode.Manual;
        c.slashScale = 1f;
        c.slashRightHandOverride = false;
        return c;
    }

    // ═══════════════════════ 動畫 ═══════════════════════

    private Animator GetAnimator() =>
        player == null ? null : (player.anim != null ? player.anim : player.GetComponent<Animator>());

    private void ResolveClip(MeleeAttackStep step)
    {
        _clip = null; _hitboxTime = -1f;
        Animator anim = GetAnimator();
        if (anim == null) { _clipInfo = "找不到 Animator。"; return; }

        string stateName = step.GetStateName(!rightHand);
        RuntimeAnimatorController rac = anim.runtimeAnimatorController;
        var aoc = rac as AnimatorOverrideController;
        var ac = (aoc != null ? aoc.runtimeAnimatorController : rac) as AnimatorController;
        if (ac == null) { _clipInfo = "Animator 沒有 AnimatorController。"; return; }

        AnimatorControllerLayer layer = null;
        foreach (var l in ac.layers) if (l.name == meleeLayerName) { layer = l; break; }
        if (layer == null) { _clipInfo = $"找不到層 '{meleeLayerName}'。"; return; }

        AnimatorState state = FindState(layer.stateMachine, stateName);
        if (state == null) { _clipInfo = $"層裡找不到 State '{stateName}'。"; return; }

        Motion motion = state.motion;
        if (motion is BlendTree bt && bt.children.Length > 0) motion = bt.children[0].motion;
        _clip = motion as AnimationClip;
        if (_clip != null && aoc != null && aoc[_clip] != null) _clip = aoc[_clip];
        if (_clip == null) { _clipInfo = $"State '{stateName}' 沒有動畫片段。"; return; }

        foreach (AnimationEvent e in AnimationUtility.GetAnimationEvents(_clip))
            if (e.functionName == "AnimEvent_MeleeHitboxOn") { _hitboxTime = e.time; break; }

        _clipInfo = $"{stateName} → {_clip.name}（{_clip.length:0.00}s）" +
                    (_hitboxTime >= 0f ? $"，HitboxOn @ {_hitboxTime:0.00}s" : "，片段上沒有 HitboxOn 事件");
    }

    private static AnimatorState FindState(AnimatorStateMachine sm, string name)
    {
        foreach (var cs in sm.states) if (cs.state.name == name) return cs.state;
        foreach (var sub in sm.stateMachines)
        {
            var r = FindState(sub.stateMachine, name);
            if (r != null) return r;
        }
        return null;
    }

    private void Pose()
    {
        Animator anim = GetAnimator();
        if (anim == null || _clip == null) return;
        if (!AnimationMode.InAnimationMode()) AnimationMode.StartAnimationMode();
        AnimationMode.BeginSampling();
        AnimationMode.SampleAnimationClip(anim.gameObject, _clip, animTime);
        AnimationMode.EndSampling();
        _posed = true;
        SceneView.RepaintAll();
    }

    // ═══════════════════════ 預覽 ═══════════════════════

    private void EnsurePreview(GameObject src)
    {
        if (src == _previewSource && (_preview != null || src == null)) return;

        if (_preview != null) DestroyImmediate(_preview);
        _preview = null; _previewRoots = null; _previewSource = src;
        if (src == null) return;

        _preview = Instantiate(src);
        _preview.name = "[Slash Preview] " + src.name;
        _preview.hideFlags = HideFlags.DontSave | HideFlags.NotEditable;
        MeleeSlashVfx.Prepare(_preview, WorldSpaceNames, RemoveNames, immediate: true);
        _refRadius = MeleeSlashVfx.MeasureReferenceRadius(_preview);
        _previewRoots = MeleeSlashVfx.FindRootSystems(_preview);
    }

    private Transform Model => player != null ? player.transform : null;

    private void UpdatePreviewPose(MeleeAttackStep step)
    {
        if (_preview == null || Model == null) return;

        MeleeSlashVfx.ComputeLocalPose(step, !rightHand, _refRadius, out Vector3 p, out Quaternion q, out Vector3 s);
        _preview.transform.SetPositionAndRotation(Model.TransformPoint(p), Model.rotation * q);
        _preview.transform.localScale = s;

        if (_previewRoots != null)
            foreach (ParticleSystem ps in _previewRoots)
                if (ps != null) ps.Simulate(Mathf.Max(0.001f, slashTime), true, true);
    }

    // ═══════════════════════ Scene 視窗 ═══════════════════════

    private void OnSceneGUI(SceneView view)
    {
        if (player == null || library == null || EditorApplication.isPlaying) return;
        MeleeAttackStep step = CurrentStep;
        if (step == null) return;

        Transform model = Model;

        if (showArc)
        {
            Vector3 o = model.position + Vector3.up * 1f;
            Vector3 fwd = model.forward;
            Handles.color = new Color(1f, 0.45f, 0.1f, 0.9f);
            Handles.DrawWireArc(o, model.up, Quaternion.AngleAxis(-step.arcAngle * 0.5f, model.up) * fwd, step.arcAngle, step.arcRange);
            Handles.DrawLine(o, o + Quaternion.AngleAxis(-step.arcAngle * 0.5f, model.up) * fwd * step.arcRange);
            Handles.DrawLine(o, o + Quaternion.AngleAxis(step.arcAngle * 0.5f, model.up) * fwd * step.arcRange);

            // 上下張角（Arc Pitch Angle）：繞角色右手軸畫一條垂直的弧線
            float pitch = step.arcPitchAngle > 0f ? step.arcPitchAngle : 90f;   // 跟 MeleeHitbox 一樣，0 = 預設 90
            Vector3 low = Quaternion.AngleAxis(pitch * 0.5f, model.right) * fwd;
            Vector3 high = Quaternion.AngleAxis(-pitch * 0.5f, model.right) * fwd;
            Handles.color = new Color(1f, 0.75f, 0.3f, 0.9f);
            Handles.DrawWireArc(o, model.right, high, pitch, step.arcRange);
            Handles.DrawLine(o, o + high * step.arcRange);
            Handles.DrawLine(o, o + low * step.arcRange);
        }

        if (_preview == null) return;
        UpdatePreviewPose(step);

        Transform t = _preview.transform;
        Vector3 pos = t.position;
        Quaternion rot = t.rotation;
        float size = Mathf.Abs(t.localScale.y);

        EditorGUI.BeginChangeCheck();
        switch (Tools.current)
        {
            case Tool.Rotate: rot = Handles.RotationHandle(rot, pos); break;
            case Tool.Scale:
                size = Handles.ScaleValueHandle(size, pos, rot, HandleUtility.GetHandleSize(pos) * 1.2f, Handles.CubeHandleCap, 0f);
                break;
            default: pos = Handles.PositionHandle(pos, rot); break;
        }
        if (!EditorGUI.EndChangeCheck()) return;

        Undo.RecordObject(library, "Edit Slash VFX");

        Vector3 lp = model.InverseTransformPoint(pos);
        Quaternion lq = Quaternion.Inverse(model.rotation) * rot;
        bool editRight = rightHand && step.slashRightHandOverride;

        if (rightHand && !editRight)
        {
            // 右手是左手的鏡像 → 反鏡像回左手再寫入
            lp.x = -lp.x;
            lq = MeleeSlashVfx.MirrorX(lq);
        }

        if (Tools.current == Tool.Rotate)
        {
            if (editRight) step.slashRotationRight = lq.eulerAngles; else step.slashRotation = lq.eulerAngles;
        }
        else if (Tools.current == Tool.Scale)
        {
            step.slashScale = step.slashScaleMode == MeleeSlashScaleMode.ArcRange
                ? size * _refRadius / Mathf.Max(0.01f, step.arcRange)
                : size;
        }
        else
        {
            if (editRight) step.slashOffsetRight = lp; else step.slashOffset = lp;
        }

        EditorUtility.SetDirty(library);
        Repaint();
    }
}
#endif
