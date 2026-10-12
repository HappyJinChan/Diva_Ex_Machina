using UnityEngine;

/// <summary>
/// 把攻擊動畫的 Animation Event 轉給 ModularEntityMeleeManager。
///
/// Animation Event 只能呼叫「Animator 所在物件」上的函式，而 MeleeManager 掛在實體的根物件上，
/// 所以在 Animator 物件（獵犬是 Wolf Mesh）掛上這個元件當轉接。
///
/// 在攻擊動畫裡加的事件（Function 欄位填下面的名稱，不需要參數）：
///   MeleeDashStart  開始衝刺 / 前進（之前是預備階段：減速、轉向）
///   MeleeDashEnd    停止推進，進入收招
///   MeleeHitOpen    開啟判定
///   MeleeHitClose   關閉判定
///   MeleeEnd        攻擊結束，交還控制權給 Brain
///   MeleeWarning    播放攻擊預警（招式的 Show Warning 關掉時，用這個指定亮起的幀）
/// </summary>
public class EntityMeleeAnimationRelay : MonoBehaviour
{
    [Tooltip("留空會往父物件找。")]
    [SerializeField] private ModularEntityMeleeManager melee;

    private void Awake()
    {
        if (melee == null) melee = GetComponentInParent<ModularEntityMeleeManager>();
        if (melee == null)
        {
            var brain = GetComponentInParent<ModularEntityBrain>();
            if (brain != null) melee = brain.GetComponentInChildren<ModularEntityMeleeManager>();
        }
    }

    public void MeleeDashStart() { if (melee != null) melee.OnMeleeDashStart(); }
    public void MeleeDashEnd()   { if (melee != null) melee.OnMeleeDashEnd(); }
    public void MeleeHitOpen()   { if (melee != null) melee.OnMeleeHitOpen(); }
    public void MeleeHitClose()  { if (melee != null) melee.OnMeleeHitClose(); }
    public void MeleeEnd()       { if (melee != null) melee.OnMeleeEnd(); }
    public void MeleeWarning()   { if (melee != null) melee.OnMeleeWarning(); }
}
