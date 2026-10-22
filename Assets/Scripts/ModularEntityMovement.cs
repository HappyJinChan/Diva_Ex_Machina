using UnityEngine;

public class ModularEntityMovement : MonoBehaviour, IManagedEntity
{
    [Header("Rigidbody")]
    [SerializeField] protected Rigidbody entityRigidbody;
    [Header("Entity Stats")]
    [SerializeField] protected ModularEntityStats modularEntityStats;

    [SerializeField] protected Transform entityOrientation;
    [SerializeField] protected Transform entityMesh;
    protected Vector3 moveDirection = new Vector3(0, 0, 0);

    [Header("Jump Setting")]
    [SerializeField] protected Transform groundPoint;
    [SerializeField] protected LayerMask whatIsGround;
    [SerializeField] protected bool grounded;
    [SerializeField] protected bool readyToJump;
    protected float jumpCooldown = 0.25f;
    protected float groundCheckDistance = 0.1f;

    // ── 集中更新（ModularEntityManager） ─────────────────────────────────
    //
    // 原本這裡是 FixedUpdate。現在由 ModularEntityManager 每個物理步統一呼叫 ManagedFixedUpdate，
    // 省掉 Unity 對每隻敵人各自呼叫 FixedUpdate 的開銷。仍然是每步都跑，跟原本一樣。
    // 子類別覆寫 OnEnable / OnDisable 時記得呼叫 base，否則不會被登記。

    private int _managerIndex = -1;   // 在 ModularEntityManager 清單裡的位置，-1 = 沒登記
    int IManagedEntity.ManagerIndex { get => _managerIndex; set => _managerIndex = value; }

    protected virtual void OnEnable()
    {
        ModularEntityManager.Register(this);
    }

    protected virtual void OnDisable()
    {
        ModularEntityManager.Unregister(this);
    }

    /// <summary>每個物理步由 ModularEntityManager 呼叫。子類別覆寫時記得呼叫 base。</summary>
    public virtual void ManagedFixedUpdate(float dt)
    {
        GroundCheck();
        CarryWithPlatformRotation();   // 船轉向時，跟著船一起轉（位置 + 模型朝向）

        // 失衡開始的那一步：水平速度（相對平台）先砍到 staggerSpeedRetain，之後用 decelerationSpeed 減速到 0。
        // 正在被撞飛時不砍，讓它照物理飛完。
        bool staggered = IsStaggered;
        if (staggered && !_wasStaggered && !IsKnockedBack)
            CutHorizontalSpeedForStagger();
        _wasStaggered = staggered;

        // 被撞飛期間：不控制移動也不轉向，交給物理（速度、重力）自己飛
        if (IsKnockedBack) return;

        // 失衡：無法行動。清掉移動輸入、朝向、近戰覆寫，下面照常用 decelerationSpeed 減速到停。
        // 失衡結束後由 Brain 的下一次思考重新指定。
        if (staggered)
        {
            moveDirection = Vector3.zero;
            _hasFacingTarget = false;
            _hasVelocityOverride = false;
        }

        ApplyHorizontalMovementFixed(dt);
        ApplySteepSlopeLimit(dt);
        UpdateFacing();
    }

    // ── 陡坡限制 ─────────────────────────────────────────────────────────
    //
    // 水平速度每步被直接設成目標值，撞上陡坡時物理會把實體往上推，速度夠快就能衝上任何坡。
    // 這裡在速度寫入之後，把「往陡坡上衝」的那部分水平速度扣掉。只對 Unity Terrain 有效，詳見 TerrainSlopeLimiter。

    [Header("Steep Slope")]
    [Tooltip("啟用陡坡限制。飛行單位（隼）如果被崖壁卡住，可以在 prefab 上關掉。")]
    [SerializeField] protected bool limitSteepSlopes = true;
    [Tooltip("超過這個角度的地形坡面爬不上去。")]
    [SerializeField, Range(10f, 89f)] protected float maxSlopeAngle = 45f;
    [Tooltip("往前取樣的距離（公尺，不含這一步會移動的距離）。約等於膠囊半徑。")]
    [SerializeField, Min(0f)] protected float slopeProbeAhead = 0.5f;
    [Tooltip("前方地面比腳底高超過這個值才會擋（公尺）。避免平地小起伏誤判。")]
    [SerializeField, Min(0f)] protected float slopeMinRise = 0.3f;

