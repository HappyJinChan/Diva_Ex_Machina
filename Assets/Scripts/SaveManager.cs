using System.Collections.Generic;
using UnityEngine;

// ─────────────────────────────────────────────────────────────────────────────
// 存檔資料（只放純資料：字串、數字、enum、Color、struct）
//
// 不直接存 ItemInstance：它指向 ItemObject（ScriptableObject），ES3 存 UnityEngine.Object
// 要靠 Reference Manager，只靠合成取得、場景沒參照到的物品讀回來會變 null。
// 所以物品類型存成 ItemObject.itemId，讀檔時用 ItemDatabase 找回來。
// ─────────────────────────────────────────────────────────────────────────────

[System.Serializable]
public class PartSaveData
{
    public string uid;
    public string itemId;          // 空字串 = 這個掛點沒裝零件（ItemInstance.item == null）
    public int amount;
    public WeaponPartType partType;
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();
    public List<Color> colors = new List<Color>();
    public string shaderName;
}

[System.Serializable]
public class ItemSaveData
{
    public const string KindItem = "Item";
    public const string KindRange = "Range";
    public const string KindMelee = "Melee";
    public const string KindShoulder = "Shoulder";
    public const string KindArmor = "Armor";
    public const string KindPart = "Part";

    public string kind;            // 用哪個 ItemInstance 子類別還原
    public string uid;
    public string itemId;
    public int amount;

    public string newWeaponName;   // 武器
    public bool hasAttachment;     // attachment 是 null 還是空清單要分開記
    public List<PartSaveData> attachment = new List<PartSaveData>();

    public WeaponPartType partType; // 零件

    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();
    public List<Color> colors = new List<Color>();
    public string shaderName;
}

[System.Serializable]
public class SlotUidSaveData
{
    public EquipmentSlotId slotId;
    public string uid;
}

[System.Serializable]
public class EquipmentSetSaveData
{
    public List<SlotUidSaveData> entries = new List<SlotUidSaveData>();
}

/// <summary>
/// 用 Easy Save 3 存讀背包與三套裝備。
///
/// 存的內容：
///   - 背包：每件物品的 uid、物品類型 id、數量、詞條、顏色、零件
///   - 三套裝備：每套「格子 id → 物品 uid」
///   - 目前穿第幾套
/// 身上穿的不另外存：存檔前先把身上的記回目前這套，讀檔時再穿上那一套。
///
/// 設定：場景裡放一個 SaveManager，Item Database 欄位拖入 ItemDatabase asset。
/// 測試：Play Mode 中對元件按 ⋮ → Save Now / Load Now / Delete Save。
/// </summary>
public class SaveManager : MonoBehaviour
{
    public static SaveManager Instance { get; private set; }

    [SerializeField] private ItemDatabase itemDatabase;

    [Tooltip("存檔檔名（ES3 預設放在 Application.persistentDataPath）。")]
    [SerializeField] private string fileName = "SaveData.es3";

    [Tooltip("開場自動讀檔。預設關閉，避免改變目前的遊戲流程。")]
    [SerializeField] private bool loadOnStart = false;

    private const int SaveVersion = 1;
    private const string KeyVersion = "version";
    private const string KeyInventory = "inventory";
    private const string KeySets = "equipmentSets";
    private const string KeyActiveSet = "activeSet";

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    private void Start()
    {
        // Start：所有 Awake 之後，InventoryManager / EquipmentManager 的 Instance 都已設好
        if (loadOnStart && HasSave()) Load();
    }

    public bool HasSave() => ES3.FileExists(fileName);

    public void DeleteSave()
    {
        if (HasSave()) ES3.DeleteFile(fileName);
    }

    // ===== 存檔 =====

