﻿using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

#region Instances (keep in this file for compatibility)

[System.Serializable]
public class ItemInstance
{
    [Tooltip("唯一 id。加入背包時自動產生，之後不變。裝備格、三套裝備、存檔都用它認物品。")]
    public string uid;

    public ItemObject item;
    public int amount;
}

[System.Serializable]
public class RangeWeaponInstance : ItemInstance
{
    public string newWeaponName;                 // 用來存鍛造後的新名稱
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();
    public List<PartInstance> attachment;

    // 用來存不同 shader 的顏色
    [SerializeField] public List<Color> colors = new List<Color>();

    // 記錄此裝備用的 shader 名稱，方便還原
    [SerializeField] public string shaderName;
}

[System.Serializable]
public class MeleeWeaponInstance : ItemInstance
{
    public string newWeaponName;                 // 用來存鍛造後的新名稱
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();
    public List<PartInstance> attachment;
    // 用來存不同 shader 的顏色
    [SerializeField] public List<Color> colors = new List<Color>();

    // 記錄此裝備用的 shader 名稱，方便還原
    [SerializeField] public string shaderName;
}

[System.Serializable]
public class ShoulderWeaponInstance : ItemInstance
{
    public string newWeaponName;                 // 用來存鍛造後的新名稱
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();
    public List<PartInstance> attachment;

    // 用來存不同 shader 的顏色
    [SerializeField] public List<Color> colors = new List<Color>();

    // 記錄此裝備用的 shader 名稱，方便還原
    [SerializeField] public string shaderName;
}

[System.Serializable]
public class ArmorInstance : ItemInstance
{
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();

    [SerializeField] public List<Color> colors = new List<Color>();
    [SerializeField] public string shaderName;
}

[System.Serializable]
public class PartInstance : ItemInstance
{
    public WeaponPartType partType;
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();

    [SerializeField] public List<Color> colors = new List<Color>();
    [SerializeField] public string shaderName;
}

#endregion

public class InventoryManager : MonoBehaviour
{
    public static InventoryManager Instance { get; private set; }

    [Header("Inventory Slot")]
    [SerializeReference] public List<ItemInstance> inventory = new();

    [Header("UI Button Prefab")]
    public GameObject buttonPrefab;
    public Transform itemsButtonParent;      // 右邊清單按鈕容器
    public Transform inventoryButtonParent;  // 左邊裝備槽按鈕容器
    [Header("UI Button Image")]
    public Sprite scopeIconImage;
    public Sprite barrelIconImage;
    public Sprite gunIconImage;
    public Sprite melIconImage;
    public Sprite handleIconImage;
    public Sprite coatingIconImage;

    [Header("Color Block/ Inventory Block")]
    public GameObject inventoryBlock;
    public GameObject characterColorBlock;
    public GameObject statBlock;

    [Header("Color Panel")]
    public ColorPicker characterEquipmentColorPicker;

    [Header("Button Toggle Group")]
    public ToggleGroup inventoryToggleGroup;         // 裝備槽 ToggleGroup
    public ToggleGroup WeaponPartsColorToggleGroup;  // 槍/零件 ToggleGroup

    [Header("Page Switch")]
    [SerializeField] private UIPageSwitch pageSwitch;
    private GameObject currentPage;

    [Header("Weapon Parts Buttons")]
    public Transform partsButtonParent;
    public GameObject partButtonPrefab;
    public Color selectedColor;
    public Color normalColor;

    // 紀錄右邊清單 Toggle 對應的物品
    private readonly Dictionary<Toggle, ItemInstance> toggleItemMap = new();

    private bool IsColorMode => characterColorBlock != null && characterColorBlock.activeSelf;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // 場景裡預先放好的物品可能沒有 uid，或在 Inspector 複製元素時帶了重複的 uid
        int fixedCount = EnsureUniqueUids();
        if (fixedCount > 0)
            Debug.Log($"InventoryManager: 補上 / 重新產生了 {fixedCount} 件物品的 uid", this);
    }

    // ===== 物品 uid =====

    private static string NewUid() => Guid.NewGuid().ToString("N");

    /// <summary>所有放進背包的物品都經過這裡：沒有 uid 的先補上。</summary>
    private void AddToInventory(ItemInstance inst)
    {
        if (inst == null) return;
        if (string.IsNullOrEmpty(inst.uid)) inst.uid = NewUid();
        inventory.Add(inst);
    }

    /// <summary>
    /// 整個背包換成 items（讀檔用）。物品原本的 uid 會保留。
    /// 呼叫前要先卸下身上的裝備（EquipmentManager.UnequipAll），不然身上穿的會是已經不在背包裡的舊物件。
    /// </summary>
    public void ReplaceInventory(List<ItemInstance> items)
    {
        inventory.Clear();
        if (items != null)
            foreach (var inst in items) AddToInventory(inst);

        int fixedCount = EnsureUniqueUids();
        if (fixedCount > 0)
            Debug.LogWarning($"ReplaceInventory: {fixedCount} 件物品的 uid 缺少或重複，已重新產生", this);

        ClearInventoryButton(); // 右側清單的按鈕還指著舊物件
    }

    /// <summary>用 uid 找背包裡的物品；找不到回傳 null。</summary>
    public ItemInstance FindByUid(string uid)
    {
        if (string.IsNullOrEmpty(uid)) return null;
        for (int i = 0; i < inventory.Count; i++)
        {
            var inst = inventory[i];
            if (inst != null && inst.uid == uid) return inst;
        }
        return null;
    }

    // 沒有 uid 或 uid 重複的物品重新產生。回傳處理了幾件。
    private int EnsureUniqueUids()
    {
        if (inventory == null) return 0;
        var seen = new HashSet<string>();
        int fixedCount = 0;
        for (int i = 0; i < inventory.Count; i++)
        {
            var inst = inventory[i];
            if (inst == null) continue;
            if (string.IsNullOrEmpty(inst.uid) || seen.Contains(inst.uid))
            {
                inst.uid = NewUid();
                fixedCount++;
            }
            seen.Add(inst.uid);
        }
        return fixedCount;
    }

