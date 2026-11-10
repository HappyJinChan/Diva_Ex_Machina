using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 戰艦 HUD（Ship UI）：核心血量條 + 支援炮擊冷卻。掛在 Ship UI 物件上。
///
/// 核心：Core 留空 → CoreStats.Instance，再沒有就從 LandshipNavigation.Instance.core 找。
///   Core Health Bar（Slider，跟玩家的 AP Bar 同一種）和 Core Health Fill（Image，Filled）二選一或都填。
/// 冷卻：Supply Manager 留空 → SupplyManager.Instance。
///   Cooldown Text 顯示剩餘秒數（無條件進位，30 → 1），冷卻結束顯示 Ready Label。
///   Cooldown Fill（Image，Type = Filled、Radial 360）可選：1 = 可以用，冷卻剛開始時是 0。
///
/// 換裝射出冷卻：跟炮擊冷卻同一套顯示方式（文字、Filled 進度、圖示變色）；瞄準中顯示 Gear Busy Label。
/// 核心圖示（可選）：依血量比例沿 Core Icon Gradient 變色（綠 → 黃 → 橘 → 紅），顏色會慢慢追上。
///
/// 每幀讀數值；文字只在顯示的數字變了才重寫，不會每幀產生字串。
/// </summary>
public class ShipHud : MonoBehaviour
{
    [Header("Core Health")]
    [SerializeField] private CoreStats core;
    [SerializeField] private Slider coreHealthBar;
    [SerializeField] private Image coreHealthFill;
    [Tooltip("可留空。顯示目前血量（整數）。")]
    [SerializeField] private TMP_Text coreHealthText;

    [Tooltip("可留空。核心圖示（建議用單色版 Core_Icon_Mono，白色才染得出準確的顏色）。")]
    [SerializeField] private Graphic coreIcon;
    [Tooltip("血量比例 → 顏色。左 = 0%（紅），右 = 100%（綠）。")]
    [SerializeField] private Gradient coreIconGradient = DefaultCoreGradient();
    [Tooltip("顏色追上目標的速度。越小越慢；0 = 立刻變色。")]
    [SerializeField, Min(0f)] private float coreIconColorSpeed = 3f;

    [Header("Mortar Cooldown")]
    [SerializeField] private SupplyManager supplyManager;
    [SerializeField] private TMP_Text cooldownText;
    [Tooltip("可留空。Image Type 要設成 Filled。")]
    [SerializeField] private Image cooldownFill;
    [SerializeField] private string readyLabel = "READY";
    [Tooltip("{0} = 剩餘秒數。")]
    [SerializeField] private string cooldownFormat = "{0}s";
    [Tooltip("可留空。冷卻中 / 可用時，冷卻圖示（或任何 Graphic）的顏色。")]
    [SerializeField] private Graphic cooldownIcon;
    [SerializeField] private Color iconCoolingColor = new Color(1f, 1f, 1f, 0.35f);
    [SerializeField] private Color iconReadyColor = Color.white;

    [Header("Gear Change Cooldown")]
    [Tooltip("留空 = 場上第一個 SupportTurretBrain。")]
    [SerializeField] private SupportTurretBrain supportTurret;
    [SerializeField] private TMP_Text gearCooldownText;
    [Tooltip("可留空。Image Type 要設成 Filled。")]
    [SerializeField] private Image gearCooldownFill;
    [Tooltip("可留空。冷卻中 / 可用時，換裝圖示（或任何 Graphic）的顏色。")]
    [SerializeField] private Graphic gearCooldownIcon;
    [SerializeField] private Color gearIconCoolingColor = new Color(1f, 1f, 1f, 0.35f);
    [SerializeField] private Color gearIconReadyColor = Color.white;
    [Tooltip("砲塔正在瞄準、支援艙還沒射出時顯示的文字。")]
    [SerializeField] private string gearBusyLabel = "...";

    private int _shownHealth = int.MinValue;
    private int _shownGearSeconds = int.MinValue;
    private bool _supportSearched;
    private bool _iconColorInit;

