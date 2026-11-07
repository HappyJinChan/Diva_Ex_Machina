using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

public class TooltipManager : MonoBehaviour
{
    public static TooltipManager Instance { get; private set; }

    public GameObject tooltipRoot;
    public TextMeshProUGUI equipmentText;
    public TextMeshProUGUI statText;
    public RectTransform tooltipPanel;
    public Vector2 offset = new Vector2(20f, -20f);

    [Header("Damage Colors")]
    [Tooltip("四種傷害類型的顏色。傷害行不寫類型名稱，靠顏色區分。")]
    public Color physicalColor = new Color(1f, 1f, 1f);
    public Color explosionColor = new Color(1f, 0.55f, 0.2f);
    public Color energyColor = new Color(0.35f, 0.8f, 1f);
    public Color coldColor = new Color(0.6f, 0.85f, 1f);

    // Hover 只記錄「游標下是什麼」，不直接顯示 —— 顯示與關閉都由右鍵控制。
    private ItemInstance _hoveredItem;

    // 目前面板上顯示的是哪一個。用來判斷右鍵該「切換物品」還是「關閉」。
    private ItemInstance _shownItem;

    // 合併後的屬性值。跟 PlayerStats.ApplyBuffListToValue 同邏輯：
    // 先把所有 Add 加總，再乘上所有 Multiplier。
    private struct StatValue
    {
        public float add;
        public float mul;
    }

    private void Awake()
    {
        Instance = this;
        HideTooltipNow();
    }

    private void Update()
    {
        if (!Input.GetMouseButtonDown(1)) return;

        bool visible = tooltipRoot != null && tooltipRoot.activeSelf;

        // 開著時若游標已經移到另一個物品，右鍵直接換成那個 —— 不用先關再開
        if (visible && _hoveredItem != null && _hoveredItem != _shownItem)
            ShowTooltipNow(_hoveredItem);
        else if (visible)
            HideTooltipNow();
        else if (_hoveredItem != null)
            ShowTooltipNow(_hoveredItem);
    }

    /// <summary>
    /// 由 UI slot 的 hover handler 呼叫。只記錄游標下的物品，不顯示面板。
    /// </summary>
    public void ShowTooltip(ItemInstance itemInstance, RectTransform sourceRect = null)
    {
        _hoveredItem = (itemInstance != null && itemInstance.item != null) ? itemInstance : null;
    }

    /// <summary>
    /// 游標離開 slot。只清除記錄 —— 已經用右鍵釘住的面板不該因為滑鼠移開就消失。
    /// </summary>
    public void HideTooltip()
    {
        _hoveredItem = null;
    }

    private void ShowTooltipNow(ItemInstance itemInstance)
    {
        if (itemInstance == null || itemInstance.item == null)
        {
            HideTooltipNow();
            return;
        }

        _shownItem = itemInstance;

        tooltipRoot.SetActive(true);
        equipmentText.text = GetDisplayName(itemInstance);
        statText.text = BuildStatText(itemInstance);

        // 只在開啟的當下定位一次。面板是釘住的，不跟隨滑鼠。
        UpdateTooltipPosition();
    }

    private void HideTooltipNow()
    {
        _shownItem = null;

        if (tooltipRoot != null)
            tooltipRoot.SetActive(false);
    }

    // 固定往左下展開。
    //
    // pivot.x = 1 表示 position 對應到面板的右緣，所以面板整個長在游標左側 ——
    // 不需要事先知道面板寬度。面板高度會隨屬性數量變動，用 pivot 就不必每幀去量它。
    private void UpdateTooltipPosition()
    {
        if (tooltipPanel == null) return;

        tooltipPanel.pivot = new Vector2(1f, 1f);

        // offset 取絕對值再固定為負，方向由這裡決定 ——
        // Inspector 上的 offset 填正填負都不影響結果。
        Vector2 finalOffset = new Vector2(
            -Mathf.Abs(offset.x),
            -Mathf.Abs(offset.y));

        tooltipPanel.position = (Vector2)Input.mousePosition + finalOffset;
    }

    private string GetDisplayName(ItemInstance itemInstance)
    {
        if (itemInstance is RangeWeaponInstance rwi && !string.IsNullOrEmpty(rwi.newWeaponName))
            return rwi.newWeaponName;

        if (itemInstance is MeleeWeaponInstance mwi && !string.IsNullOrEmpty(mwi.newWeaponName))
            return mwi.newWeaponName;

        if (itemInstance is ShoulderWeaponInstance swi && !string.IsNullOrEmpty(swi.newWeaponName))
            return swi.newWeaponName;

        return itemInstance.item.itemName;
    }

