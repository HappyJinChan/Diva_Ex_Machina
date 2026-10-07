using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 射擊場的結算踏板（紅色平面）。
///
/// 玩家踏上時，把這一輪的擊殺結算成一筆 RoundSummary 交給 ShootingRangeScoreboard：
///   回合起點 = 這一輪「第一個敵人被打中」的時間
///   每個被擊殺的敵人記下：起點 → 這個敵人被擊殺 花了多久
///
/// 「這一輪」= 上一次結算之後的所有擊殺與首次命中。結算完就清零，下一輪重新開始。
/// 被打中但還沒死的敵人也會參與決定起點；它之後才被打死的話，會算進下一輪。
///
/// 用法：掛在 Plane (2) 上（Box Collider 要勾 Is Trigger），Scoreboard 拖入計分板。
/// </summary>
[RequireComponent(typeof(Collider))]
public class ShootingRangeFinishPad : MonoBehaviour
{
    [Tooltip("計分板。留空會在場景裡自動找。")]
    [SerializeField] private ShootingRangeScoreboard scoreboard;

    [Header("Player Detection")]
    [Tooltip("玩家根物件。進入 Trigger 的 Collider 只要屬於它（或它的子物件）就算玩家。\n" +
             "留空則改用下方的 Tag 判斷。")]
    [SerializeField] private Transform player;

    [Tooltip("Player 欄位留空時使用。")]
    [SerializeField] private string playerTag = "Player";

    [Header("Debug")]
    [Tooltip("沒有任何擊殺可以結算時，在 Console 提示。")]
    [SerializeField] private bool logWhenEmpty = true;

    private void Awake()
    {
        var col = GetComponent<Collider>();
        if (!col.isTrigger)
            Debug.LogWarning($"[ShootingRangeFinishPad] '{name}' 的 Collider 沒有勾 Is Trigger，踏上去不會觸發。", this);
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsPlayer(other)) return;
        FinishRound();
    }

    private bool IsPlayer(Collider other)
    {
        Transform t = (other.attachedRigidbody != null) ? other.attachedRigidbody.transform : other.transform;

        if (player != null)
            return t == player || t.IsChildOf(player);

        return !string.IsNullOrEmpty(playerTag) &&
               (t.CompareTag(playerTag) || other.CompareTag(playerTag));
    }

    /// <summary>結算目前這一輪。回傳 roundId；沒有擊殺可結算時回傳 -1。</summary>
    public int FinishRound()
    {
        if (scoreboard == null)
            scoreboard = FindFirstObjectByType<ShootingRangeScoreboard>();

        if (scoreboard == null)
        {
            Debug.LogWarning("[ShootingRangeFinishPad] 場景裡沒有 ShootingRangeScoreboard。", this);
            return -1;
        }

        List<ShootingRangeScoreboard.KillRecord> kills = scoreboard.GetPendingKills();
        List<ShootingRangeScoreboard.FirstHitEvent> hits = scoreboard.GetPendingFirstHits();

        if (kills.Count == 0)
        {
            if (logWhenEmpty)
                Debug.Log("[ShootingRangeFinishPad] 這一輪還沒有擊殺，不結算。", this);
            return -1;
        }

        // 回合起點：這一輪所有「首次命中」裡最早的一個。
        // 擊殺紀錄自己的 firstHitTime 也一起比 —— 它的首次命中可能已經被上一輪結算掉了。
        float roundStart = float.MaxValue;
        foreach (var h in hits) roundStart = Mathf.Min(roundStart, h.time);
        foreach (var k in kills) roundStart = Mathf.Min(roundStart, k.firstHitTime);

        // 依擊殺順序排
        kills.Sort((a, b) => a.timestamp.CompareTo(b.timestamp));

        var round = new ShootingRangeScoreboard.RoundSummary
        {
            roundStartTime = roundStart,
            killCount = kills.Count,
            // 粗估：首次命中數 − 擊殺數。同一輪裡每個敵人只會有一次首次命中。
            hitButNotKilled = Mathf.Max(0, hits.Count - kills.Count),
        };

        float lastKill = roundStart;
        foreach (var k in kills)
        {
            round.entries.Add(new ShootingRangeScoreboard.RoundEntry
            {
                enemyName = k.enemyName,
                timeFromRoundStart = k.timestamp - roundStart,
                timeToKill = k.timeToKill,
                killTimestamp = k.timestamp,
                targetHP = k.targetHP,
            });
            lastKill = Mathf.Max(lastKill, k.timestamp);
        }

        round.totalTime = lastKill - roundStart;

        // 整輪血量：全部相同就記那個值，不同就記 -1
        round.targetHP = kills[0].targetHP;
        foreach (var k in kills)
        {
            if (!Mathf.Approximately(k.targetHP, round.targetHP)) { round.targetHP = -1f; break; }
        }

        return scoreboard.SubmitRound(round);
    }
}
