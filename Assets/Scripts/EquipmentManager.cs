﻿using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// 裝備格的身分。取代「equipmentSlots 第幾格」的整數索引。
/// 數值明確寫死：enum 存檔是存數字，之後插新值不能改到舊的。
/// </summary>
public enum EquipmentSlotId
{
    None = 0,
    Head = 1,
    Chest = 2,
    LeftHandArmor = 3,
    RightHandArmor = 4,
    Waist = 5,
    Legs = 6,
    Thruster = 7,
    LeftHandWeapon = 8,
    RightHandWeapon = 9,
    LeftShoulderWeapon = 10,
    RightShoulderWeapon = 11,
}

[System.Serializable]
public class EquipmentSlot
{
    [Tooltip("這一格的身分。PlayerStats、槍口火光、三套裝備都用它認格子，不再看順序。")]
    public EquipmentSlotId id;

    public GameObject equipedItem;

    // ── 執行時的場景參照（不存檔）──
    // 原本放在 RangeWeaponInstance / MeleeWeaponInstance / ShoulderWeaponInstance 上，
    // 但它們屬於「穿在這一格的那把武器模型」，不是物品資料。裝備時由 EquipmentManager 填入，卸下時清空。
    [System.NonSerialized] public Transform muzzlePoint;
    [System.NonSerialized] public MeleeHitbox hitbox;

    [Tooltip("武器掛點（角色骨架上的 Transform）。留空時沿用 UI 格子上 WeaponTransform 傳進來的掛點。")]
    public Transform mountPoint;

    [SerializeField] public List<ItemType> equipmentTypes = new List<ItemType>();

    [SerializeField, HideInInspector] public ItemType equipmentType;

    // 目前穿在這一格的物品：背包裡那件本身（執行時的參照，不存檔）。
    // 不序列化是刻意的：
    //   - 一般欄位的 [Serializable] class 反序列化後永遠不是 null，空格子會變成一件「沒有內容的物品」；
    //   - Play Mode 中改 EquipmentManager 的 Inspector 時，Unity 寫回會把它換成新物件，跟背包那件斷開。
    // 要存檔就存 item.uid（見 ItemUid），讀檔時用 InventoryManager.FindByUid 找回來。
    [System.NonSerialized] public ItemInstance item;

    /// <summary>這一格物品的 uid；空格子回傳 null。</summary>
    public string ItemUid => item?.uid;

    public bool Accepts(ItemType t)
    {
        if (equipmentTypes != null && equipmentTypes.Count > 0)
            return equipmentTypes.Contains(t);

        // 若主人還沒填新 list，就用舊欄位當 fallback
        return equipmentType == t;
    }

#if UNITY_EDITOR
    // 自動把舊值塞進 list（只在 list 是空的時候）
    public void MigrateIfNeeded()
    {
        if ((equipmentTypes == null || equipmentTypes.Count == 0) && equipmentType != 0)
            equipmentTypes = new List<ItemType> { equipmentType };
    }
#endif
}

/// <summary>
/// 一套裝備的「資料」：格子 id → 物品 uid。不含模型、掛點等場景狀態。
/// 用的時候到背包裡用 uid 找那件（InventoryManager.FindByUid）：
///   - 背包裡改了顏色 / 零件，每一套裡的那件都會一起變；
///   - 那件已經不在背包裡（例如被合成用掉），Get 會回傳 null；
///   - 存檔只要存這張 uid 表。
/// </summary>
public class EquipmentSet
{
    private readonly Dictionary<EquipmentSlotId, string> uids = new();

    /// <summary>這一格存的 uid；沒有就回傳 null。</summary>
    public string GetUid(EquipmentSlotId id)
        => uids.TryGetValue(id, out var uid) ? uid : null;

    /// <summary>這一格存的物品（到背包裡找）。沒存、或那件已經不在背包裡，回傳 null。</summary>
    public ItemInstance Get(EquipmentSlotId id)
    {
        string uid = GetUid(id);
        if (string.IsNullOrEmpty(uid) || InventoryManager.Instance == null) return null;
        return InventoryManager.Instance.FindByUid(uid);
    }

    /// <summary>item 為 null 代表這格不放東西。</summary>
    public void Set(EquipmentSlotId id, ItemInstance item)
    {
        if (id == EquipmentSlotId.None) return;
        if (item == null)
        {
            uids.Remove(id);
            return;
        }
        if (string.IsNullOrEmpty(item.uid))
        {
            Debug.LogWarning($"EquipmentSet: '{item.item?.itemName}' 沒有 uid（不是經由 InventoryManager.AddToInventory 加入的？），{id} 這格不記");
            uids.Remove(id);
            return;
        }
        uids[id] = item.uid;
    }

    public void Clear() => uids.Clear();

    /// <summary>直接設定某格的 uid（讀檔用）。uid 為空代表這格不放東西。</summary>
    public void SetUid(EquipmentSlotId id, string uid)
    {
        if (id == EquipmentSlotId.None) return;
        if (string.IsNullOrEmpty(uid)) uids.Remove(id);
        else uids[id] = uid;
    }

    public bool IsEmpty => uids.Count == 0;

    public bool Contains(ItemInstance item)
        => item != null && !string.IsNullOrEmpty(item.uid) && uids.ContainsValue(item.uid);

    /// <summary>整張 uid 表（存檔用）。</summary>
    public IReadOnlyDictionary<EquipmentSlotId, string> Uids => uids;
}

public class EquipmentManager : MonoBehaviour
{
    [Header("Runtime Mix -> UTS3 Baking")]
    [Tooltip("Assign MixAlbedoBaker.shader here. Used by armor controllers created at runtime.")]
    [SerializeField] private Shader mixAlbedoBakeShader;
    [SerializeField] private Transform leftMuzzleFlash;
    [SerializeField] private Transform rightMuzzleFlash;


    [SerializeField] private Transform leftMuzzleFlashHolder;   // e.g. Player/FXHolders/L_MuzzleFlashHolder
    [SerializeField] private Transform rightMuzzleFlashHolder;  // e.g. Player/FXHolders/R_MuzzleFlashHolder

    public static EquipmentManager Instance { get; private set; }

    /// <summary>
    /// 某一格的裝備變了（裝上或卸下）。參數是 equipmentSlots 的 index。
    /// UI 監聽這個事件更新圖示；不經過 UI 的換裝（SwitchSet、換裝射出）也會觸發。
    /// </summary>
    public event Action<int> OnSlotChanged;
    public Transform equipmentPage;
    [SerializeField] public List<EquipmentSlot> equipmentSlots = new();

    [Header("Equipment Stat Block")]
    [Tooltip("Left side: Health / Defence / Jump Height / Max EN / EN regeneration / Rate of Climb / Dash Speed / Cruising Speed")]
    public TextMeshProUGUI leftStatBlock;
    [Tooltip("Right side: LH Attack / RH Attack")]
    public TextMeshProUGUI rightStatBlock;

