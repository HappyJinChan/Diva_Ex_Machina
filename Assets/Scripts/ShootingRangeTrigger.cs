using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 射擊場的觸發踏板。
///
/// 玩家踏上這個物件的 Trigger Collider 時，在每個生成點各生成一個敵人。
/// 某個生成點生出來的敵人還活著，那個點就不再生成 —— 打掉之後再踏一次才會補上。
///
/// 判斷「還活著」的方式跟 EnemySpawner 一樣：敵人死亡時會被 Destroy（ModularEntityStats.Die），
/// 被 Destroy 的物件在 Unity 中等於 null，所以不需要訂閱死亡事件。
///
/// 每個生成點的位置和名稱存在一起（SpawnSlot）。名稱留空時用生成點的物件名稱。
/// 生成的敵人身上有 DamageRecord 時，會用這個名稱回報給 ShootingRangeScoreboard。
///
/// 踏上踏板時也會把玩家目前的武器配置（左右手、左右肩；只含武器與零件）
/// 記成一筆快照存到計分板。之後的擊殺紀錄會帶著這筆快照的 id。
///
/// 用法：
///   1. 把這個腳本掛在 Plane (1) 上（Box Collider 要勾 Is Trigger）。
///   2. Slots 每一項拖入一個生成點、填名稱。
///   3. Enemy Prefab 指定要生成的敵人（prefab 上掛 DamageRecord 才會計時）。
///   4. Player 拖入玩家根物件（有 Rigidbody 的那個）。留空則改用 Tag 判斷。
///   5. Scoreboard 拖入場景裡的 ShootingRangeScoreboard。
/// </summary>
[RequireComponent(typeof(Collider))]
public class ShootingRangeTrigger : MonoBehaviour
{
    /// <summary>一個生成點：位置 + 名稱。</summary>
    [System.Serializable]
    public class SpawnSlot
    {
        [Tooltip("取消勾選 = 暫時不使用這個生成點（不生成、不重生）。\n" +
                 "已經在場上的敵人不會被移除。")]
        public bool enabled = true;

        [Tooltip("生成點。")]
        public Transform point;

        [Tooltip("這個點生成的敵人名稱（計分板與 CSV 用）。留空 = 用生成點的物件名稱。")]
        public string enemyName;

        [Tooltip("這個點的敵人被摧毀後，幾秒後自動重生。\n" +
                 "0 = 立刻重生。\n" +
                 "負數 = 不自動重生，要等玩家再踩一次踏板。")]
        public float respawnTime = -1f;

        // ───── 執行期狀態（不序列化，只在 Play 期間有意義）─────

        // 這個點目前的敵人
        [System.NonSerialized] public GameObject spawned;

        // 曾經生成過、還沒處理它的死亡。用來區分「被摧毀」和「從來沒生成過」——
        // 兩者的 spawned 都是 null。
        [System.NonSerialized] public bool awaitingRespawn;

        // 預定重生的時間（Time.time）；< 0 = 沒有排程
        [System.NonSerialized] public float respawnAt = -1f;

        /// <summary>實際使用的名稱。</summary>
        public string ResolvedName =>
            !string.IsNullOrEmpty(enemyName) ? enemyName
            : (point != null ? point.name : "(no point)");
    }

    [Header("Spawn")]
    [Tooltip("生成點與名稱。每個點最多同時存在一個由這個踏板生成的敵人。")]
    [SerializeField] private List<SpawnSlot> slots = new List<SpawnSlot>();

    [Tooltip("要生成的敵人 prefab。")]
    [SerializeField] private GameObject enemyPrefab;

    [Tooltip("勾選 = 敵人朝向生成點的 forward；取消 = 敵人轉向踏板（面對玩家方向）。")]
    [SerializeField] private bool useSpawnPointRotation = false;

    [Tooltip("生成的敵人血量（同時設定 maxHealth 與 health）。\n" +
             "0 或負數 = 不覆蓋，沿用 prefab 上的值。\n" +
             "改了之後，下一次踏上踏板生成的敵人才會套用；還活著的不受影響。")]
    [SerializeField] private float hp = 0f;