    private static Gradient DefaultCoreGradient()
    {
        var g = new Gradient();
        g.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(0.90f, 0.15f, 0.12f), 0f),     // 紅
                new GradientColorKey(new Color(1.00f, 0.50f, 0.10f), 0.33f),  // 橘
                new GradientColorKey(new Color(1.00f, 0.85f, 0.15f), 0.66f),  // 黃
                new GradientColorKey(new Color(0.30f, 0.90f, 0.35f), 1f),     // 綠
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        return g;
    }
    private int _shownSeconds = int.MinValue;

    private void Update()
    {
        UpdateCore();
        UpdateCooldown();
        UpdateGearCooldown();
    }

    private void UpdateGearCooldown()
    {
        if (supportTurret == null)
        {
            if (_supportSearched) return;   // 只找一次（場上沒有的話不要每幀找）
            _supportSearched = true;
            supportTurret = FindFirstObjectByType<SupportTurretBrain>();
            if (supportTurret == null) return;
        }

        float remaining = supportTurret.CooldownRemaining;
        float duration = supportTurret.CooldownDuration;
        bool ready = remaining <= 0f;
        bool busy = ready && supportTurret.IsBusy;   // 還在瞄準（冷卻從射出才開始）

        if (gearCooldownFill != null)
            gearCooldownFill.fillAmount = busy ? 0f : duration > 0f ? 1f - remaining / duration : 1f;

        if (gearCooldownIcon != null)
            gearCooldownIcon.color = ready && !busy ? gearIconReadyColor : gearIconCoolingColor;

        if (gearCooldownText != null)
        {
            int secs = busy ? -1 : ready ? 0 : Mathf.CeilToInt(remaining);
            if (secs != _shownGearSeconds)
            {
                _shownGearSeconds = secs;
                gearCooldownText.text = busy ? gearBusyLabel : ready ? readyLabel : string.Format(cooldownFormat, secs);
            }
        }
    }

    private void UpdateCore()
    {
        if (core == null)
        {
            core = CoreStats.Instance;
            if (core == null && LandshipNavigation.Instance != null && LandshipNavigation.Instance.core != null)
                core = LandshipNavigation.Instance.core.GetComponentInParent<CoreStats>();
            if (core == null) return;
        }

        if (coreHealthBar != null)
        {
            coreHealthBar.minValue = 0f;
            coreHealthBar.maxValue = core.MaxHealth;
            coreHealthBar.value = core.Health;
        }
        if (coreHealthFill != null) coreHealthFill.fillAmount = core.HealthNormalized;

        if (coreIcon != null && coreIconGradient != null)
        {
            Color target = coreIconGradient.Evaluate(core.HealthNormalized);
            // 第一次直接套用；之後慢慢追上（被大傷害打到時顏色是漸變過去，不是跳過去）
            if (!_iconColorInit || coreIconColorSpeed <= 0f)
            {
                coreIcon.color = target;
                _iconColorInit = true;
            }
            else
            {
                coreIcon.color = Color.Lerp(coreIcon.color, target, 1f - Mathf.Exp(-coreIconColorSpeed * Time.deltaTime));
            }
        }

        if (coreHealthText != null)
        {
            int hp = Mathf.CeilToInt(core.Health);
            if (hp != _shownHealth)
            {
                _shownHealth = hp;
                coreHealthText.text = hp.ToString();
            }
        }
    }

    private void UpdateCooldown()
    {
        if (supplyManager == null)
        {
            supplyManager = SupplyManager.Instance;
            if (supplyManager == null) return;
        }

        float remaining = supplyManager.CooldownRemaining;
        float duration = supplyManager.CooldownDuration;
        bool ready = remaining <= 0f;

        if (cooldownFill != null)
            cooldownFill.fillAmount = duration > 0f ? 1f - remaining / duration : 1f;

        if (cooldownIcon != null)
            cooldownIcon.color = ready ? iconReadyColor : iconCoolingColor;

        if (cooldownText != null)
        {
            int secs = ready ? 0 : Mathf.CeilToInt(remaining);
            if (secs != _shownSeconds)
            {
                _shownSeconds = secs;
                cooldownText.text = ready ? readyLabel : string.Format(cooldownFormat, secs);
            }
        }
    }
}