    [Tooltip("TMP <pos=...> for left value column")]
    public float leftValuePos = 180f;
    [Tooltip("TMP <pos=...> for right value column")]
    public float rightValuePos = 150f;

    private bool _hookedPlayerStats;

    void Awake()
    {
        // If an instance already exists and it's not this one, destroy this new instance
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return; // Exit to prevent further execution of this Awake method
        }
        // Otherwise, set this instance as the Singleton
        Instance = this;

        WarnInvalidSlotIds();
    }

    // ===== 用 id 找格子 =====

    /// <summary>回傳該 id 在 equipmentSlots 的索引；找不到回傳 -1。</summary>
    public int IndexOf(EquipmentSlotId id)
    {
        if (id == EquipmentSlotId.None) return -1;
        for (int i = 0; i < equipmentSlots.Count; i++)
            if (equipmentSlots[i] != null && equipmentSlots[i].id == id) return i;
        return -1;
    }

    public EquipmentSlot GetSlot(EquipmentSlotId id)
    {
        int i = IndexOf(id);
        return i >= 0 ? equipmentSlots[i] : null;
    }

    public EquipmentSlotId IdOf(int slotIndex)
        => (slotIndex >= 0 && slotIndex < equipmentSlots.Count && equipmentSlots[slotIndex] != null)
            ? equipmentSlots[slotIndex].id
            : EquipmentSlotId.None;

    public bool Equip(EquipmentSlotId id, ItemInstance item)
    {
        int i = IndexOf(id);
        if (i < 0)
        {
            Debug.LogWarning($"Equip: 找不到 id 為 {id} 的裝備格");
            return false;
        }
        return Equip(i, item);
    }

    public void Unequip(EquipmentSlotId id)
    {
        int i = IndexOf(id);
        if (i < 0)
        {
            Debug.LogWarning($"Unequip: 找不到 id 為 {id} 的裝備格");
            return;
        }
        Unequip(i);
    }

    // ===== 三套裝備 =====
    //
    // equipmentSlots 永遠只有一份，代表「身上實際穿著的」。
    // 三套只存資料；SwitchSet 時把目標那套逐格 Equip / Unequip 上身。
    // 「正在編輯第幾套」= 「正在穿第幾套」：UI 切分頁呼叫 SwitchSet，
    // 之後用原本的背包 UI 換裝，換完的結果就是那一套的內容。
    //
    // 只在執行時存在（不序列化）：關掉遊戲就沒了，之後要接 SaveManager。
    // 不用 [SerializeReference] 是刻意的 —— Inspector 寫回時會把參照換成新物件，
    // 跟背包裡那件就斷開了。

    public const int SetCount = 3;

    private readonly EquipmentSet[] equipmentSets = { new EquipmentSet(), new EquipmentSet(), new EquipmentSet() };

    /// <summary>目前穿在身上的是第幾套（0 ~ SetCount-1）。</summary>
    public int ActiveSetIndex { get; private set; } = 0;

    /// <summary>切換套裝完成後觸發，參數是新的 ActiveSetIndex。</summary>
    public event Action<int> OnActiveSetChanged;

    private static bool IsValidSetIndex(int setIndex) => setIndex >= 0 && setIndex < SetCount;

    /// <summary>讀取某一套某一格存的裝備（UI 預覽用）。沒有就回傳 null。</summary>
    public ItemInstance GetSetItem(int setIndex, EquipmentSlotId id)
        => IsValidSetIndex(setIndex) ? equipmentSets[setIndex].Get(id) : null;

    /// <summary>某件裝備有沒有被放進任何一套（例如背包 UI 要標示）。</summary>
    /// <summary>
    /// 這一套有沒有存任何裝備。注意：目前穿著的那套，真實內容是身上穿的（記錄可能是舊的），
    /// 這裡只看記錄 —— 用在「別的套」才準。
    /// </summary>
    public bool IsSetEmpty(int setIndex)
        => !IsValidSetIndex(setIndex) || equipmentSets[setIndex].IsEmpty;

    public bool IsItemInAnySet(ItemInstance item)
    {
        for (int k = 0; k < SetCount; k++)
            if (equipmentSets[k].Contains(item)) return true;
        return false;
    }

    // ===== 同一件裝備只能屬於一套 =====
    //
    // 設定上：一套穿在 Bradamant 身上，另外兩套在貝努鳥號上待射出，同一件不可能同時在兩個地方。
    // 目前這套的真實內容是「身上穿的」（equipmentSlots），記在 equipmentSets[ActiveSetIndex] 的可能是舊的，
    // 所以「屬於別套」只看其他兩套。

    /// <summary>這件裝備放在哪一套「別的」套裝裡（不含目前這套）；沒有回傳 -1。</summary>
    public int GetOtherSetIndexOf(ItemInstance item)
    {
        if (item == null) return -1;
        for (int k = 0; k < SetCount; k++)
        {
            if (k == ActiveSetIndex) continue;
            if (equipmentSets[k].Contains(item)) return k;
        }
        return -1;
    }

    /// <summary>
    /// 清掉重複：同一個 uid 出現在多套時，只留在一套。優先留在 keepSetIndex，其次留在編號小的那套。
    /// 讀到舊存檔（還沒有這條規則時存的）時用。回傳清掉幾格。
    /// </summary>
    public int RemoveDuplicatesAcrossSets(int keepSetIndex)
    {
        var owner = new Dictionary<string, int>();
        int removed = 0;

        // 先登記要保留的那套，再依編號登記其他套
        var order = new List<int>();
        if (IsValidSetIndex(keepSetIndex)) order.Add(keepSetIndex);
        for (int k = 0; k < SetCount; k++) if (k != keepSetIndex) order.Add(k);

        foreach (int k in order)
        {
            var set = equipmentSets[k];
            var toRemove = new List<EquipmentSlotId>();
            foreach (var kv in set.Uids)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                if (owner.TryGetValue(kv.Value, out int first))
                {
                    Debug.LogWarning($"EquipmentManager: 同一件裝備同時在第 {first + 1} 套和第 {k + 1} 套，已從第 {k + 1} 套的 {kv.Key} 移除", this);
                    toRemove.Add(kv.Key);
                }
                else owner[kv.Value] = k;
            }
            foreach (var id in toRemove) set.SetUid(id, null);
            removed += toRemove.Count;
        }
        return removed;
    }

    /// <summary>把身上目前穿的記進指定那一套（覆蓋）。id 是 None 的格子不記。</summary>
    public void SaveCurrentToSet(int setIndex)
    {
        if (!IsValidSetIndex(setIndex))
        {
            Debug.LogWarning($"SaveCurrentToSet: invalid set index {setIndex}");
            return;
        }

        var set = equipmentSets[setIndex];
        set.Clear();
        for (int i = 0; i < equipmentSlots.Count; i++)
        {
            var slot = equipmentSlots[i];
            if (slot == null || slot.id == EquipmentSlotId.None) continue;
            if (slot.item != null && slot.item.item != null)
                set.Set(slot.id, slot.item);
        }
    }

    /// <summary>
    /// 切換到指定那一套：先把身上的記回目前這套，再穿上目標那套。
    /// 目標那套沒存的格子會卸下。回傳 false 代表有格子裝失敗（那幾格會是空的）。
    /// </summary>
    public bool SwitchSet(int setIndex)
    {
        if (!IsValidSetIndex(setIndex))
        {
            Debug.LogWarning($"SwitchSet: invalid set index {setIndex}");
            return false;
        }
        if (setIndex == ActiveSetIndex) return true;

        SaveCurrentToSet(ActiveSetIndex);
        bool ok = ApplySetToSlots(setIndex);

        ActiveSetIndex = setIndex;
        OnActiveSetChanged?.Invoke(ActiveSetIndex);
        return ok;
    }

    // ===== 讀檔用 =====

    /// <summary>某一套的整張 uid 表（存檔用）。</summary>
    public IReadOnlyDictionary<EquipmentSlotId, string> GetSetUids(int setIndex)
        => IsValidSetIndex(setIndex) ? equipmentSets[setIndex].Uids : null;

    /// <summary>覆蓋某一套的內容（讀檔用）。不會動到身上穿的。</summary>
    public void SetSetUids(int setIndex, IEnumerable<KeyValuePair<EquipmentSlotId, string>> entries)
    {
        if (!IsValidSetIndex(setIndex)) return;
        var set = equipmentSets[setIndex];
        set.Clear();
        if (entries == null) return;
        foreach (var kv in entries) set.SetUid(kv.Key, kv.Value);
    }

    /// <summary>卸下身上所有裝備。</summary>
    public void UnequipAll()
    {
        for (int i = 0; i < equipmentSlots.Count; i++)
        {
            var slot = equipmentSlots[i];
            if (slot != null && (slot.item != null || slot.equipedItem != null)) Unequip(i);
        }
    }

    /// <summary>
    /// 直接穿上指定那一套，並設為目前這套（讀檔用）。
    /// 跟 SwitchSet 不同：不會先把身上的記回目前這套，同一套也會重新套用。
    /// </summary>
    public bool ApplyActiveSet(int setIndex)
    {
        if (!IsValidSetIndex(setIndex))
        {
            Debug.LogWarning($"ApplyActiveSet: invalid set index {setIndex}");
            return false;
        }
        bool ok = ApplySetToSlots(setIndex);
        ActiveSetIndex = setIndex;
        OnActiveSetChanged?.Invoke(ActiveSetIndex);
        return ok;
    }

    // 逐格讓身上的裝備變成 set 的內容。已經穿著同一件的格子不重裝（省掉生成模型 / 烘焙材質）。
    private bool ApplySetToSlots(int setIndex)
    {
        var set = equipmentSets[setIndex];
        bool allOk = true;

        for (int i = 0; i < equipmentSlots.Count; i++)
        {
            var slot = equipmentSlots[i];
            if (slot == null) continue;
            if (slot.id == EquipmentSlotId.None)
            {
                Debug.LogWarning($"SwitchSet: slot[{i}] 沒有 id，跳過");
                continue;
            }

            var desired = set.Get(slot.id);

            if (desired == null && !string.IsNullOrEmpty(set.GetUid(slot.id)))
                Debug.LogWarning($"SwitchSet: 第 {setIndex + 1} 套的 {slot.id} 記的物品已經不在背包裡（可能被合成用掉），這格改為空", this);

            if (desired == null)
            {
                if (slot.item != null || slot.equipedItem != null) Unequip(i);
                continue;
            }

            if (desired == slot.item && slot.equipedItem != null) continue;

            if (!EquipUnchecked(i, desired))
            {
                // 失敗的格子不能留著上一套的裝備，不然身上會混著兩套
                Debug.LogWarning($"SwitchSet: 第 {setIndex + 1} 套的 {slot.id}（'{desired.item.itemName}'）裝不上，這格改為空", this);
                if (slot.item != null || slot.equipedItem != null) Unequip(i);
                allOk = false;
            }
        }
        return allOk;
    }

    // 沒設 id 或 id 重複的格子：PlayerStats、槍口火光、三套裝備會認不到或認錯
    private void WarnInvalidSlotIds()
    {
        if (equipmentSlots == null) return;
        var seen = new HashSet<EquipmentSlotId>();
        for (int i = 0; i < equipmentSlots.Count; i++)
        {
            var id = equipmentSlots[i]?.id ?? EquipmentSlotId.None;
            if (id == EquipmentSlotId.None)
            {
                Debug.LogWarning($"EquipmentManager: slot[{i}] 沒有設定 id", this);
                continue;
            }
            if (!seen.Add(id))
                Debug.LogWarning($"EquipmentManager: id {id} 重複（slot[{i}]），IndexOf 只會找到第一格", this);
        }
    }


    private void OnEnable()
    {
        HookPlayerStatsEvents();
    }

    private void Start()
    {
        HookPlayerStatsEvents();
        RefreshEquipmentStatBlock();
    }

    private void OnDisable()
    {
        UnhookPlayerStatsEvents();
    }

    private void HookPlayerStatsEvents()
    {
        if (_hookedPlayerStats) return;
        if (PlayerStats.Instance == null) return;

        PlayerStats.Instance.OnHandWeaponDataChanged += RefreshEquipmentStatBlock;
        _hookedPlayerStats = true;
    }

    private void UnhookPlayerStatsEvents()
    {
        if (!_hookedPlayerStats) return;
        if (PlayerStats.Instance == null) return;

        PlayerStats.Instance.OnHandWeaponDataChanged -= RefreshEquipmentStatBlock;
        _hookedPlayerStats = false;
    }

    // ===== Equipment Stat Block UI =====
    public void RefreshEquipmentStatBlock()
    {
        if (leftStatBlock == null || rightStatBlock == null)
            return;

        var ps = PlayerStats.Instance;
        if (ps == null)
        {
            leftStatBlock.text = string.Empty;
            rightStatBlock.text = string.Empty;
            return;
        }

        // Left side (8 stats)
        string left =
            LineLeft("Health", ps.GetDisplayHealth()) + "\n" +
            LineLeft("Defence", ps.GetDisplayDefenceAverage()) + "\n" +
            LineLeft("Jump Height", FormatFloat(ps.GetDisplayJumpHeight())) + "\n" +
            LineLeft("Max EN", ps.GetDisplayMaxEnergy()) + "\n" +
            LineLeft("EN regeneration", FormatFloat(ps.GetDisplayEnergyRegen())) + "\n" +
            LineLeft("Rate of Climb", FormatFloat(ps.GetDisplayFlySpeed())) + "\n" +
            LineLeft("Dash Speed", FormatFloat(ps.GetDisplayDashSpeed())) + "\n" +
            LineLeft("Cruising Speed", FormatFloat(ps.GetDisplaySprintSpeed()));

        // Right side (4 stats)
        // Mobility = weight curve factor m (100% = light-frame baseline).
        string right =
            LineRight("LH Attack", ps.GetDisplayLhAttack()) + "\n" +
            LineRight("RH Attack", ps.GetDisplayRhAttack()) + "\n" +
            LineRight("Weight", Mathf.RoundToInt(ps.GetTotalWeight())) + "\n" +
            LineRight("Mobility", Mathf.RoundToInt(ps.GetMobilityFactor() * 100f) + "%");

        leftStatBlock.text = left;
        rightStatBlock.text = right;
    }

    private string LineLeft(string label, int value)
        => $"{label}:<pos={leftValuePos}>{value}";

    private string LineLeft(string label, string value)
        => $"{label}:<pos={leftValuePos}>{value}";

    private string LineRight(string label, int value)
        => $"{label}:<pos={rightValuePos}>{value}";

    private string LineRight(string label, string value)
        => $"{label}:<pos={rightValuePos}>{value}";

    private static string FormatFloat(float v)
        => v.ToString("0.##");

    // ===== 統一入口 =====

    /// <summary>
    /// 把 item 裝到指定格子。武器、護甲都走這裡；腿部顯示/隱藏也在這裡處理。
    /// uiMountPoint：UI 路徑傳入的武器掛點備用值，EquipmentSlot.mountPoint 有填時以格子為準。
    /// </summary>
    public bool Equip(int slotIndex, ItemInstance item, Transform uiMountPoint = null)
    {
        if (item == null || item.item == null) return false;

        // 同一件裝備只能屬於一套：已經放在別套的，不能穿進目前這套
        int otherSet = GetOtherSetIndexOf(item);
        if (otherSet >= 0)
        {
            Debug.LogWarning($"Equip: '{item.item.itemName}' 已經在第 {otherSet + 1} 套裡，同一件裝備只能屬於一套");
            return false;
        }

        return EquipUnchecked(slotIndex, item, uiMountPoint);
    }

    // 實際裝備，不檢查「是否屬於別套」。SwitchSet 穿上目標那套時用（那時目標那套還不是目前這套）。
    private bool EquipUnchecked(int slotIndex, ItemInstance item, Transform uiMountPoint = null)
    {
        if (item == null || item.item == null) return false;

        if (slotIndex < 0 || slotIndex >= equipmentSlots.Count)
        {
            Debug.LogWarning($"Equip: invalid slot index {slotIndex}");
            return false;
        }

        if (item is RangeWeaponInstance || item is MeleeWeaponInstance || item is ShoulderWeaponInstance)
            return TryEquipWeaponFromInventory(item, uiMountPoint, slotIndex);

        if (!equipmentSlots[slotIndex].Accepts(item.item.type))
        {
            Debug.LogWarning($"Equip: slot[{slotIndex}] 不接受 {item.item.type}");
            return false;
        }

        return EquipArmorToSlot(slotIndex, item);
    }

    /// <summary>卸下指定格子。腿部恢復顯示也在這裡處理。</summary>
    public void Unequip(int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= equipmentSlots.Count)
        {
            Debug.LogWarning($"Unequip: invalid slot index {slotIndex}");
            return;
        }
        CleanEquipmentSlot(slotIndex);
    }

    // 舊入口：裝到「第一個接受該類型的格子」。UI 已改走 Equip(選中的格子)，目前沒有呼叫端。
    public bool TryEquipFromInventory(ItemInstance item)
    {
        if (item == null || item.item == null) return false;

        for (int i = 0; i < equipmentSlots.Count; i++)
        {
            var slot = equipmentSlots[i];

            if (!slot.Accepts(item.item.type)) continue;

            return EquipArmorToSlot(i, item);
        }
        return false;
    }

    // 護甲實際裝備。原本是 TryEquipFromInventory 迴圈內的本體；
    // 改成先檢查、生成新模型，成功才拆舊的。結尾處理腿部隱藏。
    private bool EquipArmorToSlot(int i, ItemInstance item)
    {
        var slot = equipmentSlots[i];

        // 先確認新護甲生成得出來，才拆掉舊的。失敗時 slot 維持原狀，
        // 不會出現「模型沒了、數值還算舊護甲、腿還隱藏著」的情況。
        if (!(item.item is Armor armor && armor.skinnedMeshRenderer))
        {
            Debug.LogWarning($"EquipArmorToSlot: '{item.item.itemName}' 不是有 SkinnedMeshRenderer 的 Armor，slot[{i}] 維持原狀");
            return false;
        }
        if (BoneCombiner.Instance == null)
        {
            Debug.LogWarning($"EquipArmorToSlot: 找不到 BoneCombiner，slot[{i}] 維持原狀");
            return false;
        }

        Debug.Log($"TryEquipFromInventory: found matching slot {i} for item {item.item.itemName}");
        Debug.Log($"TryEquipFromInventory: equipping armor {armor.itemName} to slot {i}");
        var newMesh = BoneCombiner.Instance.InstantiateMesh(armor.skinnedMeshRenderer);
        Debug.Log($"TryEquipFromInventory: BoneCombiner instantiated mesh for {armor.itemName}");
        if (!newMesh)
        {
            Debug.LogWarning("TryEquipFromInventory: BoneCombiner failed to instantiate mesh");
            return false; // slot 維持原狀
        }

        // ²M±¼ÂÂ¹ê¨Ò
        if (slot.equipedItem) Destroy(slot.equipedItem);

        slot.equipedItem = newMesh;
        Debug.Log($"TryEquipFromInventory: equipped armor instance created for {armor.itemName}");
        slot.item = item;

        // ­Y¦³¦s¦â±m¡A³o¸Ì®M¦^¥h¡]¥i¿ï¡^
        if (item is ArmorInstance ai && slot.equipedItem)
        {
            var smr = slot.equipedItem.GetComponent<SkinnedMeshRenderer>();
            if (smr)
            {
                var mat = smr.material; // ¨C¥ó¸Ë³Æ¿W¥ß§÷½è
                if (!string.IsNullOrEmpty(ai.shaderName))
                {
                    // ½d¨Ò¡G¨Ì shaderName ®M¦^ÃC¦â
                    if (ai.shaderName.Contains("Mix 3") && ai.colors.Count >= 3)
                    {
                        mat.SetColor("_BaseColor", ai.colors[0]);
                        mat.SetColor("_Layer1Color", ai.colors[1]);
                        mat.SetColor("_Layer2Color", ai.colors[2]);
                    }
                    else if (ai.shaderName.Contains("Mix 4") && ai.colors.Count >= 4)
                    {
                        mat.SetColor("_BaseColor", ai.colors[0]);
                        mat.SetColor("_Layer1Color", ai.colors[1]);
                        mat.SetColor("_Layer2Color", ai.colors[2]);
                        mat.SetColor("_Layer3Color", ai.colors[3]);
                    }
                    else if (ai.shaderName.Contains("Mix 5") && ai.colors.Count >= 5)
                    {
                        mat.SetColor("_BaseColor", ai.colors[0]);
                        for (int k = 1; k < 5; k++) mat.SetColor($"_Layer{k}Color", ai.colors[k]);
                    }
                }
            }
        }
        Debug.Log($"TryEquipFromInventory: armor {armor.itemName} equipped to slot {i}");

        // Armor is instantiated directly from a SkinnedMeshRenderer reference,
        // so add the baking controller to the runtime armor object here.
        // This happens after ArmorInstance.colors have been restored.
        var armorRenderer = slot.equipedItem.GetComponent<SkinnedMeshRenderer>();
        if (armorRenderer == null)
            armorRenderer = slot.equipedItem.GetComponentInChildren<SkinnedMeshRenderer>(true);

        if (armorRenderer != null && armor.uts3MaterialTemplate != null)
        {
            var toonController =
                armorRenderer.GetComponent<LayeredToonMaterialController>();

            if (toonController == null)
                toonController =
                    armorRenderer.gameObject.AddComponent<LayeredToonMaterialController>();

            toonController.Configure(
                armorRenderer,
                armor.uts3MaterialTemplate,
                mixAlbedoBakeShader
            );
        }
        else if (armorRenderer != null && armor.uts3MaterialTemplate == null)
        {
            Debug.LogWarning(
                $"Armor '{armor.itemName}' has no UTS3 Material Template. " +
                "It will continue using its Mix material."
            );
        }

        // 腿甲蓋住原本的腿（原本寫在 InventoryManager.OnClickInventoryItem）
        if (item.item.type == ItemType.LegsArmor)
            BoneCombiner.Instance?.HideLegs();

        PlayerStats.Instance?.RecalculateFromEquipment();
        RefreshEquipmentStatBlock();
        OnSlotChanged?.Invoke(i);
        return true;
    }

    /// <summary>
    /// 不經過 UI 的入口：掛點只從 EquipmentSlot.mountPoint 取。
    /// SwitchSet、換裝射出都走這裡。
    /// </summary>
    public bool TryEquipWeaponFromInventory(ItemInstance item, int slotIndex)
        => TryEquipWeaponFromInventory(item, null, slotIndex);

    public bool TryEquipWeaponFromInventory(ItemInstance item, Transform mountPoint, int slotIndex)
    {
        if (item == null || item.item == null) return false;

        if (slotIndex < 0 || slotIndex >= equipmentSlots.Count)
        {
            Debug.LogWarning($"TryEquipWeaponFromInventory: invalid slot index {slotIndex}");
            return false;
        }

        var slot = equipmentSlots[slotIndex];

        if (!slot.Accepts(item.item.type))
        {
            Debug.LogWarning(
                $"TryEquipWeaponFromInventory: slot[{slotIndex}] accepts [{string.Join(", ", slot.equipmentTypes)}], item.type = {item.item.type}"
            );
            return false;
        }

        // 掛點以 EquipmentSlot 為主；沒填時才用呼叫端（UI）傳進來的。
        // 兩邊都有但不一樣，通常代表 UI 按鈕順序和 equipmentSlots 沒對齊。
        if (slot.mountPoint != null)
        {
            if (mountPoint != null && mountPoint != slot.mountPoint)
            {
                Debug.LogWarning(
                    $"TryEquipWeaponFromInventory: slot[{slotIndex}] 的 mountPoint '{slot.mountPoint.name}' " +
                    $"與 UI 傳入的 '{mountPoint.name}' 不同，以 EquipmentSlot 為準。" +
                    "請檢查 UI 按鈕順序與 equipmentSlots 是否對齊。", slot.mountPoint);
            }
            mountPoint = slot.mountPoint;
        }

        // 必須在拆掉舊裝備之前檢查，不然會拆了卻裝不上
        if (mountPoint == null)
        {
            Debug.LogWarning($"TryEquipWeaponFromInventory: slot[{slotIndex}] 沒有掛點（EquipmentSlot.mountPoint 與 UI 都沒提供）");
            return false;
        }

        if (slot.equipedItem)
        {
            if (slot.id == EquipmentSlotId.LeftHandWeapon)
                DetachMuzzleFlashToHolder(leftMuzzleFlash, leftMuzzleFlashHolder);
            else if (slot.id == EquipmentSlotId.RightHandWeapon)
                DetachMuzzleFlashToHolder(rightMuzzleFlash, rightMuzzleFlashHolder);

            Destroy(slot.equipedItem);
        }
        slot.muzzlePoint = null;
        slot.hitbox = null;

        if (item is RangeWeaponInstance rwi && rwi.item is RangeWeapon rw)
        {
            // 1)
            slot.equipedItem = Instantiate(rw.weaponPrefab, mountPoint, false);
            slot.equipedItem.transform.localRotation = Quaternion.Euler(new Vector3(90, 0, 0));
            slot.item = item;
            if (FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint") != null)
            {
                slot.muzzlePoint = FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint");
            }

            // 1-1) §â RangeWeaponInstance ¤W¦sªºÃC¦â®M¦^¥h
            if (rwi.colors != null && rwi.colors.Count > 0 && !string.IsNullOrEmpty(rwi.shaderName))
            {
                var rend = slot.equipedItem.GetComponentInChildren<Renderer>();
                if (rend)
                {
                    var mat = rend.material; // ¹ê¨Ò§÷½è
                    if (rwi.shaderName.Contains("Mix 3") && rwi.colors.Count >= 3)
                    {
                        mat.SetColor("_BaseColor", rwi.colors[0]);
                        mat.SetColor("_Layer1Color", rwi.colors[1]);
                        mat.SetColor("_Layer2Color", rwi.colors[2]);
                    }
                    else if (rwi.shaderName.Contains("Mix 4") && rwi.colors.Count >= 4)
                    {
                        mat.SetColor("_BaseColor", rwi.colors[0]);
                        mat.SetColor("_Layer1Color", rwi.colors[1]);
                        mat.SetColor("_Layer2Color", rwi.colors[2]);
                        mat.SetColor("_Layer3Color", rwi.colors[3]);
                    }
                    else if (rwi.shaderName.Contains("Mix 5") && rwi.colors.Count >= 5)
                    {
                        mat.SetColor("_BaseColor", rwi.colors[0]);
                        for (int k = 1; k < 5; k++)
                            mat.SetColor($"_Layer{k}Color", rwi.colors[k]);
                    }
                }
            }

            // 2)
            if (rwi.attachment != null)
            {
                foreach (var attach in rwi.attachment)
                {
                    if (attach?.item is not RangeWeaponPart rwp) continue;

                    foreach (var ap in rw.attachmentPoints)
                    {
                        if (ap.allowPart != attach.partType) continue;

                        var target = FindChildRecursive(slot.equipedItem.transform, ap.pointTransform.name);
                        if (target == null)
                        {
                            Debug.LogWarning($"Equip: cannot find mount '{ap.pointTransform.name}' on weapon instance");
                            continue;
                        }

                        var part = Instantiate(rwp.rangeWeaponPartPrefab, target, false);
                        part.transform.localPosition = Vector3.zero;
                        part.transform.localRotation = Quaternion.identity;
                        part.transform.localScale = Vector3.one;
                        if (FindChildRecursive(part.transform, "MuzzlePoint") != null)
                        {
                            slot.muzzlePoint = FindChildRecursive(part.transform, "MuzzlePoint");
                        }

                        if (attach.colors != null && attach.colors.Count > 0 && !string.IsNullOrEmpty(attach.shaderName))
                        {
                            var partRend = part.GetComponentInChildren<Renderer>();
                            if (partRend)
                            {
                                var mat = partRend.material;
                                if (attach.shaderName.Contains("Mix 3") && attach.colors.Count >= 3)
                                {
                                    mat.SetColor("_BaseColor", attach.colors[0]);
                                    mat.SetColor("_Layer1Color", attach.colors[1]);
                                    mat.SetColor("_Layer2Color", attach.colors[2]);
                                }
                                else if (attach.shaderName.Contains("Mix 4") && attach.colors.Count >= 4)
                                {
                                    mat.SetColor("_BaseColor", attach.colors[0]);
                                    mat.SetColor("_Layer1Color", attach.colors[1]);
                                    mat.SetColor("_Layer2Color", attach.colors[2]);
                                    mat.SetColor("_Layer3Color", attach.colors[3]);
                                }
                                else if (attach.shaderName.Contains("Mix 5") && attach.colors.Count >= 5)
                                {
                                    mat.SetColor("_BaseColor", attach.colors[0]);
                                    for (int k = 1; k < 5; k++)
                                        mat.SetColor($"_Layer{k}Color", attach.colors[k]);
                                }
                            }
                        }
                    }
                }
            }

            bool isLeftHand = (slot.id == EquipmentSlotId.LeftHandWeapon);
            ParentMuzzleFlashToMuzzle(isLeftHand ? leftMuzzleFlash : rightMuzzleFlash, slot.muzzlePoint);
            if (slot.muzzlePoint == null)
                Debug.LogWarning($"Equip: '{rw.itemName}' 的模型和零件上都找不到 'MuzzlePoint'，這把槍不會開火", slot.equipedItem);

            // Saved Mix colors and all weapon parts are now restored.
            // Bake them and switch their visible materials to untouched UTS3 clones.
            LayeredToonMaterialController.InitializeAll(slot.equipedItem);

            PlayerStats.Instance?.RecalculateFromEquipment();
            RefreshEquipmentStatBlock();
            OnSlotChanged?.Invoke(slotIndex);
            Debug.Log($"Equipped weapon '{rw.itemName}' to slot index {slotIndex}");
            return true;
        }
        else if (item is MeleeWeaponInstance mwi && mwi.item is MeleeWeapon mw)
        {
            slot.equipedItem = Instantiate(mw.weaponPrefab, mountPoint, false);

            // 握把對位不在這裡做：裝了 Handle 零件時，真正的握點由零件決定，
            // 所以必須等零件全部掛完才對位（見本區塊末端的 AlignMeleeGrip）。

            slot.item = item;
            if (mwi.colors != null && mwi.colors.Count > 0 && !string.IsNullOrEmpty(mwi.shaderName))
            {
                var rend = slot.equipedItem.GetComponentInChildren<Renderer>();
                if (rend)
                {
                    var mat = rend.material;
                    if (mwi.shaderName.Contains("Mix 3") && mwi.colors.Count >= 3)
                    {
                        mat.SetColor("_BaseColor", mwi.colors[0]);
                        mat.SetColor("_Layer1Color", mwi.colors[1]);
                        mat.SetColor("_Layer2Color", mwi.colors[2]);
                    }
                    else if (mwi.shaderName.Contains("Mix 4") && mwi.colors.Count >= 4)
                    {
                        mat.SetColor("_BaseColor", mwi.colors[0]);
                        mat.SetColor("_Layer1Color", mwi.colors[1]);
                        mat.SetColor("_Layer2Color", mwi.colors[2]);
                        mat.SetColor("_Layer3Color", mwi.colors[3]);
                    }
                    else if (mwi.shaderName.Contains("Mix 5") && mwi.colors.Count >= 5)
                    {
                        mat.SetColor("_BaseColor", mwi.colors[0]);
                        for (int k = 1; k < 5; k++)
                            mat.SetColor($"_Layer{k}Color", mwi.colors[k]);
                    }
                }
            }

            // 記下 Handle 零件的實體，零件掛完後要用它的握點做最後平移
            GameObject handlePartGO = null;
            MeleeWeaponPart handlePartSO = null;

            if (mwi.attachment != null)
            {
                foreach (var attach in mwi.attachment)
                {
                    if (attach?.item == null) continue;

                    foreach (var ap in mw.attachmentPoints)
                    {
                        if (ap.allowPart != attach.partType) continue;

                        var target = FindChildRecursive(slot.equipedItem.transform, ap.pointTransform.name);
                        if (target == null)
                        {
                            Debug.LogWarning($"Equip (Melee): cannot find mount '{ap.pointTransform.name}' on weapon instance");
                            continue;
                        }

                        // Handle / other mesh parts
                        if (attach.item is MeleeWeaponPart mwp)
                        {
                            if (attach.partType == WeaponPartType.Handle)
                            {
                                // hide default handle (optional)
                                if (mw.defaultHandle != null)
                                {
                                    var t = FindChildRecursive(slot.equipedItem.transform, mw.defaultHandle.name);
                                    if (t != null) t.gameObject.SetActive(false);
                                }
                                else
                                {
                                    var t = FindChildRecursive(slot.equipedItem.transform, "default handle");
                                    if (t != null) t.gameObject.SetActive(false);
                                }
                            }

                            var part = Instantiate(mwp.meleeWeaponPartPrefab, target, false);
                            part.transform.localPosition = Vector3.zero;
                            part.transform.localRotation = Quaternion.identity;
                            part.transform.localScale = Vector3.one;

                            if (attach.partType == WeaponPartType.Handle)
                            {
                                handlePartGO = part;
                                handlePartSO = mwp;
                            }

                            // apply saved colors (same as your existing logic)
                            if (attach.colors != null && attach.colors.Count > 0 && !string.IsNullOrEmpty(attach.shaderName))
                            {
                                var partRend = part.GetComponentInChildren<Renderer>();
                                if (partRend)
                                {
                                    var mat = partRend.material;
                                    if (attach.shaderName.Contains("Mix 3") && attach.colors.Count >= 3)
                                    {
                                        mat.SetColor("_BaseColor", attach.colors[0]);
                                        mat.SetColor("_Layer1Color", attach.colors[1]);
                                        mat.SetColor("_Layer2Color", attach.colors[2]);
                                    }
                                    else if (attach.shaderName.Contains("Mix 4") && attach.colors.Count >= 4)
                                    {
                                        mat.SetColor("_BaseColor", attach.colors[0]);
                                        mat.SetColor("_Layer1Color", attach.colors[1]);
                                        mat.SetColor("_Layer2Color", attach.colors[2]);
                                        mat.SetColor("_Layer3Color", attach.colors[3]);
                                    }
                                    else if (attach.shaderName.Contains("Mix 5") && attach.colors.Count >= 5)
                                    {
                                        mat.SetColor("_BaseColor", attach.colors[0]);
                                        for (int k = 1; k < 5; k++)
                                            mat.SetColor($"_Layer{k}Color", attach.colors[k]);
                                    }
                                }
                            }
                        }
                        // Coating particle effect
                        else if (attach.item is MeleeWeaponCoating mwc)
                        {
                            var fx = Instantiate(mwc.meleeCoatingPrefab, target, false);
                            fx.transform.localPosition = Vector3.zero;


                            fx.transform.localRotation = Quaternion.Euler(0, 90f, -90f);
                            fx.transform.localScale = new Vector3(mw.swordLength, mw.swordLength, 1);

                            ApplyCoatingColors(fx, attach.colors);

                            if (mw.defaultCoatingEffect != null)
                            {
                                var t = FindChildRecursive(slot.equipedItem.transform, mw.defaultCoatingEffect.name);
                                if (t != null) t.gameObject.SetActive(false);
                            }
                        }
                        else
                        {
                            Debug.LogWarning($"Equip (Melee): unsupported attach item type: {attach.item.GetType().Name}");
                        }
                    }
                }
            }

            // ───── 握把對位（必須在零件全部掛完之後）─────
            AlignMeleeGrip(slot.equipedItem.transform, mountPoint, mw, handlePartGO, handlePartSO);

            // Saved Mix colors and all weapon parts are now restored.
            LayeredToonMaterialController.InitializeAll(slot.equipedItem);

            // 註冊 hitbox。必須在零件掛完之後 —— hitbox 有可能長在零件上。
            // 也必須在 RecalculateFromEquipment 之前，因為那會觸發
            // OnHandWeaponDataChanged → AttackManager.ApplyHand 讀取這個欄位。
            slot.hitbox = slot.equipedItem.GetComponentInChildren<MeleeHitbox>(true);
            if (slot.hitbox == null)
            {
                Debug.LogWarning($"Equip (Melee): '{mw.itemName}' 上找不到 MeleeHitbox，" +
                                 $"這把武器揮空氣。檢查刀刃上有沒有掛該組件。", slot.equipedItem);
            }

            PlayerStats.Instance?.RecalculateFromEquipment();
            RefreshEquipmentStatBlock();
            OnSlotChanged?.Invoke(slotIndex);
            Debug.Log($"Equipped weapon '{mw.itemName}' to slot index {slotIndex}");
            return true;
        }
        else if (item is ShoulderWeaponInstance swi && swi.item is ShoulderWeapon sw)
        {
            // 1)
            slot.equipedItem = Instantiate(sw.weaponPrefab, mountPoint, false);
            slot.equipedItem.transform.localRotation = Quaternion.Euler(new Vector3(-90, 0, 0));
            slot.item = item;
            if (FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint") != null)
            {
                slot.muzzlePoint = FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint");
            }


            if (swi.colors != null && swi.colors.Count > 0 && !string.IsNullOrEmpty(swi.shaderName))
            {
                var rend = slot.equipedItem.GetComponentInChildren<Renderer>();
                if (rend)
                {
                    var mat = rend.material;
                    if (swi.shaderName.Contains("Mix 3") && swi.colors.Count >= 3)
                    {
                        mat.SetColor("_BaseColor", swi.colors[0]);
                        mat.SetColor("_Layer1Color", swi.colors[1]);
                        mat.SetColor("_Layer2Color", swi.colors[2]);
                    }
                    else if (swi.shaderName.Contains("Mix 4") && swi.colors.Count >= 4)
                    {
                        mat.SetColor("_BaseColor", swi.colors[0]);
                        mat.SetColor("_Layer1Color", swi.colors[1]);
                        mat.SetColor("_Layer2Color", swi.colors[2]);
                        mat.SetColor("_Layer3Color", swi.colors[3]);
                    }
                    else if (swi.shaderName.Contains("Mix 5") && swi.colors.Count >= 5)
                    {
                        mat.SetColor("_BaseColor", swi.colors[0]);
                        for (int k = 1; k < 5; k++)
                            mat.SetColor($"_Layer{k}Color", swi.colors[k]);
                    }
                }
            }
            // 2)
            if (swi.attachment != null)
            {
                foreach (var attach in swi.attachment)
                {
                    if (attach?.item is not ShoulderWeaponPart swp) continue;

                    foreach (var ap in sw.attachmentPoints)
                    {
                        if (ap.allowPart != attach.partType) continue;

                        var target = FindChildRecursive(slot.equipedItem.transform, ap.pointTransform.name);
                        if (target == null)
                        {
                            Debug.LogWarning($"Equip: cannot find mount '{ap.pointTransform.name}' on weapon instance");
                            continue;
                        }

                        var part = Instantiate(swp.shoulderWeaponPartPrefab, target, false);
                        part.transform.localPosition = Vector3.zero;
                        part.transform.localRotation = Quaternion.identity;
                        part.transform.localScale = Vector3.one;
                        if (FindChildRecursive(part.transform, "MuzzlePoint") != null)
                        {
                            slot.muzzlePoint = FindChildRecursive(part.transform, "MuzzlePoint");
                        }

                        if (attach.colors != null && attach.colors.Count > 0 && !string.IsNullOrEmpty(attach.shaderName))
                        {
                            var partRend = part.GetComponentInChildren<Renderer>();
                            if (partRend)
                            {
                                var mat = partRend.material;
                                if (attach.shaderName.Contains("Mix 3") && attach.colors.Count >= 3)
                                {
                                    mat.SetColor("_BaseColor", attach.colors[0]);
                                    mat.SetColor("_Layer1Color", attach.colors[1]);
                                    mat.SetColor("_Layer2Color", attach.colors[2]);
                                }
                                else if (attach.shaderName.Contains("Mix 4") && attach.colors.Count >= 4)
                                {
                                    mat.SetColor("_BaseColor", attach.colors[0]);
                                    mat.SetColor("_Layer1Color", attach.colors[1]);
                                    mat.SetColor("_Layer2Color", attach.colors[2]);
                                    mat.SetColor("_Layer3Color", attach.colors[3]);
                                }
                                else if (attach.shaderName.Contains("Mix 5") && attach.colors.Count >= 5)
                                {
                                    mat.SetColor("_BaseColor", attach.colors[0]);
                                    for (int k = 1; k < 5; k++)
                                        mat.SetColor($"_Layer{k}Color", attach.colors[k]);
                                }
                            }
                        }
                    }
                }
            }

            if (slot.muzzlePoint == null)
                Debug.LogWarning($"Equip: '{sw.itemName}' 的模型和零件上都找不到 'MuzzlePoint'，這把肩武器不會開火", slot.equipedItem);

            // Saved Mix colors and all shoulder-weapon parts are now restored.
            LayeredToonMaterialController.InitializeAll(slot.equipedItem);

            PlayerStats.Instance?.RecalculateFromEquipment();
            RefreshEquipmentStatBlock();
            OnSlotChanged?.Invoke(slotIndex);
            Debug.Log($"Equipped weapon '{sw.itemName}' to slot index {slotIndex}");
            return true;
        }
        else
        {
            return false;
        }
    }

    public void CleanEquipmentSlot(int equipmentSlotsIndex)
    {
        EquipmentSlot slot = equipmentSlots[equipmentSlotsIndex];

        // 卸下腿甲 → 原本的腿恢復顯示（原本寫在 InventoryManager.Unequip）
        if (slot.item?.item != null && slot.item.item.type == ItemType.LegsArmor)
            BoneCombiner.Instance?.ShowLegs();

        if (slot.equipedItem)
        {
            if (slot.id == EquipmentSlotId.LeftHandWeapon)
                DetachMuzzleFlashToHolder(leftMuzzleFlash, leftMuzzleFlashHolder);
            else if (slot.id == EquipmentSlotId.RightHandWeapon)
                DetachMuzzleFlashToHolder(rightMuzzleFlash, rightMuzzleFlashHolder);

            Destroy(slot.equipedItem);
        }

        slot.equipedItem = null;
        slot.item = null;
        slot.muzzlePoint = null;
        slot.hitbox = null;
        PlayerStats.Instance?.RecalculateFromEquipment();
        RefreshEquipmentStatBlock();
        OnSlotChanged?.Invoke(equipmentSlotsIndex);
    }

    // 近戰武器的握把對位。必須在所有零件掛完之後呼叫。
    //
    // 兩段式：
    //   1) 用武器本體 SO 的 mainHandGrip 做完整對位（位置 + 旋轉）—— 維持既有行為
    //   2) 有裝 Handle 零件 → 再做一次純平移，讓零件的 mainHandGripOffset 落到掛點
    //
    // 旋轉只由第 1 步決定。裝長柄在物理上就是「握點沿桿子往下滑」，是純位移，
    // 刀刃朝向不變。順序不能反 —— TransformPoint 要等旋轉定案後才算得對。
    //
    // 效果：握點被推到柄上 → 刀刃自然離手更遠 → 攻擊距離自動變長，
    //       所以 hitbox 不需要任何依 swordLength 的縮放邏輯。
    private void AlignMeleeGrip(Transform weaponRoot, Transform mountPoint, MeleeWeapon mw,
                                GameObject handlePartGO, MeleeWeaponPart handlePartSO)
    {
        if (weaponRoot == null || mountPoint == null || mw == null) return;

        // 1) 本體握把：位置 + 旋轉
        var baseGripName = (mw.mainHandGrip != null) ? mw.mainHandGrip.name : null;
        var baseGrip = string.IsNullOrEmpty(baseGripName)
            ? null
            : FindChildRecursive(weaponRoot, baseGripName);

        if (baseGrip != null)
        {
            SnapRootSoChildMatches(weaponRoot, baseGrip, mountPoint, Quaternion.identity);
        }
        else
        {
            Debug.LogWarning($"Equip (Melee): 找不到 '{mw.itemName}' 的握把節點 '{baseGripName}'，" +
                             $"武器將沿用 prefab 相對掛點的原始姿態。");
        }

        // 2) Handle 零件的握點：純平移
        if (handlePartGO != null && handlePartSO != null)
        {
            Vector3 gripWorld = handlePartGO.transform.TransformPoint(handlePartSO.mainHandGripOffset);
            weaponRoot.position += (mountPoint.position - gripWorld);
        }

        weaponRoot.SetParent(mountPoint, true);
    }
    private static void SnapRootSoChildMatches(Transform root, Transform child, Transform target, Quaternion childRotationOffset)
    {
        if (root == null || child == null || target == null) return;


        Quaternion desiredChildRot = target.rotation * childRotationOffset;
        Quaternion deltaRot = desiredChildRot * Quaternion.Inverse(child.rotation);
        root.rotation = deltaRot * root.rotation;


        Vector3 deltaPos = target.position - child.position;
        root.position += deltaPos;
    }

    private static Transform FindChildRecursive(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            var c = FindChildRecursive(root.GetChild(i), name);
            if (c) return c;
        }
        return null;
    }

    private void ApplyCoatingColors(GameObject fxRoot, List<Color> colors)
    {
        if (fxRoot == null || colors == null || colors.Count == 0) return;

        var ecc = fxRoot.GetComponentInParent<EffectColorController>()
               ?? fxRoot.GetComponentInChildren<EffectColorController>(true);

        if (ecc == null) return;

        ecc.colors = new List<Color>(colors);
        ecc.ApplyFromColorsList();
    }

    private static void ParentMuzzleFlashToMuzzle(Transform muzzleFlash, Transform muzzle)
    {
        if (muzzleFlash == null || muzzle == null) return;


        muzzleFlash.SetParent(muzzle, false);
        muzzleFlash.localPosition = Vector3.zero;
        muzzleFlash.localRotation = Quaternion.identity;
        muzzleFlash.localScale = Vector3.one;


        var ps = muzzleFlash.GetComponentInChildren<ParticleSystem>();
        if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }


    private static void DetachMuzzleFlashToHolder(Transform muzzleFlash, Transform holder)
    {
        if (muzzleFlash == null || holder == null) return;

        muzzleFlash.SetParent(holder, false);
        muzzleFlash.localPosition = Vector3.zero;
        muzzleFlash.localRotation = Quaternion.identity;
        muzzleFlash.localScale = Vector3.one;

        var ps = muzzleFlash.GetComponentInChildren<ParticleSystem>();
        if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (equipmentSlots == null) return;
        foreach (var s in equipmentSlots)
            s?.MigrateIfNeeded();
    }
#endif
}