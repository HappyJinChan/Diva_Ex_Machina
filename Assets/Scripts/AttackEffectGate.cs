using UnityEngine;

/// <summary>
/// 只在攻擊期間播放特效（粒子 + BodySpaceTrail）。掛在特效的根物件上。
///
/// Trigger：
///   Assault Kick    - 突擊推進踢期間（PlayerMovement.IsAssaultKicking）。例：腿上的 FX_SwordT_CircleStar。
///   Melee Attacking - 近戰攻擊的整段（從出招到 StepEnd，含舉刀、收招）。
///   Melee Hitbox    - 近戰 hitbox 開著的期間（AnimEvent_MeleeHitboxOn → Off）。刀光通常用這個：只有真的在砍的那一下才亮。
///
/// 兩隻手各拿一把近戰武器時：Melee 兩種模式只在「這把武器」揮擊時才亮。
/// 判斷方式是 Hitbox 欄位（留空會在父物件 / 子物件上找武器的 MeleeHitbox）。
///
///   開始：這個物件底下所有的粒子系統 Play，BodySpaceTrail Play。
///   結束：粒子只停止發射，已經噴出的照壽命自然消失；BodySpaceTrail Stop（拖尾照壽命淡出）。
///
/// 開場會先把粒子停掉並清空，所以粒子系統的 Play On Awake、Looping 勾著也沒關係
/// （Looping 要勾著，攻擊比 Duration 長時才不會中途停掉）。
/// BodySpaceTrail 的 Min Emit Speed 建議設成 0，改由這裡控制。
/// </summary>
[DisallowMultipleComponent]
public class AttackEffectGate : MonoBehaviour
{
    public enum Trigger { AssaultKick, MeleeAttacking, MeleeHitbox }

    [Tooltip("什麼時候播放。")]
    [SerializeField] private Trigger trigger = Trigger.AssaultKick;

    [Tooltip("Assault Kick 用。留空會往父物件找。")]
    [SerializeField] private PlayerMovement movement;

    [Tooltip("Melee Attacking 用。留空會自動找場景裡的 MeleeAttackController。")]
    [SerializeField] private MeleeAttackController melee;

    [Tooltip("Melee 兩種模式用：這把武器的 MeleeHitbox。留空會在父物件、子物件上找。\n" +
             "Melee Attacking 模式下，用它判斷是不是「這把武器」在揮；找不到時，任何一隻手攻擊都會亮。")]
    [SerializeField] private MeleeHitbox hitbox;

    [Tooltip("一起開關的拖尾（留空會在這個物件和子物件上找）。")]
    [SerializeField] private BodySpaceTrail trail;

    [Tooltip("攻擊結束後，特效再多維持幾秒才停止發射。")]
    [SerializeField, Min(0f)] private float endHold = 0f;

    [Tooltip("攻擊結束時立刻清掉所有粒子（不等它們自然消失）。")]
    [SerializeField] private bool clearOnEnd = false;

    private ParticleSystem[] _systems;
    private bool _active;
    private float _stopAt = -1f;

    private void Awake()
    {
        if (movement == null) movement = GetComponentInParent<PlayerMovement>();
        if (trail == null) trail = GetComponentInChildren<BodySpaceTrail>(true);
        if (hitbox == null) hitbox = GetComponentInParent<MeleeHitbox>();
        if (hitbox == null) hitbox = GetComponentInChildren<MeleeHitbox>(true);
        _systems = GetComponentsInChildren<ParticleSystem>(true);
    }

    private void OnEnable()
    {
        _active = false;
        _stopAt = -1f;
        StopAll(clear: true);
    }

    private void Update()
    {
        bool on = IsTriggered();

        if (on)
        {
            _stopAt = -1f;
            if (!_active)
            {
                _active = true;
                PlayAll();
            }
        }
        else if (_active)
        {
            if (_stopAt < 0f) _stopAt = Time.time + endHold;
            if (Time.time >= _stopAt)
            {
                _active = false;
                _stopAt = -1f;
                StopAll(clearOnEnd);
            }
        }
    }

    private bool IsTriggered()
    {
        switch (trigger)
        {
            case Trigger.AssaultKick:
                return movement != null && movement.IsAssaultKicking;

            case Trigger.MeleeAttacking:
                if (melee == null) melee = FindFirstObjectByType<MeleeAttackController>();
                if (melee == null || !melee.IsAttacking) return false;
                return hitbox == null || melee.ActiveHitbox == hitbox;

            case Trigger.MeleeHitbox:
                return hitbox != null && hitbox.IsOpen;
        }
        return false;
    }

    private void PlayAll()
    {
        // 只對最上層的系統呼叫 Play(true)：子系統會一起播，不會重複觸發
        for (int i = 0; i < _systems.Length; i++)
        {
            ParticleSystem ps = _systems[i];
            if (ps == null || IsChildSystem(ps)) continue;
            ps.Play(true);
        }
        if (trail != null) trail.Play();
    }

    private void StopAll(bool clear)
    {
        var behavior = clear ? ParticleSystemStopBehavior.StopEmittingAndClear : ParticleSystemStopBehavior.StopEmitting;
        for (int i = 0; i < _systems.Length; i++)
        {
            ParticleSystem ps = _systems[i];
            if (ps == null || IsChildSystem(ps)) continue;
            ps.Stop(true, behavior);
        }
        if (trail != null)
        {
            trail.Stop();
            if (clear) trail.Clear();
        }
    }

    // 上一層也是粒子系統 → 由上一層的 Play(true) / Stop(true) 帶動
    private bool IsChildSystem(ParticleSystem ps)
    {
        Transform p = ps.transform.parent;
        if (p == null || ps.transform == transform) return false;
        return p.GetComponentInParent<ParticleSystem>() != null && p.IsChildOf(transform);
    }
}
