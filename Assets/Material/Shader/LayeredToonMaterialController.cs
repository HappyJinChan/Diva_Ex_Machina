using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Keeps the original Mix 3/4/5 material as an albedo recipe,
/// bakes that recipe into a RenderTexture, then feeds the texture to
/// an untouched UTS3 material instance.
///
/// Put this component on the root of each weapon / weapon-part prefab
/// that currently renders with Mix 3, Mix 4 or Mix 5.
/// </summary>
[DisallowMultipleComponent]
public class LayeredToonMaterialController : MonoBehaviour
{
    [Header("Renderer")]
    [SerializeField] private Renderer targetRenderer;

    [Tooltip("Optional. Leave empty to use the Mix material already assigned to targetRenderer.")]
    [SerializeField] private Material sourceMixMaterialOverride;

    [Header("UTS3")]
    [Tooltip("An ORIGINAL UTS3 material used as the template. This script clones it at runtime and never modifies the asset.")]
    [SerializeField] private Material uts3MaterialTemplate;

    [Tooltip("Usually _BaseMap in UTS3.")]
    [SerializeField] private string utsBaseMapProperty = "_BaseMap";

    [Tooltip("Optional compatibility property. _MainTex is also filled when it exists.")]
    [SerializeField] private string utsSecondaryBaseMapProperty = "_MainTex";

    [SerializeField] private bool resetUTSBaseTintToWhite = true;

    [Header("Bake")]
    [Tooltip("0 = use the Base texture resolution. A positive value caps the longest side.")]
    [Min(0)]
    [SerializeField] private int maxBakeSize = 1024;

    [SerializeField] private Shader bakeShader;

    private Material sourceMixMaterial;
    private Material bakerMaterial;
    private Material runtimeUTSMaterial;
    private RenderTexture bakedAlbedo;

    private bool sourceCaptured;
    private bool initialized;
    private bool warnedMissingUTS;
    private bool warnedUnsupportedMix;

    public Renderer TargetRenderer => targetRenderer;

    /// <summary>
    /// Runtime copy of the Mix material. ColorPicker should edit this material.
    /// </summary>
    public Material SourceMixMaterial
    {
        get
        {
            EnsureSourceCaptured();
            return sourceMixMaterial;
        }
    }

    public Material RuntimeUTSMaterial => runtimeUTSMaterial;
    public RenderTexture BakedAlbedo => bakedAlbedo;
    public bool IsInitialized => initialized;


    /// <summary>
    /// Runtime setup path used by armor. Armor has no prefab component, so
    /// EquipmentManager adds this controller to the instantiated
    /// SkinnedMeshRenderer after saved Mix colors are restored.
    /// </summary>
    public void Configure(Renderer renderer, Material utsTemplate, Shader bakerShader = null)
    {
        if (renderer == null)
        {
            Debug.LogWarning($"{name}: Configure received a null Renderer.", this);
            return;
        }

        if (initialized)
        {
            Debug.LogWarning(
                $"{name}: LayeredToonMaterialController is already initialized. " +
                "Runtime reconfiguration was ignored.",
                this);
            return;
        }

        targetRenderer = renderer;
        uts3MaterialTemplate = utsTemplate;

        if (bakerShader != null)
            bakeShader = bakerShader;

        EnsureInitialized();
    }

    private void Start()
    {
        // EquipmentManager restores saved Mix colors immediately after Instantiate.
        // Start happens afterwards, so we capture the restored colors here.
        EnsureInitialized();
    }

    /// <summary>
    /// Finds the controller associated with a clicked root / child.
    /// </summary>
    public static LayeredToonMaterialController FindFor(GameObject go)
    {
        if (go == null) return null;

        var c = go.GetComponent<LayeredToonMaterialController>();
        if (c != null) return c;

        c = go.GetComponentInChildren<LayeredToonMaterialController>(true);
        if (c != null) return c;

        return go.GetComponentInParent<LayeredToonMaterialController>(true);
    }

    /// <summary>
    /// Initializes every baked-toon controller under a newly instantiated weapon.
    /// Useful after EquipmentManager has restored all saved Mix colors.
    /// </summary>
    public static void InitializeAll(GameObject root)
    {
        if (root == null) return;

        var controllers = root.GetComponentsInChildren<LayeredToonMaterialController>(true);
        foreach (var c in controllers)
            if (c != null)
                c.EnsureInitialized();
    }

