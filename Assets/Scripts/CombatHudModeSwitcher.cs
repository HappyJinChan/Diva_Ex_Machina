using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 戰鬥 HUD 在「一般戰鬥」和「迫擊砲鎖定模式」之間切換。掛在 Combat UI 上。
///
///   · Combat Only：只在一般戰鬥顯示（Weapon HUD：彈藥條、準星…）。
///   · Lock On Only：只在鎖定模式顯示（Mortar HUD：邊角框線…）。
///   · HUD Frame：貼邊元素（AP、小地圖…）的容器，錨點要是 Stretch（撐滿）。
///     切換時把它的四邊內縮從 Combat Insets 過渡到 Lock On Insets，裡面的元素一起往中間移。
///
///   · Lens Volume：鎖定模式的「鏡片」後處理（Vignette、Post Exposure…）。一個獨立的 Global Volume，
///     Priority 比主要的 Global Volume 高；這裡只控制它的 Weight（0 → 1，跟版面同步過渡）。
///     只有在它的 Profile 裡勾選覆寫的項目會蓋過去，其他照原本的設定。
///     不直接改主要的 Volume Profile，所以不會跟 SpeedMotionBlur（突擊推進的 Vignette）互相搶。
///
/// 狀態讀 SupplyManager.IsMortarTargeting（每幀看一個 bool，不用管事件的訂閱時機）。
///
/// 排版：在 Scene 裡把 HUD Frame 拉到想要的大小，元件右上角 ⋮ →
///   「Capture Current As Lock On」/「Capture Current As Combat」記下來；
///   「Preview Combat」/「Preview Lock On」不用進 Play Mode 就能切換看看。
/// </summary>
public class CombatHudModeSwitcher : MonoBehaviour
{
    [SerializeField] private SupplyManager supplyManager;

    [Header("Groups")]
    [Tooltip("只在一般戰鬥顯示（例如 Weapon HUD、準星）。")]
    [SerializeField] private GameObject[] combatOnly;

    [Tooltip("只在鎖定模式顯示（例如 Mortar HUD）。")]
    [SerializeField] private GameObject[] lockOnOnly;

    [Header("HUD Frame")]
    [Tooltip("貼邊元素的容器（錨點 Stretch）。留空 = 不調整版面。")]
    [SerializeField] private RectTransform hudFrame;

    [Tooltip("一般戰鬥時的內縮（像素）：x = 左、y = 右、z = 上、w = 下。")]
    [SerializeField] private Vector4 combatInsets = Vector4.zero;

    [Tooltip("鎖定模式時的內縮（像素）：x = 左、y = 右、z = 上、w = 下。")]
    [SerializeField] private Vector4 lockOnInsets = new Vector4(120f, 120f, 40f, 40f);

    [Header("Lens (Post Processing)")]
    [Tooltip("鎖定模式的鏡片效果 Volume（Global、Priority 比主 Volume 高、Weight 設 0）。留空 = 不使用。")]
    [SerializeField] private Volume lensVolume;

    [Tooltip("版面 / 鏡片的過渡時間（秒）。0 = 瞬間切換。")]
    [SerializeField, Min(0f)] private float transitionTime = 0.15f;

    private bool _lockOn;
    private bool _initialized;
    private float _blend;   // 0 = 一般戰鬥，1 = 鎖定模式

    private void OnEnable()
    {
        // 重新啟用（例如從裝備介面回來）時直接套用目前狀態，不播過渡
        _initialized = false;
    }

    private void Update()
    {
        if (supplyManager == null) supplyManager = SupplyManager.Instance;
        bool lockOn = supplyManager != null && supplyManager.IsMortarTargeting;

        if (!_initialized || lockOn != _lockOn)
        {
            _lockOn = lockOn;
            SetGroups(lockOn);
            if (!_initialized) _blend = lockOn ? 1f : 0f;
            _initialized = true;
        }

        float target = _lockOn ? 1f : 0f;
        _blend = transitionTime <= 0f
            ? target
            : Mathf.MoveTowards(_blend, target, Time.unscaledDeltaTime / transitionTime);

        ApplyFrame(_blend);
    }

    private void SetGroups(bool lockOn)
    {
        if (combatOnly != null)
            foreach (GameObject go in combatOnly)
                if (go != null && go.activeSelf == lockOn) go.SetActive(!lockOn);

        if (lockOnOnly != null)
            foreach (GameObject go in lockOnOnly)
                if (go != null && go.activeSelf != lockOn) go.SetActive(lockOn);
    }

    private void ApplyFrame(float blend)
    {
        // 平滑一點的曲線（開頭結尾慢）
        float s = blend * blend * (3f - 2f * blend);

        if (lensVolume != null) lensVolume.weight = s;

        if (hudFrame == null) return;
        Vector4 i = Vector4.Lerp(combatInsets, lockOnInsets, s);

        hudFrame.offsetMin = new Vector2(i.x, i.w);     // 左、下
        hudFrame.offsetMax = new Vector2(-i.y, -i.z);   // 右、上
    }

    // ── 編輯器排版工具 ────────────────────────────────────────

    [ContextMenu("Preview Combat")]
    private void PreviewCombat() => Preview(false);

    [ContextMenu("Preview Lock On")]
    private void PreviewLockOn() => Preview(true);

    private void Preview(bool lockOn)
    {
#if UNITY_EDITOR
        var objs = new System.Collections.Generic.List<Object> { this };
        if (hudFrame != null) objs.Add(hudFrame);
        if (lensVolume != null) objs.Add(lensVolume);
        if (combatOnly != null) foreach (var go in combatOnly) if (go != null) objs.Add(go);
        if (lockOnOnly != null) foreach (var go in lockOnOnly) if (go != null) objs.Add(go);
        UnityEditor.Undo.RecordObjects(objs.ToArray(), lockOn ? "Preview Lock On HUD" : "Preview Combat HUD");
#endif
        SetGroups(lockOn);
        ApplyFrame(lockOn ? 1f : 0f);
    }

    [ContextMenu("Capture Current As Combat")]
    private void CaptureCombat() => Capture(ref combatInsets);

    [ContextMenu("Capture Current As Lock On")]
    private void CaptureLockOn() => Capture(ref lockOnInsets);

    private void Capture(ref Vector4 insets)
    {
        if (hudFrame == null) return;
#if UNITY_EDITOR
        UnityEditor.Undo.RecordObject(this, "Capture HUD Insets");
#endif
        insets = new Vector4(hudFrame.offsetMin.x, -hudFrame.offsetMax.x, -hudFrame.offsetMax.y, hudFrame.offsetMin.y);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }
}
