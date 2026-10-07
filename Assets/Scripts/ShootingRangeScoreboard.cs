using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 射擊場計分板。記錄兩種資料：
///
///   1. Loadouts：玩家踏上踏板時的武器配置快照（左手、右手、左肩、右肩；
///      只含武器本體與武器零件，不含護甲）。由 ShootingRangeTrigger 呼叫 AddLoadout()。
///   2. Records：每個被摧毀的敵人名稱、從第一次受傷到被摧毀的時間，
///      以及當時使用的配置編號（loadoutId）。由 DamageRecord 呼叫 Report()。
///
/// 兩者用 loadoutId 對應，之後就能拿「哪套武器 → 擊殺時間」做平衡分析。
/// 場景裡放一個就好。
/// </summary>
public class ShootingRangeScoreboard : MonoBehaviour
{
    // ═══════════════ 資料結構 ═══════════════

    [Serializable]
    public struct KillRecord
    {
        public string enemyName;
        public float timeToKill;   // 秒
        public float timestamp;    // 被摧毀時的 Time.time，方便看出順序
        public int loadoutId;      // 擊殺時的武器配置；-1 = 還沒有任何配置紀錄
        public float targetHP;     // 這個敵人的最大血量（ShootingRangeTrigger 的 HP 或 prefab 值）
        public float distance;     // 跟踏板的水平距離（公尺）；-1 = 未知
        public float targetSpeed;  // 靶子左右移動速度（m/s）；0 = 靜止
        public float firstHitTime; // 這個敵人第一次被打中時的 Time.time（= timestamp − timeToKill）
        public int roundId;        // 歸屬的回合；-1 = 還沒被 ShootingRangeFinishPad 結算

        public override string ToString() => $"{enemyName} [HP {targetHP:0}]: {timeToKill:F2}s (loadout #{loadoutId})";
    }

    /// <summary>某個敵人第一次被打中。被打中但沒打死的也會記。</summary>
    [Serializable]
    public struct FirstHitEvent
    {
        public string enemyName;
        public float time;         // Time.time
        public int roundId;        // -1 = 還沒被結算
    }

    /// <summary>一輪結算中，一個被擊殺的敵人。</summary>
    [Serializable]
    public struct RoundEntry
    {
        public string enemyName;
        public float timeFromRoundStart;   // 從「這一輪第一個敵人被打中」到「這個敵人被擊殺」
        public float timeToKill;           // 從「這個敵人自己第一次被打中」到被擊殺（同 KillRecord）
        public float killTimestamp;
        public float targetHP;
    }

    /// <summary>踏上結算踏板時產生的一輪紀錄。</summary>
    [Serializable]
    public class RoundSummary
    {
        public int roundId;
        public int loadoutId;              // 結算時的最新配置
        public float roundStartTime;       // 第一個敵人被打中的 Time.time
        public float totalTime;            // 起點到最後一個擊殺
        public int killCount;
        public int hitButNotKilled;        // 被打中但到結算時還沒死的敵人數
        public float targetHP;             // 這一輪靶子的血量；各靶子不同時為 -1（看 entries 裡各自的值）
        public List<RoundEntry> entries = new List<RoundEntry>();   // 依擊殺順序
    }

    /// <summary>一個屬性的最終值。</summary>
    [Serializable]
    public struct AttributeValue
    {
        public Attributes attribute;
        public float value;
    }

    [Serializable]
    public class WeaponPartSnapshot
    {
        public string partName;
        public WeaponPartType partType;
        public List<EquipmentBuff> buffs = new List<EquipmentBuff>();
    }

    [Serializable]
    public class WeaponSlotSnapshot
    {
        public string slot;          // LeftHand / RightHand / LeftShoulder / RightShoulder
        public string kind;          // Range / Melee / Shoulder / None
        public string weaponName;    // 鍛造後的名稱優先，沒有就用物品名稱
        public string itemName;      // ScriptableObject 上的原始名稱

        public List<EquipmentBuff> weaponBuffs = new List<EquipmentBuff>();
        public List<WeaponPartSnapshot> parts = new List<WeaponPartSnapshot>();