    private Terrain _slopeTerrainHint;

    private void ApplySteepSlopeLimit(float dt)
    {
        if (!limitSteepSlopes || entityRigidbody == null || entityRigidbody.isKinematic || groundPoint == null) return;

        ShipPassenger p = Passenger;
        if (p != null && p.isOnShip) return;   // 在船上不管（船不是 Terrain）

        Vector3 v = entityRigidbody.linearVelocity;
        // 腳底位置用 Rigidbody 的物理位置推算（transform 有插值時會落後）
        Vector3 feet = entityRigidbody.position + (groundPoint.position - entityRigidbody.transform.position);

        Vector3 h = TerrainSlopeLimiter.Limit(v, feet, slopeProbeAhead, dt, maxSlopeAngle, slopeMinRise, ref _slopeTerrainHint);
        if (h.x != v.x || h.z != v.z)
            entityRigidbody.linearVelocity = new Vector3(h.x, v.y, h.z);
    }

    // ── 失衡 ─────────────────────────────────────────────────────────────

    /// <summary>這個實體的特殊狀態管理（失衡、ACS…）。</summary>
    public EntityStatusEffects Status => modularEntityStats != null ? modularEntityStats.Status : null;

    [Header("Stagger")]
    [Tooltip("失衡開始時，水平速度（相對平台）保留的比例，之後用 decelerationSpeed 減速到 0。\n" +
             "0.1 = 瞬間降到原本的 10%。1 = 不砍，直接用 decelerationSpeed 慢慢停。")]
    [SerializeField, Range(0f, 1f)] protected float staggerSpeedRetain = 0.1f;

    private bool _wasStaggered;

    private void CutHorizontalSpeedForStagger() => ScaleHorizontalVelocity(staggerSpeedRetain);

    /// <summary>
    /// 把水平速度（相對平台）立刻乘上 ratio。垂直速度、平台速度不變。
    /// 失衡開始、近戰衝刺結束（MeleeDashEnd）用來「踩煞車」。
    /// </summary>
    public void ScaleHorizontalVelocity(float ratio)
    {
        if (entityRigidbody == null || entityRigidbody.isKinematic) return;

        ratio = Mathf.Clamp01(ratio);
        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = entityRigidbody.linearVelocity - platformVel;
        vRel.x *= ratio;
        vRel.z *= ratio;
        entityRigidbody.linearVelocity = vRel + platformVel;
    }

    /// <summary>是否正在失衡（期間無法行動）。</summary>
    public bool IsStaggered
    {
        get
        {
            EntityStatusEffects s = Status;
            return s != null && s.IsStaggered;
        }
    }

    // ── 撞飛 ─────────────────────────────────────────────────────────────
    //
    // 一般的移動每一步都會用加速度把速度拉回 AI 想要的值，直接 AddForce 的話馬上就被拉回去。
    // 撞飛期間暫停移動控制，速度完全交給物理；時間到才恢復。

    private float _knockbackUntil;

    /// <summary>是否正在被撞飛（期間 AI 的移動控制暫停）。</summary>
    public bool IsKnockedBack => Time.time < _knockbackUntil;

    /// <summary>
    /// 撞飛：把速度直接設成 worldVelocity（世界座標），並在 duration 秒內暫停移動控制。
    /// Kinematic 的實體不受影響。
    /// </summary>
    public virtual void ApplyKnockback(Vector3 worldVelocity, float duration)
    {
        if (entityRigidbody == null || entityRigidbody.isKinematic) return;

        entityRigidbody.linearVelocity = worldVelocity;
        _knockbackUntil = Time.time + Mathf.Max(0f, duration);
    }

    // ── 近戰覆寫 ─────────────────────────────────────────────────────────
    //
    // 近戰攻擊期間，移動方向與速度由 ModularEntityMeleeManager 指定。
    // 速度仍然只在這裡（ApplyHorizontalMovementFixed）寫進 Rigidbody，Movement 是唯一的寫入者，
    // 近戰只是下指令，不會跟 AI 的移動互相覆蓋。

    private bool _hasVelocityOverride;
    private Vector3 _overrideDirection;
    private float _overrideSpeed;
    private float _overrideAcceleration;

    /// <summary>是否正在套用近戰覆寫。</summary>
    public bool HasVelocityOverride => _hasVelocityOverride;

