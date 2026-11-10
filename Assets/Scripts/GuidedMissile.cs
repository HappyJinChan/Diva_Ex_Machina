using UnityEngine;
using UnityEngine.Serialization;

/// <summary>
/// 導引飛彈：Bullet 的子類別。飛彈 prefab 上把 Bullet 換成這個元件就好（只要一個腳本）。
/// 傷害、爆炸、命中特效、壽命、物件池都沿用 Bullet；只有「怎麼飛」換掉。
///
/// MissileLauncherController：生成 → 給初速 → SetTarget(target)。
/// 第一個物理步時讀目標和發射方向，開始三段飛行：
///   1. 彈射：低速往「側上方」彈出、不追蹤。發射點在機體左邊就往左彈、右邊往右（依 attacker 判斷左右）。
///   2. 點火：加速到巡航速度，同時往目標飛 —— 瞄準點 = 目標 + 側向偏移 + 向上偏移，
///      偏移隨距離縮小而淡出，所以飛彈會先往外繞一個弧，越接近越直接撲向目標。
///   3. 防撞地：離目標還遠時，貼地或前方有地面就往上拉；進入 Ground Avoid Disable Within 以內才解除。
///
/// 重力會自動關掉。沒有目標（或目標消失）時直線飛，壽命到了照 Bullet 的規則爆炸。
/// </summary>
public class GuidedMissile : Bullet
{
    [Header("1. Eject")]
    [Tooltip("彈射段長度（秒），期間不追蹤。")]
    [SerializeField, Min(0f)] private float ejectDuration = 0.35f;

    [Tooltip("彈射速度（m/s）。")]
    [SerializeField, Min(0f)] private float ejectSpeed = 15f;

    [Tooltip("往側面彈出的比例（相對發射管方向）。0 = 不往側面。")]
    [SerializeField, Min(0f)] private float ejectSide = 0.8f;

    [Tooltip("往上彈出的比例。")]
    [SerializeField, Min(0f)] private float ejectUp = 0.5f;

    [Header("2. Boost")]
    [Tooltip("巡航速度（m/s）。")]
    [SerializeField, Min(1f)] private float cruiseSpeed = 70f;

    [Tooltip("點火後的加速度（m/s²）。")]
    [SerializeField, Min(0f)] private float acceleration = 120f;

    [Tooltip("轉向角速度（度/秒）。數字越大追得越緊；越接近目標會自動提高到 Terminal Turn Multiplier 倍，才收得進去。\n" +
             "public：之後玩家武器的屬性可以直接寫這個欄位（每次從物件池重用時會還原成 prefab 的值）。")]
    [FormerlySerializedAs("turnRate")]
    [Min(0f)] public float homingDegreePerSecond = 120f;

    [SerializeField, Min(1f)] private float terminalTurnMultiplier = 3f;

    [Tooltip("繞弧的側向偏移（公尺，隨機範圍）。")]
    [SerializeField] private Vector2 curveWidth = new Vector2(10f, 25f);

    [Tooltip("繞弧的向上偏移（公尺，隨機範圍）。")]
    [SerializeField] private Vector2 curveHeight = new Vector2(5f, 15f);

    [Tooltip("點火時播放的特效（尾焰等），可留空。")]
    [SerializeField] private ParticleSystem[] ignitionVfx;

    [Header("3. Ground Avoidance")]
    [Tooltip("地面的層（Ground、Terrain、建築…）。Nothing = 不做防撞地。")]
    [SerializeField] private LayerMask groundMask;

    [Tooltip("離地低於這個高度（公尺）就往上拉。")]
    [SerializeField, Min(0f)] private float minAltitude = 4f;

    [Tooltip("往前看幾秒的距離有沒有地面（速度 × 這個秒數）。")]
    [SerializeField, Min(0f)] private float lookAheadTime = 0.5f;

    [Tooltip("離目標這麼近（公尺）以內就不再防撞地。")]
    [SerializeField, Min(0f)] private float groundAvoidDisableWithin = 20f;

    [Tooltip("防撞地時的轉向角速度（度/秒）。")]
    [SerializeField, Min(0f)] private float avoidTurnRate = 360f;

    private Transform _target;
    private Collider _targetCol;
    private bool _launched;
    private bool _needsInit;
    private bool _ignited;
    private float _ignitionTime;
    private float _sideSign;
    private float _width, _height;
    private float _curveDistance;   // 點火時離目標的距離（偏移從這裡開始淡出）

    // homingDegreePerSecond 的 prefab 值（發射端改過之後，重用時要還原）
    private float _defaultHoming;
    private bool _defaultCaptured;

    protected override void OnBulletSpawned()
    {
        if (!_defaultCaptured) { _defaultHoming = homingDegreePerSecond; _defaultCaptured = true; }
        else homingDegreePerSecond = _defaultHoming;

        if (rb != null) rb.useGravity = false;
        _launched = false;
        _ignited = false;
        _needsInit = true;
        _target = null;
        _targetCol = null;
        StopVfx();
    }

    /// <summary>由發射器在生成之後指定目標。</summary>
    public void SetTarget(Transform target)
    {
        _target = target;
        _targetCol = target != null ? target.GetComponentInChildren<Collider>() : null;
    }

