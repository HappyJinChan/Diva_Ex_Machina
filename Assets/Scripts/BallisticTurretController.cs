﻿using UnityEngine;

/// <summary>
/// 可以被拋物線砲塔發射的砲彈（例如 MortarShell）。BallisticTurretController 生成砲彈後立刻呼叫 Launch。
/// （SupplyPod 走它自己的 Launch，不經過這個介面。）
/// </summary>
public interface IBallisticProjectile
{
    /// <param name="velocity">世界速度（已經包含發射平台 / 船的速度）。</param>
    /// <param name="gravityMultiplier">砲彈的重力 = Physics.gravity × 這個值（跟瞄準計算用的一樣）。</param>
    /// <param name="expectedFlightTime">發射時算出的飛行時間（秒）。</param>
    /// <param name="target">要追的目標；沒有就是 null。</param>
    /// <param name="targetPoint">瞄準的點：沒有目標、或目標中途消失時打這裡。</param>
    void Launch(Vector3 velocity, float gravityMultiplier, float expectedFlightTime, Transform target, Vector3 targetPoint);
}

/// <summary>
/// 拋物線射擊的砲塔。繼承 TurretController：瞄準指定（TrackTarget / AimAtRest …）、
/// 轉向（Yaw / Pitch）、射擊閘門（Shoot：彈匣、冷卻、換彈）全部沿用，只改兩件事：
///
///   1. 瞄哪裡（UpdateTrackedAim / UpdatePointAim）：不再把炮管直接指向目標，而是算出拋物線的發射方向，
///      把 targetLocation 放在那個方向上，原本的 Yaw() / Pitch() 就會把炮管轉過去。
///   2. 怎麼射（SpawnBullet）：算出初速，再加上發射平台（船）在炮口位置的速度，交給砲彈。
///
/// 兩種模式：
///   · 一般（支援艙）：仰角 = 目標仰角 + Elevation Margin，夾在 [Min Elevation, Max Elevation]，
///     初速由仰角反推。Bullet Speed = 「最低初速」：初速不足時改用高弧線，最多抬到 Max Elevation。
///   · Fixed Speed High Arc（迫擊砲）：初速固定 = Bullet Speed，一律高弧線（由初速反推仰角）。
///     仰角超出 [Min Elevation, Max Elevation] 就打不到（超過 Max = 目標太近）。建議 Min 45、Max 89。
///     最大射程 ≈ Bullet Speed² ÷ (9.81 × Gravity Multiplier)。
///
/// 瞄準：
///   · TrackTarget(目標)：Brain 每次思考續約，砲塔每幀用目標當下的位置與速度重算。
///   · TrackPoint(點)：瞄一個固定的世界座標點，砲塔每幀重算（船在動，拋物線也要跟著變）。
///     改用 AimAtRest 等其他瞄準方式之前，先呼叫 ClearPointTarget()。
///   Brain 確認 IsAimed 之後再呼叫 Shoot()。
///
/// 仰角用一般的方向表示（正數 = 往上），不用 TurretController 的 Min / Max Pitch
/// （那兩個是負數 = 往上，很容易填反）。這裡的設定會自動換算寫進去，Inspector 上的 Min / Max Pitch 不用改。
///
/// 砲彈：bulletPrefab 上有 SupplyPod 就呼叫它的 Launch；有 IBallisticProjectile（例如 MortarShell）就呼叫介面的 Launch；
/// 否則有 Rigidbody 就直接設速度（Rigidbody 要開 useGravity，不然不會走拋物線）。不會設定 Bullet.cs 的傷害欄位。
/// </summary>
public class BallisticTurretController : TurretController
{
    [Header("Ballistic")]
    [Tooltip("最低仰角（度，正數 = 往上）。高射砲 60；迫擊砲（Fixed Speed High Arc）45。")]
    [SerializeField, Range(0f, 89f)] private float minElevation = 60f;

    [Tooltip("最高仰角（度）。高射砲 85；迫擊砲 89（越高，能打的最近距離越近）。")]
    [SerializeField, Range(0f, 89f)] private float maxElevation = 85f;

