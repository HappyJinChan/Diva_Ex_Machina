using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 把 ShootingRangeScoreboard 的資料匯出成 CSV，給 pandas 分析用。
///
/// 每次匯出寫三個檔案（同一個 session 重複匯出會覆蓋，內容永遠是完整快照）：
///
///   {session}_kills.csv     一列 = 一次擊殺（主表）
///   {session}_loadouts.csv  一列 = 一個配置 × 一個槽位（左手 / 右手 / 左肩 / 右肩）
///   {session}_rounds.csv    一列 = 一輪結算
///
/// 每個檔案都有 session_id 欄位，所以可以把很多次 Play 的檔案直接 concat：
///   kills = pd.concat(pd.read_csv(f) for f in glob("ShootingRangeData/*_kills.csv"))
/// loadout_id / round_id 只在同一個 session 內唯一，join 時要連同 session_id 一起當 key。
///
/// 格式：UTF-8（無 BOM）、逗號分隔、小數點固定用「.」（InvariantCulture）、
/// 缺值是空字串（pandas 讀成 NaN）。
/// </summary>
public static class ShootingRangeCsvExporter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // 屬性欄位固定列出 Attributes 的全部成員 —— 每次匯出的欄位都一樣，
    // 不會因為這次剛好沒裝某種零件就少一欄，concat 多個檔案時才不會對不齊。
    private static readonly Attributes[] AllAttributes = (Attributes[])Enum.GetValues(typeof(Attributes));

    /// <summary>匯出到 folder，回傳寫出的三個檔案路徑。</summary>
    public static string[] Export(ShootingRangeScoreboard board, string folder, string sessionId)
    {
        Directory.CreateDirectory(folder);

        string kills = Path.Combine(folder, $"{sessionId}_kills.csv");
        string loadouts = Path.Combine(folder, $"{sessionId}_loadouts.csv");
        string rounds = Path.Combine(folder, $"{sessionId}_rounds.csv");

        var utf8NoBom = new UTF8Encoding(false);
        File.WriteAllText(kills, BuildKills(board, sessionId), utf8NoBom);
        File.WriteAllText(loadouts, BuildLoadouts(board, sessionId), utf8NoBom);
        File.WriteAllText(rounds, BuildRounds(board, sessionId), utf8NoBom);

        return new[] { kills, loadouts, rounds };
    }

    // ═══════════════ kills.csv ═══════════════

    private static string BuildKills(ShootingRangeScoreboard board, string session)
    {
        var sb = new StringBuilder();
        Row(sb, "session_id", "kill_index", "round_id", "loadout_id", "weapon_combo",
                "enemy_name", "distance", "target_hp", "target_speed",
                "time_to_kill", "time_from_round_start", "interval_since_prev_kill",
                "first_hit_time", "kill_timestamp");

        var records = board.Records;

        // 每輪內依擊殺時間排序，算出「距上一次擊殺的間隔」。
        // 這個指標不受流彈影響（見對話中的分析），通常比 time_to_kill 可靠。
        var interval = new Dictionary<int, float>();   // record index → interval
        var byRound = new Dictionary<int, List<int>>();
        for (int i = 0; i < records.Count; i++)
        {
            int r = records[i].roundId;
            if (r < 0) continue;
            if (!byRound.TryGetValue(r, out var list)) byRound[r] = list = new List<int>();
            list.Add(i);
        }
        foreach (var kv in byRound)
        {
            var round = GetRound(board, kv.Key);
            if (round == null) continue;

            kv.Value.Sort((a, b) => records[a].timestamp.CompareTo(records[b].timestamp));
            float prev = round.roundStartTime;
            foreach (int i in kv.Value)
            {
                interval[i] = records[i].timestamp - prev;
                prev = records[i].timestamp;
            }
        }

        for (int i = 0; i < records.Count; i++)
        {
            var k = records[i];
            var round = (k.roundId >= 0) ? GetRound(board, k.roundId) : null;

            Row(sb,
                session,
                I(i),
                I(k.roundId),
                I(k.loadoutId),
                WeaponCombo(board.GetLoadout(k.loadoutId)),
                k.enemyName,
                k.distance >= 0f ? F(k.distance) : "",
                F(k.targetHP),
                F(k.targetSpeed),
                F(k.timeToKill),
                round != null ? F(k.timestamp - round.roundStartTime) : "",
                interval.TryGetValue(i, out float iv) ? F(iv) : "",
                F(k.firstHitTime),
                F(k.timestamp));
        }

        return sb.ToString();
    }

    // ═══════════════ loadouts.csv ═══════════════

    private static string BuildLoadouts(ShootingRangeScoreboard board, string session)
    {
        var sb = new StringBuilder();

        var header = new List<string>
        {
            "session_id", "loadout_id", "timestamp", "slot", "kind", "weapon_name", "item_name",
            "part_count", "parts",
            "recoil_control", "recoil", "recoil_ratio", "max_deviation", "deviation_decay_per_second",
        };
        foreach (var a in AllAttributes) header.Add("attr_" + a);
        Row(sb, header.ToArray());

        foreach (var l in board.Loadouts)
        {
            foreach (var s in l.slots)
            {
                var row = new List<string>
                {
                    session, I(l.id), F(l.timestamp), s.slot, s.kind,
                    s.IsEmpty ? "" : s.weaponName,
                    s.IsEmpty ? "" : s.itemName,
                    I(s.parts.Count),
                    PartsText(s),
                    s.recoilControl > 0f ? F(s.recoilControl) : "",
                    s.recoilControl > 0f ? F(s.recoil) : "",
                    s.recoilControl > 0f ? F(s.recoilRatio) : "",
                    s.recoilControl > 0f ? F(s.maxDeviation) : "",
                    s.recoilControl > 0f ? F(s.deviationDecayPerSecond) : "",
                };

                // 屬性：武器 + 零件合併後的最終值（不含護甲）。沒有這個屬性就留空。
                foreach (var a in AllAttributes)
                    row.Add(s.TryGetTotal(a, out float v) ? F(v) : "");

                Row(sb, row.ToArray());
            }
        }

        return sb.ToString();
    }

    // ═══════════════ rounds.csv ═══════════════

    private static string BuildRounds(ShootingRangeScoreboard board, string session)
    {
        var sb = new StringBuilder();
        Row(sb, "session_id", "round_id", "loadout_id", "weapon_combo", "target_hp",
                "round_start_time", "total_time", "kill_count", "hit_but_not_killed");

        foreach (var r in board.Rounds)
        {
            Row(sb,
                session,
                I(r.roundId),
                I(r.loadoutId),
                WeaponCombo(board.GetLoadout(r.loadoutId)),
                r.targetHP >= 0f ? F(r.targetHP) : "",   // 混合血量 → 空（看 kills.csv 的 target_hp）
                F(r.roundStartTime),
                F(r.totalTime),
                I(r.killCount),
                I(r.hitButNotKilled));
        }

        return sb.ToString();
    }

    // ═══════════════ 工具 ═══════════════

    private static ShootingRangeScoreboard.RoundSummary GetRound(ShootingRangeScoreboard board, int id)
    {
        var rounds = board.Rounds;
        return (id >= 0 && id < rounds.Count) ? rounds[id] : null;
    }

    /// <summary>
    /// 一眼看得出配置的短字串，例如 "L:SMG|R:SMG|LS:-|RS:Cannon"。
    /// 方便 groupby，不必先 join loadouts.csv。
    /// </summary>
    private static string WeaponCombo(ShootingRangeScoreboard.LoadoutSnapshot l)
    {
        if (l == null) return "";

        var parts = new List<string>();
        foreach (var s in l.slots)
        {
            string tag = s.slot switch
            {
                "LeftHand" => "L",
                "RightHand" => "R",
                "LeftShoulder" => "LS",
                "RightShoulder" => "RS",
                _ => s.slot,
            };
            parts.Add($"{tag}:{(s.IsEmpty ? "-" : s.weaponName)}");
        }
        return string.Join("|", parts);
    }

    // 零件清單："Barrel=SMG Barrel;Scope=Iron Sight"
    private static string PartsText(ShootingRangeScoreboard.WeaponSlotSnapshot s)
    {
        var list = new List<string>();
        foreach (var p in s.parts) list.Add($"{p.partType}={p.partName}");
        return string.Join(";", list);
    }

    private static string F(float v) => v.ToString("0.######", Inv);
    private static string I(int v) => v.ToString(Inv);

    private static void Row(StringBuilder sb, params string[] cells)
    {
        for (int i = 0; i < cells.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Escape(cells[i]));
        }
        sb.Append('\n');
    }

    // RFC 4180：含逗號、引號、換行時整格加引號，引號重複一次
    private static string Escape(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
