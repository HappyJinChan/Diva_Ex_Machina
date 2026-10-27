using NaughtyAttributes;
using System.Collections.Generic;
using UnityEngine;

[System.Serializable]
public enum EntityState
{
    Idle,
    Patrolling,
    Retreat,
    Chasing,
    Combat,
}

[System.Serializable]
public enum TargetType
{
    Player,
    Enemy,
    Building,
    Core,
    Obstacle,
}

[System.Serializable]
public class Target
{
    public Transform targetTransform;
    public TargetType type;
    public float targetPriority;
    public float priorityDecreaseMultiplier;
    public Target(Transform targetTransform, float targetPriority, float priorityDecreaseMultiplier)
    {
        this.targetTransform = targetTransform;
        this.targetPriority = targetPriority;
        this.priorityDecreaseMultiplier = priorityDecreaseMultiplier;
    }
}

[System.Serializable]
public class TargetPreference
{
    public TargetType targetType;
    public float priorityMultiplier;
    public float priorityDecreaseMultiplier;
}

public class ModularEntityBrain : MonoBehaviour, IManagedEntity, IPooled
{
    public ModularEntityMovement modularEntityMovement;

    [Header("Turrets")]
    [SerializeField] protected TurretController[] turrets;   // 0..N 座，Inspector 拖或自動抓

    [Header("Sensors")]
    [Tooltip("偵測範圍（取代 EnemyDetectRange 的 trigger）。留空會自動抓子物件上所有 EnemySensor。")]
    [SerializeField] protected EnemySensor[] sensors;

    protected PathFinder pathFinder;
    protected ShipPassenger selfPassenger;   // 自己的船上狀態（掛在同一個 GameObject 上）

    public EntityState currentState;

    /// <summary>
    /// 這隻實體目前應該「看著」的目標（給頭部注視之類的表現用）。
    /// 每次 Think 更新一次，讀取不需要任何運算；沒有目標時為 null。
    /// 要改成別的規則（例如只在 Combat 才看）就覆寫 SelectLookTarget。
    /// </summary>
    public Transform LookTarget { get; protected set; }

    /// <summary>
    /// Combat 狀態下的交戰目標（每次 Think 更新；其他狀態為 null）。
    /// ★ 這是近戰等系統取得目標的唯一來源 —— 它們不自己存目標，每次都來這裡讀。
    /// </summary>
    public Transform CombatTarget { get; protected set; }

    [Header("Melee")]
    [Tooltip("近戰攻擊。留空會自動在自己或子物件上尋找；沒有就不做近戰。")]
    [SerializeField] protected ModularEntityMeleeManager meleeManager;

    /// <summary>是否正在進行近戰攻擊（期間移動與轉向由近戰接管）。</summary>
    public bool IsMeleeAttacking => meleeManager != null && meleeManager.IsAttacking;
    public List<TargetPreference> targetPreferences = new List<TargetPreference>();
    public List<Target> targets = new List<Target>();

    protected Vector3[] path;
    public Vector3 destination;
    public Vector3 spawnLocation;
    [Header("Idle Setting")]
    [MinMaxSlider(0f, 15f)]
    public Vector2 idleDestinationUpdateRange = new Vector2(1f, 3f);
    public float idleActivityAreaRadius = 10f;
    [Header("Patrol Setting")]
    [MinMaxSlider(0f, 15f)]
    public Vector2 patrolDestinationUpdateRange = new Vector2(1f, 3f);
    public float patrolActivityAreaRadius = 20f;
    [Header("Retreat Setting")]
    [MinMaxSlider(0f, 15f)]
    public Vector2 retreatDestinationUpdateRange = new Vector2(1f, 3f);
    [Header("Chase Setting")]
    [MinMaxSlider(0f, 15f)]
    public Vector2 chaseDestinationUpdateRange = new Vector2(1f, 3f);
    [Header("Combat Setting")]
    [MinMaxSlider(0f, 15f)]
    public Vector2 combatDestinationUpdateRange = new Vector2(1f, 3f);
    public float combatActivityAreaRadius = 20f;
    public float reactionCoolDown = 3f;
    public float reactionChance = 0.5f;

    [Header("Docking Proximity Update")]
    [Tooltip("離最近的停靠點小於這個水平距離時，改用下面的更新間隔。所有狀態都適用。")]
    public float dockUpdateDistance = 30f;

    [Tooltip("靠近停靠點時的路徑更新間隔（秒）。不要設 0 —— NavMesh.CalculatePath 是主執行緒同步呼叫，" +
             "每個 physics step 算一次，敵人一多就會卡。0.1~0.3 通常夠了。")]
    [MinMaxSlider(0f, 15f)]
    public Vector2 dockDestinationUpdateRange = new Vector2(0.1f, 0.3f);

    [Header("Attack Slot")]
    [Tooltip("持有攻擊名額的時間上限（秒）。逾時交還名額，並把第一、第二優先目標的優先度對調，讓這隻敵人改打另一個。\n" +
             "名額是「每個目標」各自計算的，上限在 GameManager.maxAttackersPerTarget。\n" +
             "0 = 不設上限（拿到就一直持有到死亡為止）。")]
    public float attackSlotHoldSeconds = 8f;

    private bool _hasAttackSlot;
    private float _attackSlotExpireTime;
    private Transform _slotTarget;   // 名額是針對哪個目標拿的（換目標時要先還舊的）

    protected float destinationTimer;

    private readonly Dictionary<TargetType, float> decayMultiplierByType = new Dictionary<TargetType, float>();

    [Header("Priority Retention")]
    [Tooltip("目標與實體的距離小於這個半徑時，該目標的 targetPriority 不會隨時間衰減 —— " +
             "近身的威脅不會被「遺忘」。只在多目標（targets.Count > 1）情境下有意義，" +
             "因為單一目標本來就不衰減。\n" +
             "0 = 關閉此功能，維持原本的衰減行為。距離為 3D 直線距離。")]
    [SerializeField] protected float priorityRetentionRadius = 0f;

    [Header("Damage Threat（被打就拉仇恨）")]
    [Tooltip("受到傷害時提高攻擊者的優先度。\n" +
             "· 用減防後的實際傷害計算\n" +
             "· 其他敵人造成的傷害（例如死亡爆炸）不算\n" +
             "· 玩家和玩家的防禦砲塔都算")]
    public bool damageThreat = true;

    [Tooltip("受傷累積出來的優先度上限。參考：防禦工事 50、Core 30、玩家 25。")]
    [Min(0f)] public float maxThreatPriority = 60f;

