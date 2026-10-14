using NaughtyAttributes;
using UnityEngine;

public class FalconBrain : ModularEntityBrain
{
    private FalconMovement falconMovement;
    public FalconStats modularEntityStats;
    [Header("Flight Height (per state)")]
    [SerializeField] private float idleHeight = 10f;
    [SerializeField] private float patrolHeight = 10f;
    [SerializeField] private float retreatHeight = 10f;
    [SerializeField] private float chaseHeight = 10f;
    [SerializeField] private float combatHeight = 10f;
    [Header("Retreat Setting")]
    [MinMaxSlider(0f, 100f)]
    public Vector2 retreatAreaRadius = new Vector2(50f, 60f);


    public MissileLauncherController missileLauncherController; // Missile launcher controller reference
    protected override void Start()
    {
        base.Start();

        falconMovement = modularEntityMovement as FalconMovement;
        if (falconMovement == null)
            Debug.LogError($"{name}: modularEntityMovement 不是 FalconMovement，飛行邏輯會失效。");
    }

    // ============================================================
    // 飛行移動：先轉向航點，再沿「機首方向」前進；接近航點時降低 throttle 好轉彎。
    // 不需要 base 的角度 gate —— 隼永遠朝機首飛，不存在「方向不對先別動」的情況。
    // ============================================================
    private void FlyTowards(Vector3 moveDirection, float dist)
    {
        FaceMoveDirection(moveDirection);   // 先把 mesh 轉向航點

        // 越接近航點 → throttle 越小 → 速度越低 → 迴轉半徑越小，好轉彎
        float throttle = Mathf.Clamp01(dist / slowDownRadius);

        // ★ 用 MeshForward 而非 moveDirection：隼只會朝機首飛
        Vector3 dir = modularEntityMovement.MeshForward * throttle;
        modularEntityMovement.HorizontalMovement(dir.x, dir.z);
    }

    // 停止：交給 base 的減速邏輯收到零，靠 VerticalMovement 懸停在空中
    private void Hover()
    {
        modularEntityMovement.HorizontalMovement(0f, 0f);
    }

    protected override void IdleBehaviour()
    {
        // 高度控制放在最前面：即使沒有路徑也要維持懸停，否則會掉下去
        falconMovement.VerticalMovement(idleHeight);

        if (path == null || path.Length == 0) return;

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;                    // 只看水平：航點在地面、隼在空中
        float dist = moveDirection.magnitude;

        ResetTurretAiming(moveDirection);

        if (dist < waypointArriveRadius)
        {
            Hover();   // 到達路徑終點 → 停下來懸停（跟一般敵人一樣會停）
        }
        else
        {
            FlyTowards(moveDirection, dist);
        }

        if (targets.Count != 0)
        {
            ChangeState(EntityState.Chasing);
        }
    }

    protected override void PatrollingBehaviour()
    {
        falconMovement.VerticalMovement(patrolHeight);

        if (path == null || path.Length == 0) return;

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;
        float dist = moveDirection.magnitude;

        ResetTurretAiming(moveDirection);

        if (currentWaypointIndex == path.Length - 1 && dist < waypointArriveRadius)
        {
            destinationTimer = 0f;   // 到達巡邏點 → 下一幀重挑新點（留在 Patrolling）
        }
        else
        {
            FlyTowards(moveDirection, dist);
        }
    }

    protected override void RetreatBehaviour()
    {
        falconMovement.VerticalMovement(retreatHeight);

        if (path == null || path.Length == 0) return;

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;
        float dist = moveDirection.magnitude;

        ResetTurretAiming(moveDirection);

        // 目標消失時 FindTarget() 是 null → 原本這裡會 NullReferenceException。
        // 沒目標就不算「離太遠」；下一次 PathUpdate 會自己切回 Patrolling。
        Transform retreatFrom = FindTarget();
        bool tooFarFromTarget = retreatFrom != null &&
                                Vector3.Distance(transform.position, retreatFrom.position) > combatActivityAreaRadius;

        if (currentWaypointIndex == path.Length - 1 && dist < waypointArriveRadius || tooFarFromTarget)
        {
            ChangeState(EntityState.Combat);   // 撤退到位 → 真正切換狀態
            Invoke(nameof(ChangeStateToRetreat), Random.Range(5f, 10f)); // 延遲調用 ChangeStateToRetreat 方法
        }
        else
        {
            FlyTowards(moveDirection, dist);
        }
    }

    private void ChangeStateToRetreat()
    {
        ChangeState(EntityState.Retreat);
        CancelInvoke(nameof(ChangeStateToRetreat)); // 取消延遲調用，避免重複調用
    }