#if UNITY_EDITOR
    // 編輯模式下就補好，存場景時 uid 會一起存進去
    private void OnValidate()
    {
        if (Application.isPlaying) return;
        if (EnsureUniqueUids() > 0)
            UnityEditor.EditorUtility.SetDirty(this);
    }
#endif

    // 在 Start 訂閱（所有 Awake 之後，EquipmentManager.Instance 一定已設好），
    // OnDestroy 才退訂：UI 頁面關著的時候換裝（例如換裝射出），圖示一樣會更新。
    private void Start()
    {
        if (EquipmentManager.Instance != null)
        {
            EquipmentManager.Instance.OnSlotChanged += HandleEquipmentSlotChanged;
            EquipmentManager.Instance.OnActiveSetChanged += HandleActiveSetChanged;
        }
        else
            Debug.LogWarning("InventoryManager: 找不到 EquipmentManager，裝備格圖示不會自動更新");
    }

    private void OnDestroy()
    {
        if (EquipmentManager.Instance != null)
        {
            EquipmentManager.Instance.OnSlotChanged -= HandleEquipmentSlotChanged;
            EquipmentManager.Instance.OnActiveSetChanged -= HandleActiveSetChanged;
        }
    }

    // 切換套裝後，右側清單的「已裝備」鎖定和上色頁都還是上一套的狀態，重開一次目前的頁面
    private void HandleActiveSetChanged(int setIndex)
    {
        if (GetSelectedSlotIndex() < 0)
        {
            ClearInventoryButton();
            return;
        }

        if (IsColorMode) OpenColorPage();
        else OpenInventoryPage();
    }

    // 裝備格的圖示 / 卸下按鈕，一律由 EquipmentManager.OnSlotChanged 驅動
    private void HandleEquipmentSlotChanged(int slotIndex)
    {
        var slots = EquipmentManager.Instance != null ? EquipmentManager.Instance.equipmentSlots : null;
        if (slots == null || slotIndex < 0 || slotIndex >= slots.Count) return;

        var slot = slots[slotIndex];
        bool hasEquipment = slot.equipedItem != null;

        SetEquipmentSlotIcon(slotIndex, hasEquipment ? slot.item?.item?.icon : null);

        // 卸下按鈕只顯示在目前選中的格子（和 OpenPartsInventory 的規則一致）
        if (slotIndex == GetSelectedSlotIndex())
            SetRemoveButtonForSlot(slotIndex, hasEquipment);
    }

    #region Public entry for Equipment Slot Toggles (IMPORTANT)

    // 主人：把每個「裝備槽 Toggle」的 OnValueChanged(bool) 只綁這個就好
    public void OnEquipmentSlotToggleChanged(bool isOn)
    {
        // ON：照舊，走「開背包」或「切上色目標」
        if (isOn)
        {
            SFXManager.Instance.PlayFeedback("ClickFeedback");
            if (IsColorMode)
            {
                OpenColorPage();
            }
            else
            {
                OpenInventoryPage();
            }
            return;
        }

        // OFF：如果目前已經沒有任何裝備槽被選中，就要把 UI 清乾淨
        bool anySlotSelected = inventoryToggleGroup != null && inventoryToggleGroup.AnyTogglesOn();
        if (anySlotSelected) return;

        SetAllRemoveButtonsActive(false);

        if (IsColorMode)
        {
            // Color Mode：清空零件按鈕 & ColorPicker 目標
            CleanPartButtons();
            ClearColorPickerState();
            return;
        }

        // Inventory Mode：清空右側清單，回到 Stat Page
        ClearInventoryButton();
        ShowStatPage();
    }

    #endregion

    #region Inventory CRUD

    public void AddItemToInventory(ItemObject item)
    {
        if (item == null) return;

        if (item is Armor a)
        {
            var inst = new ArmorInstance
            {
                item = item,
                amount = 1,
            };

            // 讀顏色
            if (a.skinnedMeshRenderer && a.skinnedMeshRenderer.sharedMaterial)
            {
                var mat = a.skinnedMeshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            foreach (EquipmentBuff buff in a.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = a.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }

        if (item is RangeWeapon rw)
        {
            var attachmentPoints = new List<PartInstance>();
            foreach (var ap in rw.attachmentPoints)
            {
                attachmentPoints.Add(new PartInstance
                {
                    item = null,
                    amount = 0,
                    partType = ap.allowPart
                });
            }

            var inst = new RangeWeaponInstance
            {
                item = item,
                amount = 1,
                attachment = attachmentPoints,
            };

            // 讀顏色（修正：Mix5 也要把 BaseColor 放進來）
            if (rw.meshRenderer && rw.meshRenderer.sharedMaterial)
            {
                var mat = rw.meshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            foreach (EquipmentBuff buff in rw.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = rw.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }

        if (item is RangeWeaponPart rwp)
        {
            var inst = new PartInstance
            {
                item = item,
                amount = 1,
                partType = rwp.partType
            };

            if (rwp.meshRenderer && rwp.meshRenderer.sharedMaterial)
            {
                var mat = rwp.meshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            foreach (EquipmentBuff buff in rwp.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = rwp.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }

        if (item is MeleeWeapon mw)
        {
            var attachmentPoints = new List<PartInstance>();
            foreach (var ap in mw.attachmentPoints)
            {
                attachmentPoints.Add(new PartInstance
                {
                    item = null,
                    amount = 0,
                    partType = ap.allowPart
                });
            }

            var inst = new MeleeWeaponInstance
            {
                item = item,
                amount = 1,
                attachment = attachmentPoints,
            };

            // 讀顏色（修正：Mix5 也要把 BaseColor 放進來）
            if (mw.meshRenderer && mw.meshRenderer.sharedMaterial)
            {
                var mat = mw.meshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            foreach (EquipmentBuff buff in mw.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = mw.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }
        if (item is MeleeWeaponPart mwp)
        {
            var inst = new PartInstance
            {
                item = item,
                amount = 1,
                partType = mwp.partType
            };

            // 讀顏色（跟 RangeWeaponPart 同規格：Mix3/4/5）
            if (mwp.meshRenderer && mwp.meshRenderer.sharedMaterial)
            {
                var mat = mwp.meshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            // buffs + random buff
            foreach (EquipmentBuff buff in mwp.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = mwp.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }
        if (item is MeleeWeaponCoating mwc)
        {
            var inst = new PartInstance
            {
                item = item,
                amount = 1,
                partType = mwc.partType
            };

            // -------------------------
            // 讀粒子顏色（從 EffectColorController）
            // -------------------------
            EffectColorController ecc = null;

            // 如果主人有在 SO 上手動指定，就直接用（你 SO 裡有這個欄位）
            if (mwc.effectColorController != null)
            {
                ecc = mwc.effectColorController;
            }
            else if (mwc.meleeCoatingPrefab != null)
            {
                // 主人說 controller 在 prefab 的 parent 物件上，所以優先 InParent
                ecc = mwc.meleeCoatingPrefab.GetComponentInParent<EffectColorController>();

                // 保險：如果主人其實拖的是 root 或結構不同，就再找 children
                if (ecc == null)
                    ecc = mwc.meleeCoatingPrefab.GetComponentInChildren<EffectColorController>(true);
            }

            if (ecc != null)
            {
                // prefab 沒有跑 Start()，所以要手動 cache
                ecc.CacheColorsFromGroups();

                // 把顏色 snapshot 存進 instance（避免之後 prefab 顏色改了影響已入庫物品）
                inst.colors = new List<Color>(ecc.colors);

                // shaderName 對粒子沒意義，但可以當 marker（避免你之後誤判 Mix3/4/5）
                inst.shaderName = "EffectColorController";
            }

            // -------------------------
            // buffs + random buff（你原本的）
            // -------------------------
            foreach (EquipmentBuff buff in mwc.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = mwc.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }
        if (item is ShoulderWeapon sw)
        {
            var attachmentPoints = new List<PartInstance>();
            foreach (var ap in sw.attachmentPoints)
            {
                attachmentPoints.Add(new PartInstance
                {
                    item = null,
                    amount = 0,
                    partType = ap.allowPart
                });
            }

            var inst = new ShoulderWeaponInstance
            {
                item = item,
                amount = 1,
                attachment = attachmentPoints,
            };

            // 讀顏色（修正：Mix5 也要把 BaseColor 放進來）
            if (sw.meshRenderer && sw.meshRenderer.sharedMaterial)
            {
                var mat = sw.meshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            foreach (EquipmentBuff buff in sw.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = sw.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }

        if (item is ShoulderWeaponPart swp)
        {
            var inst = new PartInstance
            {
                item = item,
                amount = 1,
                partType = swp.partType
            };

            if (swp.meshRenderer && swp.meshRenderer.sharedMaterial)
            {
                var mat = swp.meshRenderer.sharedMaterial;
                inst.shaderName = mat.shader.name;

                if (inst.shaderName.Contains("Mix 3"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                }
                else if (inst.shaderName.Contains("Mix 4"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    inst.colors.Add(mat.GetColor("_Layer1Color"));
                    inst.colors.Add(mat.GetColor("_Layer2Color"));
                    inst.colors.Add(mat.GetColor("_Layer3Color"));
                }
                else if (inst.shaderName.Contains("Mix 5"))
                {
                    inst.colors.Add(mat.GetColor("_BaseColor"));
                    for (int i = 1; i < 5; i++)
                        inst.colors.Add(mat.GetColor($"_Layer{i}Color"));
                }
            }

            foreach (EquipmentBuff buff in swp.buffs)
                inst.buffs.Add(buff);

            var pickedBuff = swp.GetRandomBuff();
            if (pickedBuff != null)
                inst.buffs.Add(pickedBuff.buff);

            AddToInventory(inst);
            return;
        }

        // 只對「一般 ItemInstance」做堆疊
        int stackMax = Mathf.Max(1, item.maxStack);
        if (stackMax > 1)
        {
            for (int i = 0; i < inventory.Count; i++)
            {
                var inst = inventory[i];
                if (inst == null) continue;

                // 避免把 RangeWeaponInstance / ArmorInstance 之類的也拿去疊
                if (inst.GetType() != typeof(ItemInstance)) continue;

                if (inst.item == item && inst.amount < stackMax)
                {
                    inst.amount += 1;
                    return;
                }
            }
        }

        AddToInventory(new ItemInstance { item = item, amount = 1 });
    }

    public void RemoveItemFromInventory(ItemInstance itemInstance)
    {
        if (itemInstance == null) return;
        int idx = inventory.IndexOf(itemInstance);
        if (idx >= 0) inventory.RemoveAt(idx);
    }

    public void AddCraftedRangeWeaponToInventory(RangeWeaponInstance baseWeaponInstance, List<PartInstance> rangeWeaponParts)
    {
        if (baseWeaponInstance == null)
        {
            Debug.LogWarning("AddCraftedRangeWeaponToInventory: baseWeaponInstance is null");
            return;
        }

        var newInst = new RangeWeaponInstance
        {
            item = baseWeaponInstance.item,
            amount = 1,
            newWeaponName = baseWeaponInstance.newWeaponName,
            buffs = new List<EquipmentBuff>(baseWeaponInstance.buffs),
            attachment = new List<PartInstance>(),
            shaderName = baseWeaponInstance.shaderName,
            colors = baseWeaponInstance.colors != null ? new List<Color>(baseWeaponInstance.colors) : new List<Color>()
        };

        if (rangeWeaponParts != null)
        {
            foreach (var part in rangeWeaponParts)
                if (part != null) newInst.attachment.Add(part);
        }

        AddToInventory(newInst);
    }

    public void AddCraftedMeleeWeaponToInventory(MeleeWeaponInstance baseWeaponInstance, List<PartInstance> meleeWeaponParts)
    {
        if (baseWeaponInstance == null)
        {
            Debug.LogWarning("AddCraftedMeleeWeaponToInventory: baseWeaponInstance is null");
            return;
        }

        var newInst = new MeleeWeaponInstance
        {
            item = baseWeaponInstance.item,
            amount = 1,
            newWeaponName = baseWeaponInstance.newWeaponName,
            buffs = new List<EquipmentBuff>(baseWeaponInstance.buffs),
            attachment = new List<PartInstance>(),
            shaderName = baseWeaponInstance.shaderName,
            colors = baseWeaponInstance.colors != null ? new List<UnityEngine.Color>(baseWeaponInstance.colors) : new List<UnityEngine.Color>()
        };

        if (meleeWeaponParts != null)
        {
            foreach (var part in meleeWeaponParts)
                if (part != null) newInst.attachment.Add(part);
        }

        AddToInventory(newInst);
    }

    public void AddCraftedShoulderWeaponToInventory(ShoulderWeaponInstance baseWeaponInstance, List<PartInstance> rangeWeaponParts)
    {
        if (baseWeaponInstance == null)
        {
            Debug.LogWarning("AddCraftedRangeWeaponToInventory: baseWeaponInstance is null");
            return;
        }

        var newInst = new ShoulderWeaponInstance
        {
            item = baseWeaponInstance.item,
            amount = 1,
            newWeaponName = baseWeaponInstance.newWeaponName,
            buffs = new List<EquipmentBuff>(baseWeaponInstance.buffs),
            attachment = new List<PartInstance>(),
            shaderName = baseWeaponInstance.shaderName,
            colors = baseWeaponInstance.colors != null ? new List<Color>(baseWeaponInstance.colors) : new List<Color>()
        };

        if (rangeWeaponParts != null)
        {
            foreach (var part in rangeWeaponParts)
                if (part != null) newInst.attachment.Add(part);
        }

        AddToInventory(newInst);
    }

    #endregion

    #region Inventory Page Openers (keep for Inspector)

    public void OpenWeaponInventory() => OpenPartsInventory(ItemType.RangeWeapon);
    public void OpenHeadArmorInventory() => OpenPartsInventory(ItemType.HeadArmor);
    public void OpenChestArmorInventory() => OpenPartsInventory(ItemType.ChestArmor);
    public void OpenLeftHandInventory() => OpenPartsInventory(ItemType.LeftHandArmor);
    public void OpenRightHandInventory() => OpenPartsInventory(ItemType.RightHandArmor);
    public void OpenWaistArmorInventory() => OpenPartsInventory(ItemType.WaistArmor);
    public void OpenLegsArmorInventory() => OpenPartsInventory(ItemType.LegsArmor);
    public void OpenThrusterInventory() => OpenPartsInventory(ItemType.Thruster);
    public void OpenConsumableInventory() => OpenPartsInventory(ItemType.Consumable);
    public void OpenMaterialInventory() => OpenPartsInventory(ItemType.Material);

    #endregion

    #region Inventory List UI (right panel)

    public void OpenInventoryPage()
    {
        int index = GetSelectedSlotIndex();
        var slots = EquipmentManager.Instance.equipmentSlots;
        if (index < 0 || index >= slots.Count)
        {
            Debug.LogWarning($"OpenInventoryPage: invalid slot index {index}");
            ClearInventoryButton();
            ShowStatPage();
            SetAllRemoveButtonsActive(false);
            return;
        }
        OpenPartsInventory(slots[index].equipmentTypes);
    }

    public void OpenPartsInventory(ItemType itemType)
    {
        // Color mode 時：不要在這裡開 inventory 清單（避免裝備槽點一下又開清單）
        if (IsColorMode) return;

        bool anySlotSelected = inventoryToggleGroup != null && inventoryToggleGroup.AnyTogglesOn();
        if (!anySlotSelected)
        {
            ClearInventoryButton();
            ShowStatPage();
            SetAllRemoveButtonsActive(false);
            return;
        }

        ClearInventoryButton();
        int slotIndex = GetSelectedSlotIndex();

        SetAllRemoveButtonsActive(false);

        bool slotHasEquipment = false;
        if (slotIndex >= 0 &&
            EquipmentManager.Instance != null &&
            slotIndex < EquipmentManager.Instance.equipmentSlots.Count)
        {
            slotHasEquipment = EquipmentManager.Instance.equipmentSlots[slotIndex].equipedItem != null;
        }

        if (slotHasEquipment)
            SetRemoveButtonForSlot(slotIndex, true);

        foreach (var inv in inventory)
        {
            if (inv == null || inv.item == null) continue;
            if (inv.item.type != itemType) continue;

            var button = Instantiate(buttonPrefab, itemsButtonParent);

            ItemInstance capturedItem = inv;

            var trigger = button.GetComponent<InventoryItemTooltipTrigger>();
            if (trigger == null)
                trigger = button.AddComponent<InventoryItemTooltipTrigger>();

            trigger.Setup(capturedItem);

            // icon
            var icon = button.transform.Find("Item Icon")?.GetComponent<Image>();
            if (icon != null) icon.sprite = inv.item.icon;

            // name
            var label = button.transform.Find("Item Name")?.GetComponent<TMP_Text>();
            if (label != null)
            {
                if (inv is RangeWeaponInstance rwi && !string.IsNullOrEmpty(rwi.newWeaponName))
                    label.text = rwi.newWeaponName;
                else
                    label.text = inv.item.itemName;
            }

            var tgl = button.GetComponent<Toggle>();
            if (tgl == null) continue;

            toggleItemMap[tgl] = capturedItem;

            // 先鎖住：已裝備中的物品
            tgl.interactable = !IsItemEquippedAnywhere(capturedItem);

            tgl.onValueChanged.AddListener(isOn =>
            {
                if (!isOn) return;
                SFXManager.Instance.PlayFeedback("ClickFeedback");
                OnClickInventoryItem(capturedItem, tgl);
            });
        }

        ShowInventoryPage();
    }
    public void OpenPartsInventory(List<ItemType> allowedTypes)
    {
        if (IsColorMode) return;

        bool anySlotSelected = inventoryToggleGroup != null && inventoryToggleGroup.AnyTogglesOn();
        if (!anySlotSelected)
        {
            ClearInventoryButton();
            ShowStatPage();
            SetAllRemoveButtonsActive(false);
            return;
        }

        if (allowedTypes == null || allowedTypes.Count == 0)
        {
            ClearInventoryButton();
            ShowStatPage();
            SetAllRemoveButtonsActive(false);
            return;
        }

        ClearInventoryButton();
        int slotIndex = GetSelectedSlotIndex();

        SetAllRemoveButtonsActive(false);

        bool slotHasEquipment = false;
        if (slotIndex >= 0 &&
            EquipmentManager.Instance != null &&
            slotIndex < EquipmentManager.Instance.equipmentSlots.Count)
        {
            slotHasEquipment = EquipmentManager.Instance.equipmentSlots[slotIndex].equipedItem != null;
        }

        if (slotHasEquipment)
            SetRemoveButtonForSlot(slotIndex, true);

        // 用 HashSet 提升 Contains 效率
        var allowed = new HashSet<ItemType>(allowedTypes);

        foreach (var inv in inventory)
        {
            if (inv == null || inv.item == null) continue;
            if (!allowed.Contains(inv.item.type)) continue;

            var button = Instantiate(buttonPrefab, itemsButtonParent);
            var trigger = button.GetComponent<InventoryItemTooltipTrigger>();
            if (trigger == null)
                trigger = button.AddComponent<InventoryItemTooltipTrigger>();

            trigger.Setup(inv);
            var icon = button.transform.Find("Item Icon")?.GetComponent<UnityEngine.UI.Image>();
            if (icon != null) icon.sprite = inv.item.icon;

            var label = button.transform.Find("Item Name")?.GetComponent<TMPro.TMP_Text>();
            if (label != null)
            {
                if (inv is RangeWeaponInstance rwi && !string.IsNullOrEmpty(rwi.newWeaponName))
                    label.text = rwi.newWeaponName;
                else
                    label.text = inv.item.itemName;
            }

            var tgl = button.GetComponent<UnityEngine.UI.Toggle>();
            if (tgl == null) continue;

            ItemInstance capturedItem = inv;
            toggleItemMap[tgl] = capturedItem;

            tgl.interactable = !IsItemEquippedAnywhere(capturedItem);

            tgl.onValueChanged.AddListener(isOn =>
            {
                if (!isOn) return;
                SFXManager.Instance.PlayFeedback("ClickFeedback");
                OnClickInventoryItem(capturedItem, tgl);
            });
        }

        ShowInventoryPage();
    }
    public void ClearInventoryButton()
    {
        if (itemsButtonParent != null)
        {
            for (int i = itemsButtonParent.childCount - 1; i >= 0; i--)
                Destroy(itemsButtonParent.GetChild(i).gameObject);
        }

        toggleItemMap.Clear();
    }

    private void OnClickInventoryItem(ItemInstance item, Toggle btn)
    {
        if (item == null || item.item == null)
        {
            btn.isOn = false;
            return;
        }

        // Weapon equip（需要 mountPoint）
        if (item is RangeWeaponInstance)
        {
            int slotIndex = GetSelectedSlotIndex();
            if (slotIndex < 0)
            {
                Debug.LogWarning("OnClickInventoryItem (weapon): no equipment slot selected");
                btn.isOn = false;
                return;
            }

            var slotButton = inventoryButtonParent.GetChild(slotIndex);
            var weaponTransform = slotButton.GetComponentInChildren<WeaponTransform>();
            if (weaponTransform == null || weaponTransform.weaponTransform == null)
            {
                Debug.LogWarning($"OnClickInventoryItem (weapon): WeaponTransform missing on slot {slotIndex}");
                btn.isOn = false;
                return;
            }

            Transform mountPoint = weaponTransform.weaponTransform;

            if (!EquipmentManager.Instance.Equip(slotIndex, item, mountPoint))
            {
                btn.isOn = false;
                return;
            }

            // 裝備格圖示 / 卸下按鈕由 OnSlotChanged 更新
            RefreshItemListLockStates(btn);

            // 鎖住當前已裝備的按鈕
            btn.interactable = false;
            return;
        }
        if (item is MeleeWeaponInstance)
        {
            int slotIndex = GetSelectedSlotIndex();
            if (slotIndex < 0)
            {
                Debug.LogWarning("OnClickInventoryItem (weapon): no equipment slot selected");
                btn.isOn = false;
                return;
            }

            var slotButton = inventoryButtonParent.GetChild(slotIndex);
            var weaponTransform = slotButton.GetComponentInChildren<WeaponTransform>();
            if (weaponTransform == null || weaponTransform.weaponTransform == null)
            {
                Debug.LogWarning($"OnClickInventoryItem (weapon): WeaponTransform missing on slot {slotIndex}");
                btn.isOn = false;
                return;
            }
            Transform mountPoint = weaponTransform.weaponTransform;

            if (!EquipmentManager.Instance.Equip(slotIndex, item, mountPoint))
            {
                btn.isOn = false;
                return;
            }

            // 裝備格圖示 / 卸下按鈕由 OnSlotChanged 更新
            RefreshItemListLockStates(btn);

            // 鎖住當前已裝備的按鈕
            btn.interactable = false;
            return;
        }
        if (item is ShoulderWeaponInstance)
        {
            int slotIndex = GetSelectedSlotIndex();
            if (slotIndex < 0)
            {
                Debug.LogWarning("OnClickInventoryItem (weapon): no equipment slot selected");
                btn.isOn = false;
                return;
            }

            var slotButton = inventoryButtonParent.GetChild(slotIndex);
            var weaponTransform = slotButton.GetComponentInChildren<WeaponTransform>();
            if (weaponTransform == null || weaponTransform.weaponTransform == null)
            {
                Debug.LogWarning($"OnClickInventoryItem (weapon): WeaponTransform missing on slot {slotIndex}");
                btn.isOn = false;
                return;
            }

            Transform mountPoint = weaponTransform.weaponTransform;

            if (!EquipmentManager.Instance.Equip(slotIndex, item, mountPoint))
            {
                btn.isOn = false;
                return;
            }

            // 裝備格圖示 / 卸下按鈕由 OnSlotChanged 更新
            RefreshItemListLockStates(btn);

            // 鎖住當前已裝備的按鈕
            btn.interactable = false;
            return;
        }
        // Armor / other equip：裝到目前選中的格子，和下面 UI 圖示用同一個 index
        int idx = GetSelectedSlotIndex();
        if (idx < 0)
        {
            Debug.LogWarning("OnClickInventoryItem (armor): no equipment slot selected");
            btn.isOn = false;
            return;
        }

        if (!EquipmentManager.Instance.Equip(idx, item))
        {
            btn.isOn = false;
            return;
        }

        // 腿部隱藏已搬進 EquipmentManager（EquipArmorToSlot）
        // 裝備格圖示 / 卸下按鈕由 OnSlotChanged 更新

        RefreshItemListLockStates(btn);
        btn.interactable = false;
    }

    private void RefreshItemListLockStates(Toggle selectedBtn)
    {
        foreach (var t in itemsButtonParent.GetComponentsInChildren<Toggle>(true))
        {
            if (t == null) continue;
            if (t == selectedBtn) continue;

            // 關掉其它按鈕的選取狀態
            t.isOn = false;

            if (!toggleItemMap.TryGetValue(t, out var mappedItem))
            {
                t.interactable = true;
                continue;
            }

            // 已裝備的物品維持鎖住
            t.interactable = !IsItemEquippedAnywhere(mappedItem);
        }
    }

    // 不能選的物品：已經穿在身上（目前這套），或已經放在別套裡（同一件裝備只能屬於一套）
    private bool IsItemEquippedAnywhere(ItemInstance item)
    {
        if (item == null) return false;
        foreach (var slot in EquipmentManager.Instance.equipmentSlots)
        {
            if (slot != null && slot.item == item)
                return true;
        }
        return EquipmentManager.Instance.GetOtherSetIndexOf(item) >= 0;
    }

    #endregion

    #region Unequip

    public void Unequip()
    {
        int index = GetSelectedSlotIndex();
        if (index < 0) return;

        SFXManager.Instance.PlayFeedback("ClickFeedback");

        // 腿部恢復顯示已搬進 EquipmentManager（CleanEquipmentSlot）
        EquipmentManager.Instance.Unequip(index);
        // 裝備格圖示 / 卸下按鈕由 OnSlotChanged 更新

        // 依模式刷新
        if (IsColorMode) OpenColorPage();
        else OpenInventoryPage();
    }

    #endregion

    #region Color Page (select equipment / weapon parts)

    public void OpenColorPage()
    {
        if (!IsColorMode) return;

        CleanPartButtons();
        SetAllRemoveButtonsActive(false);

        int index = GetSelectedSlotIndex();
        var slots = EquipmentManager.Instance.equipmentSlots;

        if (index < 0 || index >= slots.Count)
        {
            ClearColorPickerState();
            return;
        }

        var slot = slots[index];
        var equippedGO = slot.equipedItem;

        // 沒裝備：清空 ColorPicker
        if (equippedGO == null)
        {
            ClearColorPickerState();
            return;
        }

        // 預設先選本體（非武器也適用）
        SelectPartToColor(equippedGO, slot.item);


        // 只有 RangeWeapon 才生成零件按鈕
        if (slot.item is RangeWeaponInstance rwi)
        {
            if (rwi.item is not RangeWeapon rw)
                return; // 理論上不該發生，但保險

            if (partsButtonParent == null || partButtonPrefab == null || WeaponPartsColorToggleGroup == null)
                return;

            // 建立按鈕：Gun 本體（預設選中）
            CreatePartToggle(gunIconImage, equippedGO, rwi, true);

            // 已裝上的 attachment 逐一建按鈕
            if (rwi.attachment == null)
                return;

            foreach (var attach in rwi.attachment)
            {
                if (attach?.item == null) continue;

                WeaponPartType partType = attach.partType;
                GameObject partGO = FindEquippedPartGO(equippedGO, rw, partType);
                Sprite icon = GetPartIcon(partType);

                CreatePartToggle(icon, partGO, attach, false);
            }
        }
        // MeleeWeapon：主人之後要做的分支
        else if (slot.item is MeleeWeaponInstance mwi)
        {
            if (mwi.item is not MeleeWeapon mw)
                return;
            if (partsButtonParent == null || partButtonPrefab == null || WeaponPartsColorToggleGroup == null)
                return;
            CreatePartToggle(melIconImage, equippedGO, mwi, true);
            // 已裝上的 attachment 逐一建按鈕
            if (mwi.attachment == null)
                return;

            foreach (var attach in mwi.attachment)
            {
                if (attach?.item == null) continue;

                WeaponPartType partType = attach.partType;
                GameObject partGO = FindEquippedPartGO(equippedGO, mw, partType);
                Sprite icon = GetPartIcon(partType);

                CreatePartToggle(icon, partGO, attach, false);
            }
        }
        else
        {
            return;
        }
    }

    private Toggle CreatePartToggle(Sprite iconSprite, GameObject targetPart, ItemInstance instanceToSave, bool defaultOn)
    {
        var goBtn = Instantiate(partButtonPrefab, partsButtonParent);
        var tgl = goBtn.GetComponent<Toggle>();
        if (tgl == null) return null;

        tgl.group = WeaponPartsColorToggleGroup;

        var icon = goBtn.transform.Find("Part Icon")?.GetComponent<Image>();
        if (icon != null) icon.sprite = iconSprite;

        // 找不到對應零件：禁用，避免點了沒反應
        tgl.interactable = (targetPart != null);

        // 先設正常色
        ApplyToggleColors(tgl, false);

        tgl.onValueChanged.AddListener(isOn =>
        {
            SFXManager.Instance.PlayFeedback("ClickFeedback");
            ApplyToggleColors(tgl, isOn);
            if (!isOn) return;

            if (targetPart != null)
            {
                Debug.Log($"Select part to color: {targetPart.name}");
                SelectPartToColor(targetPart, instanceToSave);
            }
        });

        // 最後才設 isOn（確保事件/顏色都已綁好）
        tgl.isOn = defaultOn;
        return tgl;
    }

    private void ApplyToggleColors(Toggle tgl, bool isOn)
    {
        if (tgl == null) return;

        var cb = tgl.colors;
        var c = isOn ? selectedColor : normalColor;

        cb.normalColor = c;
        cb.selectedColor = c;
        cb.highlightedColor = c;
        cb.pressedColor = c;

        tgl.colors = cb;
    }

    private Sprite GetPartIcon(WeaponPartType partType)
    {
        if (partType == WeaponPartType.Barrel) return barrelIconImage;
        if (partType == WeaponPartType.Scope) return scopeIconImage;
        if (partType == WeaponPartType.Handle) return handleIconImage;
        if (partType == WeaponPartType.Coating) return coatingIconImage;

        return null; // 主人要擴充更多零件 icon，就在這裡加
    }

    // partType -> 找 attachmentPoints 的掛點名 -> FindChild -> 找掛點下第一個有 Renderer 的 child(或其子孫)
    private GameObject FindEquippedPartGO(GameObject weaponRoot, RangeWeapon rw, WeaponPartType partType)
    {
        if (weaponRoot == null || rw == null) return null;

        string mountName = null;
        foreach (var ap in rw.attachmentPoints)
        {
            if (ap.allowPart != partType) continue;
            if (ap.pointTransform == null) continue;
            mountName = ap.pointTransform.name;
            break;
        }

        if (string.IsNullOrEmpty(mountName)) return null;

        var mountGO = FindChild(weaponRoot, mountName);
        if (mountGO == null) return null;

        // 1) 優先：掛點底下找「有 Renderer」的 child（避免拿到 MuzzlePoint/空物件）
        for (int i = 0; i < mountGO.transform.childCount; i++)
        {
            var child = mountGO.transform.GetChild(i);
            if (child == null) continue;

            // 跳過常見的 helper
            if (child.name == "MuzzlePoint") continue;

            // 這個 child 本身或子孫有 Renderer 就算可上色目標
            if (child.GetComponentInChildren<Renderer>(true) != null)
                return child.gameObject;
        }

        // 2) fallback：如果掛點自己底下某處有 Renderer，就回傳掛點（讓 ColorPicker 往下找）
        if (mountGO.GetComponentInChildren<Renderer>(true) != null)
            return mountGO;

        return null;
    }
    private GameObject FindEquippedPartGO(GameObject weaponRoot, MeleeWeapon mw, WeaponPartType partType)
    {
        if (weaponRoot == null || mw == null) return null;
        string mountName = null;
        foreach (var ap in mw.attachmentPoints)
        {
            if (ap.allowPart != partType) continue;
            if (ap.pointTransform == null) continue;
            mountName = ap.pointTransform.name;
            break;
        }
        if (string.IsNullOrEmpty(mountName)) return null;

        var mountGO = FindChild(weaponRoot, mountName);
        if (mountGO == null) return null;

        // 1) 優先：掛點底下找「有 Renderer」的 child（避免拿到 MuzzlePoint/空物件）
        for (int i = 0; i < mountGO.transform.childCount; i++)
        {
            var child = mountGO.transform.GetChild(i);
            if (child == null) continue;

            // 跳過常見的 helper
            if (child.name == "MuzzlePoint") continue;
            if (child.name == "NormalSwordEffect") continue;
            if (partType == WeaponPartType.Coating)
            {
                Debug.Log($"Looking for Melee Coating EffectColorController under {child.name}");
                if (child.GetComponent<EffectColorController>())
                    // Melee Coating 特例：直接回傳掛點（因為粒子特效通常掛在掛點上）
                    return child.gameObject;
            }


            // 這個 child 本身或子孫有 Renderer 就算可上色目標
            if (child.GetComponentInChildren<Renderer>(true) != null)
                return child.gameObject;
        }

        return null;
    }

    public bool DeductItem(ItemInstance[] costs)
    {
        if (costs == null || costs.Length == 0) return true;

        // 只扣「純 ItemInstance」，不扣任何派生類
        bool IsPlainStack(ItemInstance inst) =>
            inst != null && inst.item != null && inst.GetType() == typeof(ItemInstance);

        // 1) 先合併需求（依 ItemObject 計總需求）
        var required = new Dictionary<ItemObject, int>();
        foreach (var c in costs)
        {
            if (!IsPlainStack(c)) continue;

            int need = Mathf.Max(0, c.amount);
            if (need == 0) continue;

            if (required.ContainsKey(c.item)) required[c.item] += need;
            else required.Add(c.item, need);
        }

        if (required.Count == 0) return true;

        // 2) 計算可用數量（只算純 ItemInstance）
        var available = new Dictionary<ItemObject, int>();
        foreach (var inst in inventory)
        {
            if (!IsPlainStack(inst)) continue;

            int amt = Mathf.Max(0, inst.amount);
            if (amt == 0) continue;

            if (available.ContainsKey(inst.item)) available[inst.item] += amt;
            else available.Add(inst.item, amt);
        }

        // 3) 不夠就直接返回 false（不扣任何東西）
        foreach (var kv in required)
        {
            int have = available.TryGetValue(kv.Key, out var v) ? v : 0;
            if (have < kv.Value) return false;
        }

        // 4) 真正扣除：從尾端往前扣，扣到 0 就移除 stack
        foreach (var kv in required)
        {
            var item = kv.Key;
            int remaining = kv.Value;

            for (int i = inventory.Count - 1; i >= 0 && remaining > 0; i--)
            {
                var inst = inventory[i];
                if (!IsPlainStack(inst)) continue;
                if (inst.item != item) continue;

                int take = Mathf.Min(Mathf.Max(0, inst.amount), remaining);
                inst.amount -= take;
                remaining -= take;

                if (inst.amount <= 0)
                    inventory.RemoveAt(i);
            }
        }

        return true;
    }


    private void ClearColorPickerState()
    {
        if (characterEquipmentColorPicker == null) return;

        characterEquipmentColorPicker.targetGameObject = null;
        characterEquipmentColorPicker.targetMaterials = new List<Material>();
        characterEquipmentColorPicker.currentMaterialIndex = -1;
        characterEquipmentColorPicker.currentTextureIndex = -1;
        characterEquipmentColorPicker.ClearnButton();
    }

    public void CleanPartButtons()
    {
        if (partsButtonParent == null) return;

        for (int i = partsButtonParent.childCount - 1; i >= 0; i--)
            Destroy(partsButtonParent.GetChild(i).gameObject);
    }

    #endregion

    #region Select Part To Color

    // 保留舊介面：有些地方可能還在用 index 版本
    public void SelectPartToColor(int equipmentSlotsIndex)
    {
        if (!IsColorMode) return;

        var slots = EquipmentManager.Instance.equipmentSlots;
        if (equipmentSlotsIndex < 0 || equipmentSlotsIndex >= slots.Count)
        {
            ClearColorPickerState();
            return;
        }

        var go = slots[equipmentSlotsIndex].equipedItem;
        SelectPartToColor(go);
    }

    public void SelectPartToColor(GameObject part)
    {
        if (!IsColorMode) return;

        if (characterEquipmentColorPicker == null)
            return;

        if (part == null)
        {
            ClearColorPickerState();
            return;
        }

        characterEquipmentColorPicker.targetGameObject = part;
        characterEquipmentColorPicker.AddTargetMaterialsToList();
        characterEquipmentColorPicker.CreateButtons();
    }
    public void SelectPartToColor(GameObject part, ItemInstance instanceToSave)
    {
        if (!IsColorMode) return;
        if (characterEquipmentColorPicker == null) return;

        characterEquipmentColorPicker.targetItemInstance = instanceToSave;
        SelectPartToColor(part); // 沿用你原本的：設 targetGameObject + AddTargetMaterialsToList + CreateButtons
    }
    #endregion

    #region Utilities

#if UNITY_EDITOR
    /// <summary>
    /// 一次性搬遷：把 UI 格子上 WeaponTransform 的掛點，依「第 i 個按鈕 → equipmentSlots[i]」
    /// 寫進 EquipmentSlot.mountPoint。對應方式和執行時 GetSelectedSlotIndex 完全相同，
    /// 所以搬完之後行為不變。已經有值的格子不覆蓋。
    /// </summary>
    [ContextMenu("Copy Weapon Mount Points To EquipmentSlots")]
    private void CopyWeaponMountPointsToEquipmentSlots()
    {
        var em = FindFirstObjectByType<EquipmentManager>(FindObjectsInactive.Include);
        if (em == null || inventoryButtonParent == null)
        {
            Debug.LogWarning("CopyWeaponMountPoints: 找不到 EquipmentManager 或 inventoryButtonParent");
            return;
        }

        UnityEditor.Undo.RecordObject(em, "Copy Weapon Mount Points");
        int count = Mathf.Min(inventoryButtonParent.childCount, em.equipmentSlots.Count);
        for (int i = 0; i < count; i++)
        {
            var wt = inventoryButtonParent.GetChild(i).GetComponentInChildren<WeaponTransform>(true);
            if (wt == null || wt.weaponTransform == null) continue;

            var slot = em.equipmentSlots[i];
            if (slot.mountPoint != null)
            {
                if (slot.mountPoint != wt.weaponTransform)
                    Debug.LogWarning($"CopyWeaponMountPoints: slot[{i}] 已有 '{slot.mountPoint.name}'，與 UI 的 '{wt.weaponTransform.name}' 不同，未覆蓋", em);
                continue;
            }

            slot.mountPoint = wt.weaponTransform;
            Debug.Log($"CopyWeaponMountPoints: slot[{i}] ← '{wt.weaponTransform.name}'（按鈕 '{inventoryButtonParent.GetChild(i).name}'）", em);
        }

        if (inventoryButtonParent.childCount != em.equipmentSlots.Count)
            Debug.LogWarning($"CopyWeaponMountPoints: UI 按鈕數 {inventoryButtonParent.childCount} ≠ equipmentSlots 數 {em.equipmentSlots.Count}", em);

        UnityEditor.EditorUtility.SetDirty(em);
    }
#endif

    public int GetSelectedSlotIndex()
    {
        if (inventoryButtonParent == null) return -1;

        for (int i = 0; i < inventoryButtonParent.childCount; i++)
        {
            var child = inventoryButtonParent.GetChild(i);
            var t = child.GetComponentInChildren<Toggle>(true);
            if (t != null && t.isOn) return i;
        }
        return -1;
    }
    public GameObject FindChild(GameObject parentObject, string targetName)
    {
        if (parentObject == null) return null;

        foreach (Transform childTransform in parentObject.transform)
        {
            if (childTransform.gameObject.name == targetName)
                return childTransform.gameObject;

            GameObject found = FindChild(childTransform.gameObject, targetName);
            if (found != null)
                return found;
        }
        return null;
    }

    private void SetEquipmentSlotIcon(int slotIndex, Sprite sprite)
    {
        if (inventoryButtonParent == null) return;
        if (slotIndex < 0 || slotIndex >= inventoryButtonParent.childCount) return;

        var iconGO = FindChild(inventoryButtonParent.GetChild(slotIndex).gameObject, "Item Icon");
        if (iconGO == null) return;

        var img = iconGO.GetComponent<Image>();
        if (img == null) return;

        img.sprite = sprite;
        img.color = (sprite != null) ? new Color(1, 1, 1, 1) : new Color(1, 1, 1, 0);
    }

    private void SetRemoveButtonForSlot(int slotIndex, bool active)
    {
        if (inventoryButtonParent == null) return;
        if (slotIndex < 0 || slotIndex >= inventoryButtonParent.childCount) return;

        var removeBtn = FindChild(inventoryButtonParent.GetChild(slotIndex).gameObject, "Remove Equipment Button");
        if (removeBtn != null) removeBtn.SetActive(active);
    }

    private void SetAllRemoveButtonsActive(bool active)
    {
        if (inventoryButtonParent == null) return;

        for (int i = 0; i < inventoryButtonParent.childCount; i++)
            SetRemoveButtonForSlot(i, active);
    }

    private void ShowInventoryPage()
    {
        if (statBlock != null) statBlock.SetActive(false);
        if (inventoryBlock != null) inventoryBlock.SetActive(true);

        currentPage = inventoryBlock;
        if (pageSwitch != null && pageSwitch.pages != null && pageSwitch.pages.Length > 0)
            pageSwitch.pages[0] = currentPage;
    }

    private void ShowStatPage()
    {
        if (statBlock != null) statBlock.SetActive(true);
        if (inventoryBlock != null) inventoryBlock.SetActive(false);

        currentPage = statBlock;
        if (pageSwitch != null && pageSwitch.pages != null && pageSwitch.pages.Length > 0)
            pageSwitch.pages[0] = currentPage;
    }

    #endregion
}