    [Tooltip("每造成「最大生命值的多少 %」的傷害，攻擊者的優先度 +1。\n" +
             "0.5 = 最大生命值 100 時，一次 20 點傷害 +40、一次 2.5 點傷害 +0.5。")]
    [Min(0.01f)] public float damagePercentPerPriority = 0.5f;

    [Tooltip("攻擊者原本不在目標清單裡（例如從偵測範圍外開槍）時，新加入的那一筆用的衰減速度。跟感測器規則一樣預設 1。")]
    [Min(0f)] public float threatPriorityDecreaseMultiplier = 1f;

    [SerializeField] protected float waypointArriveRadius = 5f;
    [SerializeField] protected float slowDownRadius = 6f;

    [Tooltip("重新定位航點時，最多往前搜尋幾個線段。太大會在 U 型路徑上抄近路穿牆，太小則在路徑折返時反應遲鈍。2 通常剛好。")]
    [SerializeField] protected int waypointSearchLookAhead = 2;

    protected int currentWaypointIndex = 0;

    [SerializeField] protected float facingDeadzone = 1f;   // 角度誤差小於此就不轉，避免抖動

    protected virtual void Start()
    {
        spawnLocation = transform.position;
        pathFinder = gameObject.GetComponent<PathFinder>();
        selfPassenger = gameObject.GetComponent<ShipPassenger>();
        currentState = EntityState.Idle;
        RebuildPreferenceCache();

        if (turrets == null || turrets.Length == 0)
            turrets = GetComponentsInChildren<TurretController>();

        if (sensors == null || sensors.Length == 0)
            sensors = GetComponentsInChildren<EnemySensor>(true);

        if (meleeManager == null)
            meleeManager = GetComponentInChildren<ModularEntityMeleeManager>();

        _started = true;
    }

    // ═══════════════════════ 集中更新（ModularEntityManager） ═══════════════════════
    //
    // 原本這裡是 FixedUpdate —— 每個物理步由 Unity 各自呼叫，每步都完整思考一次。
    // 現在改由 ModularEntityManager 統一呼叫，而且降頻：預設每 0.1 秒思考一次（錯開）。
    //
    // 思考之間的物理步，Movement 照常每步執行，沿用這次思考留下的：
    //   · 移動方向（HorizontalMovement）
    //   · 朝向（FaceMoveDirection → Movement.SetFacingTarget，Movement 每步轉過去）
    //   · 砲塔要瞄誰（turret.TrackTarget，砲塔自己每幀用目標當下的位置與速度重算瞄準點）
    //
    // ⚠ 寫在 Think() 裡、跟時間有關的計算，一律用 ThinkDeltaTime，不要用 Time.fixedDeltaTime ——
    //   兩次思考之間隔的是好幾個物理步，用 fixedDeltaTime 會讓計時器走慢好幾倍。

    private int _managerIndex = -1;   // 在 ModularEntityManager 清單裡的位置，-1 = 沒登記
    int IManagedEntity.ManagerIndex { get => _managerIndex; set => _managerIndex = value; }

    private bool _started;              // Start() 跑完之前不能思考（pathFinder 等欄位還沒抓）
    private float _lastThinkTime = -1f;

    /// <summary>下一次該思考的時間（Time.fixedTime）。由 ModularEntityManager 讀取。</summary>
    public float NextThinkTime { get; private set; }

    /// <summary>距離上一次思考經過的時間（秒）。Think() 裡的計時器請用這個。</summary>
    protected float ThinkDeltaTime { get; private set; }

    protected virtual void OnEnable()
    {
        _lastThinkTime = -1f;
        ModularEntityManager.Register(this);
    }

    protected virtual void OnDisable()
    {
        ModularEntityManager.Unregister(this);

        // 死亡 / 回收到物件池：交還攻擊名額。
        // 原本靠 Destroy 之後 GameManager 自己清掉空參照；池化後物件不會被銷毀，不還的話名額會被一直佔著。
        ReleaseAttackSlot();
    }

    // ═══════════════════════ 物件池 ═══════════════════════
    //
    // Start() 只在第一次生成時跑。重複使用時，下面這些「每一隻都要從頭開始」的狀態在這裡重置。
    // （元件參照、偏好快取這類設定不用重抓。）
    // 呼叫時機：PrefabPool.Spawn 擺好位置、SetActive(true) 之後，回傳給生成端（EnemySpawner）之前 ——
    // 所以生成端之後加的目標（例如船的 Core）不會被這裡清掉。

    public virtual void OnSpawned()
    {
        spawnLocation = transform.position;
        currentState = EntityState.Idle;
        targets.Clear();
        path = null;
        destination = transform.position;
        destinationTimer = 0f;
        currentWaypointIndex = 0;
        LookTarget = null;
        CombatTarget = null;
        _velocityTarget = null;
        _velocityTargetRb = null;
    }

    public virtual void OnDespawned() { }

    /// <summary>登記時由 ModularEntityManager 呼叫，把第一次思考錯開到隨機時間點。</summary>
    public void ScheduleFirstThink(float time)
    {
        NextThinkTime = time;
    }

    /// <summary>由 ModularEntityManager 在輪到這隻敵人時呼叫。</summary>
    public void RunThink(float now, float interval)
    {
        if (!_started) return;   // 還沒 Start → 下一步再試（NextThinkTime 不動）

        ThinkDeltaTime = (_lastThinkTime < 0f) ? Time.fixedDeltaTime : now - _lastThinkTime;
        _lastThinkTime = now;
        NextThinkTime = now + interval;

        Think();
    }

    /// <summary>一次完整的思考。子類別要加邏輯就覆寫這裡（記得呼叫 base.Think()）。</summary>
    protected virtual void Think()
    {
        // 失衡：不做任何決策（移動、朝向、近戰由 Movement / MeleeManager 自己停掉，砲塔自己停火）。
        // 只維持偵測與目標優先度，失衡結束時才有正確的目標可以接著打。
        // 攻擊名額交還，讓其他敵人可以補上。
        if (modularEntityMovement != null && modularEntityMovement.IsStaggered)
        {
            ReleaseAttackSlot();
            RunSensors();
            PriorityUpdate();
            return;
        }

        // 這次思考沒有決定朝向的話，就不要再轉（跟原本「沒呼叫 FaceMoveDirection 就不轉」一致）。
        // 近戰攻擊中的朝向由近戰每個物理步指定，這裡不能清掉。
        if (!IsMeleeAttacking)
            modularEntityMovement.ClearFacingTarget();

        RunSensors();   // 先偵測，這次思考就能用到新發現的目標
        AttackSlotUpdate();
        PriorityUpdate();
        PathUpdate();
        StateBehaviour();
        if (currentState != EntityState.Combat) CombatTarget = null;
        LookTarget = SelectLookTarget();
    }