    [Tooltip("仰角 = 目標仰角 + 這個值（再夾在最低 / 最高之間）。用來避開頭頂盲區。Fixed Speed High Arc 模式不使用。")]
    [SerializeField, Min(0f)] private float elevationMargin = 10f;

    [Tooltip("炮口方向和理想發射方向差在這個角度內，IsAimed 才是 true（Brain 用來決定能不能開火）。")]
    [SerializeField, Min(0.1f)] private float aimTolerance = 2f;

    [Tooltip("發射平台（船）的 Rigidbody。砲彈會帶上它在炮口位置的速度，瞄準時也會扣掉。留空就從自己往上找。")]
    [SerializeField] private Rigidbody platformRigidbody;

    [Tooltip("瞄準時扣掉平台速度造成的落點偏移。")]
    [SerializeField] private bool compensatePlatformVelocity = true;

    [Header("Fixed Speed High Arc（迫擊砲）")]
    [Tooltip("初速固定為 Bullet Speed，一律用高弧線（由初速反推仰角）。\n" +
             "最大射程 ≈ Bullet Speed² ÷ (9.81 × Gravity Multiplier)：3 km、5 倍重力 → 初速約 384 m/s。\n" +
             "發射方向用算出來的理想方向（炮管差 Aim Tolerance 以內看不出來，但高弧線對仰角誤差很敏感）。")]
    [SerializeField] private bool fixedSpeedHighArc = false;

    [Tooltip("砲彈的重力 = Physics.gravity × 這個值，瞄準計算也用它。\n" +
             "支援艙（SupplyPod）用的是 Physics.gravity，射支援艙的砲塔要保持 1。")]
    [SerializeField, Min(0.01f)] private float projectileGravityMultiplier = 1f;

    /// <summary>每生成一顆砲彈就觸發（已經發射出去之後）。Brain 用來把「這一發要做什麼」掛到砲彈上。</summary>
    public event System.Action<GameObject> OnProjectileSpawned;

    // 最近一次解算結果
    private bool _hasSolution;
    private Vector3 _idealDirection;     // 炮管理想方向（不含平台速度）
    private Vector3 _solvedTargetPos;    // 解算當下目標的瞄準點
    private Vector3 _solvedTargetVel;
    private float _solvedFlightTime;
    private bool _platformResolved;

    // TrackPoint
    private bool _usePoint;
    private Vector3 _pointTarget;
    private Vector3 _pointVelocity;   // 點本身的移動速度（例如甲板上的點）
    private float _pointTime;         // _pointTarget 是哪個時間點的位置

    /// <summary>瞄點時，點現在的位置（點會動的話，從上次 TrackPoint 起往前推算）。</summary>
    private Vector3 CurrentPoint => _pointTarget + _pointVelocity * (Time.time - _pointTime);

    /// <summary>目前有沒有算出可行的拋物線（頭頂盲區、射程外、太近時為 false）。</summary>
    public bool HasBallisticSolution => _hasSolution && (_usePoint || CurrentTarget != null);

    /// <summary>照目前的解算，砲彈預計飛多久（秒）。給 UI 倒數用。</summary>
    public float ExpectedFlightTime => _solvedFlightTime;

    /// <summary>砲彈的重力倍率。</summary>
    public float ProjectileGravityMultiplier => projectileGravityMultiplier;

    /// <summary>有解，而且炮口已經轉到理想方向的 Aim Tolerance 以內。</summary>
    public bool IsAimed
    {
        get
        {
            if (!HasBallisticSolution || muzzle == null) return false;
            return Vector3.Angle(muzzle.forward, _idealDirection) <= aimTolerance;
        }
    }

    private float Gravity => Physics.gravity.magnitude * projectileGravityMultiplier;

    private Rigidbody Platform
    {
        get
        {
            if (!_platformResolved)
            {
                _platformResolved = true;
                if (platformRigidbody == null)
                    platformRigidbody = GetComponentInParent<Rigidbody>();
            }
            return platformRigidbody;
        }
    }

    private Vector3 PlatformVelocityAtMuzzle =>
        Platform != null ? Platform.GetPointVelocity(MuzzlePosition) : Vector3.zero;