    /// <summary>第一個物理步：發射端已經寫好初速、attacker、目標，從這裡開始彈射。</summary>
    private void InitLaunch()
    {
        _needsInit = false;
        if (rb == null) return;

        Transform owner = attacker != null ? attacker.transform : null;
        Vector3 launchDir = rb.linearVelocity.sqrMagnitude > 0.01f ? rb.linearVelocity.normalized : transform.forward;

        // 左右：發射點在機體的哪一邊就往哪邊彈；正中間就隨機
        float localX = owner != null ? owner.InverseTransformPoint(transform.position).x : 0f;
        _sideSign = Mathf.Abs(localX) > 0.01f ? Mathf.Sign(localX) : (Random.value < 0.5f ? -1f : 1f);

        Vector3 right = owner != null ? owner.right : Vector3.Cross(Vector3.up, launchDir).normalized;
        Vector3 ejectDir = (launchDir + right * (_sideSign * ejectSide) + Vector3.up * ejectUp).normalized;
        rb.linearVelocity = ejectDir * ejectSpeed;

        _width = Random.Range(Mathf.Min(curveWidth.x, curveWidth.y), Mathf.Max(curveWidth.x, curveWidth.y));
        _height = Random.Range(Mathf.Min(curveHeight.x, curveHeight.y), Mathf.Max(curveHeight.x, curveHeight.y));

        _ignitionTime = Time.time + ejectDuration;
        _launched = true;
    }

    protected override void FixedUpdate()
    {
        // 先改速度，再跑 Bullet 的流程（命中結算、壽命、機頭對齊、命中預測會用到新的速度）
        if (IsLive)
        {
            if (_needsInit) InitLaunch();
            Guide();
        }
        base.FixedUpdate();
    }

    private void Guide()
    {
        if (!_launched || rb == null) return;
        float dt = Time.fixedDeltaTime;

        // 1. 彈射段：維持彈出的速度，不轉向
        if (Time.time < _ignitionTime) return;

        Vector3 pos = transform.position;
        Vector3 vel = rb.linearVelocity;
        float speed = vel.magnitude;
        Vector3 dir = speed > 0.01f ? vel / speed : transform.forward;

        if (!_ignited)
        {
            _ignited = true;
            _curveDistance = TargetValid ? Mathf.Max(1f, Vector3.Distance(pos, TargetAimPoint)) : 1f;
            PlayVfx();
        }

        // 2. 加速
        speed = Mathf.MoveTowards(speed, cruiseSpeed, acceleration * dt);

        // 目標不見了：直線飛（壽命到了 Bullet 會自己處理）
        if (!TargetValid)
        {
            rb.linearVelocity = dir * speed;
            return;
        }

        Vector3 aim = TargetAimPoint;
        Vector3 toTarget = aim - pos;
        float dist = toTarget.magnitude;

        // 繞弧：偏移隨距離淡出（k：1 = 剛點火，0 = 到達）
        float k = Mathf.Clamp01(dist / _curveDistance);
        Vector3 flat = new Vector3(toTarget.x, 0f, toTarget.z);
        Vector3 lateral = flat.sqrMagnitude > 0.01f ? Vector3.Cross(Vector3.up, flat.normalized) : Vector3.zero;
        Vector3 curvedAim = aim + lateral * (_sideSign * _width * k) + Vector3.up * (_height * k);
        Vector3 desired = (curvedAim - pos).normalized;
        float rate = homingDegreePerSecond * Mathf.Lerp(terminalTurnMultiplier, 1f, k);

        // 3. 防撞地
        if (groundMask.value != 0 && dist > groundAvoidDisableWithin)
        {
            bool avoiding = false;

            if (Physics.Raycast(pos, dir, out RaycastHit ahead, Mathf.Max(minAltitude, speed * lookAheadTime), groundMask, QueryTriggerInteraction.Ignore))
            {
                // 前方有地面 / 牆：沿著表面往外拉
                desired = (Vector3.ProjectOnPlane(desired, ahead.normal).normalized + ahead.normal).normalized;
                avoiding = true;
            }
            else if (Physics.Raycast(pos, Vector3.down, out RaycastHit below, minAltitude, groundMask, QueryTriggerInteraction.Ignore))
            {
                // 太低：往上加一點，越低加越多
                float t = 1f - below.distance / Mathf.Max(0.01f, minAltitude);
                if (desired.y < 0f) desired.y *= (1f - t);
                desired = (desired + Vector3.up * t).normalized;
                avoiding = true;
            }

            if (avoiding) rate = Mathf.Max(rate, avoidTurnRate);
        }

        Vector3 newDir = Vector3.RotateTowards(dir, desired, rate * Mathf.Deg2Rad * dt, 0f);
        rb.linearVelocity = newDir.normalized * speed;
    }

    private bool TargetValid => _target != null && _target.gameObject.activeInHierarchy;

    private Vector3 TargetAimPoint => _targetCol != null && _targetCol.enabled ? _targetCol.bounds.center : _target.position;

    private void PlayVfx()
    {
        if (ignitionVfx == null) return;
        foreach (ParticleSystem ps in ignitionVfx)
            if (ps != null) ps.Play(true);
    }

    private void StopVfx()
    {
        if (ignitionVfx == null) return;
        foreach (ParticleSystem ps in ignitionVfx)
            if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }
}