    public bool Save()
    {
        var inv = InventoryManager.Instance;
        var em = EquipmentManager.Instance;
        if (!CheckReady(inv, em, "Save")) return false;

        // 身上穿的先記回目前這套，三套就是完整的裝備狀態
        em.SaveCurrentToSet(em.ActiveSetIndex);

        var items = new List<ItemSaveData>();
        int skipped = 0;
        foreach (var inst in inv.inventory)
        {
            if (inst == null || inst.item == null) continue;
            var data = ToSaveData(inst);
            if (data == null) { skipped++; continue; }
            items.Add(data);
        }

        var sets = new List<EquipmentSetSaveData>();
        for (int k = 0; k < EquipmentManager.SetCount; k++)
        {
            var setData = new EquipmentSetSaveData();
            var uids = em.GetSetUids(k);
            if (uids != null)
                foreach (var kv in uids)
                    setData.entries.Add(new SlotUidSaveData { slotId = kv.Key, uid = kv.Value });
            sets.Add(setData);
        }

        ES3.Save(KeyVersion, SaveVersion, fileName);
        ES3.Save(KeyInventory, items, fileName);
        ES3.Save(KeySets, sets, fileName);
        ES3.Save(KeyActiveSet, em.ActiveSetIndex, fileName);

        if (skipped > 0)
            Debug.LogWarning($"SaveManager: {skipped} 件物品沒有存進去（物品類型不在 ItemDatabase 裡），讀檔後會消失", this);
        Debug.Log($"SaveManager: 已存檔，背包 {items.Count} 件，目前第 {em.ActiveSetIndex + 1} 套", this);
        return true;
    }

    // ===== 讀檔 =====

    public bool Load()
    {
        var inv = InventoryManager.Instance;
        var em = EquipmentManager.Instance;
        if (!CheckReady(inv, em, "Load")) return false;

        if (!HasSave())
        {
            Debug.LogWarning($"SaveManager: 找不到存檔 '{fileName}'", this);
            return false;
        }

        int version = ES3.Load(KeyVersion, fileName, 0);
        if (version != SaveVersion)
            Debug.LogWarning($"SaveManager: 存檔版本 {version} 與目前版本 {SaveVersion} 不同，仍嘗試讀取", this);

        var items = ES3.Load(KeyInventory, fileName, new List<ItemSaveData>());
        var sets = ES3.Load(KeySets, fileName, new List<EquipmentSetSaveData>());
        int active = ES3.Load(KeyActiveSet, fileName, 0);

        // 1) 先卸下身上的裝備：等一下背包會整個換掉，身上不能留著舊物件
        em.UnequipAll();

        // 2) 重建背包
        var restored = new List<ItemInstance>();
        int missing = 0;
        foreach (var data in items)
        {
            var inst = FromSaveData(data);
            if (inst == null) { missing++; continue; }
            restored.Add(inst);
        }
        inv.ReplaceInventory(restored);

        // 3) 三套裝備
        for (int k = 0; k < EquipmentManager.SetCount; k++)
        {
            var entries = new List<KeyValuePair<EquipmentSlotId, string>>();
            if (sets != null && k < sets.Count && sets[k] != null)
                foreach (var e in sets[k].entries)
                    entries.Add(new KeyValuePair<EquipmentSlotId, string>(e.slotId, e.uid));
            em.SetSetUids(k, entries);
        }

        // 4) 同一件裝備只能屬於一套：舊存檔可能有重複，優先留在目前那套
        if (active < 0 || active >= EquipmentManager.SetCount) active = 0;
        em.RemoveDuplicatesAcrossSets(active);

        // 5) 穿上目前那一套
        bool ok = em.ApplyActiveSet(active);

        if (missing > 0)
            Debug.LogWarning($"SaveManager: {missing} 件物品讀不回來（物品類型 id 在 ItemDatabase 找不到）", this);
        Debug.Log($"SaveManager: 已讀檔，背包 {restored.Count} 件，目前第 {active + 1} 套" +
                  (ok ? "" : "（有格子裝不上，見上面的警告）"), this);
        return true;
    }

