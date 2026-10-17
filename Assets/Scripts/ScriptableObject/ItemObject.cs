using UnityEngine;
public enum ItemType { 
    Weapon, 
    HeadArmor,
    ChestArmor,
    LeftHandArmor,
    RightHandArmor,
    WaistArmor,
    LegsArmor,
    Thruster,
    RangeWeapon,
    WeaponPart,
    MeleeWeapon,
    Consumable,
    Material,
    ShoulderCannon,
}

public enum WeaponPartType
{
    Gun,
    Scope,
    Barrel,
    Blade,
    Handle,
    Coating,
    Cannon,
    CannonBarrel,
}

[System.Serializable]
public class AttachmentPoint
{
    public Transform pointTransform;
    public WeaponPartType allowPart;
}

[CreateAssetMenu(fileName = "New Item", menuName = "Inventory/Item")]
public class ItemObject : ScriptableObject
{
    [Tooltip("存檔用的 id（= 這個 asset 的 GUID）。由 ItemDatabase 的 ⋮ → Rebuild 自動填入，不要手改。")]
    public string itemId;

    public string itemName;
    public ItemType type;
    public Sprite icon;
    public int maxStack;
    public GameObject dropPrefab;
}