    /// <summary>
    /// 指定水平移動：方向（世界座標，會放平）、目標速度、加速度。
    /// acceleration ≤ 0 代表立刻到達該速度（衝刺用）。
    /// </summary>
    public void SetVelocityOverride(Vector3 worldDirection, float speed, float acceleration)
    {
        worldDirection.y = 0f;
        _overrideDirection = worldDirection.sqrMagnitude > 0.0001f ? worldDirection.normalized : Vector3.zero;
        _overrideSpeed = Mathf.Max(0f, speed);
        _overrideAcceleration = acceleration;
        _hasVelocityOverride = true;
    }

    /// <summary>解除近戰覆寫，回到 AI 的一般移動。</summary>
    public void ClearVelocityOverride()
    {
        _hasVelocityOverride = false;
    }

    // ── 朝向 ─────────────────────────────────────────────────────────────
    //
    // Brain 降頻之後只會每 0.1 秒左右思考一次。轉向如果還留在 Brain 裡做，
    // RotateMesh 每次只轉 rotationSpeed × fixedDeltaTime，轉向速度就會跟著變慢好幾倍。
    // 所以 Brain 只負責「想面對哪裡」，實際轉動在這裡每步執行，速度跟原本一樣。

    private bool _hasFacingTarget;
    private Vector3 _facingTarget;
    private float _facingDeadzone;

    /// <summary>設定想面對的水平方向（世界座標）。角度誤差小於 deadzoneDegrees 就不轉，避免抖動。</summary>
    public void SetFacingTarget(Vector3 worldDir, float deadzoneDegrees)
    {
        worldDir.y = 0f;
        if (worldDir.sqrMagnitude < 0.0001f)
        {
            _hasFacingTarget = false;
            return;
        }

        _facingTarget = worldDir;
        _facingDeadzone = deadzoneDegrees;
        _hasFacingTarget = true;
    }

    /// <summary>停止轉向（維持目前朝向）。</summary>
    public void ClearFacingTarget()
    {
        _hasFacingTarget = false;
    }

    protected virtual void UpdateFacing()
    {
        if (!_hasFacingTarget) return;

        float signedAngle = Vector3.SignedAngle(MeshForward, _facingTarget, Vector3.up);
        if (Mathf.Abs(signedAngle) < _facingDeadzone) return;

        // 傳入剩餘角度的絕對值，RotateMesh 會夾住不過頭
        RotateMesh(Mathf.Sign(signedAngle), Mathf.Abs(signedAngle));
    }

    protected virtual void ApplyHorizontalMovementFixed(float dt)
    {

        Vector3 platformVel = GetMobilePlatformVelocity();

        // 用「相對平台」速度來做加速/減速
        Vector3 vWorld = entityRigidbody.linearVelocity;
        Vector3 vRel = vWorld - platformVel;

        Vector3 horizontalRel = new Vector3(vRel.x, 0f, vRel.z);

        // 近戰覆寫：由 ModularEntityMeleeManager 指定方向與速度，取代 AI 的移動方向
        if (_hasVelocityOverride)
        {
            Vector3 wanted = _overrideDirection * _overrideSpeed;
            horizontalRel = (_overrideAcceleration <= 0f)
                ? wanted                                                            // 0 = 立刻到達（衝刺）
                : Vector3.MoveTowards(horizontalRel, wanted, _overrideAcceleration * dt);
            entityRigidbody.linearVelocity = new Vector3(horizontalRel.x, vRel.y, horizontalRel.z) + platformVel;
            return;
        }

        if (moveDirection.sqrMagnitude < 0.0001f)
        {
            horizontalRel = Vector3.MoveTowards(horizontalRel, Vector3.zero, modularEntityStats.decelerationSpeed * dt);
            Vector3 outRel = new Vector3(horizontalRel.x, vRel.y, horizontalRel.z);
            entityRigidbody.linearVelocity = outRel + platformVel;   // ✅ 設定一次，不累加
            return;
        }

        Vector3 targetHorizontalRel = moveDirection * modularEntityStats.sprintSpeed;
        horizontalRel = Vector3.MoveTowards(horizontalRel, targetHorizontalRel, modularEntityStats.accelerationSpeed * dt);

        Vector3 outRel2 = new Vector3(horizontalRel.x, vRel.y, horizontalRel.z);
        entityRigidbody.linearVelocity = outRel2 + platformVel;      // ✅ 設定一次，不累加
    }

