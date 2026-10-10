using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

[System.Serializable]
public class EquipmentSlot
{
    public GameObject equipedItem;

    [SerializeField] public List<ItemType> equipmentTypes = new List<ItemType>();

    [SerializeField, HideInInspector] public ItemType equipmentType;

    public ItemInstance item;

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

    public bool TryEquipFromInventory(ItemInstance item)
    {
        if (item == null || item.item == null) return false;

        for (int i = 0; i < equipmentSlots.Count; i++)
        {
            var slot = equipmentSlots[i];

            if (!slot.Accepts(item.item.type)) continue;

            // ²M±¼ÂÂ¹ê¨Ò
            if (slot.equipedItem) Destroy(slot.equipedItem);

            Debug.Log($"TryEquipFromInventory: found matching slot {i} for item {item.item.itemName}");
            // ¸Ë¥Ò¡G¥Í¦¨¨Ã¸j°©
            if (item.item is Armor armor && armor.skinnedMeshRenderer)
            {
                Debug.Log($"TryEquipFromInventory: equipping armor {armor.itemName} to slot {i}");
                slot.equipedItem = BoneCombiner.Instance.InstantiateMesh(armor.skinnedMeshRenderer);
                Debug.Log($"TryEquipFromInventory: BoneCombiner instantiated mesh for {armor.itemName}");
                if (!slot.equipedItem)
                {
                    Debug.LogWarning("TryEquipFromInventory: BoneCombiner failed to instantiate mesh");
                    return false; // ¹ê¨Ò¤Æ¥¢±Ñ®É¤£­n·í§@¦¨¥\
                }
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

                PlayerStats.Instance?.RecalculateFromEquipment();
                RefreshEquipmentStatBlock();
                return true;
            }
            return false;
        }
        return false;
    }

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
                $"TryEquipWeaponFromInventory: slot[{slotIndex}].equipmentType = {slot.equipmentType}, item.type = {item.item.type}"
            );
            return false;
        }

        if (slot.equipedItem)
        {
            if (PlayerStats.Instance != null)
            {
                if (slotIndex == PlayerStats.Instance.leftWeaponSlotIndex)
                    DetachMuzzleFlashToHolder(leftMuzzleFlash, leftMuzzleFlashHolder);
                else if (slotIndex == PlayerStats.Instance.rightWeaponSlotIndex)
                    DetachMuzzleFlashToHolder(rightMuzzleFlash, rightMuzzleFlashHolder);
            }

            Destroy(slot.equipedItem);
        }

        if (item is RangeWeaponInstance rwi && rwi.item is RangeWeapon rw)
        {
            // 1)
            slot.equipedItem = Instantiate(rw.weaponPrefab, mountPoint, false);
            slot.equipedItem.transform.localRotation = Quaternion.Euler(new Vector3(90, 0, 0));
            slot.item = item;
            if (FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint") != null)
            {
                rwi.muzzlePoint = FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint");
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
                            rwi.muzzlePoint = FindChildRecursive(part.transform, "MuzzlePoint");
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

            bool isLeftHand = (slotIndex == PlayerStats.Instance.leftWeaponSlotIndex);
            ParentMuzzleFlashToMuzzle(isLeftHand ? leftMuzzleFlash : rightMuzzleFlash, rwi.muzzlePoint);

            // Saved Mix colors and all weapon parts are now restored.
            // Bake them and switch their visible materials to untouched UTS3 clones.
            LayeredToonMaterialController.InitializeAll(slot.equipedItem);

            PlayerStats.Instance?.RecalculateFromEquipment();
            RefreshEquipmentStatBlock();
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
            mwi.hitbox = slot.equipedItem.GetComponentInChildren<MeleeHitbox>(true);
            if (mwi.hitbox == null)
            {
                Debug.LogWarning($"Equip (Melee): '{mw.itemName}' 上找不到 MeleeHitbox，" +
                                 $"這把武器揮空氣。檢查刀刃上有沒有掛該組件。", slot.equipedItem);
            }

            PlayerStats.Instance?.RecalculateFromEquipment();
            RefreshEquipmentStatBlock();
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
                swi.muzzlePoint = FindChildRecursive(slot.equipedItem.transform, "MuzzlePoint");
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
                            swi.muzzlePoint = FindChildRecursive(part.transform, "MuzzlePoint");
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

            // Saved Mix colors and all shoulder-weapon parts are now restored.
            LayeredToonMaterialController.InitializeAll(slot.equipedItem);

            PlayerStats.Instance?.RecalculateFromEquipment();
            RefreshEquipmentStatBlock();
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

        if (slot.equipedItem)
        {
            if (PlayerStats.Instance != null)
            {
                if (equipmentSlotsIndex == PlayerStats.Instance.leftWeaponSlotIndex)
                    DetachMuzzleFlashToHolder(leftMuzzleFlash, leftMuzzleFlashHolder);
                else if (equipmentSlotsIndex == PlayerStats.Instance.rightWeaponSlotIndex)
                    DetachMuzzleFlashToHolder(rightMuzzleFlash, rightMuzzleFlashHolder);
            }

            Destroy(slot.equipedItem);
        }

        slot.equipedItem = null;
        slot.item = null;
        PlayerStats.Instance?.RecalculateFromEquipment();
        RefreshEquipmentStatBlock();
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