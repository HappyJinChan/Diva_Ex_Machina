using UnityEngine;

/// <summary>
/// 掛在測試敵人上，記錄「第一次受傷 → 被摧毀」花了多久，結束時回報給 ShootingRangeScoreboard。
///
/// ・第一次受傷：每幀比對 ModularEntityStats.health，血量第一次下降就開始計時。
///   不需要改 ModularEntityStats。誤差最多一幀。
/// ・被摧毀：訂閱 ModularEntityStats.OnDeath（死亡時在 Destroy 之前觸發）。
///   不用 OnDestroy —— 那個在停止 Play、切場景時也會觸發，會記到假的成績。
/// ・一擊必殺（受傷和死亡在同一幀）：還沒開始計時就死了，記為 0 秒。
///
/// 名稱由 ShootingRangeTrigger 生成時透過 Setup() 傳入；
/// 直接擺在場景裡的敵人沒有被 Setup，就用物件名稱。
/// </summary>
public class DamageRecord : MonoBehaviour
{
    [Tooltip("回報的對象。留空會在場景裡自動找。")]
    [SerializeField] private ShootingRangeScoreboard scoreboard;

    [Tooltip("記在計分板上的名稱。留空則使用物件名稱（去掉 \"(Clone)\"）。")]
    [SerializeField] private string recordName;

    [Tooltip("留空則在自己、父物件、子物件上自動找。")]
    [SerializeField] private ModularEntityStats stats;

    // 跟射擊位置（踏板）的距離。-1 = 未知（不是由 ShootingRangeTrigger 生成）
    private float _distance = -1f;

    private float _lastHealth;
    private bool _timing;
    private float _startTime;
    private bool _reported;

    /// <summary>目前是否已開始計時。</summary>
    public bool IsTiming => _timing;

    /// <summary>從第一次受傷到現在的秒數（還沒受傷時為 0）。</summary>
    public float ElapsedTime => _timing ? Time.time - _startTime : 0f;

    public string RecordName => string.IsNullOrEmpty(recordName)
        ? gameObject.name.Replace("(Clone)", "").Trim()
        : recordName;

    /// <summary>生成時由 ShootingRangeTrigger 呼叫。</summary>
    public void Setup(string enemyName, ShootingRangeScoreboard board) => Setup(enemyName, board, -1f);

    /// <summary>生成時由 ShootingRangeTrigger 呼叫。distance = 跟踏板的水平距離（公尺）。</summary>
    public void Setup(string enemyName, ShootingRangeScoreboard board, float distance)
    {
        _distance = distance;

        if (!string.IsNullOrEmpty(enemyName)) recordName = enemyName;
        if (board != null) scoreboard = board;

        // 生成器可能剛改過血量（ShootingRangeTrigger 的 HP）。重新記下基準，
        // 否則血量被調低會被當成「受到攻擊」。
        if (stats != null && !_timing) _lastHealth = stats.health;
    }

    private void Awake()
    {
        if (stats == null) stats = GetComponent<ModularEntityStats>();
        if (stats == null) stats = GetComponentInParent<ModularEntityStats>();
        if (stats == null) stats = GetComponentInChildren<ModularEntityStats>();

        if (stats == null)
        {
            Debug.LogWarning($"[DamageRecord] '{name}' 找不到 ModularEntityStats，無法記錄。", this);
            enabled = false;
            return;
        }

        _lastHealth = stats.health;
    }

    private void OnEnable()
    {
        if (stats != null) stats.OnDeath += HandleDeath;
    }

    private void OnDisable()
    {
        if (stats != null) stats.OnDeath -= HandleDeath;
    }

    private void Update()
    {
        if (_timing || stats == null) return;

        // 血量下降 = 受到攻擊
        if (stats.health < _lastHealth)
            StartTiming();

        _lastHealth = stats.health;
    }

    private void StartTiming()
    {
        _timing = true;
        _startTime = Time.time;

        // 通知計分板「有敵人第一次被打中」—— ShootingRangeFinishPad 用它找出整輪的起點。
        // 被打中但沒打死的敵人也算，所以不能只靠擊殺紀錄反推。
        var board = ResolveScoreboard();
        if (board != null)
            board.ReportFirstHit(RecordName, _startTime);
    }

    private ShootingRangeScoreboard ResolveScoreboard()
    {
        if (scoreboard == null)
            scoreboard = FindFirstObjectByType<ShootingRangeScoreboard>();
        return scoreboard;
    }

    private void HandleDeath()
    {
        if (_reported) return;
        _reported = true;

        // 一擊必殺：Update 還沒來得及看到血量下降
        if (!_timing) StartTiming();

        float duration = Time.time - _startTime;

        if (ResolveScoreboard() != null)
        {
            // 移動速度：只有會動的靶子（TestTargetMovement）才有意義，否則記 0
            var mover = GetComponentInChildren<TestTargetMovement>();
            float speed = (mover != null) ? mover.Speed : 0f;
            scoreboard.Report(RecordName, duration, stats.maxHealth, _distance, speed);
        }
        else
            Debug.LogWarning($"[DamageRecord] 場景裡沒有 ShootingRangeScoreboard。'{RecordName}' 花了 {duration:F2} 秒。", this);
    }
}
