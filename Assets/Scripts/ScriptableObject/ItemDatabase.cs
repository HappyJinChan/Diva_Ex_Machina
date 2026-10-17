using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 所有 ItemObject 的清單。存檔時物品類型存成字串 id（ItemObject.itemId），讀檔時用這裡找回 asset。
///
/// 建立：Project 視窗 → Create → Inventory → Item Database（整個專案只要一個）。
/// 更新：新增或刪除 ItemObject 之後，對這個 asset 按 ⋮ → Rebuild。
///       Rebuild 會找出專案裡所有 ItemObject（含 Armor、RangeWeapon 等子類別），
///       把每個的 itemId 設成它的 asset GUID，並重新填好清單。
///
/// 為什麼用 GUID：不用手動編號，改檔名或搬資料夾也不會變；
/// 複製 asset（Ctrl+D）時新 asset 會拿到新的 GUID，Rebuild 一次就會修正被一起複製過去的舊 id。
/// </summary>
[CreateAssetMenu(fileName = "ItemDatabase", menuName = "Inventory/Item Database")]
public class ItemDatabase : ScriptableObject
{
    [SerializeField] private List<ItemObject> items = new List<ItemObject>();

    private Dictionary<string, ItemObject> _lookup;

    public IReadOnlyList<ItemObject> Items => items;

    private void OnEnable() => _lookup = null;

    private void BuildLookup()
    {
        _lookup = new Dictionary<string, ItemObject>();
        foreach (var it in items)
        {
            if (it == null) continue;
            if (string.IsNullOrEmpty(it.itemId))
            {
                Debug.LogWarning($"ItemDatabase: '{it.name}' 沒有 itemId，請按 ⋮ → Rebuild", this);
                continue;
            }
            if (_lookup.TryGetValue(it.itemId, out var other) && other != it)
            {
                Debug.LogWarning($"ItemDatabase: '{it.name}' 和 '{other.name}' 的 itemId 重複，請按 ⋮ → Rebuild", this);
                continue;
            }
            _lookup[it.itemId] = it;
        }
    }

    /// <summary>用 itemId 找 ItemObject；找不到回傳 null。</summary>
    public ItemObject Find(string itemId)
    {
        if (string.IsNullOrEmpty(itemId)) return null;
        if (_lookup == null) BuildLookup();
        return _lookup.TryGetValue(itemId, out var it) ? it : null;
    }

    /// <summary>這個 ItemObject 有沒有登記在資料庫裡（存檔前檢查用）。</summary>
    public bool Contains(ItemObject item)
        => item != null && !string.IsNullOrEmpty(item.itemId) && Find(item.itemId) == item;

#if UNITY_EDITOR
    [ContextMenu("Rebuild")]
    private void Rebuild()
    {
        var guids = UnityEditor.AssetDatabase.FindAssets("t:ItemObject");
        var found = new List<ItemObject>();
        int idChanged = 0;

        foreach (var guid in guids)
        {
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
            var it = UnityEditor.AssetDatabase.LoadAssetAtPath<ItemObject>(path);
            if (it == null) continue;

            if (it.itemId != guid)
            {
                UnityEditor.Undo.RecordObject(it, "Rebuild Item Database");
                it.itemId = guid;
                UnityEditor.EditorUtility.SetDirty(it);
                idChanged++;
            }
            found.Add(it);
        }

        found.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

        UnityEditor.Undo.RecordObject(this, "Rebuild Item Database");
        items = found;
        _lookup = null;
        UnityEditor.EditorUtility.SetDirty(this);
        UnityEditor.AssetDatabase.SaveAssets();

        Debug.Log($"ItemDatabase: 共 {found.Count} 個 ItemObject，更新了 {idChanged} 個 itemId", this);
    }
#endif
}
