using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 一套完整的裝備（護甲 + 武器），對應 EquipmentManager.equipmentSlots 的每一格。
///
/// 用途：戰鬥中由貝努鳥號射出、讓 Bradamant 換上的「一套白銀之鎧」。
/// 單場戰鬥可用三套 → 之後由換裝系統持有三個 EquipmentLoadout。
///
/// 存的是「裝備實例」的深拷貝（ItemInstance 及其子類別），不是 ItemObject 而已：
///   - 隨機詞條（buffs）、鍛造後的新名稱、顏色（colors / shaderName）、武器零件（attachment）都會保留。
///   - 跟背包裡的那一件「不是同一個物件」：之後改背包裡的裝備，不會影響已存好的這一套。
///   - 場景物件的參照（muzzlePoint、hitbox）不存，裝備時由 EquipmentManager 重新填入。
///
/// 建立：Project 視窗 → Create → Inventory → Equipment Loadout。
/// 存檔：Play Mode 中先在背包裡穿好裝備，對這個 asset 按右上角 ⋮ → Capture Current Equipment。
///       Play Mode 對 ScriptableObject 的修改會留在 asset 上 —— 正好拿來做展會用的預設三套。
///
/// 注意：build 出來的遊戲裡，執行期間寫進 ScriptableObject 的內容「不會」存到硬碟，
///       關掉遊戲就沒了。玩家自訂套裝的存檔之後要另外接存檔系統（SaveManager 目前是空的）。
/// </summary>
[CreateAssetMenu(fileName = "New Equipment Loadout", menuName = "Inventory/Equipment Loadout")]
public class EquipmentLoadout : ScriptableObject
{
    [System.Serializable]
    public class Entry
    {
        [Tooltip("這格的身分。套用時以這個為準（格子順序改了也不會裝錯）。")]
        public EquipmentSlotId slotId;

        [Tooltip("存檔當下在 EquipmentManager.equipmentSlots 的索引。slotId 是 None 的舊資料才會用到。")]
        public int slotIndex;

        [Tooltip("這格存的裝備（深拷貝）。")]
        [SerializeReference] public ItemInstance item;
    }

    [Tooltip("這一套的名稱（UI 顯示用）。")]
    public string loadoutName;

    [Tooltip("UI 顯示用的圖示（可留空）。")]
    public Sprite icon;

    [SerializeField] private List<Entry> entries = new List<Entry>();

    /// <summary>所有有裝東西的格子。</summary>
    public IReadOnlyList<Entry> Entries => entries;

    public bool IsEmpty => entries.Count == 0;

    /// <summary>取得某一格存的裝備；沒有就回傳 null。</summary>
    public ItemInstance GetItem(int slotIndex)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].slotIndex == slotIndex) return entries[i].item;
        return null;
    }

    /// <summary>取得某個 id 的格子存的裝備；沒有就回傳 null。</summary>
    public ItemInstance GetItem(EquipmentSlotId slotId)
    {
        if (slotId == EquipmentSlotId.None) return null;
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].slotId == slotId) return entries[i].item;
        return null;
    }

    public void Clear() => entries.Clear();

    /// <summary>
    /// 把 EquipmentManager 目前穿著的裝備整套存進來（覆蓋原本的內容）。
    /// 空的格子不存。回傳存了幾格。
    /// </summary>
    public int CaptureFrom(EquipmentManager manager)
    {
        if (manager == null)
        {
            Debug.LogWarning($"EquipmentLoadout '{name}': EquipmentManager is null, nothing captured.", this);
            return 0;
        }

        entries.Clear();

        var slots = manager.equipmentSlots;
        for (int i = 0; i < slots.Count; i++)
        {
            ItemInstance src = slots[i]?.item;
            if (src == null || src.item == null) continue;

            entries.Add(new Entry { slotId = slots[i].id, slotIndex = i, item = Clone(src) });
        }

        MarkDirty();
        return entries.Count;
    }

    // ── 深拷貝 ───────────────────────────────────────────────────────────
    //
    // ItemInstance 是一般的 class（不是 ScriptableObject），直接存參照的話，
    // 存的就是背包裡那一件本身 —— 背包裡改了，這一套也跟著變。所以要複製一份。
    // EquipmentBuff 是 struct，new List<>(src) 就是完整複製。

    public static ItemInstance Clone(ItemInstance src)
    {
        if (src == null) return null;

        switch (src)
        {
            case RangeWeaponInstance r:
                return new RangeWeaponInstance
                {
                    item = r.item,
                    amount = r.amount,
                    newWeaponName = r.newWeaponName,
                    buffs = CopyList(r.buffs),
                    attachment = ClonePartList(r.attachment),
                    colors = CopyList(r.colors),
                    shaderName = r.shaderName,
                };

            case MeleeWeaponInstance m:
                return new MeleeWeaponInstance
                {
                    item = m.item,
                    amount = m.amount,
                    newWeaponName = m.newWeaponName,
                    buffs = CopyList(m.buffs),
                    attachment = ClonePartList(m.attachment),
                    colors = CopyList(m.colors),
                    shaderName = m.shaderName,
                };

            case ShoulderWeaponInstance s:
                return new ShoulderWeaponInstance
                {
                    item = s.item,
                    amount = s.amount,
                    newWeaponName = s.newWeaponName,
                    buffs = CopyList(s.buffs),
                    attachment = ClonePartList(s.attachment),
                    colors = CopyList(s.colors),
                    shaderName = s.shaderName,
                };

            case ArmorInstance a:
                return new ArmorInstance
                {
                    item = a.item,
                    amount = a.amount,
                    buffs = CopyList(a.buffs),
                    colors = CopyList(a.colors),
                    shaderName = a.shaderName,
                };

            case PartInstance p:
                return ClonePart(p);

            default:
                if (src.GetType() != typeof(ItemInstance))
                    Debug.LogWarning($"EquipmentLoadout.Clone: unknown ItemInstance type {src.GetType().Name}, " +
                                     "only the base fields were copied.");
                return new ItemInstance { item = src.item, amount = src.amount };
        }
    }

    private static PartInstance ClonePart(PartInstance p)
    {
        if (p == null) return null;
        return new PartInstance
        {
            item = p.item,
            amount = p.amount,
            partType = p.partType,
            buffs = CopyList(p.buffs),
            colors = CopyList(p.colors),
            shaderName = p.shaderName,
        };
    }

    private static List<PartInstance> ClonePartList(List<PartInstance> src)
    {
        // 保留 null 的位置：attachment 的索引對應武器的掛載點，空的掛點是 null
        if (src == null) return null;
        var list = new List<PartInstance>(src.Count);
        for (int i = 0; i < src.Count; i++) list.Add(ClonePart(src[i]));
        return list;
    }

    private static List<T> CopyList<T>(List<T> src) => src != null ? new List<T>(src) : new List<T>();

    // ── 編輯器 ───────────────────────────────────────────────────────────

    private void MarkDirty()
    {
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

#if UNITY_EDITOR
    [ContextMenu("Capture Current Equipment")]
    private void CaptureCurrentEquipmentMenu()
    {
        if (!Application.isPlaying)
        {
            Debug.LogWarning("Capture Current Equipment：請在 Play Mode 中、穿好裝備之後再按。", this);
            return;
        }

        int n = CaptureFrom(EquipmentManager.Instance);
        UnityEditor.AssetDatabase.SaveAssetIfDirty(this);
        Debug.Log($"EquipmentLoadout '{name}': captured {n} slot(s).", this);
    }

    [ContextMenu("Clear")]
    private void ClearMenu()
    {
        Clear();
        MarkDirty();
    }
#endif
}