    public void EnsureInitialized()
    {
        if (initialized) return;

        EnsureSourceCaptured();

        if (sourceMixMaterial == null)
            return;

        if (uts3MaterialTemplate == null)
        {
            if (!warnedMissingUTS)
            {
                Debug.LogWarning(
                    $"{name}: LayeredToonMaterialController has no UTS3 Material Template. " +
                    "The Mix material will remain visible until a UTS3 template is assigned.",
                    this);
                warnedMissingUTS = true;
            }
            return;
        }

        if (bakeShader == null)
            bakeShader = Shader.Find("Hidden/MixAlbedoBaker");

        if (bakeShader == null)
        {
            Debug.LogError(
                $"{name}: Cannot find shader 'Hidden/MixAlbedoBaker'. " +
                "Make sure MixAlbedoBaker.shader is in the project.",
                this);
            return;
        }

        bakerMaterial = new Material(bakeShader)
        {
            name = $"{name}_MixAlbedoBaker_Runtime",
            hideFlags = HideFlags.DontSave
        };

        runtimeUTSMaterial = new Material(uts3MaterialTemplate)
        {
            name = $"{uts3MaterialTemplate.name}_{name}_Runtime",
            hideFlags = HideFlags.DontSave
        };

        CreateOrResizeRenderTexture();
        BakeInternal();

        targetRenderer.material = runtimeUTSMaterial;
        initialized = true;
    }

    /// <summary>
    /// Re-bakes the current Mix material into the UTS3 Base Map.
    /// ColorPicker calls this while the player drags a color.
    /// </summary>
    public void Rebake()
    {
        EnsureInitialized();

        if (!initialized || sourceMixMaterial == null)
            return;

        CreateOrResizeRenderTexture();
        BakeInternal();
    }

    /// <summary>
    /// Optional helper if another system wants to restore ItemInstance.colors directly.
    /// The order is BaseColor, Layer1Color, Layer2Color, Layer3Color, Layer4Color.
    /// </summary>
    public void ApplySavedColors(List<Color> colors, bool rebake = true)
    {
        EnsureSourceCaptured();

        if (sourceMixMaterial == null || colors == null)
            return;

        int count = Mathf.Min(colors.Count, GetColorSlotCount());
        for (int i = 0; i < count; i++)
        {
            string property = GetColorProperty(i);
            if (sourceMixMaterial.HasProperty(property))
                sourceMixMaterial.SetColor(property, colors[i]);
        }

        if (rebake)
            Rebake();
    }

    public int GetColorSlotCount()
    {
        EnsureSourceCaptured();
        if (sourceMixMaterial == null) return 0;

        if (sourceMixMaterial.HasProperty("_Layer4") ||
            sourceMixMaterial.shader.name.Contains("Mix 5"))
            return 5;

        if (sourceMixMaterial.HasProperty("_Layer3") ||
            sourceMixMaterial.shader.name.Contains("Mix 4"))
            return 4;

        if (sourceMixMaterial.HasProperty("_Layer2") ||
            sourceMixMaterial.shader.name.Contains("Mix 3"))
            return 3;

        return 1;
    }

    // ── 上色介面的高亮（ColorPicker 用）─────────────────────
    // 只改烘焙用的材質參數再重烘一次；顏色資料（Mix 材質）完全不動，所以不會被存進去。

    private static readonly int HighlightLayerId = Shader.PropertyToID("_HighlightLayer");
    private static readonly int HighlightAmountId = Shader.PropertyToID("_HighlightAmount");
    private static readonly int HighlightLightId = Shader.PropertyToID("_HighlightLight");
    private static readonly int HighlightDarkId = Shader.PropertyToID("_HighlightDark");

    /// <summary>
    /// 高亮某一層（0 = Base，1～4 = Layer1～4）。amount 0～1。
    /// 底下亮的地方往 dark 拉、暗的地方往 light 拉。
    /// </summary>
    public void SetHighlight(int layerIndex, float amount, Color light, Color dark)
    {
        EnsureInitialized();
        if (!initialized || bakerMaterial == null) return;

        bakerMaterial.SetFloat(HighlightLayerId, layerIndex);
        bakerMaterial.SetFloat(HighlightAmountId, Mathf.Clamp01(amount));
        bakerMaterial.SetColor(HighlightLightId, light);
        bakerMaterial.SetColor(HighlightDarkId, dark);
        Rebake();
    }