    private bool CheckReady(InventoryManager inv, EquipmentManager em, string op)
    {
        if (itemDatabase == null)
        {
            Debug.LogWarning($"SaveManager.{op}: 沒有指定 ItemDatabase", this);
            return false;
        }
        if (inv == null || em == null)
        {
            Debug.LogWarning($"SaveManager.{op}: 找不到 InventoryManager 或 EquipmentManager", this);
            return false;
        }
        return true;
    }

    // ===== ItemInstance ↔ 存檔資料 =====

    private ItemSaveData ToSaveData(ItemInstance inst)
    {
        if (!itemDatabase.Contains(inst.item))
        {
            Debug.LogWarning($"SaveManager: '{inst.item.name}' 不在 ItemDatabase 裡（或 itemId 是空的），請對 ItemDatabase 按 ⋮ → Rebuild", inst.item);
            return null;
        }

        var d = new ItemSaveData
        {
            uid = inst.uid,
            itemId = inst.item.itemId,
            amount = inst.amount,
        };

        switch (inst)
        {
            case RangeWeaponInstance r:
                d.kind = ItemSaveData.KindRange;
                d.newWeaponName = r.newWeaponName;
                d.buffs = CopyList(r.buffs);
                WriteAttachment(d, r.attachment);
                d.colors = CopyList(r.colors);
                d.shaderName = r.shaderName;
                break;

            case MeleeWeaponInstance m:
                d.kind = ItemSaveData.KindMelee;
                d.newWeaponName = m.newWeaponName;
                d.buffs = CopyList(m.buffs);
                WriteAttachment(d, m.attachment);
                d.colors = CopyList(m.colors);
                d.shaderName = m.shaderName;
                break;

            case ShoulderWeaponInstance sw:
                d.kind = ItemSaveData.KindShoulder;
                d.newWeaponName = sw.newWeaponName;
                d.buffs = CopyList(sw.buffs);
                WriteAttachment(d, sw.attachment);
                d.colors = CopyList(sw.colors);
                d.shaderName = sw.shaderName;
                break;

            case ArmorInstance a:
                d.kind = ItemSaveData.KindArmor;
                d.buffs = CopyList(a.buffs);
                d.colors = CopyList(a.colors);
                d.shaderName = a.shaderName;
                break;

            case PartInstance p:
                d.kind = ItemSaveData.KindPart;
                d.partType = p.partType;
                d.buffs = CopyList(p.buffs);
                d.colors = CopyList(p.colors);
                d.shaderName = p.shaderName;
                break;

            default:
                d.kind = ItemSaveData.KindItem;
                break;
        }
        return d;
    }

    private ItemInstance FromSaveData(ItemSaveData d)
    {
        if (d == null) return null;
        var item = itemDatabase.Find(d.itemId);
        if (item == null)
        {
            Debug.LogWarning($"SaveManager: itemId '{d.itemId}' 在 ItemDatabase 找不到，這件物品略過");
            return null;
        }

        switch (d.kind)
        {
            case ItemSaveData.KindRange:
                return new RangeWeaponInstance
                {
                    uid = d.uid, item = item, amount = d.amount,
                    newWeaponName = d.newWeaponName,
                    buffs = CopyList(d.buffs),
                    attachment = ReadAttachment(d),
                    colors = CopyList(d.colors),
                    shaderName = d.shaderName,
                };

            case ItemSaveData.KindMelee:
                return new MeleeWeaponInstance
                {
                    uid = d.uid, item = item, amount = d.amount,
                    newWeaponName = d.newWeaponName,
                    buffs = CopyList(d.buffs),
                    attachment = ReadAttachment(d),
                    colors = CopyList(d.colors),
                    shaderName = d.shaderName,
                };

            case ItemSaveData.KindShoulder:
                return new ShoulderWeaponInstance
                {
                    uid = d.uid, item = item, amount = d.amount,
                    newWeaponName = d.newWeaponName,
                    buffs = CopyList(d.buffs),
                    attachment = ReadAttachment(d),
                    colors = CopyList(d.colors),
                    shaderName = d.shaderName,
                };

            case ItemSaveData.KindArmor:
                return new ArmorInstance
                {
                    uid = d.uid, item = item, amount = d.amount,
                    buffs = CopyList(d.buffs),
                    colors = CopyList(d.colors),
                    shaderName = d.shaderName,
                };

            case ItemSaveData.KindPart:
                return new PartInstance
                {
                    uid = d.uid, item = item, amount = d.amount,
                    partType = d.partType,
                    buffs = CopyList(d.buffs),
                    colors = CopyList(d.colors),
                    shaderName = d.shaderName,
                };

            case ItemSaveData.KindItem:
                return new ItemInstance { uid = d.uid, item = item, amount = d.amount };

            default:
                Debug.LogWarning($"SaveManager: 不認得的物品種類 '{d.kind}'（{item.name}），當成一般物品讀取");
                return new ItemInstance { uid = d.uid, item = item, amount = d.amount };
        }
    }

