using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// 敵人生成器。
///
/// 每隔一段隨機時間，按權重抽一個 SpawnGroup，把那一組裡面的敵人全部生出來，
/// 並註冊到 GameManager.enemies。
///
/// 生成需要同時滿足三個條件：玩家在 activeRange 內、存活數未達上限、
/// 以及（canSpawnInSight = false 時）生成器不在玩家視野內。
///
/// 任何一個條件不成立時，計時器都「不會」重置 —— 它停在 0 以下，
/// 條件一恢復就在同一幀補上生成，被擋住的等待時間不會白白浪費。
/// </summary>
public class EnemySpawner : MonoBehaviour
{
    /// <summary>
    /// 一個生成組。被抽中時，spawnEnemy 裡的敵人會「全部」生成，不是從中挑一個。
    /// </summary>
    [System.Serializable]
    public class SpawnGroup
    {
        [Tooltip("這一組被抽中的權重。是相對值 —— 權重 2 的組被抽中的機率是權重 1 的兩倍。\n" +
                 "0 或負數 = 這一組永遠不會被抽中。")]
        public float spawnWeight = 1f;

        [Tooltip("這一組被抽中時要生成的所有敵人。")]
        public GameObject[] spawnEnemy;
    }

    [Header("Spawn Groups")]
    [SerializeField] private SpawnGroup[] spawnGroups;

    [Tooltip("每隻敵人生在生成器周圍這個半徑內的隨機位置（水平），並貼到 NavMesh 上。\n" +
             "原本整組都生在同一個點，起點重疊 → 路徑重疊，而且第一個物理步會互相彈開。\n" +
             "0 = 照舊全部生在生成器位置。")]
    [SerializeField, Min(0f)] private float spawnRadius = 5f;

    [Header("Timing")]
    [Tooltip("兩次生成之間的間隔（秒）。x = 最小值，y = 最大值。")]
    [SerializeField] private Vector2 spawnTimeRange = new Vector2(5f, 10f);

    [Header("Visibility")]
    [Tooltip("false = 生成器在玩家視野內時不生成，等玩家把視線轉開才生成。\n" +
             "true  = 玩家看著也照樣生成。")]
    [SerializeField] private bool canSpawnInSight = false;

    [Tooltip("留空會自動抓 Camera.main。")]
    [SerializeField] private Camera playerCamera;

    [Header("Limits")]
    [Tooltip("這個生成器同時存活在場上的敵人數量上限。達到上限就暫停，等敵人死掉才恢復。\n" +
             "只在「生成一組之前」檢查一次，所以一組多隻時最終數量可能略高於上限。\n" +
             "0 或負數 = 不限制。")]
    [SerializeField] private int maxAliveEnemies = 10;

    [Header("Active Range")]
    [Tooltip("玩家在這個距離內才會生成敵人。用 3D 直線距離。\n" +
             "0 或負數 = 不限制距離。")]
    [SerializeField] private float activeRange = 100f;

    [Tooltip("玩家的 Transform。留空會退而使用相機位置當作玩家位置 ——\n" +
             "第三人稱下相機在玩家後方幾公尺，對 activeRange 這種尺度通常夠用。")]
    [SerializeField] private Transform playerTransform;

    [Header("Default Target")]
    [Tooltip("true = 生成的敵人一出生就把船的 Core 設成預設目標，直接往船的方向去，\n" +
             "不用等自己的偵測範圍掃到東西。\n" +
             "Core 取自 LandshipNavigation.Instance.core。")]
    [SerializeField] private bool setShipAsTarget = false;

    // 規格指定的固定值。要能在 Inspector 調的話跟我說，拉成欄位很快。
    private const float ShipTargetPriority = 30f;
    private const float ShipTargetDecayMultiplier = 0f;

    private float _spawnTimer;

    // 這個生成器生出來、目前還活著的敵人。
    // 敵人死亡時是被 Destroy 的（ModularEntityStats），所以靠 Unity 的假 null
    // 就能判斷存活，不需要任何死亡通知機制。
    // 存活上限用。敵人改用物件池之後，死掉的敵人不會變成 null（只是停用、之後還會被別的生成器重新取出），
    // 所以記下生成當下的 SpawnSerial：物件還啟用、而且序號沒變，才算是「我生的那一隻還活著」。
    private struct SpawnedEntry
    {
        public GameObject go;
        public ModularEntityStats stats;
        public int serial;
    }
    private readonly List<SpawnedEntry> _spawned = new List<SpawnedEntry>();