    protected override void ChasingBehaviour()
    {
        falconMovement.VerticalMovement(chaseHeight);

        if (path == null || path.Length == 0) return;

        Transform target = FindTarget();
        if (target == null) { ChangeState(EntityState.Patrolling); return; }   // 目標消失 → 回巡邏

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;
        float dist = moveDirection.magnitude;

        ResetTurretAiming(moveDirection);

        // 到目標的距離也只看水平，否則懸停高度會永遠計入，進不了 Combat
        Vector3 toTarget = target.position - transform.position;
        toTarget.y = 0f;

        if (toTarget.magnitude < combatActivityAreaRadius / 2)
        {
            ChangeState(EntityState.Combat);   // 進入戰鬥範圍 → 真正切換狀態
        }
        else if (currentWaypointIndex == path.Length - 1 && dist < waypointArriveRadius)
        {
            destinationTimer = 0f;   // 走完舊路徑但還沒近 → 下一幀用目標當下位置重算
        }
        else
        {
            FlyTowards(moveDirection, dist);
        }
    }

    protected override void CombatBehaviour()
    {
        falconMovement.VerticalMovement(combatHeight);

        if (path == null || path.Length == 0) return;

        Vector3 nextWaypoint = FindNextWaypoint();
        Vector3 moveDirection = nextWaypoint - transform.position;
        moveDirection.y = 0f;
        float dist = moveDirection.magnitude;

        if (currentWaypointIndex == path.Length - 1 && dist < waypointArriveRadius)
        {
            int nextMove = Random.Range(0, 2);
            if (nextMove == 0)
            {
                Invoke(nameof(LaunchMissile), 1.5f); // 延遲 0.5 秒發射導彈
                for (int i = 0; i < turrets.Length; i++)
                {
                    TurretController turret = turrets[i];
                    if (turret == null) continue;
                    turret.Reload();  // 重新裝填子彈
                }
                ChangeState(EntityState.Retreat);
            }
            else
            {
                destinationTimer = 0f;   // 走完舊路徑但還沒近 → 下一幀用目標當下位置重算
            }
        }
        else
        {
            FlyTowards(moveDirection, dist);
        }

        Transform target = FindTarget();
        if (target != null)
        {
            UpdateTurretAiming(target, GetTargetVelocity(target));
        }

    }
    protected override void PathUpdate()
    {
        destinationTimer -= ThinkDeltaTime;   // 降頻後兩次思考之間隔好幾個物理步
        if (destinationTimer > 0f) return;

        Vector2 r;
        switch (currentState)
        {
            case EntityState.Idle:
                {
                    destinationTimer = RollInterval(idleDestinationUpdateRange);
                    r = Random.insideUnitCircle * idleActivityAreaRadius;
                    destination = spawnLocation + new Vector3(r.x, 0f, r.y);
                    SyncShipFlags(null);
                    SetPath(pathFinder.FindPath(destination));
                    break;
                }
            case EntityState.Patrolling:
                {
                    destinationTimer = RollInterval(patrolDestinationUpdateRange);
                    r = Random.insideUnitCircle * patrolActivityAreaRadius;
                    destination = spawnLocation + new Vector3(r.x, 0f, r.y);
                    SyncShipFlags(null);
                    SetPath(pathFinder.FindPath(destination));
                    break;
                }
            case EntityState.Retreat:
                {
                    destinationTimer = RollInterval(retreatDestinationUpdateRange);
                    Transform chaseTarget = FindTarget();
                    if (chaseTarget == null) { ChangeState(EntityState.Patrolling); break; }   // 沒目標 → 回巡邏
                    destination = MathToolKit.GetRandomPointInRing(chaseTarget.position, retreatAreaRadius.y, retreatAreaRadius.x);
                    SyncShipFlags(chaseTarget);
                    SetPath(pathFinder.FindPath(destination));
                    break;
                }
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
                    if (combatTarget == null)
                    {
                        ChangeState(EntityState.Patrolling); break;
                    }   // 沒目標 → 回巡邏

                    // GetTargetVelocity：有快取，而且目標沒有 Rigidbody（例如建築）時回傳 0，
                    // 不會像原本的 GetComponentInParent<Rigidbody>().linearVelocity 那樣 NullReferenceException。
                    if (MathToolKit.InterceptionPoint(combatTarget.position, transform.position, (GetTargetVelocity(combatTarget) / 2), modularEntityStats.sprintSpeed, out Vector3 interceptPoint))
                    {
                        destination = MathToolKit.GetPointAtTargetBack(transform.position, interceptPoint, 10);
                    }
                    else
                    {
                        destination = MathToolKit.GetPointAtTargetBack(transform, combatTarget, 10);
                    }
                    SyncShipFlags(combatTarget);
                    SetPath(pathFinder.FindPath(destination));
                    break;
                }
        }
    }

    private void LaunchMissile()
    {
        // 失衡中不發射（這是 1.5 秒前排程的）。同時取消，失衡結束後不補發。
        if (modularEntityMovement != null && modularEntityMovement.IsStaggered)
        {
            CancelInvoke(nameof(LaunchMissile));
            return;
        }

        // 檢查隼的高度是否大於 retreatHeight 的一半
        if (falconMovement.FlyHeight() > retreatHeight / 2)
        {
            CancelInvoke(nameof(LaunchMissile)); // 取消延遲調用，避免重複調用
            Transform target = FindTarget();
            if (target != null)
            {
                StartCoroutine(missileLauncherController.Launch(target));
            }
        }
    }
}