    // attachment 的索引對應武器的掛點，空的掛點也要保留位置
    private void WriteAttachment(ItemSaveData d, List<PartInstance> attachment)
    {
        d.hasAttachment = attachment != null;
        d.attachment = new List<PartSaveData>();
        if (attachment == null) return;

        foreach (var p in attachment)
        {
            if (p == null)
            {
                d.attachment.Add(null);
                continue;
            }

            string partId = null;
            if (p.item != null)
            {
                if (itemDatabase.Contains(p.item)) partId = p.item.itemId;
                else Debug.LogWarning($"SaveManager: 零件 '{p.item.name}' 不在 ItemDatabase 裡，讀檔後這個掛點會是空的", p.item);
            }

            d.attachment.Add(new PartSaveData
            {
                uid = p.uid,
                itemId = partId,
                amount = p.amount,
                partType = p.partType,
                buffs = CopyList(p.buffs),
                colors = CopyList(p.colors),
                shaderName = p.shaderName,
            });
        }
    }

    private List<PartInstance> ReadAttachment(ItemSaveData d)
    {
        if (!d.hasAttachment) return null;
        var list = new List<PartInstance>();
        if (d.attachment == null) return list;

        foreach (var p in d.attachment)
        {
            if (p == null)
            {
                list.Add(null);
                continue;
            }

            ItemObject partItem = null;
            if (!string.IsNullOrEmpty(p.itemId))
            {
                partItem = itemDatabase.Find(p.itemId);
                if (partItem == null)
                    Debug.LogWarning($"SaveManager: 零件 itemId '{p.itemId}' 在 ItemDatabase 找不到，這個掛點改為空的");
            }

            list.Add(new PartInstance
            {
                uid = p.uid,
                item = partItem,
                amount = p.amount,
                partType = p.partType,
                buffs = CopyList(p.buffs),
                colors = CopyList(p.colors),
                shaderName = p.shaderName,
            });
        }
        return list;
    }

    private static List<T> CopyList<T>(List<T> src) => src != null ? new List<T>(src) : new List<T>();

    // ===== 測試用 =====

#if UNITY_EDITOR
    [ContextMenu("Save Now")]
    private void SaveNowMenu()
    {
        if (!Application.isPlaying) { Debug.LogWarning("Save Now：請在 Play Mode 中使用", this); return; }
        Save();
    }

    [ContextMenu("Load Now")]
    private void LoadNowMenu()
    {
        if (!Application.isPlaying) { Debug.LogWarning("Load Now：請在 Play Mode 中使用", this); return; }
        Load();
    }

    [ContextMenu("Delete Save")]
    private void DeleteSaveMenu()
    {
        DeleteSave();
        Debug.Log($"SaveManager: 已刪除存檔 '{fileName}'", this);
    }
#endif
}