        // 武器本體 + 零件合併後的最終值，算法跟 WeaponStats.GetAttribute 相同：
        // (Σ Add) × Π(1 + Multiplier)。不含護甲，所以不等於實戰中的最終數值。
        public List<AttributeValue> totals = new List<AttributeValue>();

        // ───── 後座力（只有手持遠程武器有；肩武器、近戰、空手都是 0）─────
        // 這一組「含護甲」：recoilControl = baseStats + 頭盔 + 手甲（單持時兩隻手甲都算），
        // 跟實戰中完全相同。其餘是 AttackManager 依它推導出來的值。
        public float recoilControl;            // 最終後座力控制
        public float recoil;                   // 每秒後座力 = recoilPerShooting / timeBetweenShooting × 1000
        public float recoilRatio;              // recoil / recoilControl（0.5 精準、1 可用、2 打不中；超過 2 曲線封頂）
        public float maxDeviation;             // 偏移天花板（度）
        public float deviationDecayPerSecond;  // 停火後每秒恢復幾度

        public bool IsEmpty => kind == "None";

        public bool TryGetTotal(Attributes attr, out float value)
        {
            foreach (var t in totals)
            {
                if (t.attribute == attr) { value = t.value; return true; }
            }
            value = 0f;
            return false;
        }
    }

    [Serializable]
    public class LoadoutSnapshot
    {
        public int id;
        public float timestamp;      // 踏上踏板時的 Time.time
        public List<WeaponSlotSnapshot> slots = new List<WeaponSlotSnapshot>();

        // 用來判斷「跟上一筆一樣」—— 只比對內容，不含 id / timestamp
        [HideInInspector] public string signature;
    }

    // ═══════════════ 資料 ═══════════════

    [Tooltip("武器配置快照，依時間排列。")]
    [SerializeField] private List<LoadoutSnapshot> loadouts = new List<LoadoutSnapshot>();

    [Tooltip("擊殺紀錄，依摧毀順序排列。")]
    [SerializeField] private List<KillRecord> records = new List<KillRecord>();

    [Tooltip("每個敵人第一次被打中的時間。")]
    [SerializeField] private List<FirstHitEvent> firstHits = new List<FirstHitEvent>();

    [Tooltip("結算踏板產生的回合紀錄。")]
    [SerializeField] private List<RoundSummary> rounds = new List<RoundSummary>();

    [Tooltip("踏板連續踩到相同配置時，不新增快照，沿用上一筆。")]
    [SerializeField] private bool skipDuplicateLoadouts = true;

    [Tooltip("每筆紀錄進來時印到 Console。")]
    [SerializeField] private bool logToConsole = true;

    [Header("CSV Export")]
    [Tooltip("匯出資料夾。相對路徑以「專案根目錄」（Assets 的上一層）為基準，\n" +
             "放在 Assets 外面，Unity 才不會把 CSV 當成資產匯入。\n" +
             "Build 版本則以執行檔資料夾為基準。")]
    [SerializeField] private string exportFolder = "ShootingRangeData";

    [Tooltip("停止 Play（或關閉遊戲）時自動匯出一次。")]
    [SerializeField] private bool autoExportOnQuit = true;

    [Tooltip("每一輪結算後自動匯出一次。當機或強制停止時資料也不會全丟。")]
    [SerializeField] private bool autoExportOnRound = true;

    /// <summary>
    /// true = 暫停收紀錄（Report / ReportFirstHit 直接忽略）。
    /// WeaponBalanceBot 執行時會打開，避免機器人的擊殺混進你親手打的資料。
    /// </summary>
    [NonSerialized] public bool paused;

    // 這次 Play 的識別碼，也是檔名前綴。同一次 Play 重複匯出會覆蓋同一組檔案。
    private string _sessionId;
    public string SessionId => _sessionId ??= DateTime.Now.ToString("yyyyMMdd_HHmmss");

