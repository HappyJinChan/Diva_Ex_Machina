using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敵人的掉落表。掛在敵人的根物件上（跟 ModularEntityStats 同一個物件，或它的子物件）。
///
/// 死亡時（ModularEntityStats.OnDeath，不管是誰打死的：Bradamant、迫擊砲、甲板砲塔、死亡爆炸連鎖）
/// 每一項各自擲機率，中了就掉 Amount 範圍內的數量。數量大時拆成最多 Max Pickups Per Entry 顆，
/// 每顆帶一部分數量（看起來是噴出一把，但不會一次生成幾十個物件）。
///
/// 掉落物用物品本身的 Drop Prefab（ItemObject.dropPrefab），prefab 上要有 DropItem。
/// 一律走 PrefabPool。
/// </summary>
[DisallowMultipleComponent]
public class EntityLootDrop : MonoBehaviour
{
    [Serializable]
    public class Entry
    {
        public ItemObject item;
        [Range(0f, 1f)] public float chance = 1f;
        [Tooltip("數量範圍（含兩端）。")]
        public Vector2Int amount = new Vector2Int(1, 1);
    }

    [SerializeField] private List<Entry> drops = new List<Entry>();

    [Tooltip("每一項最多拆成幾顆掉落物。")]
    [SerializeField, Min(1)] private int maxPickupsPerEntry = 3;

    [Header("Scatter")]
    [Tooltip("從哪裡噴出：敵人位置往上幾公尺。")]
    [SerializeField] private float spawnHeight = 1.5f;
    [Tooltip("往上彈的速度範圍（m/s）。")]
    [SerializeField] private Vector2 upSpeed = new Vector2(6f, 10f);
    [Tooltip("往四周散開的速度範圍（m/s）。")]
    [SerializeField] private Vector2 sideSpeed = new Vector2(2f, 6f);

    private ModularEntityStats _stats;

    private void Awake()
    {
        _stats = GetComponentInParent<ModularEntityStats>();
        if (_stats == null) _stats = GetComponentInChildren<ModularEntityStats>();
        if (_stats == null) Debug.LogWarning($"EntityLootDrop: '{name}' 找不到 ModularEntityStats，不會掉落", this);
    }

    private void OnEnable()
    {
        if (_stats != null) _stats.OnDeath += HandleDeath;
    }

    private void OnDisable()
    {
        if (_stats != null) _stats.OnDeath -= HandleDeath;
    }

    private void HandleDeath()
    {
        Vector3 origin = _stats.transform.position + Vector3.up * spawnHeight;

        for (int i = 0; i < drops.Count; i++)
        {
            Entry e = drops[i];
            if (e == null || e.item == null) continue;
            if (UnityEngine.Random.value > e.chance) continue;

            int total = UnityEngine.Random.Range(Mathf.Min(e.amount.x, e.amount.y), Mathf.Max(e.amount.x, e.amount.y) + 1);
            if (total <= 0) continue;

            GameObject prefab = e.item.dropPrefab;
            if (prefab == null)
            {
                Debug.LogWarning($"EntityLootDrop: '{e.item.itemName}' 沒有設定 Drop Prefab，不掉落", e.item);
                continue;
            }

            // 拆成 n 顆，數量盡量平均（7 拆 3 顆 = 3、2、2）
            int n = Mathf.Clamp(total, 1, maxPickupsPerEntry);
            for (int k = 0; k < n; k++)
            {
                int share = total / n + (k < total % n ? 1 : 0);
                Spawn(prefab, e.item, share, origin);
            }
        }
    }

    private void Spawn(GameObject prefab, ItemObject item, int amount, Vector3 origin)
    {
        GameObject go = PrefabPool.Spawn(prefab, origin, Quaternion.identity);
        if (go == null) return;

        DropItem drop = go.GetComponent<DropItem>();
        if (drop == null)
        {
            Debug.LogWarning($"EntityLootDrop: 掉落物 prefab '{prefab.name}' 上沒有 DropItem", prefab);
            PrefabPool.Despawn(go);
            return;
        }

        Vector2 r = UnityEngine.Random.insideUnitCircle.normalized * UnityEngine.Random.Range(sideSpeed.x, sideSpeed.y);
        Vector3 vel = new Vector3(r.x, UnityEngine.Random.Range(upSpeed.x, upSpeed.y), r.y);
        drop.Launch(item, amount, vel);
    }
}
