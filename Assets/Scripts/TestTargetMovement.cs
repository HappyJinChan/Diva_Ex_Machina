using UnityEngine;

/// <summary>
/// 射擊場靶子的移動：沿一條軸線左右來回（ping-pong）。
///
/// · 速度 = ModularEntityStats.sprintSpeed（ShootingRangeTrigger 的 Move Speed 或機器人會設定它）。0 = 不動。
/// · 加速度 = ModularEntityStats.accelerationSpeed；0 或負數 = 瞬間到速、瞬間折返（建議，速度才會是「真的」那個值）。
/// · 軸線、中心點、擺幅由 TestTargetBrain 設定。
///
/// 折返判斷在每個物理步做（不是 Brain 的 0.1 秒思考），所以高速時也不會衝出擺幅。
///
/// 跟 base 的差別：靶子沒有 groundPoint / entityOrientation / entityMesh，
/// 所以不做 GroundCheck 與轉向，只做水平移動。
/// </summary>
public class TestTargetMovement : ModularEntityMovement
{
    // ───── 由 Brain 設定 ─────
    private Vector3 _center;
    private Vector3 _axis = Vector3.right;   // 已正規化的水平軸線
    private float _halfWidth = 5f;
    private float _direction = 1f;           // +1 / −1
    private bool _configured;

    /// <summary>目前的實際水平速度（世界座標）。機器人用它算提前量 —— kinematic 剛體讀不到 linearVelocity。</summary>
    public Vector3 CurrentVelocity { get; private set; }

    /// <summary>目前設定的移動速度（m/s）。</summary>
    public float Speed => (modularEntityStats != null) ? Mathf.Max(0f, modularEntityStats.sprintSpeed) : 0f;

    public float HalfWidth => _halfWidth;

    private void Awake()
    {
        if (entityRigidbody == null) entityRigidbody = GetComponent<Rigidbody>();
        if (modularEntityStats == null) modularEntityStats = GetComponent<ModularEntityStats>();
        if (entityMesh == null) entityMesh = transform;
        if (entityOrientation == null) entityOrientation = transform;
    }

    /// <summary>設定來回移動的範圍。axis 會被壓平成水平並正規化。</summary>
    public void ConfigureStrafe(Vector3 center, Vector3 axis, float halfWidth, float startDirection)
    {
        axis.y = 0f;
        _axis = (axis.sqrMagnitude > 0.0001f) ? axis.normalized : Vector3.right;
        _center = center;
        _halfWidth = Mathf.Max(0f, halfWidth);
        _direction = (startDirection >= 0f) ? 1f : -1f;
        _configured = true;
    }

    public override void ManagedFixedUpdate(float dt)
    {
        // 不呼叫 base：靶子沒有 groundPoint，GroundCheck 會丟例外；也不需要轉向
        if (!_configured || entityRigidbody == null) return;

        float speed = Speed;
        if (speed <= 0f || _halfWidth <= 0f)
        {
            moveDirection = Vector3.zero;
        }
        else
        {
            // 在軸線上的位置（相對中心）。超出擺幅就折返 —— 只在「還往外走」時才反向，避免卡在邊界抖動
            float offset = Vector3.Dot(entityRigidbody.position - _center, _axis);
            if (offset >= _halfWidth && _direction > 0f) _direction = -1f;
            else if (offset <= -_halfWidth && _direction < 0f) _direction = 1f;

            moveDirection = _axis * _direction;
        }

        Move(dt, speed);
    }

    private void Move(float dt, float speed)
    {
        Vector3 target = moveDirection * speed;
        Vector3 current = new Vector3(CurrentVelocity.x, 0f, CurrentVelocity.z);

        float accel = (modularEntityStats != null) ? modularEntityStats.accelerationSpeed : 0f;
        Vector3 next = (accel > 0f) ? Vector3.MoveTowards(current, target, accel * dt) : target;
        CurrentVelocity = next;

        if (entityRigidbody.isKinematic)
        {
            entityRigidbody.MovePosition(entityRigidbody.position + next * dt);
        }
        else
        {
            // 保留垂直速度（重力），只改水平
            Vector3 v = entityRigidbody.linearVelocity;
            entityRigidbody.linearVelocity = new Vector3(next.x, v.y, next.z);
        }
    }

    // base 的 HorizontalMovement 依賴 entityOrientation；靶子由自己決定方向，不接受外部指令
    public override void HorizontalMovement(float moveX, float moveZ) { }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!_configured) return;
        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(_center - _axis * _halfWidth, _center + _axis * _halfWidth);
    }
#endif
}
