using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 裝備介面上的三個套裝分頁（Set 1 / Set 2 / Set 3）。
///
/// 按下第 n 個按鈕 → EquipmentManager.SwitchSet(n)：
///   身上目前的裝備記回目前這套，再穿上第 n 套。之後用原本的背包 UI 換裝，
///   換完的結果就是第 n 套的內容（「正在編輯第幾套」=「正在穿第幾套」）。
///
/// 設定：掛在任何一個 UI 物件上（例如 Character UI BG），
///       Set Buttons 依序拖入 Set 1、Set 2、Set 3 的 Button。
///       按鈕的 On Click 不用另外設定，這個元件會自己綁。
///
/// 目前這套的按鈕會設成不可按（顯示 Button 的 Disabled Color），
/// 如果有指定 Selected Indicators，也會只顯示目前這套的那一個。
/// </summary>
public class EquipmentSetTabs : MonoBehaviour
{
    [Tooltip("依序放 Set 1、Set 2、Set 3 的按鈕。")]
    [SerializeField] private Button[] setButtons = new Button[EquipmentManager.SetCount];

    [Tooltip("（可留空）每個分頁的「選中」標示物件，順序同上。只有目前這套的會顯示。")]
    [SerializeField] private GameObject[] selectedIndicators;

    private bool _hooked;

    private void Awake()
    {
        for (int i = 0; i < setButtons.Length; i++)
        {
            if (setButtons[i] == null) continue;
            int index = i; // closure 要用區域變數
            setButtons[i].onClick.AddListener(() => OnClickSet(index));
        }
    }

    private void OnEnable()
    {
        Hook();
        Refresh();
    }

    private void Start()
    {
        // OnEnable 可能早於 EquipmentManager.Awake，這裡再試一次
        Hook();
        Refresh();
    }

    private void OnDisable()
    {
        if (_hooked && EquipmentManager.Instance != null)
            EquipmentManager.Instance.OnActiveSetChanged -= HandleActiveSetChanged;
        _hooked = false;
    }

    private void Hook()
    {
        if (_hooked || EquipmentManager.Instance == null) return;
        EquipmentManager.Instance.OnActiveSetChanged += HandleActiveSetChanged;
        _hooked = true;
    }

    private void OnClickSet(int index)
    {
        var em = EquipmentManager.Instance;
        if (em == null) return;
        if (index == em.ActiveSetIndex) return;

        SFXManager.Instance?.PlayFeedback("ClickFeedback");
        em.SwitchSet(index); // 完成後觸發 OnActiveSetChanged → Refresh
    }

    private void HandleActiveSetChanged(int setIndex) => Refresh();

    private void Refresh()
    {
        var em = EquipmentManager.Instance;
        int active = em != null ? em.ActiveSetIndex : 0;

        for (int i = 0; i < setButtons.Length; i++)
            if (setButtons[i] != null)
                setButtons[i].interactable = (i != active);

        if (selectedIndicators != null)
            for (int i = 0; i < selectedIndicators.Length; i++)
                if (selectedIndicators[i] != null)
                    selectedIndicators[i].SetActive(i == active);
    }
}