    /// <summary>取消高亮，用正常的顏色重烘。</summary>
    public void ClearHighlight()
    {
        if (!initialized || bakerMaterial == null) return;

        bakerMaterial.SetFloat(HighlightLayerId, -1f);
        bakerMaterial.SetFloat(HighlightAmountId, 0f);
        Rebake();
    }

    private void EnsureSourceCaptured()
    {
        if (sourceCaptured) return;
        sourceCaptured = true;

        if (targetRenderer == null)
            targetRenderer = GetComponentInChildren<Renderer>(true);

        if (targetRenderer == null)
        {
            Debug.LogWarning($"{name}: No Renderer found for LayeredToonMaterialController.", this);
            return;
        }

        if (sourceMixMaterialOverride != null)
        {
            sourceMixMaterial = new Material(sourceMixMaterialOverride)
            {
                name = $"{sourceMixMaterialOverride.name}_{name}_MixRuntime",
                hideFlags = HideFlags.DontSave
            };
        }
        else
        {
            // renderer.material gives this object its own runtime material copy.
            // EquipmentManager may already have restored ItemInstance.colors into it.
            sourceMixMaterial = targetRenderer.material;
        }

        if (!LooksLikeMixMaterial(sourceMixMaterial) && !warnedUnsupportedMix)
        {
            Debug.LogWarning(
                $"{name}: Source material '{sourceMixMaterial?.name}' does not look like Mix 3/4/5. " +
                "Expected _Base plus _BaseColor and layer properties.",
                this);
            warnedUnsupportedMix = true;
        }
    }

    private bool LooksLikeMixMaterial(Material mat)
    {
        if (mat == null || mat.shader == null) return false;
        if (!mat.HasProperty("_Base") || !mat.HasProperty("_BaseColor")) return false;

        string shaderName = mat.shader.name;
        return shaderName.Contains("Mix 3") ||
               shaderName.Contains("Mix 4") ||
               shaderName.Contains("Mix 5") ||
               mat.HasProperty("_Layer1");
    }

    private void CreateOrResizeRenderTexture()
    {
        if (sourceMixMaterial == null) return;

        Texture baseTexture = sourceMixMaterial.HasProperty("_Base")
            ? sourceMixMaterial.GetTexture("_Base")
            : null;

        int width = baseTexture != null ? baseTexture.width : 1024;
        int height = baseTexture != null ? baseTexture.height : 1024;

        if (maxBakeSize > 0)
        {
            int longest = Mathf.Max(width, height);
            if (longest > maxBakeSize)
            {
                float scale = maxBakeSize / (float)longest;
                width = Mathf.Max(1, Mathf.RoundToInt(width * scale));
                height = Mathf.Max(1, Mathf.RoundToInt(height * scale));
            }
        }

        if (bakedAlbedo != null &&
            bakedAlbedo.width == width &&
            bakedAlbedo.height == height)
            return;

        ReleaseRenderTexture();

        RenderTextureReadWrite rw =
            QualitySettings.activeColorSpace == ColorSpace.Linear
                ? RenderTextureReadWrite.sRGB
                : RenderTextureReadWrite.Default;

        bakedAlbedo = new RenderTexture(
            width,
            height,
            0,
            RenderTextureFormat.ARGB32,
            rw)
        {
            name = $"{name}_BakedAlbedo",
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Repeat,
            useMipMap = true,
            autoGenerateMips = false,
            hideFlags = HideFlags.DontSave
        };

        bakedAlbedo.Create();
    }

    private void BakeInternal()
    {
        if (sourceMixMaterial == null ||
            bakerMaterial == null ||
            runtimeUTSMaterial == null ||
            bakedAlbedo == null)
            return;

        CopyTexture("_Base");
        CopyTexture("_Layer1");
        CopyTexture("_Layer2");
        CopyTexture("_Layer3");
        CopyTexture("_Layer4");

        CopyColor("_BaseColor", Color.white);
        CopyColor("_Layer1Color", Color.white);
        CopyColor("_Layer2Color", Color.white);
        CopyColor("_Layer3Color", Color.white);
        CopyColor("_Layer4Color", Color.white);

        // Mix 3 = Base + Layer1 + Layer2, so there are two overlay layers.
        // Mix 4 = three overlay layers. Mix 5 = four overlay layers.
        bakerMaterial.SetFloat("_LayerCount", Mathf.Max(0, GetColorSlotCount() - 1));

        Graphics.Blit(Texture2D.whiteTexture, bakedAlbedo, bakerMaterial, 0);

        if (bakedAlbedo.useMipMap)
            bakedAlbedo.GenerateMips();

        ApplyBakedTextureToUTS();
    }

