using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ColorPicker : MonoBehaviour
{
    public GameObject targetGameObject;

    // When set, ColorPicker writes colors back to this instance.
    // (InventoryManager already uses this pattern.)
    public ItemInstance targetItemInstance;

    [Header("Effect (Optional)")]
    [Tooltip("If the target is a VFX prefab (ParticleSystems), ColorPicker will control this controller instead of materials.")]
    public EffectColorController targetEffectColorController;

    public List<Material> targetMaterials = new List<Material>();
    public int currentMaterialIndex = -1;
    public int currentTextureIndex = -1;

    [Header("UI Elements")]
    public RawImage hueBar;
    public RawImage colorArea;
    public RectTransform hueCursor;
    public RectTransform colorCursor;

    [Header("UI Button Prefab")]
    public GameObject buttonPrefab;
    public Transform colorDetailButtonParent;
    public ToggleGroup toggleGroup;
    public Color normalColor = Color.white;
    public Color selectedColor = Color.white;

    [Header("Texture Settings")]
    public int hueTextureWidth = 256;
    public int hueTextureHeight = 16;
    public int colorTextureWidth = 256;
    public int colorTextureHeight = 256;

    private Texture2D hueTexture;
    private Texture2D colorTexture;

    private bool draggingHueBar = false;
    private bool draggingColorArea = false;
    private float currentHue = 0f; // 0..1

    [Header("Layer Highlight（點 1 / 2 / 3 時，那一層閃一下）")]
    [SerializeField] private bool highlightOnSelect = true;

    [Tooltip("整段閃光的時間（秒）。")]
    [SerializeField, Min(0.05f)] private float highlightDuration = 0.6f;

    [Tooltip("閃幾下。")]
    [SerializeField, Min(1)] private int highlightBlinks = 2;

    [Tooltip("最亮時的強度（0～1）。")]
    [SerializeField, Range(0f, 1f)] private float highlightStrength = 0.85f;

    [Tooltip("那一層原本比較暗的地方，往這個顏色閃。")]
    [SerializeField] private Color highlightLight = Color.white;

    [Tooltip("那一層原本比較亮的地方，往這個顏色閃。")]
    [SerializeField] private Color highlightDark = new Color(0.05f, 0.05f, 0.05f, 1f);

    private bool HasEffectTarget => targetEffectColorController != null;
    private LayeredToonMaterialController targetLayeredToonController;

    private Coroutine _highlightRoutine;
    private LayeredToonMaterialController _highlightTarget;   // 正在閃的那一件（換選別件時也要能清掉它）
    private bool _suppressHighlight;                         // 自動選第一層時不閃

    private void Awake()
    {
        hueTexture = new Texture2D(hueTextureWidth, hueTextureHeight);
        hueTexture.wrapMode = TextureWrapMode.Clamp;
        GenerateHueTexture();
        hueTexture.Apply();
        if (hueBar != null) hueBar.texture = hueTexture;

        colorTexture = new Texture2D(colorTextureWidth, colorTextureHeight);
        colorTexture.wrapMode = TextureWrapMode.Clamp;
        GenerateColorTexture();
        colorTexture.Apply();
        if (colorArea != null) colorArea.texture = colorTexture;

        UpdateHexColorFromCursor();
        UpdateHueCursor(); // initialize cursor + regenerate color texture once
    }

    private void Update()
    {
        if (Input.GetMouseButtonDown(0))
        {
            if (hueBar != null && IsPointerOverRect(hueBar.rectTransform)) draggingHueBar = true;
            else if (colorArea != null && IsPointerOverRect(colorArea.rectTransform)) draggingColorArea = true;
        }

        if (Input.GetMouseButton(0))
        {
            if (draggingHueBar) UpdateHueCursor();
            if (draggingColorArea) UpdateColorCursor();
        }

        if (Input.GetMouseButtonUp(0))
        {
            draggingHueBar = false;
            draggingColorArea = false;
        }
    }

    // -----------------------------
    // Public API
    // -----------------------------
    public void CreateButtons()
    {
        if (targetGameObject == null) return;

        ClearnButton();

        // Effect target: one toggle per color entry in EffectColorController.colors
        if (ResolveEffectTarget())
        {
            // Ensure cache exists
            if (targetEffectColorController.colors == null)
                targetEffectColorController.colors = new List<Color>();

            if (targetEffectColorController.colors.Count == 0)
                targetEffectColorController.CacheColorsFromGroups();

            int count = targetEffectColorController.colors.Count;
            for (int i = 0; i < count; i++)
            {
                var button = Instantiate(buttonPrefab, colorDetailButtonParent);
                var label = button.transform.Find("Detail Index")?.GetComponent<TMPro.TMP_Text>();
                if (label) label.text = (i + 1).ToString();

                var t = button.GetComponent<Toggle>();
                if (t == null) continue;

                int colorIndex = i;
                t.onValueChanged.AddListener(isOn =>
                {
                    if (isOn)
                    {
                        SetToggleSelectedVisual(t, true);
                        SelectDetalPart(0, colorIndex);

                        var colors = targetEffectColorController.colors;
                        Color c = (colors != null && colorIndex >= 0 && colorIndex < colors.Count) ? colors[colorIndex] : Color.white;
                        CursorToColor(c);
                    }
                    else
                    {
                        SetToggleSelectedVisual(t, false);
                    }
                });

                t.group = toggleGroup;
            }

            AutoSelectFirstToggle();
            return;
        }

        // Material target: build buttons based on shader type
        if (targetMaterials == null) targetMaterials = new List<Material>();
        for (int matIndex = 0; matIndex < targetMaterials.Count; matIndex++)
        {
            var mat = targetMaterials[matIndex];
            if (mat == null) continue;

            string shaderName = mat.shader != null ? mat.shader.name : string.Empty;
            int layerCount = GetLayerCountFromShaderName(shaderName);

            for (int texIndex = 0; texIndex < layerCount; texIndex++)
            {
                var button = Instantiate(buttonPrefab, colorDetailButtonParent);
                var label = button.transform.Find("Detail Index")?.GetComponent<TMPro.TMP_Text>();
                if (label) label.text = (matIndex + texIndex + 1).ToString();

                var t = button.GetComponent<Toggle>();
                if (t == null) continue;

                int capturedMatIndex = matIndex;
                int capturedTexIndex = texIndex;

                t.onValueChanged.AddListener(isOn =>
                {
                    if (isOn)
                    {
                        SetToggleSelectedVisual(t, true);
                        SelectDetalPart(capturedMatIndex, capturedTexIndex);

                        string propName = capturedTexIndex == 0 ? "_BaseColor" : $"_Layer{capturedTexIndex}Color";
                        Color c = Color.white;
                        if (targetMaterials != null &&
                            capturedMatIndex >= 0 && capturedMatIndex < targetMaterials.Count &&
                            targetMaterials[capturedMatIndex] != null &&
                            targetMaterials[capturedMatIndex].HasProperty(propName))
                        {
                            c = targetMaterials[capturedMatIndex].GetColor(propName);
                        }

                        CursorToColor(c);
                        StartLayerHighlight(capturedTexIndex);
                    }
                    else
                    {
                        SetToggleSelectedVisual(t, false);
                    }
                });

                t.group = toggleGroup;
            }
        }

        AutoSelectFirstToggle();
    }

    public void ClearnButton()
    {
        if (colorDetailButtonParent != null)
        {
            for (int i = colorDetailButtonParent.childCount - 1; i >= 0; i--)
                Destroy(colorDetailButtonParent.GetChild(i).gameObject);
        }

        if (toggleGroup != null)
            toggleGroup.SetAllTogglesOff();
    }

    public void SelectDetalPart(int materialIndex, int textureIndex)
    {
        currentMaterialIndex = materialIndex;
        currentTextureIndex = textureIndex;
    }

    // Call this after setting targetGameObject.
    // For VFX: it will discover EffectColorController automatically.
    public void AddTargetMaterialsToList()
    {
        StopLayerHighlight();   // 換目標前，先把上一件的高亮清掉

        if (targetMaterials == null) targetMaterials = new List<Material>();
        targetMaterials.Clear();
        targetLayeredToonController = null;

        // If this is an effect prefab, we don't use materials.
        if (ResolveEffectTarget())
            return;

        // Baked UTS3 target:
        // edit the hidden runtime Mix material, not the UTS3 display material.
        targetLayeredToonController = LayeredToonMaterialController.FindFor(targetGameObject);
        if (targetLayeredToonController != null)
        {
            targetLayeredToonController.EnsureInitialized();

            var sourceMix = targetLayeredToonController.SourceMixMaterial;
            if (sourceMix != null)
            {
                targetMaterials.Add(sourceMix);
                return;
            }
        }

        // Helper: add ONLY element 0
        void AddElement0(Renderer renderer)
        {
            if (renderer == null) return;

            // Use .materials (not .sharedMaterials) to keep the same behavior as your current code:
            // modifying color won't permanently change the asset material.
            var mats = renderer.materials;
            if (mats != null && mats.Length > 0 && mats[0] != null)
                targetMaterials.Add(mats[0]);
        }

        var smr = targetGameObject != null ? targetGameObject.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
        if (smr != null) { AddElement0(smr); return; }

        var mr = targetGameObject != null ? targetGameObject.GetComponentInChildren<MeshRenderer>(true) : null;
        if (mr != null) { AddElement0(mr); return; }

        // Fallback: any renderer
        var r = targetGameObject != null ? targetGameObject.GetComponentInChildren<Renderer>(true) : null;
        if (r != null) AddElement0(r);
    }


    public Color UpdateHexColorFromCursor()
    {
        if (colorArea == null || colorCursor == null) return Color.white;

        Rect rect = colorArea.rectTransform.rect;
        Vector2 pos = colorCursor.anchoredPosition;

        float saturation = (pos.x - rect.xMin) / rect.width;
        float brightness = (pos.y - rect.yMin) / rect.height;
        return Color.HSVToRGB(currentHue, saturation, brightness);
    }

    public void CursorToColor(Color targetColor)
    {
        if (hueBar == null || hueCursor == null || colorArea == null || colorCursor == null) return;

        Color.RGBToHSV(targetColor, out float h, out float s, out float v);
        currentHue = h;

        Rect hueRect = hueBar.rectTransform.rect;
        float hueX = hueRect.xMin + h * hueRect.width;
        Vector2 hueCursorPos = hueCursor.anchoredPosition;
        hueCursorPos.x = hueX;
        hueCursor.anchoredPosition = hueCursorPos;

        GenerateColorTexture();
        colorTexture.Apply();

        Rect colorRect = colorArea.rectTransform.rect;
        float colorX = colorRect.xMin + s * colorRect.width;
        float colorY = colorRect.yMin + v * colorRect.height;
        colorCursor.anchoredPosition = new Vector2(colorX, colorY);
    }

    public void ChangeTargetMaterialColor(Color color, int materialIndex, int textureIndex)
    {
        ApplyColorToCurrentTarget(color, materialIndex, textureIndex);
    }

    public void ApplyCurrentColorToImage(Image buttonImage)
    {
        if (buttonImage == null)
        {
            Debug.LogWarning("ColorPicker.ApplyCurrentColorToImage: buttonImage is null.");
            return;
        }

        Color currentColor = UpdateHexColorFromCursor();
        buttonImage.color = currentColor;

        var btn = buttonImage.GetComponent<AdvancedButton>();
        if (btn != null)
        {
            ColorBlock cb = btn.colors;
            cb.normalColor = currentColor;
            cb.highlightedColor = currentColor;
            cb.pressedColor = currentColor;
            cb.selectedColor = currentColor;
            btn.colors = cb;
        }
    }

    public void SetCurrentColorFromImage(Image buttonImage)
    {
        if (buttonImage == null)
        {
            Debug.LogWarning("ColorPicker.SetCurrentColorFromImage: buttonImage is null.");
            return;
        }

        CursorToColor(buttonImage.color);
        ApplyColorToCurrentTarget(UpdateHexColorFromCursor(), currentMaterialIndex, currentTextureIndex);
    }

    // -----------------------------
    // Internal: drag logic
    // -----------------------------
    private bool IsPointerOverRect(RectTransform rectTransform)
    {
        if (rectTransform == null) return false;

        Vector2 localMousePos;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            rectTransform, Input.mousePosition, null, out localMousePos);
        return rectTransform.rect.Contains(localMousePos);
    }

    private void UpdateHueCursor()
    {
        if (hueBar == null || hueCursor == null) return;

        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            hueBar.rectTransform, Input.mousePosition, null, out localPoint);
        Rect rect = hueBar.rectTransform.rect;

        float clampedX = Mathf.Clamp(localPoint.x, rect.xMin, rect.xMax);

        Vector2 cursorPos = hueCursor.anchoredPosition;
        cursorPos.x = clampedX;
        hueCursor.anchoredPosition = cursorPos;

        currentHue = (clampedX - rect.xMin) / rect.width;

        GenerateColorTexture();
        colorTexture.Apply();

        ApplyColorToCurrentTarget(UpdateHexColorFromCursor(), currentMaterialIndex, currentTextureIndex);
    }

    private void UpdateColorCursor()
    {
        if (colorArea == null || colorCursor == null) return;

        Vector2 localPoint;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(
            colorArea.rectTransform, Input.mousePosition, null, out localPoint);
        Rect rect = colorArea.rectTransform.rect;

        float clampedX = Mathf.Clamp(localPoint.x, rect.xMin, rect.xMax);
        float clampedY = Mathf.Clamp(localPoint.y, rect.yMin, rect.yMax);
        colorCursor.anchoredPosition = new Vector2(clampedX, clampedY);

        ApplyColorToCurrentTarget(UpdateHexColorFromCursor(), currentMaterialIndex, currentTextureIndex);
    }

    private void GenerateHueTexture()
    {
        for (int x = 0; x < hueTextureWidth; x++)
        {
            float h = (float)x / (hueTextureWidth - 1);
            Color col = Color.HSVToRGB(h, 1f, 1f);
            for (int y = 0; y < hueTextureHeight; y++)
                hueTexture.SetPixel(x, y, col);
        }
    }

    private void GenerateColorTexture()
    {
        for (int x = 0; x < colorTextureWidth; x++)
        {
            for (int y = 0; y < colorTextureHeight; y++)
            {
                float saturation = (float)x / (colorTextureWidth - 1);
                float brightness = (float)y / (colorTextureHeight - 1);
                Color col = Color.HSVToRGB(currentHue, saturation, brightness);
                colorTexture.SetPixel(x, y, col);
            }
        }
    }

    // -----------------------------
    // Internal: apply + persist
    // -----------------------------
    private void ApplyColorToCurrentTarget(Color color, int materialIndex, int textureIndex)
    {
        if (textureIndex < 0) return;

        // Effect first
        if (ResolveEffectTarget())
        {
            ApplyEffectColor(color, textureIndex);
            return;
        }

        // Materials
        if (targetMaterials == null || materialIndex < 0 || materialIndex >= targetMaterials.Count) return;

        var mat = targetMaterials[materialIndex];
        if (mat == null) return;

        // 開始調色就停止閃光：玩家要看到真實的顏色
        if (_highlightTarget != null) StopLayerHighlight();

        string propName = textureIndex == 0 ? "_BaseColor" : $"_Layer{textureIndex}Color";
        if (mat.HasProperty(propName))
            mat.SetColor(propName, color);
        else
            Debug.LogWarning($"{mat.name} missing property: {propName}");

        WriteColorBackToItemInstance(color, materialIndex, textureIndex);

        // Rebuild the baked albedo and push it into the untouched UTS3 material.
        if (targetLayeredToonController != null)
            targetLayeredToonController.Rebake();
    }

    private void ApplyEffectColor(Color color, int colorIndex)
    {
        if (targetEffectColorController == null) return;

        if (targetEffectColorController.colors == null)
            targetEffectColorController.colors = new List<Color>();

        while (targetEffectColorController.colors.Count <= colorIndex)
            targetEffectColorController.colors.Add(Color.white);

        targetEffectColorController.colors[colorIndex] = color;

        // Apply to particle systems
        targetEffectColorController.ApplyFromColorsList();

        // Persist
        WriteColorBackToItemInstance(color, 0, colorIndex);
    }

    private void WriteColorBackToItemInstance(Color color, int materialIndex, int textureIndex)
    {
        if (textureIndex < 0) return;
        if (targetItemInstance == null) return;

        List<Color> colorsList = null;
        string shaderName = null;

        if (targetItemInstance is ArmorInstance ai)
        {
            colorsList = ai.colors;
            shaderName = ai.shaderName;
        }
        else if (targetItemInstance is RangeWeaponInstance rwi)
        {
            colorsList = rwi.colors;
            shaderName = rwi.shaderName;
        }
        else if (targetItemInstance is MeleeWeaponInstance mwi)
        {
            colorsList = mwi.colors;
            shaderName = mwi.shaderName;
        }
        else if (targetItemInstance is ShoulderWeaponInstance swi)
        {
            colorsList = swi.colors;
            shaderName = swi.shaderName;
        }
        else if (targetItemInstance is PartInstance pi)
        {
            colorsList = pi.colors;
            shaderName = pi.shaderName;
        }
        else
        {
            return;
        }

        if (colorsList == null) colorsList = new List<Color>();

        // For material targets, try to fill shaderName when missing.
        if (!HasEffectTarget && string.IsNullOrEmpty(shaderName))
        {
            if (materialIndex >= 0 && targetMaterials != null &&
                materialIndex < targetMaterials.Count && targetMaterials[materialIndex] != null)
            {
                shaderName = targetMaterials[materialIndex].shader.name;

                if (targetItemInstance is ArmorInstance ai2) ai2.shaderName = shaderName;
                else if (targetItemInstance is RangeWeaponInstance rwi2) rwi2.shaderName = shaderName;
                else if (targetItemInstance is MeleeWeaponInstance mwi2) mwi2.shaderName = shaderName;
                else if (targetItemInstance is ShoulderWeaponInstance swi2) swi2.shaderName = shaderName;
                else if (targetItemInstance is PartInstance pi2) pi2.shaderName = shaderName;
            }
        }

        // Required length:
        // - Material shaders: follow Mix3/4/5
        // - Effect: store by index (textureIndex == colorIndex)
        int requiredCount = 1;
        if (HasEffectTarget)
        {
            requiredCount = Mathf.Max(1, textureIndex + 1);
        }
        else if (!string.IsNullOrEmpty(shaderName))
        {
            if (shaderName.Contains("Mix 5")) requiredCount = 5;
            else if (shaderName.Contains("Mix 4")) requiredCount = 4;
            else if (shaderName.Contains("Mix 3")) requiredCount = 3;
            else requiredCount = Mathf.Max(requiredCount, textureIndex + 1);
        }
        else
        {
            requiredCount = Mathf.Max(requiredCount, textureIndex + 1);
        }

        while (colorsList.Count < requiredCount) colorsList.Add(Color.white);
        while (colorsList.Count <= textureIndex) colorsList.Add(Color.white);

        colorsList[textureIndex] = color;

        if (targetItemInstance is ArmorInstance ai3) ai3.colors = colorsList;
        else if (targetItemInstance is RangeWeaponInstance rwi3) rwi3.colors = colorsList;
        else if (targetItemInstance is MeleeWeaponInstance mwi3) mwi3.colors = colorsList;
        else if (targetItemInstance is ShoulderWeaponInstance swi3) swi3.colors = colorsList;
        else if (targetItemInstance is PartInstance pi3) pi3.colors = colorsList;
    }

    // -----------------------------
    // Helpers
    // -----------------------------
    private bool ResolveEffectTarget()
    {
        if (targetGameObject == null)
        {
            targetEffectColorController = null;
            return false;
        }

        // �u��u�ؼХ���v�Ρu����t�v���@ effect �ӷ�
        // �ؼЬO weapon root �ɡG�q�`�ۤv�S�� ParticleSystem / ParticleSystemRenderer -> false
        bool targetIsParticleObject =
            targetGameObject.GetComponent<ParticleSystem>() != null ||
            targetGameObject.GetComponent<ParticleSystemRenderer>() != null;

        if (!targetIsParticleObject)
        {
            targetEffectColorController = null;
            return false;
        }

        // �u��ۤv�Τ��t�� controller�]���n�� children�^
        targetEffectColorController = targetGameObject.GetComponentInParent<EffectColorController>(true);
        return targetEffectColorController != null;
    }


    private int GetLayerCountFromShaderName(string shaderName)
    {
        if (string.IsNullOrEmpty(shaderName)) return 1;
        if (shaderName.Contains("Mix 5")) return 5;
        if (shaderName.Contains("Mix 4")) return 4;
        if (shaderName.Contains("Mix 3")) return 3;
        return 1;
    }

    private void AutoSelectFirstToggle()
    {
        if (colorDetailButtonParent == null) return;
        if (colorDetailButtonParent.childCount <= 0) return;

        var firstToggle = colorDetailButtonParent.GetChild(0).GetComponent<Toggle>();
        if (firstToggle == null) return;

        // 打開一件裝備時自動選第一層，不算玩家選的 → 不閃
        _suppressHighlight = true;
        try { firstToggle.isOn = true; }
        finally { _suppressHighlight = false; }
    }

    // ═══════════════════════ 選中那一層閃一下 ═══════════════════════

    private void OnDisable()
    {
        // 介面關掉時（協程會被 Unity 停掉）把高亮清掉，不要留在模型上
        StopLayerHighlight();
    }

    private void StartLayerHighlight(int layerIndex)
    {
        StopLayerHighlight();

        if (!highlightOnSelect || _suppressHighlight || !isActiveAndEnabled) return;
        if (targetLayeredToonController == null) return;   // 特效、沒有烘焙的材質：不支援

        _highlightTarget = targetLayeredToonController;
        _highlightRoutine = StartCoroutine(LayerHighlightRoutine(_highlightTarget, layerIndex));
    }

    private IEnumerator LayerHighlightRoutine(LayeredToonMaterialController target, int layerIndex)
    {
        float t = 0f;
        while (t < highlightDuration)
        {
            if (target == null) break;   // 那件裝備被換掉了

            // |sin| 在整段時間裡起伏 highlightBlinks 次：0 → 1 → 0 → 1 → 0
            float phase = t / highlightDuration * highlightBlinks * Mathf.PI;
            target.SetHighlight(layerIndex, Mathf.Abs(Mathf.Sin(phase)) * highlightStrength,
                                highlightLight, highlightDark);

            yield return null;
            t += Time.unscaledDeltaTime;   // 介面開著時遊戲可能暫停，用不受 timeScale 影響的時間
        }

        _highlightRoutine = null;
        StopLayerHighlight();
    }

    private void StopLayerHighlight()
    {
        if (_highlightRoutine != null)
        {
            StopCoroutine(_highlightRoutine);
            _highlightRoutine = null;
        }

        if (_highlightTarget != null) _highlightTarget.ClearHighlight();
        _highlightTarget = null;
    }

    private void SetToggleSelectedVisual(Toggle t, bool isSelected)
    {
        if (t == null) return;

        Color c = isSelected ? selectedColor : normalColor;
        ColorBlock cb = t.colors;
        cb.normalColor = c;
        cb.selectedColor = c;
        cb.highlightedColor = c;
        cb.pressedColor = c;
        t.colors = cb;
    }
}