    // ────────────────────────────────────────────────
    //  顯示文字
    // ────────────────────────────────────────────────

    private string BuildStatText(ItemInstance itemInstance)
    {
        // 武器：本體與所有零件的 buff 合併成單一份最終值，不分區列出零件。
        // 玩家要看的是「這把槍現在多強」，不是「哪個零件貢獻了多少」。
        if (itemInstance is RangeWeaponInstance rwi)
            return BuildWeaponStatText(MergeBuffs(rwi.buffs, CollectPartBuffs(rwi.attachment)));

        if (itemInstance is MeleeWeaponInstance mwi)
            return BuildWeaponStatText(MergeBuffs(mwi.buffs, CollectPartBuffs(mwi.attachment)));

        if (itemInstance is ShoulderWeaponInstance swi)
            return BuildWeaponStatText(MergeBuffs(swi.buffs, CollectPartBuffs(swi.attachment)));

        // 護甲 / 零件：本身就是「加成」而非最終值，所以保留 +/- 號原樣列出。
        if (itemInstance is ArmorInstance ai)
            return BuildBuffListText(ai.buffs);

        if (itemInstance is PartInstance pi)
            return BuildBuffListText(pi.buffs);

        return "No stat data";
    }

    private List<EquipmentBuff> CollectPartBuffs(List<PartInstance> attachment)
    {
        var result = new List<EquipmentBuff>();
        if (attachment == null) return result;

        foreach (var part in attachment)
        {
            if (part == null || part.item == null || part.buffs == null) continue;
            result.AddRange(part.buffs);
        }

        return result;
    }

    // 把多份 buff 清單合併成「屬性 → 最終值」。
    // Add 先全部加總，再乘上所有 Multiplier —— 跟 PlayerStats 的實際運算一致。
    private Dictionary<Attributes, float> MergeBuffs(params List<EquipmentBuff>[] lists)
    {
        var agg = new Dictionary<Attributes, StatValue>();

        foreach (var list in lists)
        {
            if (list == null) continue;

            foreach (var buff in list)
            {
                if (!agg.TryGetValue(buff.attribute, out var v))
                    v = new StatValue { add = 0f, mul = 1f };

                if (buff.mode == BuffApplyMode.Multiplier)
                    v.mul *= (1f + buff.value);
                else
                    v.add += buff.value;

                agg[buff.attribute] = v;
            }
        }

        var final = new Dictionary<Attributes, float>();
        foreach (var kv in agg)
            final[kv.Key] = kv.Value.add * kv.Value.mul;

        return final;
    }

    private string BuildWeaponStatText(Dictionary<Attributes, float> stats)
    {
        if (stats.Count == 0) return "No stat data";

        var sb = new StringBuilder();

        AppendDamageLine(sb, stats);

        // 射擊相關
        AppendFiringMode(sb, stats);
        AppendFireRate(sb, stats);
        AppendStat(sb, stats, Attributes.MagazineSize, "Magazine Size");
        AppendStat(sb, stats, Attributes.ReloadTime, "Reload Time");
        AppendStat(sb, stats, Attributes.BulletSpeed, "Bullet Speed");
        AppendStat(sb, stats, Attributes.Spread, "Spread");
        AppendStat(sb, stats, Attributes.RecoilPerShooting, "Recoil");

        // 近戰
        AppendStat(sb, stats, Attributes.MeleeOutput, "Melee Output");
        AppendStat(sb, stats, Attributes.MeleeSpeed, "Melee Speed");
        AppendStat(sb, stats, Attributes.MeleeReloadTime, "Melee Cooldown");
        AppendStat(sb, stats, Attributes.MeleeDashDistance, "Melee Dash Distance");

        // 其餘還沒被上面消化掉的屬性，原樣列出 ——
        // 之後新增屬性時不會悄悄從 tooltip 消失。
        AppendRemaining(sb, stats);

        return sb.ToString();
    }

