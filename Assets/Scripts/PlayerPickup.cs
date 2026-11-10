using System;
using UnityEngine;

/// <summary>
/// 撿掉落物。掛在玩家「有 Rigidbody 的那個物件」上（Unity 會把子物件 Collider 的 Trigger 事件也送到這裡）。
/// 玩家身上要有 Collider（不能是 Trigger），掉落物那邊是 Trigger。
///
/// 掉落物（DropItem）飛到玩家身上、碰到 Trigger 時收進背包，掉落物回到物件池。
/// 掉落物會飛向 Collect Point（玩家位置 + Collect Offset）。
///
/// 收到東西時發出 Collected(物品, 數量)，給之後的「+5 Ultra Mass Alloy」提示、音效用。
/// </summary>
public class PlayerPickup : MonoBehaviour
{
    public static PlayerPickup Instance { get; private set; }

    [Tooltip("掉落物飛向的點 = 這個物件的位置 + 這個偏移（世界座標，公尺）。設在胸口附近。")]
    [SerializeField] private Vector3 collectOffset = new Vector3(0f, 1.2f, 0f);

    /// <summary>撿到東西。(物品, 數量)</summary>
    public static event Action<ItemObject, int> Collected;

    /// <summary>掉落物飛向的點。</summary>
    public Vector3 CollectPoint => transform.position + collectOffset;

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning("PlayerPickup: 場上有兩個 PlayerPickup，後來的這個會取代前一個", this);
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag("DropItem")) return;
        DropItem drop = other.GetComponentInParent<DropItem>();
        if (drop != null) Collect(drop);
    }

    /// <summary>收進背包、掉落物回到物件池。DropItem 在 Trigger 沒觸發時也會直接呼叫這裡。</summary>
    public void Collect(DropItem drop)
    {
        if (drop == null || !drop.MarkCollected()) return;

        ItemObject item = drop.item;
        int amount = Mathf.Max(1, drop.amount);

        if (item != null && InventoryManager.Instance != null)
        {
            for (int i = 0; i < amount; i++)
                InventoryManager.Instance.AddItemToInventory(item);
            Collected?.Invoke(item, amount);
        }
        else if (item != null)
        {
            Debug.LogWarning($"PlayerPickup: 找不到 InventoryManager，'{item.itemName}' ×{amount} 沒有收進背包", this);
        }

        PrefabPool.Despawn(drop.gameObject);
    }
}