    private void Awake() => ApplyElevationLimits();

    private void OnValidate()
    {
        if (maxElevation < minElevation) maxElevation = minElevation;
        ApplyElevationLimits();
    }

    // TurretController 的 pitch：負數 = 往上
    private void ApplyElevationLimits()
    {
        minPitch = -maxElevation;
        maxPitch = -minElevation;
    }

    // ── 瞄準指定 ──────────────────────────────────────────────

    /// <summary>
    /// 瞄一個點（地面、建築，或會動的甲板上的點）。砲塔每幀重算拋物線。
    /// pointVelocity：點本身的移動速度（固定點 = 0）。兩次呼叫之間用它往前推算位置，解算時也當作目標速度。
    /// 點會動的話，Brain 每次思考都要用最新的位置與速度再呼叫一次。
    /// </summary>
    public void TrackPoint(Vector3 worldPoint, Vector3 pointVelocity = default)
    {
        _pointTarget = worldPoint;
        _pointVelocity = pointVelocity;
        _pointTime = Time.time;
        _usePoint = true;
        AimAtPoint(worldPoint);              // 基底：切到固定點模式、清掉追蹤目標
        Solve(worldPoint, pointVelocity);    // 立刻算一次：緊接著的 HasBallisticSolution / IsAimed 用的就是最新的
    }

    /// <summary>停止瞄點（之後要改用 AimAtRest 等其他瞄準方式時呼叫）。</summary>
    public void ClearPointTarget()
    {
        _usePoint = false;
        _hasSolution = false;
    }

    // ── 1. 瞄哪裡 ─────────────────────────────────────────────

    protected override void UpdateTrackedAim()
    {
        _usePoint = false;
        if (_trackTarget == null)
        {
            _hasSolution = false;
            return;
        }

        Vector3 targetPos = GetAimPoint(_trackTarget);
        Vector3 targetVel = _trackTargetRb != null ? _trackTargetRb.linearVelocity : Vector3.zero;
        Solve(targetPos, targetVel);
    }

    protected override void UpdatePointAim()
    {
        if (_usePoint) Solve(CurrentPoint, _pointVelocity);
    }

    private void Solve(Vector3 targetPos, Vector3 targetVel)
    {
        _hasSolution = false;
        Vector3 inherited = compensatePlatformVelocity ? PlatformVelocityAtMuzzle : Vector3.zero;

        bool ok;
        Vector3 dir;
        float flightTime;
        if (fixedSpeedHighArc)
        {
            ok = MathToolKit.SolveFixedSpeedHighArcIntercept(
                MuzzlePosition, targetPos, targetVel, inherited,
                Mathf.Max(0.01f, bulletSpeed), Gravity, minElevation, maxElevation,
                out dir, out flightTime);
        }
        else
        {
            ok = MathToolKit.SolveBallisticIntercept(
                MuzzlePosition, targetPos, targetVel, inherited,
                minElevation, maxElevation, elevationMargin, Gravity,
                out dir, out _, out flightTime,
                minSpeed: Mathf.Max(0f, bulletSpeed));
        }

        if (ok)
        {
            _hasSolution = true;
            _idealDirection = dir;
            _solvedTargetPos = targetPos;
            _solvedTargetVel = targetVel;
            _solvedFlightTime = flightTime;

            // Yaw() / Pitch() 會把炮管轉向這個點 = 轉向發射方向
            targetLocation = PitchPivotPosition + dir * 20f;
        }
        else
        {
            // 無解（頭頂盲區 / 射程外 / 太近）：朝目標方位、抬到最高仰角等著
            Vector3 d = targetPos - MuzzlePosition;
            d.y = 0f;
            if (d.sqrMagnitude > 0.0001f)
            {
                float rad = maxElevation * Mathf.Deg2Rad;
                Vector3 up = d.normalized * Mathf.Cos(rad) + Vector3.up * Mathf.Sin(rad);
                targetLocation = PitchPivotPosition + up * 20f;
            }
        }
    }

    // ── 2. 怎麼射 ─────────────────────────────────────────────

