using UnityEngine;

public class FalconMovement : ModularEntityMovement
{
    [Header("Hover")]
    [SerializeField] private float hoverRaycastDistance = 100f;

    [Header("Banking")]
    [SerializeField] private Transform bankPivot;          // FalconMesh 底下的 BankPivot（roll 專用中間層）
    [SerializeField] private float maxBankAngle = 35f;     // 最大傾斜角（度）
    [SerializeField] private float bankPerDegPerSec = 0.5f;// 迴轉率(度/秒) → 傾斜角 的比例
    [SerializeField] private float bankLerpSpeed = 5f;     // 傾斜過渡的平滑速度
    [SerializeField] private float bankReturnSpeed = 70f;  // 沒轉彎時 bank 回正速度（度/秒）


    [SerializeField] private float approachGain = 3f;       // 高度誤差 → 期望垂直速度 的比例
    [SerializeField] private float maxVerticalSpeed = 15f;  // 俯衝/爬升速度上限
    private float targetBank;    // 由 RotateMesh 每次被呼叫時設定
    private float currentBank;   // 實際套用的傾斜角，平滑追向 targetBank

    // 懸停 raycast 的結果（brain 若需要「腳下地面」的資訊可以直接讀，不用再打一次射線）
    public bool HasGroundBelow { get; private set; }
    public Vector3 GroundBelowPoint { get; private set; }

    // 懸停目標：由 brain 在每次思考時設定，這裡每個物理步套用
    private bool _hasHoverTarget;
    private float _hoverHeight;

    public override void ManagedFixedUpdate(float dt)
    {
        base.ManagedFixedUpdate(dt);   // GroundCheck + ApplyHorizontalMovementFixed（下面已 override）+ 轉向
        if (IsKnockedBack) return;     // 被撞飛期間不維持懸停，讓它真的飛出去；時間到再自己飛回懸停高度
        ApplyHover(dt);                // 每步維持懸停高度
        UpdateBank();                  // 每幀更新傾斜（含回正）
    }

    /// <summary>
    /// 設定懸停高度：維持在「腳下地面 + targetHeight」。由 brain 的各個 state behaviour 呼叫。
    ///
    /// Brain 降頻之後只會每 0.1 秒左右呼叫一次，所以這裡只做兩件事：
    /// 記住目標高度、打一次射線找腳下地面。實際的垂直速度控制在 ApplyHover 每個物理步執行 ——
    /// 如果還是只在呼叫時設一次速度，兩次思考之間重力會把隼往下拉，看起來一頓一頓的。
    /// </summary>
    public void VerticalMovement(float targetHeight)
    {
        _hoverHeight = targetHeight;
        _hasHoverTarget = true;

        HasGroundBelow = Physics.Raycast(groundPoint.position, Vector3.down, out RaycastHit hit,
                                         hoverRaycastDistance, whatIsGround);
        if (HasGroundBelow)
            GroundBelowPoint = hit.point;
    }

    /// <summary>每步把垂直速度推向「回到懸停高度」所需的速度。地面高度用上次 VerticalMovement 打到的點。</summary>
    private void ApplyHover(float dt)
    {
        if (!_hasHoverTarget || !HasGroundBelow) return;

        float heightError = (GroundBelowPoint.y + _hoverHeight) - groundPoint.position.y;

        // 目標垂直速度：離目標越遠飛越快，接近時線性收斂（類似水平的 throttle）
        float desiredVerticalSpeed = Mathf.Clamp(
            heightError * approachGain,          // 誤差 → 期望速度
            -maxVerticalSpeed, maxVerticalSpeed
        );

        float currentVy = entityRigidbody.linearVelocity.y;

        // ★ 這一幀是要加速還是減速？跟 falcon 水平移動同一套判斷
        //   朝目標速度靠近時：若「往目標的變化」是在縮小速度大小 → 用 deceleration
        bool decelerating = Mathf.Abs(desiredVerticalSpeed) < Mathf.Abs(currentVy)
                            || Mathf.Sign(desiredVerticalSpeed) != Mathf.Sign(currentVy);
        float rate = decelerating
            ? modularEntityStats.decelerationSpeed
            : modularEntityStats.accelerationSpeed;

        // 用選定的 rate 逼近目標垂直速度（rate 單位是「速度變化/秒」= 加速度）
        float newVy = Mathf.MoveTowards(currentVy, desiredVerticalSpeed, rate * dt);

        Vector3 v = entityRigidbody.linearVelocity;
        v.y = newVy;
        entityRigidbody.linearVelocity = v;
    }

    /// <summary>
    /// 與 base 唯一的差別：減速時用 decelerationSpeed，加速時才用 accelerationSpeed。
    /// moveDirection 為零時目標速度為零 → 一樣走減速路徑 → 停下來懸停。
    /// </summary>
    protected override void ApplyHorizontalMovementFixed(float dt)
    {
        Vector3 platformVel = GetMobilePlatformVelocity();
        Vector3 vRel = entityRigidbody.linearVelocity - platformVel;

        Vector3 horizontalRel = new Vector3(vRel.x, 0f, vRel.z);
        Vector3 targetHorizontalRel = moveDirection * modularEntityStats.sprintSpeed;

        float rate = (horizontalRel.magnitude > targetHorizontalRel.magnitude)
            ? modularEntityStats.decelerationSpeed
            : modularEntityStats.accelerationSpeed;

        horizontalRel = Vector3.MoveTowards(horizontalRel, targetHorizontalRel, rate * dt);

        entityRigidbody.linearVelocity = new Vector3(horizontalRel.x, vRel.y, horizontalRel.z) + platformVel;
    }

    /// <summary>
    /// base 照舊做 yaw（繞 up 累加）。這裡「額外」依這一幀的迴轉率設定目標傾斜角。
    /// roll 落在子物件 bankPivot 上，跟 entityMesh 的 yaw 完全分離，不互相污染。
    /// </summary>
    public override void RotateMesh(float direction, float maxDegrees = -1f)
    {
        base.RotateMesh(direction, maxDegrees);   // ★ 先做原本的 yaw，行為完全不變

        if (bankPivot == null) return;

        // 這一幀實際轉了多少度（base 已夾過 maxDegrees / rotationSpeed），換算成 度/秒
        float step = modularEntityStats.rotationSpeed * Time.fixedDeltaTime;
        if (maxDegrees >= 0f) step = Mathf.Min(step, maxDegrees);
        float turnRateDegPerSec = (step * Mathf.Sign(direction)) / Time.fixedDeltaTime;

        // 迴轉率越高 → 傾斜越大。負號決定傾斜側，歪錯邊就把負號拿掉。
        targetBank = Mathf.Clamp(-turnRateDegPerSec * bankPerDegPerSec, -maxBankAngle, maxBankAngle);
    }

    // 每幀更新傾斜：RotateMesh 沒被呼叫（直線飛行）時，targetBank 主動衰減回 0 → 平飛回正
    private void UpdateBank()
    {
        if (bankPivot == null) return;

        targetBank = Mathf.MoveTowards(targetBank, 0f, bankReturnSpeed * Time.fixedDeltaTime);
        currentBank = Mathf.Lerp(currentBank, targetBank, bankLerpSpeed * Time.fixedDeltaTime);

        // 只寫 z 軸；bankPivot 不負責 yaw，所以 x/y 恆為 0
        bankPivot.localEulerAngles = new Vector3(0f, 0f, currentBank);
    }
    //returns the height of the entity above the ground. If there is no ground below, returns 0.
    public float FlyHeight()
    {
        if (!HasGroundBelow) return 0f;
        return groundPoint.position.y - GroundBelowPoint.y;
    }
}