    private void CopyTexture(string property)
    {
        if (!sourceMixMaterial.HasProperty(property) ||
            !bakerMaterial.HasProperty(property))
            return;

        Texture tex = sourceMixMaterial.GetTexture(property);
        bakerMaterial.SetTexture(property, tex);

        bakerMaterial.SetTextureScale(property, sourceMixMaterial.GetTextureScale(property));
        bakerMaterial.SetTextureOffset(property, sourceMixMaterial.GetTextureOffset(property));
    }

    private void CopyColor(string property, Color fallback)
    {
        if (!bakerMaterial.HasProperty(property))
            return;

        Color color = sourceMixMaterial.HasProperty(property)
            ? sourceMixMaterial.GetColor(property)
            : fallback;

        bakerMaterial.SetColor(property, color);
    }

    private void ApplyBakedTextureToUTS()
    {
        if (runtimeUTSMaterial == null || bakedAlbedo == null)
            return;

        bool assigned = false;

        if (!string.IsNullOrEmpty(utsBaseMapProperty) &&
            runtimeUTSMaterial.HasProperty(utsBaseMapProperty))
        {
            runtimeUTSMaterial.SetTexture(utsBaseMapProperty, bakedAlbedo);
            assigned = true;
        }

        if (!string.IsNullOrEmpty(utsSecondaryBaseMapProperty) &&
            runtimeUTSMaterial.HasProperty(utsSecondaryBaseMapProperty))
        {
            runtimeUTSMaterial.SetTexture(utsSecondaryBaseMapProperty, bakedAlbedo);
            assigned = true;
        }

        if (!assigned)
        {
            Debug.LogWarning(
                $"{name}: UTS3 material '{runtimeUTSMaterial.name}' has neither " +
                $"'{utsBaseMapProperty}' nor '{utsSecondaryBaseMapProperty}'. " +
                "Set the correct Base Map property name in LayeredToonMaterialController.",
                this);
        }

        // UTS3 three-color shading:
        // Use the baked albedo for Base, 1st Shade and 2nd Shade.
        // These two float properties correspond to the UTS3 Inspector checkboxes:
        // "Apply to 1st shading map" and "Apply to 2nd shading map".
        if (runtimeUTSMaterial.HasProperty("_1st_ShadeMap"))
            runtimeUTSMaterial.SetTexture("_1st_ShadeMap", bakedAlbedo);

        if (runtimeUTSMaterial.HasProperty("_2nd_ShadeMap"))
            runtimeUTSMaterial.SetTexture("_2nd_ShadeMap", bakedAlbedo);

        if (runtimeUTSMaterial.HasProperty("_Use_BaseAs1st"))
            runtimeUTSMaterial.SetFloat("_Use_BaseAs1st", 1f);

        if (runtimeUTSMaterial.HasProperty("_Use_1stAs2nd"))
            runtimeUTSMaterial.SetFloat("_Use_1stAs2nd", 1f);

        if (resetUTSBaseTintToWhite)
        {
            if (runtimeUTSMaterial.HasProperty("_BaseColor"))
                runtimeUTSMaterial.SetColor("_BaseColor", Color.white);

            if (runtimeUTSMaterial.HasProperty("_Color"))
                runtimeUTSMaterial.SetColor("_Color", Color.white);
        }
    }

    private static string GetColorProperty(int index)
    {
        return index == 0 ? "_BaseColor" : $"_Layer{index}Color";
    }

    private void ReleaseRenderTexture()
    {
        if (bakedAlbedo == null) return;

        if (bakedAlbedo.IsCreated())
            bakedAlbedo.Release();

        DestroyRuntimeObject(bakedAlbedo);
        bakedAlbedo = null;
    }

    private void OnDestroy()
    {
        ReleaseRenderTexture();

        DestroyRuntimeObject(bakerMaterial);
        bakerMaterial = null;

        DestroyRuntimeObject(runtimeUTSMaterial);
        runtimeUTSMaterial = null;

        // sourceMixMaterial came from renderer.material or from our own clone,
        // so it is a runtime material and is safe to destroy with this instance.
        DestroyRuntimeObject(sourceMixMaterial);
        sourceMixMaterial = null;
    }

    private static void DestroyRuntimeObject(Object obj)
    {
        if (obj == null) return;

        if (Application.isPlaying)
            Object.Destroy(obj);
        else
            Object.DestroyImmediate(obj);
    }
}
