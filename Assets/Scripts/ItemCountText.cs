using TMPro;
using UnityEngine;

/// <summary>
/// 在 TMP 文字上顯示背包裡某種物品的數量（例如船的建造介面上的 Ultra Mass Alloy）。
/// 掛在要顯示數字的 TextMeshPro 物件上，Item 拖入要顯示的 ItemObject。
///
/// 背包沒有「數量改變」的事件，所以每 Refresh Interval 秒查一次；數字沒變就不重寫文字。
/// </summary>
[RequireComponent(typeof(TMP_Text))]
public class ItemCountText : MonoBehaviour
{
    [SerializeField] private ItemObject item;

    [Tooltip("{0} = 數量。例如「{0}」或「x {0}」。")]
    [SerializeField] private string format = "{0:N0}";

    [Tooltip("多久查一次背包（秒）。不受 timeScale 影響（暫停時打開介面也會更新）。")]
    [SerializeField, Min(0.02f)] private float refreshInterval = 0.2f;

    private TMP_Text _text;
    private int _shown = int.MinValue;
    private float _nextRefresh;

    private void Awake() => _text = GetComponent<TMP_Text>();

    private void OnEnable()
    {
        _shown = int.MinValue;   // 介面打開時馬上更新一次
        _nextRefresh = 0f;
    }

    private void Update()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + refreshInterval;

        InventoryManager inv = InventoryManager.Instance;
        int count = inv != null ? inv.GetItemCount(item) : 0;
        if (count == _shown) return;

        _shown = count;
        _text.text = string.Format(format, count);
    }
}