    /// <summary>新增一筆擊殺紀錄時觸發。給 UI 用。</summary>
    public event Action<KillRecord> OnRecordAdded;

    /// <summary>新增一筆配置快照時觸發（重複配置被略過時不觸發）。</summary>
    public event Action<LoadoutSnapshot> OnLoadoutAdded;

    public IReadOnlyList<KillRecord> Records => records;
    public IReadOnlyList<LoadoutSnapshot> Loadouts => loadouts;

    /// <summary>目前的配置（最後一筆快照）。沒有時為 null。</summary>
    public LoadoutSnapshot CurrentLoadout => (loadouts.Count > 0) ? loadouts[loadouts.Count - 1] : null;

    // ═══════════════ 配置 ═══════════════

    /// <summary>
    /// 新增一筆配置快照，回傳它的 id。
    /// skipDuplicateLoadouts 開啟且內容跟上一筆相同時，不新增，回傳上一筆的 id。
    /// </summary>
    public int AddLoadout(LoadoutSnapshot loadout)
    {
        if (loadout == null) return -1;

        loadout.signature = BuildSignature(loadout);

        var last = CurrentLoadout;
        if (skipDuplicateLoadouts && last != null && last.signature == loadout.signature)
            return last.id;

        loadout.id = loadouts.Count;
        loadouts.Add(loadout);

        if (logToConsole)
            Debug.Log($"[Scoreboard] 新的武器配置\n{Describe(loadout)}", this);

        OnLoadoutAdded?.Invoke(loadout);
        return loadout.id;
    }

    public LoadoutSnapshot GetLoadout(int id)
    {
        return (id >= 0 && id < loadouts.Count) ? loadouts[id] : null;
    }

    // ═══════════════ 擊殺 ═══════════════

    /// <summary>由 DamageRecord 呼叫。擊殺歸屬於「當下最新的配置」。</summary>
    public void Report(string enemyName, float timeToKill) => Report(enemyName, timeToKill, 0f, -1f);

    public void Report(string enemyName, float timeToKill, float targetHP) => Report(enemyName, timeToKill, targetHP, -1f);

    /// <summary>由 DamageRecord 呼叫。擊殺歸屬於「當下最新的配置」。targetHP = 敵人的最大血量。</summary>
    public void Report(string enemyName, float timeToKill, float targetHP, float distance)
        => Report(enemyName, timeToKill, targetHP, distance, 0f);

    public void Report(string enemyName, float timeToKill, float targetHP, float distance, float targetSpeed)
    {
        if (paused) return;

        var current = CurrentLoadout;

        var record = new KillRecord
        {
            enemyName = enemyName,
            timeToKill = timeToKill,
            targetHP = targetHP,
            distance = distance,
            targetSpeed = targetSpeed,
            timestamp = Time.time,
            loadoutId = (current != null) ? current.id : -1,
            firstHitTime = Time.time - timeToKill,
            roundId = -1,
        };

        records.Add(record);

        if (logToConsole)
            Debug.Log($"[Scoreboard] {record}", this);

        OnRecordAdded?.Invoke(record);
    }

    /// <summary>由 DamageRecord 在敵人第一次受傷時呼叫。</summary>
    public void ReportFirstHit(string enemyName, float time)
    {
        if (paused) return;

        firstHits.Add(new FirstHitEvent { enemyName = enemyName, time = time, roundId = -1 });
    }

    // ═══════════════ 回合 ═══════════════

    public IReadOnlyList<FirstHitEvent> FirstHits => firstHits;
    public IReadOnlyList<RoundSummary> Rounds => rounds;

    /// <summary>還沒被結算的擊殺紀錄。</summary>
    public List<KillRecord> GetPendingKills() => records.FindAll(r => r.roundId < 0);

    /// <summary>還沒被結算的首次命中事件。</summary>
    public List<FirstHitEvent> GetPendingFirstHits() => firstHits.FindAll(h => h.roundId < 0);

