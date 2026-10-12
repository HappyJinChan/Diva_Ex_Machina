using MoreMountains.Feedbacks;
using NaughtyAttributes;
using System.Collections.Generic;
using UnityEngine;

public class ModularEntityEffectManager : MonoBehaviour
{

    public MMF_Player damageFeedback;

    [Tooltip("攻擊預警（例如頭上亮一下）。由 Tools > Diva Ex Machina > Attack Warning Builder 建立並自動指定。\n" +
             "近戰出招時會自動播放；其他攻擊也可以隨時呼叫 PlayAttackWarning()。")]
    public MMF_Player attackWarningFeedback;

    public void PlayDamageFeedback(float damage)
    {
        damageFeedback.PlayFeedbacks(this.transform.position, RoundForDisplay(damage));
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
