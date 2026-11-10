using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 核心被擊中時，讓一個 UI Graphic 短暫閃一下（預設紅色）。用在小地圖的外框。
///
/// 放法（小地圖外框）：
///   在小地圖的 Mask 物件底下（或旁邊、同樣大小）建一個子物件，RectTransform 四角撐滿，
///   掛 LockRingGraphic（Rings 只留一層：Inset 0、Thickness 依喜好、Gap Half Width 0；Draw Cross 關掉），
///   再掛這個元件。平常是 Idle Color（預設全透明 = 看不見），被打中時閃 Flash Color 再淡出。
///
/// 連續中彈：每一發都會讓閃光重新從最亮開始；Min Interval 內的命中不重新觸發，避免機槍掃射時一直維持全亮。
/// 用不受 timeScale 影響的時間（頓幀、暫停時一樣會淡出）。
/// </summary>
public class CoreHitFlash : MonoBehaviour
{
    [Tooltip("要閃的 Graphic。留空 = 這個物件上的 Graphic。")]
    [SerializeField] private Graphic target;

    [Tooltip("留空 = CoreStats.Instance。")]
    [SerializeField] private CoreStats core;

    [SerializeField] private Color idleColor = new Color(1f, 0.2f, 0.15f, 0f);
    [SerializeField] private Color flashColor = new Color(1f, 0.2f, 0.15f, 1f);

    [Tooltip("一次閃光的長度（秒）。")]
    [SerializeField, Min(0.01f)] private float duration = 0.35f;

    [Tooltip("兩次觸發之間至少隔幾秒。")]
    [SerializeField, Min(0f)] private float minInterval = 0.15f;

    private CoreStats _subscribed;
    private float _flashStart = -999f;

    private void Awake()
    {
        if (target == null) target = GetComponent<Graphic>();
        if (target != null) target.color = idleColor;
    }

    private void OnDisable()
    {
        Unsubscribe();
        if (target != null) target.color = idleColor;
    }

    private void Update()
    {
        // 核心可能比 UI 晚生成：每幀確認一次訂閱
        CoreStats c = core != null ? core : CoreStats.Instance;
        if (c != _subscribed)
        {
            Unsubscribe();
            if (c != null) { c.Damaged += OnCoreDamaged; _subscribed = c; }
        }

        if (target == null) return;
        float t = (Time.unscaledTime - _flashStart) / duration;
        if (t >= 1f)
        {
            if (target.color != idleColor) target.color = idleColor;
            return;
        }
        float k = 1f - t;
        target.color = Color.Lerp(idleColor, flashColor, k * k);   // 一開始最亮，快速淡出
    }

    private void OnCoreDamaged(float amount, GameObject attacker)
    {
        if (Time.unscaledTime - _flashStart < minInterval) return;
        _flashStart = Time.unscaledTime;
    }

    private void Unsubscribe()
    {
        if (_subscribed != null) _subscribed.Damaged -= OnCoreDamaged;
        _subscribed = null;
    }

    [ContextMenu("Test Flash")]
    private void TestFlash() => _flashStart = Time.unscaledTime;
}