    [Tooltip("生成的敵人左右移動速度（m/s，寫入 ModularEntityStats.sprintSpeed）。\n" +
             "要搭配 TestTargetMovement + TestTargetBrain 才會動。\n" +
             "負數 = 不覆蓋，沿用 prefab 上的值。0 = 不動。")]
    [SerializeField] private float moveSpeed = -1f;

    [Header("Score")]
    [Tooltip("計分板。留空則由 DamageRecord 自己在場景裡找。")]
    [SerializeField] private ShootingRangeScoreboard scoreboard;

    [Tooltip("踏上踏板時，把左右手與肩膀的武器配置（武器本體 + 零件，不含護甲）記到計分板。")]
    [SerializeField] private bool recordLoadout = true;

    [Header("Player Detection")]
    [Tooltip("玩家根物件。進入 Trigger 的 Collider 只要屬於它（或它的子物件）就算玩家。\n" +
             "留空則改用下方的 Tag 判斷。")]
    [SerializeField] private Transform player;

    [Tooltip("Player 欄位留空時使用。")]
    [SerializeField] private string playerTag = "Player";

    [Header("Debug")]
    [SerializeField] private bool logSpawns = false;

    // ───── 舊版欄位（只用來搬家）─────
    // 舊版把生成點和名稱存在兩個分開的陣列。保留同樣的欄位名稱，Unity 才讀得到
    // 場景裡已經填好的值；OnValidate 會把它們搬進 slots，然後清空。
    [SerializeField, HideInInspector] private Transform[] spawnPoints;
    [SerializeField, HideInInspector] private string[] enemyNames;

    /// <summary>所有生成點（唯讀）。要新增或修改請用 AddSlot / SetEnemyName。</summary>
    public IReadOnlyList<SpawnSlot> Slots => slots;

    private void Awake()
    {
        MigrateLegacyArrays();

        var col = GetComponent<Collider>();
        if (!col.isTrigger)
            Debug.LogWarning($"[ShootingRangeTrigger] '{name}' 的 Collider 沒有勾 Is Trigger，踏上去不會觸發。", this);
    }

    private void OnValidate()
    {
        MigrateLegacyArrays();
    }

