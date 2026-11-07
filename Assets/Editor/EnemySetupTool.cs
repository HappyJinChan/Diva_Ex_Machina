#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Tools > Diva Ex Machina > Enemy Setup
///
/// 選一個新敵人的根物件（場景裡或 Prefab 模式裡），按 Setup：
///   1. 補齊元件：Rigidbody、PathFinder、Brain、Movement、Stats、EffectManager、ShipPassenger、StatusEffects
///      （可選：EntityHitFlash、ModularEntityMeleeManager）。已經有的跳過；Brain / Movement 可選子類別。
///   2. 補齊子物件：characterOrientation、GroundPoint（放在碰撞體 / 模型底部）。
///   3. 自動接線：Movement → Rigidbody / Stats / Orientation / Mesh / GroundPoint；Brain → Movement；
///      Stats → EffectManager；EffectManager → 已經存在、名字對得上的 Feedback 子物件。
///   4. 新加的 Rigidbody 套用敵人預設值（質量 50、Interpolate、鎖旋轉、Continuous）；可選設定 Layer / Tag。
///
/// 只「補」不「改」：已經填好的欄位不會被覆蓋。全部可以 Undo。
/// 跟模型骨架有關的東西（近戰 Hitbox、AimPoint、EnemySensor、炮塔、Feedback 內容、LayerMask）不處理，
/// 會列在報告裡提醒。
/// </summary>
public class EnemySetupTool : EditorWindow
{
    [SerializeField] private GameObject target;
    [SerializeField] private int brainTypeIndex;
    [SerializeField] private int movementTypeIndex;
    [SerializeField] private bool addHitFlash = true;
    [SerializeField] private bool addMeleeManager = true;
    [SerializeField] private bool setLayerAndTag = true;
    [SerializeField] private int layer = -1;
    [SerializeField] private string tagName = "Enemy";

    private const string OrientationName = "characterOrientation";
    private const string GroundPointName = "GroundPoint";

    // EffectManager 欄位 → 子物件名字（只連已經存在的）
    private static readonly (string field, string child)[] FeedbackLinks =
    {
        ("damageFeedback", "Damage Feedback"),
        ("criticalDamageFeedback", "Critical Damage Feedback"),
        ("hitFlashFeedback", "Hit Flash Feedback"),
        ("attackWarningFeedback", "Attack Warning Feedback"),
    };

    private Type[] _brainTypes;
    private Type[] _movementTypes;
    private string _report = "";
    private Vector2 _scroll;

    [MenuItem("Tools/Diva Ex Machina/Enemy Setup")]
    private static void Open() => GetWindow<EnemySetupTool>("Enemy Setup");

    private void OnEnable()
    {
        _brainTypes = CollectTypes<ModularEntityBrain>();
        _movementTypes = CollectTypes<ModularEntityMovement>();
        if (layer < 0) layer = Mathf.Max(0, LayerMask.NameToLayer("Enemy"));
        if (target == null) target = Selection.activeGameObject;
    }

    private void OnSelectionChange()
    {
        if (Selection.activeGameObject != null) { target = Selection.activeGameObject; Repaint(); }
    }

    private static Type[] CollectTypes<T>()
    {
        var list = new List<Type> { typeof(T) };
        list.AddRange(TypeCache.GetTypesDerivedFrom<T>().Where(t => !t.IsAbstract).OrderBy(t => t.Name));
        return list.ToArray();
    }

    // ═══════════════════════ GUI ═══════════════════════