    public virtual void HorizontalMovement(float moveX, float moveZ)
    {
        Vector3 forward = entityOrientation.forward;
        forward.y = 0f;

        Vector3 right = entityOrientation.right;
        right.y = 0f;

        if (forward.sqrMagnitude > 0.0001f) forward.Normalize();
        if (right.sqrMagnitude > 0.0001f) right.Normalize();

        moveDirection = forward * moveZ + right * moveX;

        if (moveDirection.sqrMagnitude < 0.0001f)
            moveDirection = Vector3.zero;
        else if (moveDirection.sqrMagnitude > 1f)
            moveDirection.Normalize();
    }
    protected virtual void VerticalMovement()
    {
        if (grounded && readyToJump)
        {
            readyToJump = false;
            entityRigidbody.linearVelocity = new Vector3(entityRigidbody.linearVelocity.x, 0, entityRigidbody.linearVelocity.z);
            entityRigidbody.AddForce(Vector3.up * convertJumpHeightToForce(modularEntityStats.jumpHeight), ForceMode.Impulse);
            Invoke("ResetJump", jumpCooldown);
        }
    }

    protected virtual float convertJumpHeightToForce(float jumpHeight)
    {
        float g = Mathf.Abs(Physics.gravity.y);
        float v0 = Mathf.Sqrt(2f * g * jumpHeight);
        float mass = entityRigidbody.mass;
        float impulseMagnitude = mass * v0;

        return impulseMagnitude;
    }
    public virtual void ResetJump()
    {
        readyToJump = true;
    }

    public void GroundCheck()
    {
        float castDistance = groundCheckDistance + entityRigidbody.linearVelocity.y * Time.fixedDeltaTime;

        ShipPassenger p = Passenger;
        if (p == null)
        {
            // 沒有 ShipPassenger：維持原本的短射線
            grounded = Physics.Raycast(groundPoint.position, Vector3.down, out _, castDistance, whatIsGround);
            return;
        }

        // 地面偵測 + 船上偵測共用同一條往下的射線（每步少打一條）。
        // 長射線打到的第一個東西，就是原本短射線會打到的那個（只要在 castDistance 內），
        // 所以 grounded 的結果跟原本相同。
        // 起點往上抬 lift：腳底稍微陷進甲板時，射線才不會從碰撞體裡面開始打而漏掉它。
        float lift = p.RayStartOffset;
        Vector3 origin = groundPoint.position + Vector3.up * lift;
        float distance = Mathf.Max(castDistance, p.MaxDistance) + lift;
        int mask = whatIsGround.value | p.PlatformLayers;

        bool hasHit = Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                                      distance, mask, QueryTriggerInteraction.Ignore);

        grounded = hasHit
                   && (whatIsGround.value & (1 << hit.collider.gameObject.layer)) != 0
                   && hit.distance - lift <= castDistance;

