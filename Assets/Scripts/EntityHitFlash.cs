using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 受擊時全身閃光。掛在實體的根物件上（Wolf 2 就是最上層那個物件）。
///
/// 對外只有一個 FlashAmount（0～1）。這個元件不管時間：
/// 閃多久、曲線長怎樣，由 MMF 決定 —— 用 MMF_Property 去動畫這個屬性（0 → 1 → 0）。
///
/// 做法：FlashAmount 大於 0 時，把一個疊加材質（Diva/HitFlashOverlay）暫時加到每個 Renderer 的材質陣列最後面；
/// 回到 0 就拿掉。平常不會多一次繪製，場上敵人很多也沒有額外成本。
///
/// 會收集的 Renderer：自己和子物件上的 MeshRenderer、SkinnedMeshRenderer（包括背上的砲塔）。
/// 粒子、拖尾、線條不會被收進來；要排除的模型拖進 Exclude。
///
/// 限制：一個 Renderer 如果有多個子網格（多個材質），多出來的材質只會畫在最後一個子網格上，
/// 所以那種 Renderer 只有最後一塊會閃。啟動時會在 Console 提示是哪些。
/// </summary>
[DisallowMultipleComponent]
public class EntityHitFlash : MonoBehaviour
{
    [Tooltip("疊加用的 Shader（Diva/HitFlashOverlay）。\n" +
             "一定要在這裡指定，不要只靠程式找 —— 沒有被任何東西參照的 Shader 打包時會被剔除。")]
    [SerializeField] private Shader overlayShader;

    [Tooltip("閃光顏色。HDR：強度大於 1 會吃到 Bloom。Alpha 是最大混合比例。")]
    [ColorUsage(true, true)]
    [SerializeField] private Color flashColor = Color.white;

    [Tooltip("這些物件（含子物件）底下的 Renderer 不閃。")]
    [SerializeField] private Transform[] exclude = new Transform[0];

    // MMF_Property 動畫的對象。刻意只做成屬性（沒有公開欄位），
    // 這樣 MMF 一定會走 setter，材質才會跟著更新。
    private float _flashAmount;

    /// <summary>閃光強度 0～1。由 MMF_Property 驅動。</summary>
    public float FlashAmount
    {
        get => _flashAmount;
        set => Apply(value);
    }

    private Renderer[] _renderers = new Renderer[0];
    private Material[][] _baseMaterials = new Material[0][];
    private Material[][] _flashMaterials = new Material[0][];
    private Material _overlay;
    private bool _attached;
    private static readonly int FlashColorId = Shader.PropertyToID("_FlashColor");
    private static readonly int FlashAmountId = Shader.PropertyToID("_FlashAmount");

    // ═══════════════════════ 初始化 ═══════════════════════

    private void Awake()
    {
        if (overlayShader == null)
        {
            overlayShader = Shader.Find("Diva/HitFlashOverlay");
            if (overlayShader != null)
                Debug.LogWarning($"[EntityHitFlash] {name}：Overlay Shader 沒有指定，暫時用 Shader.Find 找到。" +
                                 "請在 Inspector 指定，否則打包後可能找不到。", this);
        }
        if (overlayShader == null)
        {
            Debug.LogError($"[EntityHitFlash] {name}：找不到 Diva/HitFlashOverlay，受擊閃光停用。", this);
            enabled = false;
            return;
        }

        _overlay = new Material(overlayShader) { name = "HitFlashOverlay (Instance)" };
        _overlay.SetColor(FlashColorId, flashColor);
        _overlay.SetFloat(FlashAmountId, 0f);

        CollectRenderers();
    }

    /// <summary>
    /// 重新收集 Renderer。執行中換了模型、加了裝備時呼叫。
    /// （閃光中呼叫會先把疊加層拿掉再收集。）
    /// </summary>
    public void CollectRenderers()
    {
        Detach();

        var list = new List<Renderer>();
        foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
        {
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (IsExcluded(r.transform)) continue;
            list.Add(r);
        }

        _renderers = list.ToArray();
        _baseMaterials = new Material[_renderers.Length][];
        _flashMaterials = new Material[_renderers.Length][];

        for (int i = 0; i < _renderers.Length; i++)
        {
            Material[] baseMats = _renderers[i].sharedMaterials;
            var flashMats = new Material[baseMats.Length + 1];
            baseMats.CopyTo(flashMats, 0);
            flashMats[baseMats.Length] = _overlay;

            _baseMaterials[i] = baseMats;
            _flashMaterials[i] = flashMats;

#if UNITY_EDITOR
            if (baseMats.Length > 1)
                Debug.Log($"[EntityHitFlash] {name}：'{_renderers[i].name}' 有 {baseMats.Length} 個材質，" +
                          "受擊閃光只會出現在最後一個子網格上。", _renderers[i]);
#endif
        }
    }

    private bool IsExcluded(Transform t)
    {
        if (exclude == null) return false;
        foreach (Transform root in exclude)
            if (root != null && t.IsChildOf(root)) return true;
        return false;
    }

    // ═══════════════════════ 套用 ═══════════════════════

    private void Apply(float value)
    {
        _flashAmount = Mathf.Clamp01(value);
        if (_overlay == null) return;

        if (_flashAmount <= 0.0001f)
        {
            _flashAmount = 0f;
            _overlay.SetFloat(FlashAmountId, 0f);
            Detach();
            return;
        }

        _overlay.SetFloat(FlashAmountId, _flashAmount);
        Attach();
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].sharedMaterials = _flashMaterials[i];
    }

    private void Detach()
    {
        if (!_attached) return;
        _attached = false;
        for (int i = 0; i < _renderers.Length; i++)
            if (_renderers[i] != null) _renderers[i].sharedMaterials = _baseMaterials[i];
    }

    // ═══════════════════════ 生命週期 ═══════════════════════

    private void OnDisable()
    {
        // 物件池回收、死亡時歸零：下一次生成不會一出來就是亮的
        _flashAmount = 0f;
        if (_overlay != null) _overlay.SetFloat(FlashAmountId, 0f);
        Detach();
    }

    private void OnDestroy()
    {
        if (_overlay != null) Destroy(_overlay);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        // 執行中在 Inspector 調顏色時即時生效
        if (_overlay != null) _overlay.SetColor(FlashColorId, flashColor);
    }
#endif
}