    private void OnGUI()
    {
        _scroll = EditorGUILayout.BeginScrollView(_scroll);

        target = (GameObject)EditorGUILayout.ObjectField("Enemy Root", target, typeof(GameObject), true);
        if (target != null && EditorUtility.IsPersistent(target))
            EditorGUILayout.HelpBox("這是 Project 裡的 prefab 資產。請把它拖進場景，或雙擊進入 Prefab 模式再選根物件。", MessageType.Warning);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Components", EditorStyles.boldLabel);
        brainTypeIndex = EditorGUILayout.Popup("Brain", Mathf.Clamp(brainTypeIndex, 0, _brainTypes.Length - 1), _brainTypes.Select(t => t.Name).ToArray());
        movementTypeIndex = EditorGUILayout.Popup("Movement", Mathf.Clamp(movementTypeIndex, 0, _movementTypes.Length - 1), _movementTypes.Select(t => t.Name).ToArray());
        addHitFlash = EditorGUILayout.Toggle(new GUIContent("Entity Hit Flash", "受擊閃光（overlay shader 會自動填 Diva/HitFlashOverlay）"), addHitFlash);
        addMeleeManager = EditorGUILayout.Toggle(new GUIContent("Melee Manager", "ModularEntityMeleeManager。沒有近戰攻擊的敵人可以不勾。"), addMeleeManager);

        EditorGUILayout.Space();
        setLayerAndTag = EditorGUILayout.Toggle("Set Layer & Tag", setLayerAndTag);
        using (new EditorGUI.DisabledScope(!setLayerAndTag))
        {
            layer = EditorGUILayout.LayerField("Layer", layer);
            tagName = EditorGUILayout.TagField("Tag", tagName);
        }

        EditorGUILayout.Space();
        using (new EditorGUI.DisabledScope(target == null || EditorUtility.IsPersistent(target)))
        {
            if (GUILayout.Button("Setup", GUILayout.Height(30)))
                _report = Setup();
        }

        if (!string.IsNullOrEmpty(_report))
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_report, MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    // ═══════════════════════ Setup ═══════════════════════

    private string Setup()
    {
        GameObject go = target;
        var added = new List<string>();
        var wired = new List<string>();
        var todo = new List<string>();

        Undo.SetCurrentGroupName("Enemy Setup");
        int group = Undo.GetCurrentGroup();

        // ── 1. 元件 ──
        bool rbIsNew = go.GetComponent<Rigidbody>() == null;
        Rigidbody rb = Ensure<Rigidbody>(go, added);
        if (rbIsNew)
        {
            rb.mass = 50f;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.constraints = RigidbodyConstraints.FreezeRotation;
            rb.collisionDetectionMode = CollisionDetectionMode.Continuous;
        }

        Ensure<PathFinder>(go, added);
        var brain = (ModularEntityBrain)EnsureOfType(go, typeof(ModularEntityBrain), _brainTypes[brainTypeIndex], added, todo);
        var movement = (ModularEntityMovement)EnsureOfType(go, typeof(ModularEntityMovement), _movementTypes[movementTypeIndex], added, todo);
        var stats = Ensure<ModularEntityStats>(go, added);
        var effects = Ensure<ModularEntityEffectManager>(go, added);
        Ensure<ShipPassenger>(go, added);
        Ensure<EntityStatusEffects>(go, added);
        if (addHitFlash) Ensure<EntityHitFlash>(go, added);
        if (addMeleeManager) Ensure<ModularEntityMeleeManager>(go, added);

        // ── 2. 子物件 ──
        Transform orientation = EnsureChild(go.transform, OrientationName, added);
        bool gpIsNew = go.transform.Find(GroundPointName) == null;
        Transform groundPoint = EnsureChild(go.transform, GroundPointName, added);
        if (gpIsNew)
        {
            float bottom = FindLocalBottom(go);
            groundPoint.localPosition = new Vector3(0f, bottom, 0f);
        }

        Transform mesh = FindMeshRoot(go.transform);
        if (mesh == null) todo.Add("找不到模型（子物件裡沒有 Animator）→ Movement 的 Entity Mesh 要手動指定");

        // ── 3. 接線（只填空的欄位） ──
        var soMove = new SerializedObject(movement);
        SetIfEmpty(soMove, "entityRigidbody", rb, wired, "Movement.Entity Rigidbody");
        SetIfEmpty(soMove, "modularEntityStats", stats, wired, "Movement.Modular Entity Stats");
        SetIfEmpty(soMove, "entityOrientation", orientation, wired, "Movement.Entity Orientation");
        SetIfEmpty(soMove, "entityMesh", mesh, wired, "Movement.Entity Mesh");
        SetIfEmpty(soMove, "groundPoint", groundPoint, wired, "Movement.Ground Point");
        soMove.ApplyModifiedProperties();

        var soBrain = new SerializedObject(brain);
        SetIfEmpty(soBrain, "modularEntityMovement", movement, wired, "Brain.Modular Entity Movement");
        soBrain.ApplyModifiedProperties();

        var soStats = new SerializedObject(stats);
        SetIfEmpty(soStats, "modularEntityEffectManager", effects, wired, "Stats.Modular Entity Effect Manager");
        soStats.ApplyModifiedProperties();

        var soFx = new SerializedObject(effects);
        foreach (var (field, child) in FeedbackLinks)
        {
            Transform t = go.transform.Find(child);
            var player = t != null ? t.GetComponent<MoreMountains.Feedbacks.MMF_Player>() : null;
            if (player != null) SetIfEmpty(soFx, field, player, wired, $"EffectManager.{field}");
            else if (soFx.FindProperty(field)?.objectReferenceValue == null) todo.Add($"EffectManager.{field}：沒有 Feedback（沒有就不會播，可以留空）");
        }
        soFx.ApplyModifiedProperties();

        if (addHitFlash)
        {
            var flash = go.GetComponent<EntityHitFlash>();
            var soFlash = new SerializedObject(flash);
            Shader s = Shader.Find("Diva/HitFlashOverlay");
            if (s != null) SetIfEmpty(soFlash, "overlayShader", s, wired, "HitFlash.Overlay Shader");
            else todo.Add("找不到 Shader 'Diva/HitFlashOverlay' → EntityHitFlash 的 Overlay Shader 要手動指定");
            soFlash.ApplyModifiedProperties();
        }

        // ── 4. Layer / Tag ──
        if (setLayerAndTag)
        {
            if (go.layer != layer) { Undo.RecordObject(go, "Set Layer"); go.layer = layer; wired.Add($"Layer = {LayerMask.LayerToName(layer)}"); }
            if (!string.IsNullOrEmpty(tagName) && !go.CompareTag(tagName)) { Undo.RecordObject(go, "Set Tag"); go.tag = tagName; wired.Add($"Tag = {tagName}"); }
        }

        Undo.CollapseUndoOperations(group);
        EditorUtility.SetDirty(go);

        // ── 報告 ──
        todo.Add("LayerMask（Movement.What Is Ground、PathFinder、ShipPassenger）是腳本預設值，請檢查");
        todo.Add("碰撞體、近戰 Hitbox、AimPoint、EnemySensor（Enemy Detect Range）、炮塔要自己加");
        todo.Add("Stats 的數值（血量、速度、防禦）、Death Effect");

        var sb = new StringBuilder();
        sb.AppendLine($"【{go.name}】");
        sb.AppendLine(added.Count > 0 ? "新增：" + string.Join("、", added) : "新增：（全部已存在）");
        sb.AppendLine(wired.Count > 0 ? "接線：\n  · " + string.Join("\n  · ", wired) : "接線：（全部已填好）");
        sb.Append("手動檢查：\n  · " + string.Join("\n  · ", todo));
        Debug.Log("[Enemy Setup] " + sb, go);
        return sb.ToString();
    }

    // ═══════════════════════ 小工具 ═══════════════════════

    private static T Ensure<T>(GameObject go, List<string> added) where T : Component
    {
        T c = go.GetComponent<T>();
        if (c != null) return c;
        c = Undo.AddComponent<T>(go);
        added.Add(typeof(T).Name);
        return c;
    }

    /// <summary>已經有這個基底類別（或任何子類別）就沿用；沒有才加選擇的類型。</summary>
    private static Component EnsureOfType(GameObject go, Type baseType, Type wanted, List<string> added, List<string> todo)
    {
        Component c = go.GetComponent(baseType);
        if (c != null)
        {
            if (c.GetType() != wanted)
                todo.Add($"已經有 {c.GetType().Name}，沒有換成 {wanted.Name}（要換請先自己移除）");
            return c;
        }
        c = Undo.AddComponent(go, wanted);
        added.Add(wanted.Name);
        return c;
    }

    private static Transform EnsureChild(Transform parent, string name, List<string> added)
    {
        Transform t = parent.Find(name);
        if (t != null) return t;

        var child = new GameObject(name);
        Undo.RegisterCreatedObjectUndo(child, "Create " + name);
        Undo.SetTransformParent(child.transform, parent, "Parent " + name);
        child.transform.localPosition = Vector3.zero;
        child.transform.localRotation = Quaternion.identity;
        child.transform.localScale = Vector3.one;
        child.layer = parent.gameObject.layer;
        added.Add(name);
        return child.transform;
    }

    /// <summary>根物件的直接子物件中，包含 Animator 的那一個（模型）。</summary>
    private static Transform FindMeshRoot(Transform root)
    {
        Animator anim = root.GetComponentInChildren<Animator>(true);
        if (anim == null || anim.transform == root) return null;
        Transform t = anim.transform;
        while (t.parent != null && t.parent != root) t = t.parent;
        return t;
    }

    /// <summary>腳底在根物件座標系的高度：非 Trigger 的碰撞體底部；沒有就用模型的 Renderer；都沒有 = 0。</summary>
    private static float FindLocalBottom(GameObject go)
    {
        Bounds? b = null;
        foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue;
            b = b.HasValue ? Encapsulate(b.Value, c.bounds) : c.bounds;
        }
        if (!b.HasValue)
        {
            foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                b = b.HasValue ? Encapsulate(b.Value, r.bounds) : r.bounds;
        }
        if (!b.HasValue) return 0f;
        return go.transform.InverseTransformPoint(new Vector3(go.transform.position.x, b.Value.min.y, go.transform.position.z)).y;
    }

    private static Bounds Encapsulate(Bounds a, Bounds b) { a.Encapsulate(b); return a; }

    private static void SetIfEmpty(SerializedObject so, string prop, UnityEngine.Object value, List<string> wired, string label)
    {
        SerializedProperty p = so.FindProperty(prop);
        if (p == null || p.propertyType != SerializedPropertyType.ObjectReference) return;
        if (p.objectReferenceValue != null || value == null) return;
        p.objectReferenceValue = value;
        wired.Add(label);
    }
}
#endif
