using MoreMountains.Feedbacks;
using NaughtyAttributes;
using System.Collections.Generic;
using UnityEngine;

public class ModularEntityEffectManager : MonoBehaviour, IPooled
{

    public MMF_Player damageFeedback;

    [Tooltip("受擊閃光。用 MMF_Property 動畫 EntityHitFlash.FlashAmount（0 → 1 → 0）。\n" +
             "跟傷害數字分開：傷害數字是用傷害值當 intensity 播的，閃光放進同一個 MMF_Player 會被乘上傷害值。\n" +
             "連續中彈的頻率用這個 MMF_Player 的 Cooldown Duration 限制。")]
    public MMF_Player hitFlashFeedback;

    [Tooltip("攻擊預警（例如頭上亮一下）。由 Tools > Diva Ex Machina > Attack Warning Builder 建立並自動指定。\n" +
             "近戰出招時會自動播放；其他攻擊也可以隨時呼叫 PlayAttackWarning()。")]
    public MMF_Player attackWarningFeedback;

    // 敵人身上所有的粒子系統（含停用中的），重生時用來清掉殘留的特效
    private ParticleSystem[] _particles;

    public void PlayDamageFeedback(float damage)
    {
        damageFeedback.PlayFeedbacks(this.transform.position, RoundForDisplay(damage));

        // 閃光固定用強度 1 播（不能傳傷害值）
        if (hitFlashFeedback != null)
            hitFlashFeedback.PlayFeedbacks(this.transform.position);
    }

    /// <summary>
    /// 播放攻擊預警。intensity 會傳給 MMF_Player（支援強度的 feedback 會跟著放大）。
    /// 沒有設定 attackWarningFeedback 時什麼都不做。
    /// </summary>
    public void PlayAttackWarning(float intensity = 1f)
    {
        if (attackWarningFeedback == null) return;
        attackWarningFeedback.PlayFeedbacks(attackWarningFeedback.transform.position, intensity);
    }

    /// <summary>中止攻擊預警（例如攻擊被打斷時）。</summary>
    public void StopAttackWarning()
    {
        if (attackWarningFeedback == null) return;
        attackWarningFeedback.StopFeedbacks();
    }

    // ═══════════════════════ 物件池 ═══════════════════════

    /// <summary>
    /// 從物件池重生時，停掉並清空上一輪殘留的特效。
    ///
    /// 敵人在出招中（例如爪擊特效播放中）死亡時，物件會被停用；這時候對特效下的 Stop 不會生效
    /// （MMF / ParticleSystem 在停用狀態下停不掉），粒子系統會維持「播放中」的狀態被收進池子，
    /// 重生一啟用就接著播 —— 爪擊特效是循環的，會一直亮著，也一直佔用 ParticleSystem.Update。
    ///
    /// Spawn 會先 SetActive(true) 再呼叫這裡，所以這時物件已經啟用，Stop 會生效；同一幀內處理，畫面上不會閃一下。
    /// 只處理沒勾 Play On Awake 的粒子系統（出招、受擊這類由程式播放的特效）；
    /// 勾了 Play On Awake 的（常駐特效）本來就會在啟用時重新開始，不動它。
    /// </summary>
    public void OnSpawned()
    {
        if (attackWarningFeedback != null) attackWarningFeedback.StopFeedbacks();

        if (_particles == null)
        {
            ModularEntityStats stats = GetComponentInParent<ModularEntityStats>();
            Transform root = stats != null ? stats.transform : transform;
            _particles = root.GetComponentsInChildren<ParticleSystem>(true);
        }

        for (int i = 0; i < _particles.Length; i++)
        {
            ParticleSystem ps = _particles[i];
            if (ps == null || ps.main.playOnAwake) continue;
            if (ps.isPlaying || ps.particleCount > 0)
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        }
    }

    public void OnDespawned() { }

    /// <summary>
    /// 顯示用的傷害數值：四捨五入到整數。
    /// 有造成傷害（> 0）時至少顯示 1，避免被防禦減到很小的傷害顯示成「0」。
    ///
    /// 用 Floor(x + 0.5) 而不是 Mathf.Round：Mathf.Round 遇到 .5 會取偶數（2.5 → 2），
    /// 不是一般認知的四捨五入。
    /// 只影響顯示（Floating Text 吃的 intensity），實際扣血仍然是原本的小數值。
    /// </summary>
    private static float RoundForDisplay(float damage)
    {
        if (damage <= 0f) return 0f;
        return Mathf.Max(1f, Mathf.Floor(damage + 0.5f));
    }
}