    /// <summary>把舊版的 spawnPoints / enemyNames 搬進 slots（只在 slots 還是空的時候搬一次）。</summary>
    private void MigrateLegacyArrays()
    {
        if (spawnPoints == null || spawnPoints.Length == 0) return;

        if (slots == null) slots = new List<SpawnSlot>();

        if (slots.Count == 0)
        {
            for (int i = 0; i < spawnPoints.Length; i++)
            {
                slots.Add(new SpawnSlot
                {
                    point = spawnPoints[i],
                    enemyName = (enemyNames != null && i < enemyNames.Length) ? enemyNames[i] : "",
                });
            }
            Debug.Log($"[ShootingRangeTrigger] '{name}'：已把舊的 Spawn Points / Enemy Names 搬到 Slots（{slots.Count} 個）。" +
                      "記得存檔場景。", this);
        }

        spawnPoints = null;
        enemyNames = null;

#if UNITY_EDITOR
        // 在 OnValidate 裡改的值不一定會被標成「已修改」，不標的話存檔時搬家結果會遺失
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    // ───── 血量 ─────

    /// <summary>目前設定的血量。0 = 沿用 prefab。</summary>
    public float HP => hp;

    /// <summary>設定之後生成的敵人血量。0 或負數 = 沿用 prefab。</summary>
    public void SetHP(float value) => hp = value;

    /// <summary>目前設定的移動速度。負數 = 沿用 prefab。</summary>
    public float MoveSpeed => moveSpeed;

    /// <summary>設定之後生成的敵人移動速度（m/s）。負數 = 沿用 prefab，0 = 不動。</summary>
    public void SetMoveSpeed(float value) => moveSpeed = value;

    // ───── 生成點 / 名稱 ─────

    /// <summary>新增一個生成點。回傳它的索引。</summary>
    public int AddSlot(Transform point, string enemyName = "")
    {
        slots.Add(new SpawnSlot { point = point, enemyName = enemyName });
        return slots.Count - 1;
    }

    /// <summary>找出某個生成點的 slot。找不到回傳 null。</summary>
    public SpawnSlot GetSlot(Transform spawnPoint)
    {
        if (spawnPoint == null) return null;
        foreach (var s in slots)
            if (s != null && s.point == spawnPoint) return s;
        return null;
    }

    /// <summary>
    /// 指定之後在這個生成點生成的敵人名稱。
    /// spawnPoint 必須已經在 Slots 裡，否則回傳 false。
    /// 已經生成、還活著的敵人不會改名（它的紀錄維持生成時的名稱）。
    /// </summary>
    public bool SetEnemyName(Transform spawnPoint, string enemyName)
    {
        var slot = GetSlot(spawnPoint);
        if (slot == null)
        {
            Debug.LogWarning($"[ShootingRangeTrigger] '{(spawnPoint != null ? spawnPoint.name : "null")}' 不在 Slots 裡。", this);
            return false;
        }

        slot.enemyName = enemyName;
        return true;
    }

    /// <summary>用索引指定名稱（索引對應 Slots）。</summary>
    public bool SetEnemyName(int index, string enemyName)
    {
        if (index < 0 || index >= slots.Count || slots[index] == null) return false;
        slots[index].enemyName = enemyName;
        return true;
    }

    /// <summary>這個生成點目前會用的名稱。不在 Slots 裡回傳 null。</summary>
    public string GetEnemyName(Transform spawnPoint) => GetSlot(spawnPoint)?.ResolvedName;

    // ───── 觸發 / 生成 ─────

    private void OnTriggerEnter(Collider other)
    {
        if (!IsPlayer(other)) return;

        // 先記下這次上場的武器配置，之後的擊殺都會歸到這一筆
        if (recordLoadout) RecordLoadout();

        SpawnMissing();
    }

    // ───── 武器配置快照 ─────

    /// <summary>
    /// 把目前左手、右手、左肩、右肩的武器配置寫進計分板，回傳配置 id（失敗時 -1）。
    ///
    /// 只讀武器本體與武器零件的 buff（直接讀 Instance，不讀 WeaponStats.buffs ——
    /// 後者已經混進手甲 / 頭盔等護甲的武器側加成）。
    /// 唯一的例外是後座力控制：它只出現在護甲上，所以記錄的是含護甲的最終值（見 CaptureRecoil）。
    /// </summary>
    public int RecordLoadout()
    {
        if (scoreboard == null)
            scoreboard = FindFirstObjectByType<ShootingRangeScoreboard>();

        if (scoreboard == null)
        {
            Debug.LogWarning($"[ShootingRangeTrigger] 場景裡沒有 ShootingRangeScoreboard，無法記錄武器配置。", this);
            return -1;
        }

        var ps = PlayerStats.Instance;
        if (ps == null)
        {
            Debug.LogWarning($"[ShootingRangeTrigger] 找不到 PlayerStats，無法記錄武器配置。", this);
            return -1;
        }

        var loadout = new ShootingRangeScoreboard.LoadoutSnapshot { timestamp = Time.time };

        var left = CaptureHand("LeftHand", ps.leftHand);
        var right = CaptureHand("RightHand", ps.rightHand);
        var am = ResolveAttackManager();
        CaptureRecoil(left, true, ps, am);
        CaptureRecoil(right, false, ps, am);

        loadout.slots.Add(left);
        loadout.slots.Add(right);
        loadout.slots.Add(CaptureShoulder("LeftShoulder", ps.leftShoulder));
        loadout.slots.Add(CaptureShoulder("RightShoulder", ps.rightShoulder));

        return scoreboard.AddLoadout(loadout);
    }

    private AttackManager _attackManager;

    private AttackManager ResolveAttackManager()
    {
        if (_attackManager == null)
            _attackManager = FindFirstObjectByType<AttackManager>();
        return _attackManager;
    }

    /// <summary>
    /// 後座力控制與衍生值。只對手持遠程武器有意義。
    ///
    /// 這裡刻意「含護甲」：後座力控制本來就只出現在護甲上，不含護甲就沒有東西可記。
    /// 控制值直接問 PlayerStats（跟 AttackManager 用的是同一個函式）；
    /// ratio / 天花板 / 恢復速度讀 AttackManager 算好的值，不在這裡重算曲線。
    /// </summary>
    private static void CaptureRecoil(ShootingRangeScoreboard.WeaponSlotSnapshot snap, bool isLeftHand,
                                      PlayerStats ps, AttackManager am)
    {
        if (snap.kind != "Range") return;

        snap.recoilControl = ps.GetRecoilControlForHand(isLeftHand);

        Weapon w = (am != null) ? (isLeftHand ? am.leftHandWeapon : am.rightHandWeapon) : null;
        if (w == null) return;

        snap.recoil = w.range.recoil;
        snap.recoilRatio = w.range.recoilRatio;
        snap.maxDeviation = w.range.maxDeviation;
        snap.deviationDecayPerSecond = w.range.deviationDecayPerSecond;
    }

    private static ShootingRangeScoreboard.WeaponSlotSnapshot CaptureHand(string slot, WeaponStats hand)
    {
        if (hand != null)
        {
            if (hand.weaponKind == HandWeaponKind.Range && hand.rangeweapon != null)
                return CaptureWeapon(slot, "Range", hand.rangeweapon, hand.rangeweapon.newWeaponName,
                                     hand.rangeweapon.buffs, hand.rangeweapon.attachment);

            if (hand.weaponKind == HandWeaponKind.Melee && hand.meleeWeapon != null)
                return CaptureWeapon(slot, "Melee", hand.meleeWeapon, hand.meleeWeapon.newWeaponName,
                                     hand.meleeWeapon.buffs, hand.meleeWeapon.attachment);
        }

        return EmptySlot(slot);
    }

    private static ShootingRangeScoreboard.WeaponSlotSnapshot CaptureShoulder(string slot, ShoulderWeaponStats shoulder)
    {
        if (shoulder != null && shoulder.weaponKind == ShoulderWeaponKind.Range && shoulder.shoulderweapon != null)
            return CaptureWeapon(slot, "Shoulder", shoulder.shoulderweapon, shoulder.shoulderweapon.newWeaponName,
                                 shoulder.shoulderweapon.buffs, shoulder.shoulderweapon.attachment);

        return EmptySlot(slot);
    }

    private static ShootingRangeScoreboard.WeaponSlotSnapshot EmptySlot(string slot)
    {
        return new ShootingRangeScoreboard.WeaponSlotSnapshot { slot = slot, kind = "None" };
    }

    private static ShootingRangeScoreboard.WeaponSlotSnapshot CaptureWeapon(
        string slot, string kind, ItemInstance weapon, string forgedName,
        System.Collections.Generic.List<EquipmentBuff> weaponBuffs,
        System.Collections.Generic.List<PartInstance> attachment)
    {
        string itemName = (weapon.item != null) ? weapon.item.itemName : "";

        var snap = new ShootingRangeScoreboard.WeaponSlotSnapshot
        {
            slot = slot,
            kind = kind,
            itemName = itemName,
            weaponName = string.IsNullOrEmpty(forgedName) ? itemName : forgedName,
        };

        // 全部複製一份，不留引用 —— 之後改裝、換零件不會回頭改到舊紀錄
        if (weaponBuffs != null) snap.weaponBuffs.AddRange(weaponBuffs);

        if (attachment != null)
        {
            foreach (var part in attachment)
            {
                // item == null 代表該槽沒裝零件（跟 PlayerStats 的判斷一致）
                if (part == null || part.item == null) continue;

                var p = new ShootingRangeScoreboard.WeaponPartSnapshot
                {
                    partName = part.item.itemName,
                    partType = part.partType,
                };
                if (part.buffs != null) p.buffs.AddRange(part.buffs);

                snap.parts.Add(p);
            }
        }

        snap.totals = ComputeTotals(snap);
        return snap;
    }

    // 武器 + 零件合併後的最終值，算法跟 WeaponStats.GetAttribute 相同：(Σ Add) × Π(1 + Multiplier)
    private static System.Collections.Generic.List<ShootingRangeScoreboard.AttributeValue> ComputeTotals(
        ShootingRangeScoreboard.WeaponSlotSnapshot snap)
    {
        var add = new System.Collections.Generic.Dictionary<Attributes, float>();
        var mul = new System.Collections.Generic.Dictionary<Attributes, float>();
        var order = new System.Collections.Generic.List<Attributes>();   // 保留首次出現的順序，讀起來比較直觀

        void Accumulate(System.Collections.Generic.List<EquipmentBuff> buffs)
        {
            foreach (var b in buffs)
            {
                if (!add.ContainsKey(b.attribute))
                {
                    add[b.attribute] = 0f;
                    mul[b.attribute] = 1f;
                    order.Add(b.attribute);
                }

                if (b.mode == BuffApplyMode.Add) add[b.attribute] += b.value;
                else if (b.mode == BuffApplyMode.Multiplier) mul[b.attribute] *= (1f + b.value);
            }
        }

        Accumulate(snap.weaponBuffs);
        foreach (var p in snap.parts) Accumulate(p.buffs);

        var result = new System.Collections.Generic.List<ShootingRangeScoreboard.AttributeValue>();
        foreach (var attr in order)
            result.Add(new ShootingRangeScoreboard.AttributeValue { attribute = attr, value = add[attr] * mul[attr] });

        return result;
    }

    private bool IsPlayer(Collider other)
    {
        // 玩家的 Collider 可能在子物件上，attachedRigidbody 會指向真正在移動的根物件
        Transform t = (other.attachedRigidbody != null) ? other.attachedRigidbody.transform : other.transform;

        if (player != null)
            return t == player || t.IsChildOf(player);

        return !string.IsNullOrEmpty(playerTag) &&
               (t.CompareTag(playerTag) || other.CompareTag(playerTag));
    }

    /// <summary>只在「沒有活著的敵人」的生成點補一個。</summary>
    public void SpawnMissing()
    {
        if (enemyPrefab == null)
        {
            Debug.LogWarning($"[ShootingRangeTrigger] '{name}' 沒有指定 Enemy Prefab。", this);
            return;
        }

        foreach (var slot in slots)
        {
            if (slot == null || !slot.enabled) continue;

            // 這個點的敵人還活著 → 跳過
            if (slot.spawned != null) continue;

            SpawnInSlot(slot);
        }
    }

    // ───── 單點生成 / 清場（給 WeaponBalanceBot 用）─────

    /// <summary>
    /// 只在第 index 個生成點生成（不管 enabled，呼叫端自己決定）。
    /// 那裡已經有活著的敵人就直接回傳它。
    /// </summary>
    public GameObject SpawnSingle(int index)
    {
        if (enemyPrefab == null || index < 0 || index >= slots.Count || slots[index] == null) return null;

        var slot = slots[index];
        if (slot.spawned == null) SpawnInSlot(slot);
        return slot.spawned;
    }

    /// <summary>移除所有由這個踏板生成、還活著的敵人，並取消重生排程。不會產生擊殺紀錄。</summary>
    public void DespawnAll()
    {
        foreach (var slot in slots)
        {
            if (slot == null) continue;
            if (slot.spawned != null) Destroy(slot.spawned);
            slot.spawned = null;
            slot.awaitingRespawn = false;
            slot.respawnAt = -1f;
        }
    }

    /// <summary>踏板到第 index 個生成點的水平距離（公尺）。跟 CSV 的 distance 同一個算法。</summary>
    public float DistanceTo(int index)
    {
        if (index < 0 || index >= slots.Count || slots[index] == null || slots[index].point == null) return -1f;
        return HorizontalDistance(transform.position, slots[index].point.position);
    }

    // ───── 自動重生 ─────

    private void Update()
    {
        if (slots == null) return;

        foreach (var slot in slots)
        {
            if (slot == null) continue;

            // 偵測「被摧毀」：生成過（awaitingRespawn），而且現在是 null
            if (slot.awaitingRespawn && slot.spawned == null && slot.respawnAt < 0f)
            {
                if (slot.enabled && slot.respawnTime >= 0f)
                {
                    slot.respawnAt = Time.time + slot.respawnTime;
                }
                else
                {
                    // 不自動重生（負數或被關閉）：等玩家再踩踏板。
                    // 清掉旗標，免得之後把 respawnTime 改成正數時，補生一隻很久以前死掉的。
                    slot.awaitingRespawn = false;
                }
            }

            // 時間到 → 重生
            if (slot.respawnAt >= 0f && Time.time >= slot.respawnAt)
            {
                slot.respawnAt = -1f;
                slot.awaitingRespawn = false;

                // 等待期間被關閉、或玩家已經踩踏板補上了 → 不重生
                if (slot.enabled && slot.spawned == null && enemyPrefab != null)
                    SpawnInSlot(slot);
            }
        }
    }

    /// <summary>在這個生成點生成一個敵人。</summary>
    private void SpawnInSlot(SpawnSlot slot)
    {
        if (slot.point == null) return;
        Transform point = slot.point;

        Quaternion rot = useSpawnPointRotation ? point.rotation : FacePad(point.position);
        GameObject enemy = Instantiate(enemyPrefab, point.position, rot);

        string enemyName = slot.ResolvedName;
        enemy.name = enemyName;

        // 血量覆蓋。必須在 DamageRecord.Setup 之前 —— Setup 會重新記下目前血量，
        // 否則把血量調低會被 DamageRecord 誤判成「受到攻擊」而提早開始計時。
        var stats = enemy.GetComponentInChildren<ModularEntityStats>();
        if (hp > 0f && stats != null)
        {
            stats.maxHealth = hp;
            stats.health = hp;
        }

        // 移動速度：TestTargetMovement 讀 sprintSpeed
        if (moveSpeed >= 0f && stats != null)
            stats.sprintSpeed = moveSpeed;

        // 告訴靶子「射擊位置在哪」，它才知道哪個方向是左右
        var brain = enemy.GetComponentInChildren<TestTargetBrain>();
        if (brain != null)
            brain.SetShooterPosition(transform.position);

        // 把名稱和計分板交給 DamageRecord（prefab 沒掛就不計時）
        var record = enemy.GetComponentInChildren<DamageRecord>();
        if (record != null)
            record.Setup(enemyName, scoreboard, HorizontalDistance(transform.position, point.position));

        // 跟 EnemySpawner 一樣登記到 GameManager（AddEnemy 內部會擋重複）
        if (GameManager.Instance != null)
            GameManager.Instance.AddEnemy(enemy);

        slot.spawned = enemy;
        slot.awaitingRespawn = true;
        slot.respawnAt = -1f;   // 踏板手動補上時，取消還在等的重生排程

        if (logSpawns)
            Debug.Log($"[ShootingRangeTrigger] 在 '{point.name}' 生成 '{enemyName}'", this);
    }

    // 踏板到生成點的水平距離。不算高度差 —— 「10m 靶」指的是水平距離
    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        a.y = 0f; b.y = 0f;
        return Vector3.Distance(a, b);
    }

    // 水平轉向踏板，讓靶子面對射擊位置
    private Quaternion FacePad(Vector3 from)
    {
        Vector3 dir = transform.position - from;
        dir.y = 0f;
        return (dir.sqrMagnitude > 0.0001f) ? Quaternion.LookRotation(dir) : Quaternion.identity;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (slots == null) return;

        Gizmos.color = Color.red;
        foreach (var s in slots)
        {
            if (s == null || s.point == null) continue;
            Gizmos.color = s.enabled ? Color.red : Color.gray;   // 關閉的點畫成灰色
            Gizmos.DrawLine(transform.position, s.point.position);
            Gizmos.DrawWireSphere(s.point.position, 0.5f);
        }
    }
#endif
}