    /// <summary>決定 LookTarget：追擊與戰鬥中看著交戰目標，其他狀態不看。</summary>
    protected virtual Transform SelectLookTarget()
    {
        if (currentState == EntityState.Chasing || currentState == EntityState.Combat)
            return FindTarget();
        return null;
    }

    /// <summary>讓每個 EnemySensor 做偵測（各自依 scanInterval 決定這次要不要真的查詢）。</summary>
    protected void RunSensors()
    {
        if (sensors == null) return;

        float now = Time.fixedTime;
        for (int i = 0; i < sensors.Length; i++)
        {
            EnemySensor s = sensors[i];
            if (s != null && s.isActiveAndEnabled) s.Scan(now);
        }
    }

    protected void PriorityUpdate()
    {
        float dt = ThinkDeltaTime;

        // ★ 第一步：無條件剔除已被銷毀的目標。
        //
        //   這一段刻意放在 targets.Count > 1 的閘門「外面」。
        //   優先度衰減只在多目標時進行是刻意的設計（最後一個目標不衰減 =
        //   敵人不會忘記自己正在戰鬥中），但那個設計有一個前提：卡住的那個目標
        //   必須是活著的。
        //
        //   目標被 Destroy 之後 targetTransform 會變成 Unity 的假 null，
        //   優先度又最高 → FindTarget() 每幀都回傳它 → 各狀態判定「目標消失」
        //   → 切回 Patrolling。實體從此變成植物人。
        //
        //   在這裡剔除，就不需要任何跨物件的死亡通知機制 ——
        //   目標死掉後最多一個 physics step 就會被清掉。
        PruneDestroyedTargets();

        if (targets.Count > 1)
        {
            // priorityRetentionRadius 的平方，迴圈外算一次就好。
            float retentionSqr = priorityRetentionRadius * priorityRetentionRadius;

            for (int x = targets.Count - 1; x >= 0; x--)
            {
                Target t = targets[x];

                // ★ 近距離保留：目標離實體夠近時，跳過這一幀的衰減。
                //    這裡 t.targetTransform 一定非 null —— PruneDestroyedTargets()
                //    已在本方法開頭把假 null 的項目清掉了。
                //    用平方距離比大小，不開根號（跟本檔 SqrDistanceToSegmentXZ 一致）。
                //    retentionSqr == 0（半徑 0）時這個條件幾乎不可能成立 →
                //    等於關閉功能，行為與加這段之前完全相同。
                if (retentionSqr > 0f)
                {
                    Vector3 toTarget = t.targetTransform.position - transform.position;
                    if (toTarget.sqrMagnitude <= retentionSqr)
                        continue;   // 在範圍內 → 不衰減，這一幀也不會因衰減而被移除
                }

                float typeMultiplier = 1f;
                if (decayMultiplierByType.TryGetValue(t.type, out float found))
                    typeMultiplier = found;

                t.targetPriority -= t.priorityDecreaseMultiplier * typeMultiplier * dt;

                if (t.targetPriority <= 0f)
                    targets.RemoveAt(x);
            }
        }
    }

    /// <summary>剔除 targetTransform 已被銷毀的項目。倒著走，RemoveAt 才不會跳過元素。</summary>
    protected void PruneDestroyedTargets()
    {
        for (int x = targets.Count - 1; x >= 0; x--)
        {
            Target t = targets[x];
            if (t == null || t.targetTransform == null)
                targets.RemoveAt(x);
        }
    }
    protected virtual void RebuildPreferenceCache()
    {
        decayMultiplierByType.Clear();
        for (int i = 0; i < targetPreferences.Count; i++)
        {
            decayMultiplierByType[targetPreferences[i].targetType] = targetPreferences[i].priorityDecreaseMultiplier;
        }
    }

    protected virtual void StateBehaviour()
    {
        switch (currentState)
        {
            case EntityState.Idle:
                IdleBehaviour();
                break;
            case EntityState.Patrolling:
                PatrollingBehaviour();
                break;
            case EntityState.Retreat:
                RetreatBehaviour();
                break;
            case EntityState.Chasing:
                ChasingBehaviour();
                break;
            case EntityState.Combat:
                CombatBehaviour();
                break;
        }
    }

    // ★ 算路徑前，把「自己」與「目標」的船上狀態餵給 PathFinder。
    //   target == null 代表目的地是自己算出來的點（Idle/Patrol 隨機點、Retreat 的 spawnLocation），
    //   那種點跟自己在同一個空間，所以 isTargetOnShip 跟著 isOnShip 走。
    protected virtual void SyncShipFlags(Transform target)
    {
        pathFinder.isOnShip = selfPassenger != null && selfPassenger.isOnShip;

        if (target != null)
        {
            // 目標身上的 ShipPassenger；沒掛就當作不在船上（例如靜態建築、Core）
            // 用 InParent：EnemyDetection 加進來的是「被碰到的 collider」的 transform，
            // 建築的 ShipPassenger 通常在 prefab root，不在那個子物件上
            ShipPassenger tp = target.GetComponentInParent<ShipPassenger>();
            pathFinder.isTargetOnShip = tp != null && tp.isOnShip;
            pathFinder.targetLocation = target.position;   // GetTargetClosestDockingLocation 需要它
        }
        else
        {
            pathFinder.isTargetOnShip = pathFinder.isOnShip;
            pathFinder.targetLocation = destination;
        }
    }