    protected override void SpawnBullet()
    {
        if (bulletPrefab == null || muzzle == null) return;

        if (!_hasSolution)
        {
            Debug.LogWarning("BallisticTurretController: 沒有可行的拋物線，不發射", this);
            return;
        }

        Vector3 origin = muzzle.position;
        Vector3 inherited = PlatformVelocityAtMuzzle;
        Vector3 dir;
        float speed;
        float t;

        if (fixedSpeedHighArc)
        {
            // 初速固定：用解算出的理想方向（UpdateTrackedAim / UpdatePointAim 每幀都在重算）
            dir = _idealDirection;
            speed = Mathf.Max(0.01f, bulletSpeed);
            t = _solvedFlightTime;
        }
        else
        {
            // 用炮口「實際」朝向重算：炮管還沒完全轉到位時，初速會補償仰角差。
            dir = muzzle.forward;
            float g = Gravity;

            // 跟解算時一樣：瞄「目標預測位置 − 平台速度·T」，T 反覆修正
            t = _solvedFlightTime;
            speed = 0f;
            bool ok = false;
            for (int i = 0; i < 4; i++)
            {
                Vector3 aim = _solvedTargetPos + _solvedTargetVel * t
                              - (compensatePlatformVelocity ? inherited * t : Vector3.zero);
                if (!MathToolKit.SolveLaunchSpeed(origin, aim, dir, g, out speed, out t)) { ok = false; break; }
                ok = true;
            }
            if (!ok)
            {
                Debug.LogWarning("BallisticTurretController: 以炮口目前的朝向打不到目標，不發射", this);
                return;
            }
        }

        Vector3 velocity = dir * speed + inherited;
        GameObject go = Instantiate(bulletPrefab, origin, Quaternion.LookRotation(velocity));

        Transform target = _usePoint ? null : _trackTarget;
        Vector3 point = _usePoint ? CurrentPoint : _solvedTargetPos;

        if (go.TryGetComponent(out SupplyPod pod))
        {
            pod.Launch(velocity, t, target);   // 帶上目標 → 艙會誘導、保證命中
        }
        else if (go.TryGetComponent(out IBallisticProjectile projectile))
        {
            projectile.Launch(velocity, projectileGravityMultiplier, t, target, point);
        }
        else if (go.TryGetComponent(out Rigidbody rb))
        {
            rb.linearVelocity = velocity;
        }
        else
        {
            Debug.LogWarning($"BallisticTurretController: '{bulletPrefab.name}' 沒有 SupplyPod / IBallisticProjectile / Rigidbody，不會移動", go);
        }

        OnProjectileSpawned?.Invoke(go);

        Debug.Log($"BallisticTurretController: 發射。仰角 {90f - Vector3.Angle(dir, Vector3.up):0.0}°，" +
                  $"初速 {speed:0.0} m/s，平台速度 {inherited.magnitude:0.0} m/s，預測飛行 {t:0.00} 秒", this);
    }

#if UNITY_EDITOR
    // 畫出目前的預測拋物線（青色）
    private void OnDrawGizmosSelected()
    {
        if (!_hasSolution || muzzle == null) return;
        Gizmos.color = Color.cyan;

        Vector3 origin = muzzle.position;
        Vector3 inherited = Application.isPlaying ? PlatformVelocityAtMuzzle : Vector3.zero;
        Vector3 gravity = Physics.gravity * projectileGravityMultiplier;

        Vector3 v;
        float T;
        if (fixedSpeedHighArc)
        {
            v = _idealDirection * Mathf.Max(0.01f, bulletSpeed) + inherited;
            T = _solvedFlightTime;
        }
        else
        {
            Vector3 aim = _solvedTargetPos + (_solvedTargetVel - inherited) * _solvedFlightTime;
            if (!MathToolKit.SolveLaunchSpeed(origin, aim, _idealDirection, Gravity, out float speed, out T)) return;
            v = _idealDirection * speed + inherited;
        }

        Vector3 prev = origin;
        const int steps = 48;
        for (int i = 1; i <= steps; i++)
        {
            float t = T * i / steps;
            Vector3 p = origin + v * t + 0.5f * t * t * gravity;
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
#endif
}
