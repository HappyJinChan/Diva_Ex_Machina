using UnityEngine;

/// <summary>
/// 「停止 > 蓄力 > 攻擊」能力。任何敵人都能用：掛在根物件上，Brain 自動抓到。
///
/// 時間軸：
///   0 秒          停下，Animator Bool（例如 Sentry）= true，所有 Charge Weapons 開始蓄力（播蓄力粒子）。
///                 Periodic Launchers 開始輪流發射，總共每 Launch Interval 秒一發。
///   Charge Time   所有 Charge Weapons 同時開火，Periodic Launchers 停止。
///   + Recovery    再停 Recovery Time 秒 → Bool = false → 結束，恢復移動與一般射擊。
///
/// 期間砲塔持續瞄準目標（由 Brain 處理），一般射擊、近戰都停。只有失衡會打斷（清掉蓄力粒子，不開火）。
/// </summary>
public class StationaryChargeAttack : EntityAbility
{
    [Header("Trigger")]
    [Tooltip("目標距離超過這個值（公尺）才會發動。0 = 不限。")]
    [SerializeField, Min(0f)] private float minTargetDistance = 60f;

    [Tooltip("目標距離超過這個值（公尺）就不發動。0 = 不限。")]
    [SerializeField, Min(0f)] private float maxTargetDistance = 0f;

    [Header("Timing")]
    [Tooltip("蓄力時間（秒）：停下開始蓄力，到開火為止。")]
    [SerializeField, Min(0f)] private float chargeTime = 15f;

    [Tooltip("開火後再停幾秒才恢復移動。")]
    [SerializeField, Min(0f)] private float recoveryTime = 1f;

    [Header("Animation")]
    [Tooltip("發動期間設為 true 的 Animator Bool 參數。留空 = 不動動畫。")]
    [SerializeField] private string animatorBool = "Sentry";

    [Tooltip("留空 = 自動抓子物件裡的 Animator。")]
    [SerializeField] private Animator animator;

    [Header("Charge Weapons")]
    [Tooltip("蓄力、在 Charge Time 結束時一起開火的武器（例如槍和砲）。")]
    [SerializeField] private TurretWeapon[] chargeWeapons;

    [Header("Periodic Launchers")]
    [Tooltip("蓄力期間輪流發射的飛彈發射器。")]
    [SerializeField] private MissileLauncherController[] periodicLaunchers;

    [Tooltip("發射間隔（秒，所有發射器合計）。")]
    [SerializeField, Min(0.05f)] private float launchInterval = 1f;

    private enum Phase { Charging, Recovery }
    private Phase _phase;
    private float _fireTime;
    private float _endTime;
    private float _nextLaunchTime;
    private int _launcherIndex;
    private int _animBoolHash;

    protected override void Awake()
    {
        base.Awake();
        if (animator == null) animator = (brain != null ? brain.transform : transform.root).GetComponentInChildren<Animator>();
        if (!string.IsNullOrEmpty(animatorBool)) _animBoolHash = Animator.StringToHash(animatorBool);
    }

    public override bool CanStart(Transform target)
    {
        if (target == null) return false;
        float d = Vector3.Distance(transform.position, target.position);
        if (minTargetDistance > 0f && d < minTargetDistance) return false;
        if (maxTargetDistance > 0f && d > maxTargetDistance) return false;
        return true;
    }

    protected override void OnBegin(Transform target)
    {
        float now = Time.time;
        _phase = Phase.Charging;
        _fireTime = now + chargeTime;
        _nextLaunchTime = now + launchInterval;
        _launcherIndex = 0;

        SetAnimBool(true);
        if (chargeWeapons != null)
            foreach (TurretWeapon w in chargeWeapons)
                if (w != null) w.BeginCharge();
    }

    protected override bool OnTick(Transform target)
    {
        float now = Time.time;

        if (_phase == Phase.Charging)
        {
            // 輪流發射：總共每 launchInterval 一發（思考間隔較長時補發，但一次最多補一輪）
            int guard = 0;
            while (now >= _nextLaunchTime && now < _fireTime && guard++ < 4)
            {
                LaunchNext(target);
                _nextLaunchTime += launchInterval;
            }

            if (now >= _fireTime)
            {
                if (chargeWeapons != null)
                    foreach (TurretWeapon w in chargeWeapons)
                        if (w != null) w.Fire();

                _phase = Phase.Recovery;
                _endTime = now + recoveryTime;
            }
            return false;
        }

        return now >= _endTime;
    }

    protected override void OnFinish() => SetAnimBool(false);

    protected override void OnInterrupt()
    {
        if (chargeWeapons != null)
            foreach (TurretWeapon w in chargeWeapons)
                if (w != null) w.CancelCharge();
        SetAnimBool(false);
    }

    private void LaunchNext(Transform target)
    {
        if (target == null || periodicLaunchers == null || periodicLaunchers.Length == 0) return;

        for (int tries = 0; tries < periodicLaunchers.Length; tries++)
        {
            MissileLauncherController launcher = periodicLaunchers[_launcherIndex % periodicLaunchers.Length];
            _launcherIndex++;
            if (launcher == null || !launcher.isActiveAndEnabled) continue;
            StartCoroutine(launcher.Launch(target));
            return;
        }
    }

    private void SetAnimBool(bool value)
    {
        if (animator != null && _animBoolHash != 0) animator.SetBool(_animBoolHash, value);
    }
}
