using UnityEngine;

/// <summary>
/// 恐狼（第一個 Boss）的 Brain。
///
/// 目前和 ModularEntityBrain 的差別：
///   Combat 狀態的目的地改成「目標後方」—— 跟 Falcon 一樣先預判目標的位置（攔截點），
///   再從自己往攔截點的方向延伸 Charge Overshoot 公尺，當成目的地。
///   看起來就是直直朝玩家衝過去、衝過頭，到了之後（base 的 CombatBehaviour 會把 destinationTimer 歸零）
///   再重新瞄準下一次衝刺。
///
///   Combat 狀態每次思考檢查一次：目標比恐狼高 Missile Height Threshold 以上時，
///   有 Missile Chance 的機率讓所有 Missile Launcher 同時發射（之後冷卻 Missile Cooldown 秒）。
///   用來處理玩家跳到高處、近戰打不到的情況。
///
/// 其他狀態（Idle / Patrolling / Retreat / Chasing）、近戰出招、砲塔都沿用 base。
/// </summary>
public class DirewolfBrain : ModularEntityBrain
{
    [Header("Direwolf: Combat Charge")]
    [Tooltip("衝刺目的地在目標後方多遠（公尺）。越大衝得越過頭。")]
    [SerializeField, Min(0f)] private float chargeOvershoot = 10f;

    [Tooltip("預判目標移動的比例。1 = 完整預判攔截點；0 = 直接衝向目標現在的位置。Falcon 用 0.5。")]
    [SerializeField, Range(0f, 1f)] private float targetLeadFactor = 0.5f;

    [Header("Direwolf: Missiles")]
    [Tooltip("兩邊的飛彈發射器。留空 = 自動抓子物件裡所有的 MissileLauncherController。")]
    [SerializeField] private MissileLauncherController[] missileLaunchers;

    [Tooltip("目標比恐狼高多少（公尺）以上才考慮發射。")]
    [SerializeField] private float missileHeightThreshold = 10f;

    [Tooltip("條件成立時，每次思考（預設約 0.1 秒一次）發射的機率。0.1 ≈ 平均 1 秒內會發射。")]
    [SerializeField, Range(0f, 1f)] private float missileChance = 0.1f;

    [Tooltip("發射後的冷卻時間（秒）。發射器本身沒有冷卻，不設的話條件成立期間會一直發射。")]
    [SerializeField, Min(0f)] private float missileCooldown = 6f;

    private ModularEntityStats _stats;
    private float _nextMissileTime;

    protected override void Start()
    {
        base.Start();
        _stats = GetComponent<ModularEntityStats>();

        if (missileLaunchers == null || missileLaunchers.Length == 0)
            missileLaunchers = GetComponentsInChildren<MissileLauncherController>(true);
    }

    public override void OnSpawned()
    {
        base.OnSpawned();
        _nextMissileTime = 0f;   // 從物件池重用時不要沿用上一隻的冷卻
    }

    protected override void CombatBehaviour()
    {
        base.CombatBehaviour();
        TryLaunchMissiles();
    }

    /// <summary>目標在高處時，有機率讓所有發射器同時發射。</summary>
    private void TryLaunchMissiles()
    {
        if (missileLaunchers == null || missileLaunchers.Length == 0) return;
        if (Time.time < _nextMissileTime) return;
        if (modularEntityMovement != null && modularEntityMovement.IsStaggered) return;   // 失衡中不發射

        Transform target = FindTarget();
        if (target == null) return;
        if (target.position.y - transform.position.y < missileHeightThreshold) return;
        if (Random.value >= missileChance) return;

        _nextMissileTime = Time.time + missileCooldown;
        for (int i = 0; i < missileLaunchers.Length; i++)
        {
            MissileLauncherController launcher = missileLaunchers[i];
            if (launcher == null || !launcher.isActiveAndEnabled) continue;
            StartCoroutine(launcher.Launch(target));
        }
    }

    protected override void PathUpdate()
    {
        // Combat 以外照舊
        if (currentState != EntityState.Combat)
        {
            base.PathUpdate();
            return;
        }

        destinationTimer -= ThinkDeltaTime;
        if (destinationTimer > 0f) return;

        destinationTimer = RollInterval(combatDestinationUpdateRange);
        Transform combatTarget = FindTarget();
        if (combatTarget == null) { ChangeState(EntityState.Patrolling); return; }   // 沒目標 → 回巡邏

        destination = GetChargeDestination(combatTarget);
        SyncShipFlags(combatTarget);
        SetPath(pathFinder.FindPath(destination));
    }

    /// <summary>目標後方的衝刺點：預判攔截點，再沿「自己 → 攔截點」的方向延伸 chargeOvershoot。</summary>
    private Vector3 GetChargeDestination(Transform target)
    {
        float speed = _stats != null ? _stats.sprintSpeed : 0f;
        Vector3 targetVel = GetTargetVelocity(target) * targetLeadFactor;

        if (speed > 0f && targetLeadFactor > 0f &&
            MathToolKit.InterceptionPoint(target.position, transform.position, targetVel, speed, out Vector3 interceptPoint))
        {
            return MathToolKit.GetPointAtTargetBack(transform.position, interceptPoint, chargeOvershoot);
        }
        return MathToolKit.GetPointAtTargetBack(transform, target, chargeOvershoot);
    }
}
