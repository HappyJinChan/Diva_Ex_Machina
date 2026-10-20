using System;
using UnityEngine;

/// <summary>
/// 支援艙（換裝射出用的砲彈）。不繼承任何東西，也不使用 Bullet.cs。
///
/// 飛行分三段：
///   1. 上升段：純拋物線（Rigidbody 重力），不修正 —— 從遠處看得到完整的弧線。
///   2. 中段誘導（過了最高點）：每一步算「從現在的位置、剩下的時間（tgo）內，只靠重力打到
///      目標預測位置所需的速度」，用有上限的加速度把速度往那裡推（Max Guidance Accel）。
///      原本的軌跡就是對的時候幾乎不用修正，看起來仍是拋物線。
///   3. 終端攔截（剩下 Terminal Time 秒）：關掉重力（也可關掉碰撞），速度至少 Terminal Min Speed，
///      以 Terminal Turn Rate 轉向「目標 + 目標速度 × 剩餘距離 / 艙速」的攔截點，一直追到命中為止。
///      這一段保證命中：就算她突然轉向、衝刺，艙會一直追，直到 Max Lifetime。
///
/// 命中判定用距離（Capture Radius），不用碰撞：每一步檢查「上一步到這一步的線段」離目標多近，
/// 艙速度很快也不會穿過去。命中時觸發 OnHitTarget 並銷毀（換裝、爆炸之後接在這個事件上）。
///
/// 模擬（Python，目標每 1～5 秒隨機轉向、速度 0～80 m/s，各距離 60 次）：命中率 100%，
/// 最多比預測晚約 1.6 秒。目標速度到 120 m/s 時仍 100% 命中，最多晚約 5 秒。
///
/// Prefab 設定：
///   - 這個元件會自動加上 Rigidbody，並在 Awake 設成：useGravity、Interpolate、ContinuousDynamic。
///   - 要穿過貝努鳥號船身：把 prefab 放在自己的 layer（例如新建 "SupplyPod"），
///     再到 Project Settings → Physics → Layer Collision Matrix 取消它和 Mobile Platform 的碰撞。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SupplyPod : MonoBehaviour
{
    [Header("Lifetime")]
    [Tooltip("飛行超過這個秒數就自動銷毀。")]
    [SerializeField] private float maxLifetime = 90f;

    [Tooltip("撞到東西時在 Console 印出位置與飛行時間。")]
    [SerializeField] private bool logImpact = true;

    [Header("Guidance - Midcourse（過了最高點）")]
    [Tooltip("中段誘導從最高點才開始（上升段保持純拋物線）。關掉 = 發射後就開始修正。")]
    [SerializeField] private bool guidanceStartsAtApex = true;

    [Tooltip("修正的反應時間（秒）。越小越積極。")]
    [SerializeField, Min(0.05f)] private float guidanceResponse = 0.5f;

    [Tooltip("中段誘導的最大加速度（m/s²，不含重力）。30 ≈ 3G。")]
    [SerializeField, Min(0f)] private float maxGuidanceAccel = 30f;

    [Header("Guidance - Terminal（最後幾秒）")]
    [Tooltip("預計到達前多少秒進入終端攔截。")]
    [SerializeField, Min(0.1f)] private float terminalTime = 1.5f;

    [Tooltip("終端攔截時每秒最多轉幾度。")]
    [SerializeField, Min(1f)] private float terminalTurnRate = 360f;

    [Tooltip("終端攔截時的最低速度（m/s）。要比 Bradamant 的最高速度快，才追得上。")]
    [SerializeField, Min(1f)] private float terminalMinSpeed = 120f;

    [Tooltip("終端攔截時關掉碰撞（穿過地形 / 障礙物也要送到）。")]
    [SerializeField] private bool terminalIgnoreCollisions = true;

    [Header("Capture")]
    [Tooltip("離目標瞄準點這個距離以內就算命中（公尺）。")]
    [SerializeField, Min(0.1f)] private float captureRadius = 4f;

    /// <summary>命中目標時觸發。參數：這個艙、目標。</summary>
    public event Action<SupplyPod, Transform> OnHitTarget;

    /// <summary>艙被銷毀時觸發（命中、逾時、場景卸載都會）。</summary>
    public event Action<SupplyPod> OnDespawn;

    private enum Phase { Ballistic, Midcourse, Terminal, Done }

    private Rigidbody _rb;
    private Phase _phase = Phase.Done;
    private float _launchTime = -1f;
    private float _expectedFlightTime = -1f;
    private float _arrivalTime;
    private bool _impactLogged;

    private Transform _target;
    private Rigidbody _targetRb;
    private Vector3 _prevPosition;

    public Rigidbody Body => _rb;
    public bool IsLaunched => _launchTime >= 0f;
    public Transform Target => _target;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity = true;
        _rb.interpolation = RigidbodyInterpolation.Interpolate;
        _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    /// <summary>
    /// 以 worldVelocity 發射（已經包含發射平台 / 船的速度）。
    /// expectedFlightTime：發射時算出的飛行時間，誘導用它當「預計到達時間」。
    /// target：要命中的目標（Bradamant）。null = 不誘導，純拋物線。
    /// </summary>
    public void Launch(Vector3 worldVelocity, float expectedFlightTime = -1f, Transform target = null)
    {
        if (_rb == null) _rb = GetComponent<Rigidbody>();

        transform.SetParent(null, true);   // 離開炮管後不再跟著船移動
        _rb.isKinematic = false;
        _rb.useGravity = true;
        _rb.detectCollisions = true;
        _rb.linearVelocity = worldVelocity;
        if (worldVelocity.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(worldVelocity);

        _launchTime = Time.time;
        _expectedFlightTime = expectedFlightTime;
        _arrivalTime = Time.time + Mathf.Max(0f, expectedFlightTime);
        _impactLogged = false;

        _target = target;
        _targetRb = target != null ? target.GetComponentInParent<Rigidbody>() : null;
        _prevPosition = _rb.position;
        _phase = Phase.Ballistic;

        Destroy(gameObject, maxLifetime);
    }

    private void FixedUpdate()
    {
        if (!IsLaunched || _phase == Phase.Done) return;

        if (_target != null)
        {
            if (CheckCapture()) return;
            Guide();
        }

        if (_rb.linearVelocity.sqrMagnitude > 0.01f)
            _rb.MoveRotation(Quaternion.LookRotation(_rb.linearVelocity));

        _prevPosition = _rb.position;
    }

    // ── 誘導 ────────────────────────────────────────────────

    private void Guide()
    {
        float tgo = _arrivalTime - Time.time;
        Vector3 v = _rb.linearVelocity;

        // 階段切換
        if (_phase == Phase.Ballistic)
        {
            bool pastApex = v.y <= 0f;
            if (!guidanceStartsAtApex || pastApex) _phase = Phase.Midcourse;
        }
        if (_phase == Phase.Midcourse && tgo <= terminalTime)
            EnterTerminal();

        Vector3 aim = TurretController.GetAimPoint(_target);
        Vector3 targetVel = _targetRb != null ? _targetRb.linearVelocity : Vector3.zero;

        if (_phase == Phase.Midcourse)
        {
            Vector3 predicted = aim + targetVel * tgo;
            Vector3 required = MathToolKit.BallisticVelocityToReach(_rb.position, predicted, tgo, Physics.gravity);
            Vector3 accel = (required - v) / guidanceResponse;
            accel = Vector3.ClampMagnitude(accel, maxGuidanceAccel);
            _rb.AddForce(accel, ForceMode.Acceleration);
        }
        else if (_phase == Phase.Terminal)
        {
            float speed = Mathf.Max(v.magnitude, terminalMinSpeed);
            Vector3 dir = v.sqrMagnitude > 0.0001f ? v.normalized : (aim - _rb.position).normalized;

            float dist = Vector3.Distance(_rb.position, aim);
            Vector3 lead = aim + targetVel * (dist / speed);
            Vector3 toLead = lead - _rb.position;
            if (toLead.sqrMagnitude > 0.0001f)
            {
                float maxRad = terminalTurnRate * Mathf.Deg2Rad * Time.fixedDeltaTime;
                dir = Vector3.RotateTowards(dir, toLead.normalized, maxRad, 0f);
            }
            _rb.linearVelocity = dir * speed;
        }
    }

    private void EnterTerminal()
    {
        _phase = Phase.Terminal;
        _rb.useGravity = false;
        if (terminalIgnoreCollisions) _rb.detectCollisions = false;
    }

    // ── 命中判定：上一步到這一步的線段，離目標最近的距離 ──────────

    private bool CheckCapture()
    {
        Vector3 aim = TurretController.GetAimPoint(_target);
        Vector3 a = _prevPosition;
        Vector3 b = _rb.position;
        Vector3 ab = b - a;
        float len2 = ab.sqrMagnitude;
        float u = len2 > 0f ? Mathf.Clamp01(Vector3.Dot(aim - a, ab) / len2) : 0f;
        Vector3 closest = a + ab * u;

        if ((aim - closest).sqrMagnitude > captureRadius * captureRadius) return false;

        _phase = Phase.Done;
        float t = Time.time - _launchTime;
        string expected = _expectedFlightTime > 0f ? $"（預測 {_expectedFlightTime:0.00} 秒）" : "";
        Debug.Log($"SupplyPod: 命中 '{_target.name}'，飛行 {t:0.00} 秒{expected}", this);

        OnHitTarget?.Invoke(this, _target);
        Destroy(gameObject);
        return true;
    }

    private void OnDestroy()
    {
        OnDespawn?.Invoke(this);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsLaunched || _impactLogged) return;
        _impactLogged = true;

        if (!logImpact) return;
        float t = Time.time - _launchTime;
        string expected = _expectedFlightTime > 0f ? $"（預測 {_expectedFlightTime:0.00} 秒）" : "";
        string targetInfo = _target != null
            ? $"，離目標 {Vector3.Distance(collision.GetContact(0).point, TurretController.GetAimPoint(_target)):0.0} m（{_phase}）"
            : "";
        Debug.Log($"SupplyPod: 撞到 '{collision.collider.name}'，位置 {collision.GetContact(0).point}，" +
                  $"飛行 {t:0.00} 秒{expected}{targetInfo}", this);
    }
}
