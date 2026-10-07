using UnityEngine;

/// <summary>
/// 射擊場靶子的大腦。只負責「設定好怎麼動」，不追擊、不尋路、不還手。
///
/// 移動方向是「左右」= 跟射擊方向垂直的水平軸線。射擊位置由 ShootingRangeTrigger 在生成時
/// 用 SetShooterPosition 傳進來；沒有傳就找場景裡的 ShootingRangeTrigger，再沒有就用主相機。
///
/// 每次生成隨機決定先往左或往右，避免機器人每次都遇到同一種情況。
///
/// 覆寫 Think() 而且不呼叫 base —— base 的偵測、仇恨、尋路對靶子都沒有意義，
/// PathFinder 不存在時還可能出錯。OnDamaged 也覆寫成什麼都不做（靶子被打不會改變行為）。
/// </summary>
public class TestTargetBrain : ModularEntityBrain
{
    [Header("Strafe")]
    [Tooltip("左右擺幅（公尺）：從中心往左右各走這麼遠。總寬度 = 2 倍。")]
    [SerializeField, Min(0f)] private float strafeHalfWidth = 5f;

    [Tooltip("勾選 = 每次生成隨機決定先往左還是往右。")]
    [SerializeField] private bool randomStartDirection = true;

    private Vector3? _shooterPosition;
    private TestTargetMovement _movement;

    public float StrafeHalfWidth => strafeHalfWidth;

    /// <summary>生成時由 ShootingRangeTrigger 呼叫（要在 Start 之前，Instantiate 之後立刻呼叫即可）。</summary>
    public void SetShooterPosition(Vector3 position) => _shooterPosition = position;

    /// <summary>覆寫擺幅（例如機器人想測不同寬度）。Start 之後呼叫會立刻重設。</summary>
    public void SetStrafeHalfWidth(float halfWidth)
    {
        strafeHalfWidth = Mathf.Max(0f, halfWidth);
        if (_movement != null) Configure();
    }

    protected override void Start()
    {
        base.Start();

        _movement = modularEntityMovement as TestTargetMovement;
        if (_movement == null) _movement = GetComponent<TestTargetMovement>();
        if (_movement == null)
        {
            Debug.LogError($"[TestTargetBrain] '{name}' 沒有 TestTargetMovement，靶子不會移動。", this);
            return;
        }
        modularEntityMovement = _movement;

        Configure();
    }

    private void Configure()
    {
        Vector3 center = transform.position;

        Vector3 shooter = ResolveShooter(center);
        Vector3 toTarget = center - shooter;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.0001f) toTarget = Vector3.forward;

        // 「左右」= 射擊方向的水平垂直軸
        Vector3 axis = Vector3.Cross(Vector3.up, toTarget.normalized);

        float dir = randomStartDirection ? (Random.value < 0.5f ? -1f : 1f) : 1f;
        _movement.ConfigureStrafe(center, axis, strafeHalfWidth, dir);
    }

    private Vector3 ResolveShooter(Vector3 fallbackCenter)
    {
        if (_shooterPosition.HasValue) return _shooterPosition.Value;

        var trigger = FindFirstObjectByType<ShootingRangeTrigger>();
        if (trigger != null) return trigger.transform.position;

        if (Camera.main != null) return Camera.main.transform.position;

        return fallbackCenter - Vector3.forward;
    }

    // 移動已經在 TestTargetMovement 每個物理步處理，這裡不需要思考
    protected override void Think() { }

    // 靶子被打不改變行為，也不把攻擊者加進目標清單
    public override void OnDamaged(float damage, float maxHealth, GameObject attacker) { }
}