    protected virtual void PathUpdate()
    {
        destinationTimer -= ThinkDeltaTime;
        if (destinationTimer > 0f) return;

        Vector2 r;
        switch (currentState)
        {
            case EntityState.Idle:
                destinationTimer = RollInterval(idleDestinationUpdateRange);
                r = Random.insideUnitCircle * idleActivityAreaRadius;
                destination = spawnLocation + new Vector3(r.x, 0f, r.y);
                SyncShipFlags(null);
                SetPath(pathFinder.FindPath(destination));
                break;
            case EntityState.Patrolling:
                destinationTimer = RollInterval(patrolDestinationUpdateRange);
                r = Random.insideUnitCircle * patrolActivityAreaRadius;
                destination = spawnLocation + new Vector3(r.x, 0f, r.y);
                SyncShipFlags(null);
                SetPath(pathFinder.FindPath(destination));
                break;
            case EntityState.Retreat:
                destinationTimer = RollInterval(retreatDestinationUpdateRange);
                destination = spawnLocation;
                SyncShipFlags(null);
                SetPath(pathFinder.FindPath(destination));
                break;
            case EntityState.Chasing:
                {
                    destinationTimer = RollInterval(chaseDestinationUpdateRange);
                    Transform chaseTarget = FindTarget();
                    if (chaseTarget == null) { ChangeState(EntityState.Patrolling); break; }   // 沒目標 → 回巡邏
                    destination = chaseTarget.position;
                    SyncShipFlags(chaseTarget);
                    SetPath(pathFinder.FindPath(destination));
                    break;
                }
            case EntityState.Combat:
                {
                    destinationTimer = RollInterval(combatDestinationUpdateRange);
                    Transform combatTarget = FindTarget();
                    if (combatTarget == null) { ChangeState(EntityState.Patrolling); break; }   // 沒目標 → 回巡邏
                    // 多點採樣 + 視線檢查，避免目標在崖邊時選到崖底
                    destination = pathFinder.PickCombatDestination(combatTarget, combatActivityAreaRadius);
                    SyncShipFlags(combatTarget);
                    SetPath(pathFinder.FindPath(destination));
                    break;
                }
        }
    }

    public Transform FindTarget()
    {
        // 原本直接拿 targets[0] 當起始最佳解，不檢查有效性 ——
        // 只要它是假 null，整個迴圈比出來的贏家就可能是一個不存在的東西。
        Target bestTarget = null;

        for (int i = 0; i < targets.Count; i++)
        {
            Target t = targets[i];
            if (!IsTargetUsable(t)) continue;

            if (bestTarget == null || t.targetPriority > bestTarget.targetPriority)
                bestTarget = t;
        }

        return (bestTarget != null) ? bestTarget.targetTransform : null;
    }

    /// <summary>
    /// 這個目標現在能不能當作交戰對象。
    ///
    /// 已銷毀 → 不能用（而且 PruneDestroyedTargets 會在下一個 physics step 清掉它）。
    /// 停用中 → 不能用，但「保留在清單裡」——
    ///   物件被 SetActive(false) 通常是暫時的（物件池、隱藏、過場），
    ///   直接移除會讓敵人在目標回來時失憶。銷毀才是永久的。
    /// </summary>
    protected virtual bool IsTargetUsable(Target t)
    {
        if (t == null) return false;
        if (t.targetTransform == null) return false;
        if (!t.targetTransform.gameObject.activeInHierarchy) return false;
        return true;
    }
    protected void SetPath(Vector3[] newPath)
    {
        path = newPath;

        // ★ 這裡就是「敵人重算路徑後會回頭走第一個點」的根源。
        //
        //   舊版：currentWaypointIndex = 0;
        //
        //   問題在於 corner[0] 並不是實體的位置，而是
        //   NavMesh.SamplePosition(transform.position) 吸附之後的起點。
        //   實體是 Rigidbody 驅動的（不是 NavMeshAgent），隨時可能：
        //     · 走出 NavMesh 邊緣
        //     · 站在甲板 / 箱子 / 任何沒烘焙的東西上
        //     · 被撞飛而暫時離地
        //     · ghost ↔ real 投影有一點誤差
        //   這時 sample 會往「最近的合法位置」吸附 —— 而最近的合法位置，
        //   通常就在它剛剛走過來的那個方向。navSampleMaxDistance 是 100，
        //   最遠可以吸到 100 公尺外。
        //
        //   歸零之後，唯一能跳過這個過期 corner 的機制是 FindNextWaypoint 的
        //   「距離小於 waypointArriveRadius(5) 就 ++」。corner[0] 在身後 30 公尺
        //   時這條規則不會成立 → 實體轉頭走回去 → 走進 5 公尺內才前進
        //   → 1~3 秒後 timer 到期又重算又歸零 → 不斷回頭。
        //
        //   路徑短時 corner 間距本來就跟 5 公尺同量級，回頭幅度小看不出來；
        //   路徑長時 corner 拉開，一次回頭就是一個幾十公尺的大轉彎。
        //
        //   改成用投影定位：找出實體現在落在折線的哪一段，直接瞄準那一段的終點。
        //   corner[0] 在身後時，實體位於 [corner0, corner1] 這一段上
        //   → 瞄準 corner1 → 結構上不可能再走回頭路。
        currentWaypointIndex = ResolveWaypointIndex(newPath, 0);
    }

    /// <summary>
    /// 把實體投影到路徑折線上，回傳「應該瞄準的 corner index」。
    ///
    /// 做法：在 [fromSegment, fromSegment + waypointSearchLookAhead] 這個窗口內
    /// 找出離實體最近的線段，瞄準該線段的終點；接著再套用抵達半徑推進。
    ///
    /// 為什麼要限制搜尋窗口而不是掃整條路徑：
    /// U 型繞路（例如繞過一面牆）時，歐氏距離上最近的線段可能在牆的另一側。
    /// 直接跳過去等於叫實體穿牆。限制成只能往前看幾段，就不會抄到那種捷徑。
    /// </summary>
    protected int ResolveWaypointIndex(Vector3[] p, int fromSegment)
    {
        if (p == null || p.Length == 0) return 0;
        if (p.Length == 1) return 0;   // 保底路徑（只有目的地一個點）

        Vector3 pos = transform.position;

        int lastSegment = p.Length - 2;                                   // 最後一段的起點 index
        int start = Mathf.Clamp(fromSegment, 0, lastSegment);
        int end = Mathf.Min(lastSegment, start + Mathf.Max(0, waypointSearchLookAhead));

        int bestSegment = start;
        float bestSqr = float.PositiveInfinity;

        for (int i = start; i <= end; i++)
        {
            float sqr = SqrDistanceToSegmentXZ(pos, p[i], p[i + 1]);
            if (sqr < bestSqr)
            {
                bestSqr = sqr;
                bestSegment = i;
            }
        }

        int index = bestSegment + 1;   // 瞄準所在線段的「終點」，不是起點

        // 已經站在該 corner 的抵達半徑內 → 繼續往前推進（原本的行為，保留）
        while (index < p.Length - 1 && DistanceXZ(pos, p[index]) < waypointArriveRadius)
            index++;

        return index;
    }