    private void OnEnable()
    {
        RollTimer();
    }

    private void Update()
    {
        if (_spawnTimer > 0f)
        {
            _spawnTimer -= Time.deltaTime;
            return;
        }

        // ★ 以下三個條件任何一個不成立都直接 return，刻意「不」重置計時器。
        //   計時器停在 0 以下，條件一恢復就在同一幀補上生成，
        //   而不是重新等一輪。被擋住的時間不會白白浪費。
        //
        //   順序由便宜到貴：IsPlayerInRange 只是一次距離平方比較，
        //   CountAlive 要走訪清單，所以放在後面。
        if (!IsPlayerInRange()) return;
        if (IsAtAliveCap()) return;
        if (!canSpawnInSight && IsVisibleToPlayer()) return;

        SpawnOnce();
        RollTimer();
    }

    private void RollTimer()
    {
        float min = Mathf.Max(0f, spawnTimeRange.x);
        float max = Mathf.Max(min, spawnTimeRange.y);
        _spawnTimer = Random.Range(min, max);
    }

    /// <summary>玩家在不在 activeRange 內。</summary>
    private bool IsPlayerInRange()
    {
        if (activeRange <= 0f) return true;   // 不限制距離

        Transform player = ResolvePlayer();

        // ★ 找不到玩家 → 回傳 true（不擋）。
        //   跟 IsVisibleToPlayer 同一個規則：評估不了的限制條件就不套用。
        //   反過來（找不到就永不生成）會變成一個沒有任何錯誤訊息的靜默失效。
        if (player == null) return true;

        return (player.position - transform.position).sqrMagnitude <= activeRange * activeRange;
    }

    private Transform ResolvePlayer()
    {
        if (playerTransform != null) return playerTransform;

        // 退路：用相機位置近似玩家位置。第三人稱下相機在玩家後方幾公尺，
        // 對 activeRange 這種數十公尺的尺度通常夠用。要精準就把 playerTransform 指好。
        Camera cam = ResolveCamera();
        return (cam != null) ? cam.transform : null;
    }

    /// <summary>這個生成器的存活敵人數是否已達上限。</summary>
    private bool IsAtAliveCap()
    {
        if (maxAliveEnemies <= 0) return false;   // 不限制
        return CountAlive() >= maxAliveEnemies;
    }

    /// <summary>
    /// 清掉已被銷毀的項目，回傳還活著的數量。
    ///
    /// 只在計時器歸零那一幀才會被呼叫，所以這個 O(n) 走訪的成本可以忽略 ——
    /// n 本身也被 maxAliveEnemies 綁住了。
    /// </summary>
    private int CountAlive()
    {
        // 倒著走，RemoveAt 才不會跳過元素
        for (int i = _spawned.Count - 1; i >= 0; i--)
        {
            SpawnedEntry e = _spawned[i];
            bool alive = e.go != null && e.go.activeInHierarchy
                         && (e.stats == null || (e.stats.SpawnSerial == e.serial && !e.stats.IsDead));
            if (!alive) _spawned.RemoveAt(i);
        }
        return _spawned.Count;
    }

    /// <summary>
    /// 生成器現在在不在玩家相機的視錐內。
    ///
    /// 只做視錐判定，不做遮擋判定 —— 生成器躲在牆後面但落在畫面範圍內時，
    /// 這裡仍然算「看得到」，會擋住生成。
    /// </summary>
    private bool IsVisibleToPlayer()
    {
        Camera cam = ResolveCamera();
        if (cam == null) return false;   // 沒有相機 = 沒有人在看，不擋

        Vector3 vp = cam.WorldToViewportPoint(transform.position);

        // z <= 0 代表在相機背後。WorldToViewportPoint 對背後的點
        // 仍然會回傳看似合法的 x/y，所以這個檢查不能省。
        return vp.z > 0f
            && vp.x >= 0f && vp.x <= 1f
            && vp.y >= 0f && vp.y <= 1f;
    }

    /// <summary>
    /// 每次都確認一下相機還活著。
    /// 專案裡有四組 camera set 會被 UIManager 開開關關，
    /// 快取住的那台可能已經被 SetActive(false) 了。
    /// </summary>
    private Camera ResolveCamera()
    {
        if (playerCamera == null || !playerCamera.isActiveAndEnabled)
            playerCamera = Camera.main;

        return playerCamera;
    }