        p.ReportGroundHit(hasHit, hit, origin, distance);
    }

    // ── Mobile Platform carry (velocity-based) ────────────────────────────
    //
    // 原本這裡有一組自己的 _onMobilePlatform / _mobilePlatformRb，靠本檔的
    // OnTriggerStay 設成 true —— 但沒有 OnTriggerExit，所以它永遠不會變回 false。
    // 實體踏上船一次之後，就算走回地面、走到地圖另一頭，ApplyHorizontalMovementFixed
    // 還是會一直把船速加進去，表現成「被一股看不見的力往船的方向拖著跑」。
    //
    // 現在改成讀 ShipPassenger。專案裡本來就有兩套獨立的「在不在船上」狀態
    // （這一套給移動速度用，ShipPassenger 那套給 PathFinder 用），
    // 收斂成一個真相來源就不會再有兩邊不一致的問題。
    [Header("Mobile Platform")]
    [Tooltip("留空會自動在自己或父物件上找。ShipPassenger 必須跟實體的 collider 在同一個 GameObject 上，才收得到 trigger 訊息。")]
    [SerializeField] protected ShipPassenger shipPassenger;
    private bool _passengerResolved;

    /// <summary>
    /// 延遲解析，不用 Awake —— 子類別（例如 FalconMovement）如果自己宣告了 Awake，
    /// 會蓋掉基底的 Awake，那樣 shipPassenger 就永遠是 null 而且很難查。
    /// </summary>
    protected ShipPassenger Passenger
    {
        get
        {
            if (!_passengerResolved)
            {
                _passengerResolved = true;
                if (shipPassenger == null) shipPassenger = GetComponent<ShipPassenger>();
                if (shipPassenger == null) shipPassenger = GetComponentInParent<ShipPassenger>();
            }
            return shipPassenger;
        }
    }

    /// <summary>目前所在移動平台（船）的速度；不在船上為 0。近戰預判要扣掉它。</summary>
    public Vector3 PlatformVelocity => GetMobilePlatformVelocity();

    /// <summary>目前所在移動平台（船）的 Transform；不在船上為 null。</summary>
    public Transform PlatformTransform
    {
        get
        {
            ShipPassenger p = Passenger;
            if (p == null || !p.isOnShip) return null;
            Rigidbody rb = p.PlatformRigidbody;
            return rb != null ? rb.transform : null;
        }
    }

    protected virtual Vector3 GetMobilePlatformVelocity()
    {
        ShipPassenger p = Passenger;
        if (p == null || !p.isOnShip) return Vector3.zero;

        Rigidbody platformRb = p.PlatformRigidbody;
        return (platformRb != null) ? platformRb.linearVelocity : Vector3.zero;   // Unity 6
    }

    // ── Mobile Platform carry (rotation) ──────────────────────────────────
    //
    // 平台速度（linearVelocity）只處理「船往前開」，不處理「船轉向」。
    // 船轉向時甲板上每一點的移動方向都不同，只疊 linearVelocity 的話，
    // 實體會相對甲板往外滑。這裡每步把實體的位置繞船轉同樣的角度。
    // 跟 PlayerMovement.CarryWithPlatformRotation 同一套做法。

    private Rigidbody _rotCarryShip;          // 上一步所在的船；換船 / 下船時重新記錄
    private Quaternion _rotCarryLastRot;

    private void CarryWithPlatformRotation()
    {
        ShipPassenger p = Passenger;
        Rigidbody ship = (p != null && p.isOnShip) ? p.PlatformRigidbody : null;

        if (ship == null || entityRigidbody == null)
        {
            _rotCarryShip = null;
            return;
        }

        Quaternion current = ship.rotation;

        // 剛上船（或換了一艘船）：這一步只記錄姿態，下一步才開始補償
        if (ship != _rotCarryShip)
        {
            _rotCarryShip = ship;
            _rotCarryLastRot = current;
            return;
        }

        Quaternion delta = current * Quaternion.Inverse(_rotCarryLastRot);
        _rotCarryLastRot = current;

        // 旋轉中心 = 船 Rigidbody 的物理位置（原點）。
        // 不用質心（船是繞原點轉的，用質心會漂，偏移約 40 m）；
        // 也不用 transform.position（船有插值，那是畫面上落後的位置）。
        Vector3 pivot = ship.position;
        Vector3 pos = entityRigidbody.position;
        entityRigidbody.MovePosition(pivot + delta * (pos - pivot));

        // 模型朝向也吃同一個 yaw，不然船轉了、敵人還面對原本的世界方向
        if (entityMesh == null) return;
        delta.ToAngleAxis(out float angle, out Vector3 axis);
        if (axis.sqrMagnitude < 0.000001f) return;
        float yaw = Vector3.Dot(axis.normalized, Vector3.up) * angle;
        if (Mathf.Abs(yaw) > 0.0001f)
            entityMesh.Rotate(0f, yaw, 0f, Space.World);
    }

    public virtual void RotateMesh(float direction, float maxDegrees = -1f)
    {
        if (direction == 0f) return;
        float step = modularEntityStats.rotationSpeed * Time.fixedDeltaTime;
        if (maxDegrees >= 0f) step = Mathf.Min(step, maxDegrees);   // 不超過剩餘角度 → 不過頭
        entityMesh.Rotate(Vector3.up, direction * step);
    }

    // OnTriggerStay 已移除 —— 平台偵測統一由 ShipPassenger 負責。
    // 舊版本在這裡每個 physics step 對每個重疊 collider 呼叫一次
    // GetComponentInParent<Rigidbody>()，那本身也是不必要的開銷。

    public Vector3 MeshForward
    {
        get
        {
            Vector3 f = entityMesh.forward;
            f.y = 0f;
            return f.sqrMagnitude > 0.0001f ? f.normalized : entityMesh.forward;
        }
    }

    public Transform GetGroundPoint()
    {
        return groundPoint;
    }
}