    /// <summary>點到線段的平方距離（只看水平面，Y 忽略）。開平方很貴，比大小不需要開。</summary>
    protected static float SqrDistanceToSegmentXZ(Vector3 point, Vector3 a, Vector3 b)
    {
        Vector2 p = new Vector2(point.x, point.z);
        Vector2 s = new Vector2(a.x, a.z);
        Vector2 e = new Vector2(b.x, b.z);

        Vector2 se = e - s;
        float lenSqr = se.sqrMagnitude;
        if (lenSqr < 0.000001f) return (p - s).sqrMagnitude;   // 重複 corner

        float t = Mathf.Clamp01(Vector2.Dot(p - s, se) / lenSqr);
        return (p - (s + se * t)).sqrMagnitude;
    }

    protected static float DistanceXZ(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    [Header("Path Failure")]
    [Tooltip("算不出路徑時多久重試一次（秒）。不要設 0，否則每個 physics step 都會呼叫 NavMesh.CalculatePath。")]
    [SerializeField] protected float pathRetryInterval = 0.25f;

    /// <summary>
    /// 現在有沒有可以走的路徑。沒有的話：把移動輸入歸零，並縮短下一次重算的等待時間。
    ///
    /// ★ 這是這次修的 bug 的另一半。
    ///   舊版每個 Behaviour 開頭都是 `if (path == null || path.Length == 0) return;`，
    ///   一次算不出路徑就把整段行為跳掉，連帶造成三個後果：
    ///
    ///   1. HorizontalMovement 沒被呼叫 → ModularEntityMovement.moveDirection 留著上一次的值
    ///      → 實體以 sprintSpeed 朝著舊方向一直滑，撞牆或滑出 NavMesh。
    ///   2. 狀態轉換全部被跳過 → Idle 不會轉 Chasing、Chasing 不會轉 Combat、
    ///      Combat 不會瞄準開火。從外面看就是「AI 整個死掉」。
    ///   3. 實體因此停在（或滑到）一個 SamplePosition 會失敗的位置，
    ///      下一次 FindPath 的起點還是那裡 → 永遠算不出路 → 死鎖。
    ///
    ///   現在「沒有路徑」是一個明確處理的狀態：停下來、砲塔歸位、盡快重試，
    ///   而狀態轉換與砲塔邏輯照常執行。
    /// </summary>
    protected bool HasUsablePath()
    {
        if (path != null && path.Length > 0) return true;

        modularEntityMovement.HorizontalMovement(0f, 0f);   // 清掉殘留的移動輸入，避免無限滑行
        if (destinationTimer > pathRetryInterval)
            destinationTimer = pathRetryInterval;
        return false;
    }
    public Vector3 FindNextWaypoint()
    {
        // ★ 每幀跟 PathFinder 要「用當下船姿態投影出來的」即時路徑，而不是讀凍結快取
        //    → 船移動時航點跟著貼合，不 stale。地面路徑時直接回傳原路徑，行為不變。
        Vector3[] livePath = pathFinder.GetCurrentWorldPath();

        if (livePath == null || livePath.Length == 0)
            return transform.position;

        // path 可能換過、變短，夾住避免越界
        if (currentWaypointIndex > livePath.Length - 1)
            currentWaypointIndex = livePath.Length - 1;

        // ★ 舊版只有「距離夠近就 ++」這一條規則，代表實體只能跳過它剛好站在
        //   5 公尺內的 corner。走太快直接掠過、或 corner 落在身後（重算路徑時的
        //   sample 吸附），這條規則都不會成立 → 實體轉頭回去撿那個點。
        //
        //   改成投影定位：找出自己落在折線的哪一段，瞄準那一段的終點。
        //   從 currentWaypointIndex - 1（也就是目前所在線段）開始往前找。
        int resolved = ResolveWaypointIndex(livePath, currentWaypointIndex - 1);

        // 只前進不後退。同一條路徑內索引單調遞增 ——
        // 折返型路徑（例如繞出去又繞回來）上，前後兩段可能貼得很近，
        // 沒有這道閘門就會在兩段之間來回跳，看起來又是在原地打轉。
        // 路徑換掉時由 SetPath 重新定位，不受這道閘門影響。
        if (resolved > currentWaypointIndex)
            currentWaypointIndex = resolved;

        return livePath[currentWaypointIndex];
    }

    /// <summary>
    /// 滾出下一次路徑更新的間隔。
    ///
    /// ★ 靠近停靠點時改用 dockDestinationUpdateRange（更短）。
    ///
    ///   為什麼要這樣：地面路徑（!isOnShip && isTargetOnShip）走的是
    ///   ComputeGroundPath，lastPathOnShip 是 false，所以 GetCurrentWorldPath()
    ///   直接回傳凍結的世界座標 —— 整條路徑在下一次重算之前完全不會跟著船更新。
    ///   終點那個 docking point 也一樣是凍結的。
    ///
    ///   離得遠的時候這無所謂（船的位移相對於路徑長度可以忽略）；
    ///   但快登船時，1~3 秒的凍結誤差就等於整個登船口的寬度，
    ///   敵人會一直撲到船「幾秒前」的位置。靠近時提高更新頻率就能收斂。
    ///
    ///   刻意寫在 RollInterval 而不是各個 state 裡：所有狀態自動適用，
    ///   FalconBrain 覆寫的 PathUpdate 也是呼叫這個方法，一併生效。
    /// </summary>
    protected float RollInterval(Vector2 range)
    {
        if (IsNearDockingPoint()&& !pathFinder.isOnShip)
            range = dockDestinationUpdateRange;

        return Random.Range(range.x, range.y);
    }

    /// <summary>
    /// 離任何一個停靠點夠近嗎。
    /// 用水平距離 —— 飛行單位（Falcon）懸停在 10 公尺高空，
    /// 3D 距離會永遠把飛行高度計進去，等於門檻被墊高。
    /// </summary>
    protected virtual bool IsNearDockingPoint()
    {
        if (dockUpdateDistance <= 0f) return false;

        LandshipNavigation nav = LandshipNavigation.Instance;
        if (nav == null || nav.dockingPoints == null) return false;

        float sqrThreshold = dockUpdateDistance * dockUpdateDistance;
        Vector3 pos = transform.position;

        for (int i = 0; i < nav.dockingPoints.Length; i++)
        {
            Transform dock = nav.dockingPoints[i];
            if (dock == null) continue;

            Vector3 delta = dock.position - pos;
            delta.y = 0f;

            if (delta.sqrMagnitude < sqrThreshold) return true;
        }

        return false;
    }
    protected virtual void IdleBehaviour()
    {
        if (HasUsablePath())
        {
            Vector3 nextWaypoint = FindNextWaypoint();
            Vector3 moveDirection = nextWaypoint - transform.position;
            moveDirection.y = 0f;
            float dist = moveDirection.magnitude;


            ResetTurretAiming(moveDirection); // 巡邏時砲塔不瞄準，避免亂射

            if (currentWaypointIndex == path.Length - 1 && DistanceToPoint(nextWaypoint) < waypointArriveRadius)
            {
                destinationTimer = 0f;   // 到達巡邏點 → 下一幀重挑新點（留在 Patrolling）
            }
            else if (dist > 0.0001f)   // dist 為 0 時 moveDirection / dist 會變成 NaN，直接污染 Rigidbody
            {
                float throttle = Mathf.Clamp01(dist / slowDownRadius);
                Vector3 dir = moveDirection / dist * throttle;

                float signedAngle = Vector3.SignedAngle(modularEntityMovement.MeshForward, moveDirection, Vector3.up);
                if (Mathf.Abs(signedAngle) < 180)
                {
                    modularEntityMovement.HorizontalMovement(dir.x, dir.z);
                }
                FaceMoveDirection(moveDirection);
            }
        }
        else
        {
            ResetTurretAiming(Vector3.zero);   // 沒路可走 → 砲塔回中立，不要維持上一幀的朝向
        }

        //if there is target in the list, change state to chasing
        // ★ 移到 path 檢查外面：算不出路徑不代表不該切狀態
        if (targets.Count > 0)
        {
            ChangeState(EntityState.Chasing);
        }
    }

    protected virtual void PatrollingBehaviour()
    {
        if (!HasUsablePath())
        {
            ResetTurretAiming(Vector3.zero);
            return;
        }

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;
        float dist = moveDirection.magnitude;


        ResetTurretAiming(moveDirection); // 巡邏時砲塔不瞄準，避免亂射

        if (currentWaypointIndex == path.Length - 1 && DistanceToPoint(nextWaypoint) < waypointArriveRadius)
        {
            destinationTimer = 0f;   // 到達巡邏點 → 下一幀重挑新點（留在 Patrolling）
        }
        else if (dist > 0.0001f)   // 防除以 0 → NaN
        {
            float throttle = Mathf.Clamp01(dist / slowDownRadius);
            Vector3 dir = moveDirection / dist * throttle;

            float signedAngle = Vector3.SignedAngle(modularEntityMovement.MeshForward, moveDirection, Vector3.up);
            if (Mathf.Abs(signedAngle) < 180)
            {
                modularEntityMovement.HorizontalMovement(dir.x, dir.z);
            }
            FaceMoveDirection(moveDirection);
        }
    }

    protected virtual void RetreatBehaviour()
    {
        if (!HasUsablePath())
        {
            ResetTurretAiming(Vector3.zero);
            return;
        }

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;

        ResetTurretAiming(moveDirection); // 撤退時砲塔不瞄準，避免亂射

        if (currentWaypointIndex == path.Length - 1 && DistanceToPoint(nextWaypoint) < waypointArriveRadius)
        {
            ChangeState(EntityState.Patrolling);   // 撤退到位 → 真正切換狀態
        }
        else
        {
            float signedAngle = Vector3.SignedAngle(modularEntityMovement.MeshForward, moveDirection, Vector3.up);
            if (Mathf.Abs(signedAngle) < 120)
            {
                modularEntityMovement.HorizontalMovement(moveDirection.x, moveDirection.z);
            }
            FaceMoveDirection(moveDirection);
        }
    }

    protected virtual void ChasingBehaviour()
    {
        // ★ 目標判定與 Combat 轉換不需要路徑，所以放在 path 檢查前面。
        //   舊版放在後面 → 一次算不出路就永遠卡在 Chasing，不會進 Combat 也不會回 Patrolling。
        Transform target = FindTarget();
        if (target == null) { ChangeState(EntityState.Patrolling); return; }   // 目標消失 → 回巡邏

        if (DistanceToPoint(target.position) < combatActivityAreaRadius / 2)
        {
            ChangeState(EntityState.Combat);   // 進入戰鬥範圍 → 真正切換狀態
            return;
        }

        if (!HasUsablePath())
        {
            ResetTurretAiming(Vector3.zero);
            return;
        }

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;

        ResetTurretAiming(moveDirection);   // 追擊時砲塔不瞄準，避免亂射

        if (currentWaypointIndex == path.Length - 1 && DistanceToPoint(nextWaypoint) < waypointArriveRadius)
        {
            destinationTimer = 0f;   // 走完舊路徑但還沒近 → 下一幀用目標當下位置重算（留在 Chasing）
        }
        else
        {
            float signedAngle = Vector3.SignedAngle(modularEntityMovement.MeshForward, moveDirection, Vector3.up);
            if (Mathf.Abs(signedAngle) < 120)
            {
                modularEntityMovement.HorizontalMovement(moveDirection.x, moveDirection.z);
            }
            FaceMoveDirection(moveDirection);
        }
    }

    protected virtual void CombatBehaviour()
    {
        Transform target = FindTarget();
        CombatTarget = target;

        // ── 近戰 ──
        //   攻擊中：移動、轉向全部交給近戰，這裡只負責砲塔（停不停火由 UpdateTurretAiming 判斷）。
        //   沒在攻擊：讓近戰判斷要不要出招；出招了這次思考就到此為止。
        if (meleeManager != null && target != null)
        {
            if (meleeManager.IsAttacking || meleeManager.TryStartAttack())
            {
                UpdateTurretAiming(target, GetTargetVelocity(target));
                return;
            }
        }

        // ★ 移動段可以沒有路徑，但瞄準開火段一定要照跑。
        //   舊版整段 return → 敵人一旦算不出徘徊路徑就連射擊都停了。
        if (HasUsablePath())
        {
            Vector3 nextWaypoint = FindNextWaypoint();
            Vector3 moveDirection = nextWaypoint - transform.position;
            moveDirection.y = 0f;
            if (currentWaypointIndex == path.Length - 1 && DistanceToPoint(nextWaypoint) < waypointArriveRadius)
            {
                destinationTimer = 0f;   // 到達徘徊點 → 下一幀重挑新徘徊點（留在 Combat）
            }
            else
            {
                float signedAngle = Vector3.SignedAngle(modularEntityMovement.MeshForward, moveDirection, Vector3.up);
                if (Mathf.Abs(signedAngle) < 160)
                {
                    modularEntityMovement.HorizontalMovement(moveDirection.x, moveDirection.z);
                }
                FaceMoveDirection(moveDirection);
            }
        }

        if (target != null)
        {
            UpdateTurretAiming(target, GetTargetVelocity(target));
        }
    }

    // ═══════════════════════ 給近戰等系統讀取的目標資料 ═══════════════════════
    //
    // 目標資料只有 Brain 這一份。近戰不存目標，需要時就呼叫這些方法。

    /// <summary>目標的瞄準點（碰撞體中心；沒有碰撞體就是原點）。</summary>
    public Vector3 GetTargetAimPoint(Transform target)
    {
        return TurretController.GetAimPoint(target);
    }

    /// <summary>交戰目標目前的速度（目標或它父物件的 Rigidbody；沒有就是 0）。</summary>
    public Vector3 GetCombatTargetVelocity()
    {
        return GetTargetVelocity(CombatTarget);
    }

    /// <summary>近戰出招前取得攻擊名額（與砲塔開火共用同一份名額）。</summary>
    public bool TryAcquireMeleeSlot()
    {
        return TryAcquireAttackSlot(CombatTarget);
    }

    // 目標的 Rigidbody 快取：目標沒換就不用每次 GetComponentInParent
    private Transform _velocityTarget;
    private Rigidbody _velocityTargetRb;

    /// <summary>目標（或它的父物件）的 Rigidbody 速度；沒有 Rigidbody（例如靜態建築）時回傳 0。</summary>
    protected Vector3 GetTargetVelocity(Transform target)
    {
        if (target == null) return Vector3.zero;

        if (target != _velocityTarget)
        {
            _velocityTarget = target;
            _velocityTargetRb = target.GetComponentInParent<Rigidbody>();
        }
        return _velocityTargetRb != null ? _velocityTargetRb.linearVelocity : Vector3.zero;
    }
    protected virtual void ChangeState(EntityState next)
    {
        currentState = next;
        destinationTimer = 0f;
    }
    /// <summary>
    /// 設定想面對的方向。實際轉動由 Movement 每個物理步執行（Brain 降頻之後，
    /// 如果還在這裡直接呼叫 RotateMesh，轉向速度會跟著思考頻率一起變慢）。
    /// </summary>
    protected virtual void FaceMoveDirection(Vector3 worldDir)
    {
        worldDir.y = 0f;
        if (worldDir.sqrMagnitude < 0.0001f) return;

        modularEntityMovement.SetFacingTarget(worldDir, facingDeadzone);
    }

    // 砲塔瞄準 + 開火判定
    //
    // 只告訴砲塔「瞄誰」，攔截點由砲塔自己每幀用目標當下的位置與速度、自己的彈速重算。
    // 以前是這裡算好一個世界座標點寫進 turret.targetLocation —— Brain 降頻成每 0.1 秒思考一次後，
    // 那個點在兩次思考之間是靜止的；站在移動的船上時，砲塔會一直瞄在目標後方。
    //
    // targetVelocity 參數保留給子類別 / 呼叫端相容用；速度現在由砲塔自己每幀從目標的 Rigidbody 讀。
    protected virtual void UpdateTurretAiming(Transform target, Vector3 targetVelocity)
    {
        // ★ 攻擊名額只擋「開火」，不擋瞄準。
        //   拿不到名額的敵人照常走位、照常把砲塔轉向目標，只是不扣扳機。
        //   狀態機、路徑、移動邏輯完全不受影響。
        bool mayFire = TryAcquireAttackSlot(target);

        // 近戰攻擊中、而且這招設定「砲塔停火」→ 照常瞄準，但不開火
        if (meleeManager != null && meleeManager.IsAttacking && meleeManager.StopsTurretFire)
            mayFire = false;

        for (int i = 0; i < turrets.Length; i++)
        {
            TurretController turret = turrets[i];
            if (turret == null) continue;

            turret.TrackTarget(target);

            if (mayFire && turret.HasLineOfSightTo(target))
                turret.Shoot();
        }
    }

    // ═══════════════════════ 攻擊名額 ═══════════════════════
    //
    // GameManager.maxAttackersPerTarget 限制「單一目標最多能被幾隻敵人同時鎖定開火」。
    // 是每個目標各自一組額度，不是全場總量 —— 玩家、船、Core 各算各的。
    //
    // 那組 API 原本收的型別是 EnemyBrain，而場上跑的是 ModularEntityBrain ——
    // 兩套系統從來沒接上，所以那個上限一直是空轉的。
    //
    // 名額只擋 UpdateTurretAiming 裡的 turret.Shoot()。移動、路徑、狀態轉換
    // 一律不受影響：拿不到名額的敵人照樣圍上來、照樣把砲塔轉向目標，只是不開火。

    /// <summary>
    /// 取得或續用攻擊名額。沒有 GameManager 時一律放行，維持沒有限制器的舊行為。
    /// </summary>
    protected bool TryAcquireAttackSlot(Transform target)
    {
        if (target == null) return false;

        GameManager gm = GameManager.Instance;
        if (gm == null) return true;

        // 已經持有、而且就是同一個目標 → 續用，不用再跟 GameManager 要
        if (_hasAttackSlot && _slotTarget == target) return true;

        // 換目標了（例如逾時對調之後）→ 先把舊目標的名額還回去，
        // 否則舊目標的額度會被一隻已經不打它的敵人一直佔著。
        if (_hasAttackSlot) ReleaseAttackSlot();

        if (!gm.TryClaimAttackSlot(this, target)) return false;

        _hasAttackSlot = true;
        _slotTarget = target;
        _attackSlotExpireTime = Time.time + Mathf.Max(0f, attackSlotHoldSeconds);
        return true;
    }

    /// <summary>交還名額。沒有持有時是 no-op。</summary>
    protected void ReleaseAttackSlot()
    {
        if (!_hasAttackSlot) return;
        _hasAttackSlot = false;

        Transform released = _slotTarget;
        _slotTarget = null;

        GameManager gm = GameManager.Instance;
        if (gm != null) gm.ReleaseAttackSlot(this, released);
    }

    /// <summary>
    /// 持有逾時 → 交還名額，並把第一、第二優先目標的優先度對調。
    ///
    /// 為什麼要在每次思考（Think）都檢查，而不是在 TryAcquireAttackSlot 裡順便看：
    /// 敵人離開戰鬥（目標消失、切回 Chasing / Patrolling）之後就不會再呼叫
    /// UpdateTurretAiming，逾時判定永遠不會被執行 → 名額會被一隻根本沒在打架的
    /// 敵人佔到死為止，其他人全部啞火。
    ///
    /// 這個方法只動名額與目標優先度，不碰移動、路徑、狀態。
    /// </summary>
    private void AttackSlotUpdate()
    {
        if (!_hasAttackSlot) return;
        if (attackSlotHoldSeconds <= 0f) return;          // 0 = 不設上限
        // 近戰出招中不交還名額、不對調目標：否則 CombatTarget 會在出招途中被換掉，
        // 預備階段轉向新目標、判定也改比對新目標，原本撲過去的那個人反而打不到。
        // 收招後下一次思考就會照常處理逾時。
        if (IsMeleeAttacking) return;
        if (Time.time < _attackSlotExpireTime) return;

        ReleaseAttackSlot();
        SwapTopTwoTargetPriorities();
    }

    /// <summary>
    /// 把最高與次高優先度的兩個目標「互換優先度數值」，讓這隻敵人改打另一個。
    ///
    /// ★ 交換的是 targetPriority 的「值」，不是它們在 targets 清單裡的位置。
    ///   FindTarget() 是掃全清單挑 targetPriority 最大的那個，跟索引完全無關 ——
    ///   交換位置不會有任何效果。
    ///
    /// 有效目標少於兩個時什麼都不做（IsTargetUsable 會濾掉已銷毀與停用的）。
    /// </summary>
    protected virtual void SwapTopTwoTargetPriorities()
    {
        Target first = null;
        Target second = null;

        for (int i = 0; i < targets.Count; i++)
        {
            Target t = targets[i];
            if (!IsTargetUsable(t)) continue;

            if (first == null || t.targetPriority > first.targetPriority)
            {
                second = first;
                first = t;
            }
            else if (second == null || t.targetPriority > second.targetPriority)
            {
                second = t;
            }
        }

        if (first == null || second == null) return;   // 有效目標不到兩個

        float tmp = first.targetPriority;
        first.targetPriority = second.targetPriority;
        second.targetPriority = tmp;
    }

    //將每座砲塔轉向實體的移動方向（砲管放平）；沒有移動方向時退回中立朝向
    protected virtual void ResetTurretAiming(Vector3 moveDirection)
    {
        if (turrets == null) return;

        moveDirection.y = 0f;
        bool hasDirection = moveDirection.sqrMagnitude > 0.0001f;
        if (hasDirection) moveDirection.Normalize();

        for (int i = 0; i < turrets.Length; i++)
        {
            TurretController turret = turrets[i];
            if (turret == null) continue;

            if (hasDirection)
            {
                // 沿移動方向放平：yaw 對準移動方向、pitch 歸零（砲塔每幀重算，船上不會落後）
                turret.AimAlongDirection(moveDirection);
            }
            else
            {
                // 靜止（沒有移動方向）→ 回中立朝向
                turret.AimAtRest();
            }
        }
    }

    public virtual void AddTarget(Transform targetTransform, TargetType type, float priority, float priorityDecreaseMultiplier)
    {
        if (targetTransform == null) return;

        // 去重移到這裡。EnemyDetection 呼叫前本來就有檢查，但那個檢查在呼叫端 ——
        // 之後把 TakeDamage 的仇恨接上來時（那是每次中彈都會觸發的高頻呼叫），
        // 沒有這道防線就會瞬間產生大量重複項目。
        //
        // 已存在時取較高的優先度，而不是再加一筆：
        // 重複偵測到同一個目標的語意是「重新確認威脅」，不是「多了一個威脅」。
        for (int i = 0; i < targets.Count; i++)
        {
            Target existing = targets[i];
            if (existing == null || existing.targetTransform != targetTransform) continue;

            existing.type = type;
            existing.priorityDecreaseMultiplier = priorityDecreaseMultiplier;
            existing.targetPriority = Mathf.Max(existing.targetPriority, priority);
            return;
        }

        // 原本沒有設定 type —— Target 的建構子不收它，所以每一筆的 type 都停在
        // 預設值 Player。decayMultiplierByType 是用 type 查表的，等於
        // targetPreferences 對非玩家目標從來沒生效過。
        targets.Add(new Target(targetTransform, priority, priorityDecreaseMultiplier) { type = type });
    }

    /// <summary>
    /// 受到傷害時由 ModularEntityStats 呼叫：依傷害量提高攻擊者的優先度（被打就拉仇恨）。
    ///
    /// 提升量 = (實際傷害 ÷ 最大生命值 × 100) ÷ damagePercentPerPriority，
    /// 加完之後不超過 maxThreatPriority；原本就高於上限的優先度不會被拉低。
    /// 攻擊者不在清單裡就新加一筆（從偵測範圍外開槍也會被找上）。
    /// 閒置 / 巡邏中被打會立刻切到追擊。
    /// </summary>
    public virtual void OnDamaged(float damage, float maxHealth, GameObject attacker)
    {
        if (!damageThreat || attacker == null) return;
        if (damage <= 0f || maxHealth <= 0f) return;

        Transform source = attacker.transform;

        // 自己打到自己 / 其他敵人造成的傷害（例如死亡爆炸）→ 不算仇恨，否則敵人會開始互打
        if (source == transform || source.IsChildOf(transform)) return;
        if (attacker.GetComponentInParent<ModularEntityStats>() != null) return;

        float gain = (damage / maxHealth * 100f) / damagePercentPerPriority;
        if (gain <= 0f) return;

        Target entry = FindTargetEntry(source);
        if (entry != null)
        {
            if (entry.targetPriority < maxThreatPriority)
                entry.targetPriority = Mathf.Min(maxThreatPriority, entry.targetPriority + gain);
        }
        else
        {
            TargetType type = attacker.CompareTag("Player") ? TargetType.Player : TargetType.Building;
            targets.Add(new Target(source, Mathf.Min(maxThreatPriority, gain), threatPriorityDecreaseMultiplier) { type = type });
        }

        if (currentState == EntityState.Idle || currentState == EntityState.Patrolling)
            ChangeState(EntityState.Chasing);
    }

    /// <summary>
    /// 找出清單裡代表這個攻擊者的那一筆。
    ///
    /// 感測器加進來的是「被掃到的 collider」所在的 Transform，攻擊者則是開火物件的根 ——
    /// 玩家兩者都是 Player 根物件；防禦砲塔的 collider 可能在子物件上。
    /// 所以除了完全相同，也接受「清單裡那一筆是攻擊者的子物件」。
    /// 反方向（攻擊者是清單那一筆的子物件）不接受：砲塔掛在船底下，不能被當成船上其他目標。
    /// </summary>
    protected Target FindTargetEntry(Transform source)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            Target t = targets[i];
            if (t == null || t.targetTransform == null) continue;

            if (t.targetTransform == source || t.targetTransform.IsChildOf(source))
                return t;
        }
        return null;
    }

    protected virtual float DistanceToPoint(Vector3 point)
    {
        return Vector3.Distance(transform.position, point);
    }
}