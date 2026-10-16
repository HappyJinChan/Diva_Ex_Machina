using UnityEngine;

/// <summary>
/// 診斷用：量玩家站在船上時「相對船」漂了多少，每隔一段時間在 Console 印一行。
/// 不改任何遊戲行為，查完就可以移除。
///
/// 掛在玩家（有 Rigidbody 和 ShipPassenger 的那個物件）上，進遊戲站在船上不要動，讓船轉向。
///
/// 印出來的欄位：
///   drift      玩家在「船的座標系」裡的移動速度（cm/s），拆成 船前後 / 船左右 / 上下 三軸。
///              站著不動時理想值是 0；有固定方向的數值 = 真的在滑。
///   relVel     玩家速度 − 船上那一點的實際速度（含旋轉，GetPointVelocity），cm/s。
///   yawRate    船的偏航角速度（度/秒）。drift 跟它成正比 → 旋轉補償的問題。
///   tilt       甲板傾斜（度，pitch / roll）。drift 往低處走 → 斜坡下滑。
///   shipSpeed  船速（m/s）。
///   rFromCom   玩家離船質心的水平距離（公尺）。drift 跟它成正比 → 旋轉中心的問題。
///   comOffset  質心離 Transform 原點的距離（公尺）。
///   input      這段時間內有沒有移動輸入（有的話這一行不準）。
/// </summary>
[DefaultExecutionOrder(10000)]   // 排在 PlayerMovement 之後
public class ShipDriftProbe : MonoBehaviour
{
    [Tooltip("每幾秒印一次。")]
    [Min(0.1f)] [SerializeField] private float interval = 1f;

    private Rigidbody _rb;
    private ShipPassenger _passenger;
    private PlayerController _controller;

    private Rigidbody _ship;
    private Vector3 _startLocal;
    private float _startTime;
    private bool _hadInput;
    private float _yawAccum;
    private Quaternion _lastShipRot;

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _passenger = GetComponent<ShipPassenger>();
        _controller = GetComponentInParent<PlayerController>();
        if (_controller == null) _controller = PlayerController.Instance;
    }

    private void FixedUpdate()
    {
        Rigidbody ship = (_passenger != null && _passenger.isOnShip) ? _passenger.PlatformRigidbody : null;
        if (ship == null || _rb == null) { _ship = null; return; }

        // 船的物理姿態（不用 transform：船有插值，transform 是畫面上的位置）
        Vector3 local = Quaternion.Inverse(ship.rotation) * (_rb.position - ship.position);

        if (ship != _ship)
        {
            _ship = ship;
            Begin(local);
            return;
        }

        if (_controller != null && _controller.LastMoveInput.sqrMagnitude > 0.0001f) _hadInput = true;

        float yawStep = Mathf.DeltaAngle(_lastShipRot.eulerAngles.y, ship.rotation.eulerAngles.y);
        _yawAccum += yawStep;
        _lastShipRot = ship.rotation;

        float elapsed = Time.fixedTime - _startTime;
        if (elapsed < interval) return;

        Vector3 drift = (local - _startLocal) / elapsed * 100f;   // cm/s，船的座標系：x 左右、y 上下、z 前後
        Vector3 relVel = _rb.linearVelocity - ship.GetPointVelocity(_rb.position);
        Vector3 relLocal = Quaternion.Inverse(ship.rotation) * relVel * 100f;

        Vector3 e = ship.rotation.eulerAngles;
        float pitch = Mathf.DeltaAngle(0f, e.x), roll = Mathf.DeltaAngle(0f, e.z);

        Vector3 toCom = _rb.position - ship.worldCenterOfMass; toCom.y = 0f;

        Debug.Log(
            $"[ShipDriftProbe] drift(cm/s) 左右 {drift.x:F1} 上下 {drift.y:F1} 前後 {drift.z:F1} | " +
            $"relVel(cm/s) 左右 {relLocal.x:F1} 上下 {relLocal.y:F1} 前後 {relLocal.z:F1} | " +
            $"yawRate {_yawAccum / elapsed:F2}°/s | tilt pitch {pitch:F2}° roll {roll:F2}° | " +
            $"shipSpeed {ship.linearVelocity.magnitude:F1} m/s | rFromCom {toCom.magnitude:F1} m | " +
            $"comOffset {Vector3.Distance(ship.worldCenterOfMass, ship.position):F2} m | " +
            $"input {(_hadInput ? "有（這行不準）" : "無")}", this);

        Begin(local);
    }

    private void Begin(Vector3 local)
    {
        _startLocal = local;
        _startTime = Time.fixedTime;
        _hadInput = false;
        _yawAccum = 0f;
        _lastShipRot = _ship != null ? _ship.rotation : Quaternion.identity;
    }
}