    /// <summary>
    /// 收下一輪結算結果（由 ShootingRangeFinishPad 計算），回傳 roundId。
    /// 目前所有未結算的擊殺與首次命中都會被標記成這一輪，下一輪從零開始。
    /// </summary>
    public int SubmitRound(RoundSummary round)
    {
        if (round == null) return -1;

        round.roundId = rounds.Count;
        var current = CurrentLoadout;
        round.loadoutId = (current != null) ? current.id : -1;
        rounds.Add(round);

        for (int i = 0; i < records.Count; i++)
        {
            if (records[i].roundId >= 0) continue;
            var r = records[i];
            r.roundId = round.roundId;
            records[i] = r;
        }

        for (int i = 0; i < firstHits.Count; i++)
        {
            if (firstHits[i].roundId >= 0) continue;
            var h = firstHits[i];
            h.roundId = round.roundId;
            firstHits[i] = h;
        }

        if (logToConsole)
            Debug.Log($"[Scoreboard] {Describe(round)}", this);

        OnRoundSubmitted?.Invoke(round);

        if (autoExportOnRound) ExportCsv();
        return round.roundId;
    }

    /// <summary>新增一輪結算時觸發。</summary>
    public event Action<RoundSummary> OnRoundSubmitted;

    public static string Describe(RoundSummary round)
    {
        var sb = new StringBuilder(
            $"Round #{round.roundId}（loadout #{round.loadoutId}，HP {(round.targetHP >= 0f ? round.targetHP.ToString("0") : "混合")}）擊殺 {round.killCount}，" +
            $"總時間 {round.totalTime:F2}s，被打中但沒死 {round.hitButNotKilled}\n");

        foreach (var e in round.entries)
            sb.AppendLine($"  {e.enemyName} [HP {e.targetHP:0}]: 從第一擊起 {e.timeFromRoundStart:F2}s（自身 {e.timeToKill:F2}s）");

        return sb.ToString();
    }

    [ContextMenu("Print Rounds")]
    public void PrintRounds()
    {
        var sb = new StringBuilder($"[Scoreboard] {rounds.Count} 輪\n");
        foreach (var r in rounds) sb.AppendLine(Describe(r));
        Debug.Log(sb.ToString(), this);
    }

    // ═══════════════ 查詢 ═══════════════

    /// <summary>某個名稱的所有紀錄。</summary>
    public List<KillRecord> GetRecords(string enemyName)
    {
        return records.FindAll(r => r.enemyName == enemyName);
    }

    /// <summary>某套配置的所有紀錄。</summary>
    public List<KillRecord> GetRecords(int loadoutId)
    {
        return records.FindAll(r => r.loadoutId == loadoutId);
    }

    /// <summary>某個名稱的最快時間。沒有紀錄時回傳 false。</summary>
    public bool TryGetBestTime(string enemyName, out float best)
    {
        best = float.MaxValue;
        bool found = false;

        foreach (var r in records)
        {
            if (r.enemyName != enemyName) continue;
            if (r.timeToKill < best) best = r.timeToKill;
            found = true;
        }

        if (!found) best = 0f;
        return found;
    }

    /// <summary>某個名稱的平均時間。沒有紀錄時回傳 false。</summary>
    public bool TryGetAverageTime(string enemyName, out float average)
    {
        float sum = 0f;
        int count = 0;

        foreach (var r in records)
        {
            if (r.enemyName != enemyName) continue;
            sum += r.timeToKill;
            count++;
        }

        average = (count > 0) ? sum / count : 0f;
        return count > 0;
    }

    // ═══════════════ 工具 ═══════════════

    // ═══════════════ CSV ═══════════════

    /// <summary>匯出資料夾的完整路徑。</summary>
    public string ExportFolderFullPath =>
        Path.IsPathRooted(exportFolder)
            ? exportFolder
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", exportFolder));