    private void SpawnOnce()
    {
        SpawnGroup group = PickGroup();
        if (group == null || group.spawnEnemy == null) return;

        // 生成器中心在 NavMesh 上的高度，用來把每個分散點的高度差加回生成器的高度
        //（生成器的 pivot 不一定剛好貼地，直接用 NavMesh 高度可能讓敵人生在半空或地下）
        NavMeshHit centerHit = default;
        bool canSpread = spawnRadius > 0f &&
                         NavMesh.SamplePosition(transform.position, out centerHit, spawnRadius + 5f, NavMesh.AllAreas);
        float centerNavY = canSpread ? centerHit.position.y : 0f;

        for (int i = 0; i < group.spawnEnemy.Length; i++)
        {
            GameObject prefab = group.spawnEnemy[i];
            if (prefab == null) continue;

            Vector3 spawnPos = transform.position;
            if (canSpread)
            {
                Vector2 r = Random.insideUnitCircle * spawnRadius;
                Vector3 candidate = transform.position + new Vector3(r.x, 0f, r.y);
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                    spawnPos = new Vector3(hit.position.x, transform.position.y + (hit.position.y - centerNavY), hit.position.z);
            }

            // 物件池取代 Instantiate（場景裡沒有 PrefabPool 時會自動退回 Instantiate）。
            // Spawn 回傳前已經呼叫過敵人各元件的 OnSpawned（重置血量、AI、移動狀態），
            // 所以下面加的目標（船的 Core）不會被清掉。
            GameObject enemy = PrefabPool.Spawn(prefab, spawnPos, transform.rotation);
            if (enemy == null) continue;

            // 敵人也可能在自己的 Awake / OnEnable 裡就呼叫過 AddEnemy
            //（Instantiate 是同步的，那些回呼在這一行之前就跑完了）。
            // GameManager.AddEnemy 內部有 Contains 檢查，重複註冊是安全的。
            if (GameManager.Instance != null)
                GameManager.Instance.AddEnemy(enemy);

            if (setShipAsTarget) AssignShipAsTarget(enemy);

            ModularEntityStats stats = enemy.GetComponent<ModularEntityStats>();
            _spawned.Add(new SpawnedEntry
            {
                go = enemy,
                stats = stats,
                serial = stats != null ? stats.SpawnSerial : 0,
            });
        }
    }

    /// <summary>
    /// 把船的 Core 加進這隻敵人的目標清單。
    ///
    /// 時機是安全的：Instantiate 會同步跑完 Awake / OnEnable，而 targets 是在
    /// 欄位宣告時就 new 好的，所以這裡加得進去。ModularEntityBrain.Start()
    /// 只設 spawnLocation / pathFinder / 狀態，不會清空 targets，
    /// 所以這一筆不會在下一幀被洗掉。
    ///
    /// AddTarget 內部有去重，跟敵人自己偵測到 Core 的情況不會打架 ——
    /// 重複時取較高的優先度。
    /// </summary>
    private void AssignShipAsTarget(GameObject enemy)
    {
        LandshipNavigation nav = LandshipNavigation.Instance;
        if (nav == null || nav.core == null) return;

        // 用 GetComponentInChildren 而不是 GetComponent：目前的 prefab
        // （Wolf / Falcon）brain 都掛在 root，但這樣不挑階層結構。
        ModularEntityBrain brain = enemy.GetComponentInChildren<ModularEntityBrain>();
        if (brain == null) return;

        brain.AddTarget(nav.core, TargetType.Core, ShipTargetPriority, ShipTargetDecayMultiplier);
    }

    /// <summary>按 spawnWeight 加權抽一組。權重 &lt;= 0 的組會被跳過。</summary>
    private SpawnGroup PickGroup()
    {
        if (spawnGroups == null || spawnGroups.Length == 0) return null;

        float total = 0f;
        SpawnGroup lastValid = null;

        for (int i = 0; i < spawnGroups.Length; i++)
        {
            SpawnGroup g = spawnGroups[i];
            if (g == null || g.spawnWeight <= 0f) continue;

            total += g.spawnWeight;
            lastValid = g;
        }

        if (total <= 0f) return null;   // 沒有任何一組有正權重

        float roll = Random.value * total;

        for (int i = 0; i < spawnGroups.Length; i++)
        {
            SpawnGroup g = spawnGroups[i];
            if (g == null || g.spawnWeight <= 0f) continue;

            roll -= g.spawnWeight;
            if (roll <= 0f) return g;
        }

        // 浮點誤差讓 roll 剛好沒被扣完時的保底。回傳最後一個有效組，
        // 不要回傳 null —— 那會表現成「生成器偶爾莫名其妙跳過一次」。
        return lastValid;
    }
}