    // 傷害行：四種類型相加，靠顏色區分而不寫類型名稱。
    // BulletPerShot / RoundPerPull 只在大於 1 時顯示成乘數。
    //
    //   Damage: 20 + 20  x3 x10
    //
    // 這六個屬性在這裡消化掉，不會再單獨出現在下面的列表中。
    private void AppendDamageLine(StringBuilder sb, Dictionary<Attributes, float> stats)
    {
        var parts = new List<string>();

        TryAppendDamagePart(parts, stats, Attributes.PhysicalDamage, physicalColor);
        TryAppendDamagePart(parts, stats, Attributes.ExplosionDamage, explosionColor);
        TryAppendDamagePart(parts, stats, Attributes.EnergyDamage, energyColor);
        TryAppendDamagePart(parts, stats, Attributes.ColdDamage, coldColor);

        stats.Remove(Attributes.PhysicalDamage);
        stats.Remove(Attributes.ExplosionDamage);
        stats.Remove(Attributes.EnergyDamage);
        stats.Remove(Attributes.ColdDamage);

        float pellets = Take(stats, Attributes.BulletPerShot);
        float burst = Take(stats, Attributes.RoundPerPull);

        if (parts.Count == 0) return;

        sb.Append("Damage: ").Append(string.Join(" + ", parts));

        if (pellets > 1f) sb.Append($"  x{pellets:0.##}");
        if (burst > 1f) sb.Append($" x{burst:0.##}");

        sb.AppendLine();
        sb.AppendLine();
    }

    private void TryAppendDamagePart(List<string> parts, Dictionary<Attributes, float> stats,
                                     Attributes attr, Color color)
    {
        if (!stats.TryGetValue(attr, out float v)) return;
        if (Mathf.Approximately(v, 0f)) return;

        parts.Add($"<color=#{ColorUtility.ToHtmlStringRGB(color)}>{v:0.##}</color>");
    }

    // Attributes.FiringMode 的 0 / 1 / 2。
    //
    // 注意：這跟 PlayerStats 裡那個同名的 enum FiringMode { Salvo, ShootingInTurn }
    // 是兩回事，不能直接 cast —— 那個是肩武器的齊射模式。
    private void AppendFiringMode(StringBuilder sb, Dictionary<Attributes, float> stats)
    {
        if (!stats.TryGetValue(Attributes.FiringMode, out float v)) return;
        stats.Remove(Attributes.FiringMode);

        string label = Mathf.RoundToInt(v) switch
        {
            0 => "Single",
            1 => "Auto",
            2 => "Charge",
            _ => "Unknown",
        };

        sb.AppendLine($"Fire Mode: {label}");
    }

    // TimeBetweenShooting 是「兩次扣扳機的間隔」，對玩家不直觀 ——
    // 改顯示成每秒發數。TimeBetweenShots（連發內的間隔）直接隱藏。
    private void AppendFireRate(StringBuilder sb, Dictionary<Attributes, float> stats)
    {
        float interval = Take(stats, Attributes.TimeBetweenShooting);
        Take(stats, Attributes.TimeBetweenShots);   // 消化掉，不顯示

        if (interval <= 0f) return;

        sb.AppendLine($"Fire Rate: {(1f / interval):0.#}/s");
    }

    private void AppendStat(StringBuilder sb, Dictionary<Attributes, float> stats,
                            Attributes attr, string label)
    {
        if (!stats.TryGetValue(attr, out float v)) return;
        stats.Remove(attr);

        if (Mathf.Approximately(v, 0f)) return;

        sb.AppendLine($"{label}: {v:0.##}");
    }

    private void AppendRemaining(StringBuilder sb, Dictionary<Attributes, float> stats)
    {
        foreach (var kv in stats)
        {
            if (IsRetired(kv.Key)) continue;
            if (Mathf.Approximately(kv.Value, 0f)) continue;
            sb.AppendLine($"{kv.Key}: {kv.Value:0.##}");
        }
    }

    // 已停用的屬性：舊裝備資料上可能還留著，但不再有任何效果，不顯示以免誤導玩家。
    private static bool IsRetired(Attributes attr)
    {
        return false;   // MeleeDashDistance re-enabled (two-stage melee max dash distance)
    }

    private float Take(Dictionary<Attributes, float> stats, Attributes attr)
    {
        if (!stats.TryGetValue(attr, out float v)) return 0f;
        stats.Remove(attr);
        return v;
    }

    // 護甲 / 零件用：它們的數值本來就是「加成」，保留 +/- 號比較好懂。
    private string BuildBuffListText(List<EquipmentBuff> buffs)
    {
        if (buffs == null || buffs.Count == 0) return "No buffs";

        var sb = new StringBuilder();

        foreach (var buff in buffs)
        {
            if (IsRetired(buff.attribute)) continue;

            string sign = buff.mode == BuffApplyMode.Multiplier
                ? $"x{1f + buff.value:0.##}"
                : $"{(buff.value >= 0 ? "+" : "")}{buff.value:0.##}";

            sb.AppendLine($"{buff.attribute}: {sign}");
        }

        return sb.ToString();
    }
}