    /// <summary>把目前所有資料匯出成 CSV。在元件的 ⋮ 選單選「Export CSV」也可以手動匯出。</summary>
    [ContextMenu("Export CSV")]
    public void ExportCsv()
    {
        if (records.Count == 0 && loadouts.Count == 0 && rounds.Count == 0)
        {
            if (logToConsole) Debug.Log("[Scoreboard] 沒有資料，不匯出。", this);
            return;
        }

        try
        {
            var files = ShootingRangeCsvExporter.Export(this, ExportFolderFullPath, SessionId);
            if (logToConsole)
                Debug.Log($"[Scoreboard] 已匯出 CSV（{records.Count} 筆擊殺、{loadouts.Count} 筆配置、{rounds.Count} 輪）\n" +
                          string.Join("\n", files), this);
        }
        catch (Exception e)
        {
            // 檔案被 Excel 開著時會寫入失敗 —— 只警告，不要讓遊戲中斷
            Debug.LogWarning($"[Scoreboard] CSV 匯出失敗：{e.Message}", this);
        }
    }

    private void OnApplicationQuit()
    {
        if (autoExportOnQuit) ExportCsv();
    }

    [ContextMenu("Clear Records")]
    public void Clear()
    {
        records.Clear();
        firstHits.Clear();
        rounds.Clear();
    }

    [ContextMenu("Clear All (Records + Loadouts)")]
    public void ClearAll()
    {
        Clear();
        loadouts.Clear();
    }

    /// <summary>在元件的 ⋮ 選單選「Print Records」，把所有紀錄印到 Console。</summary>
    [ContextMenu("Print Records")]
    public void PrintRecords()
    {
        var sb = new StringBuilder($"[Scoreboard] {records.Count} 筆擊殺紀錄\n");
        for (int i = 0; i < records.Count; i++)
            sb.AppendLine($"{i + 1}. {records[i]}");

        Debug.Log(sb.ToString(), this);
    }

    [ContextMenu("Print Loadouts")]
    public void PrintLoadouts()
    {
        var sb = new StringBuilder($"[Scoreboard] {loadouts.Count} 筆武器配置\n");
        foreach (var l in loadouts)
            sb.AppendLine(Describe(l));

        Debug.Log(sb.ToString(), this);
    }

    public static string Describe(LoadoutSnapshot loadout)
    {
        var sb = new StringBuilder($"Loadout #{loadout.id} @ {loadout.timestamp:F1}s\n");

        foreach (var s in loadout.slots)
        {
            if (s.IsEmpty)
            {
                sb.AppendLine($"  {s.slot}: (空)");
                continue;
            }

            sb.AppendLine($"  {s.slot}: {s.weaponName} [{s.kind}]");

            foreach (var p in s.parts)
                sb.AppendLine($"    + {p.partType}: {p.partName}");

            foreach (var t in s.totals)
                sb.AppendLine($"      {t.attribute} = {t.value:0.###}");

            if (s.recoilControl > 0f)
                sb.AppendLine($"      後座力 {s.recoil:0} / 控制 {s.recoilControl:0} = ratio {s.recoilRatio:0.##}，" +
                              $"天花板 {s.maxDeviation:0.##}°，恢復 {s.deviationDecayPerSecond:0.##}°/s");
        }

        return sb.ToString();
    }

    private static string BuildSignature(LoadoutSnapshot loadout)
    {
        var sb = new StringBuilder();

        foreach (var s in loadout.slots)
        {
            sb.Append(s.slot).Append('|').Append(s.kind).Append('|').Append(s.weaponName).Append('|');
            // 換護甲只會改到後座力控制，所以它也要進簽章，否則換頭盔不會產生新的配置
            sb.Append(s.recoilControl).Append('|');
            AppendBuffs(sb, s.weaponBuffs);

            foreach (var p in s.parts)
            {
                sb.Append('{').Append(p.partType).Append(':').Append(p.partName).Append(':');
                AppendBuffs(sb, p.buffs);
                sb.Append('}');
            }
            sb.Append(';');
        }

        return sb.ToString();
    }

    private static void AppendBuffs(StringBuilder sb, List<EquipmentBuff> buffs)
    {
        if (buffs == null) return;
        foreach (var b in buffs)
            sb.Append((int)b.attribute).Append(',').Append((int)b.mode).Append(',').Append(b.value).Append(' ');
    }
}
