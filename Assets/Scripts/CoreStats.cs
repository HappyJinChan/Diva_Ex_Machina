using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 貝努鳥號核心的血量。掛在 LandshipNavigation.core 指向的物件上（有 SphereCollider 的那個）。
///
/// 跟 BuildingStats 的差別：血量歸零時「不會」Destroy 自己 ——
/// 核心被刪掉的話，LandshipNavigation.core 和所有敵人的目標參考都會變成 null。
/// 歸零只發出 Died / onDied，失敗流程（Game Over 畫面、重來）由訂閱的人處理。
///
/// 敵人的 Bullet / MeleeHitbox 用 GetComponentInParent&lt;IDamageable&gt;() 找目標，
/// 所以核心底下子物件的 Collider 被打到也會算到這裡。
/// 要讓敵人真的打得到：核心 Collider 所在的層要包含在敵人武器的目標層裡
/// （TurretController.bulletTargetLayer、近戰的 hitLayers、飛彈的 enemyLayer）。
///
/// 防禦公式跟其他實體相同（DefenseFormula）：防禦 1000 = 減傷 50%。
/// </summary>
[DisallowMultipleComponent]
public class CoreStats : MonoBehaviour, IDamageable
{
    public static CoreStats Instance { get; private set; }

    [Header("Health")]
    [Min(1f)] public float maxHealth = 15000f;
    [SerializeField] private float health;

    [Header("Defense（1000 = 減傷 50%）")]
    public float physicalDefense;
    public float explosionDefense = 300f;
    public float energyDefense;
    public float coldDefense;

    [Header("Feedback (可留空)")]
    [Tooltip("受傷回饋（傷害數字、Hit Flash）。留空就不播放。")]
    public ModularEntityEffectManager effectManager;

    [Header("Debug")]
    [SerializeField] private bool logDamage = false;

    [Header("Events")]
    public UnityEvent<float> onDamaged;   // 參數：實際扣掉的血量
    public UnityEvent onDied;

    /// <summary>血量變化。(目前血量, 最大血量)。UI 用。</summary>
    public event Action<float, float> HealthChanged;
    public event Action<float, GameObject> Damaged;   // (實際傷害, 攻擊者)
    public event Action Died;

    public float Health => health;
    public float MaxHealth => maxHealth;
    public float HealthNormalized => maxHealth > 0f ? health / maxHealth : 0f;
    public bool IsDead => _dead;

    private bool _dead;

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning("CoreStats: 場上有兩個核心，後來的這個會取代前一個", this);
        Instance = this;
        health = maxHealth;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public void TakeDamage(DamageInfo dmg, GameObject attacker)
    {
        if (_dead) return;

        float amount = DefenseFormula.Apply(dmg, physicalDefense, explosionDefense, energyDefense, coldDefense);
        if (amount <= 0f) return;

        health = Mathf.Max(0f, health - amount);

        if (logDamage)
            Debug.Log($"[CoreStats] took {amount:F1} from {(attacker != null ? attacker.name : "null")}, HP {health:F0}/{maxHealth:F0}", this);

        if (effectManager != null)
            effectManager.PlayDamageFeedback(amount, dmg.isCritical);

        HealthChanged?.Invoke(health, maxHealth);
        Damaged?.Invoke(amount, attacker);
        onDamaged?.Invoke(amount);

        if (health <= 0f) Die();
    }

    /// <summary>修理。已經歸零的核心不能修（要重來請用 ResetHealth）。</summary>
    public void Repair(float amount)
    {
        if (_dead || amount <= 0f) return;
        health = Mathf.Min(maxHealth, health + amount);
        HealthChanged?.Invoke(health, maxHealth);
    }

    /// <summary>回滿血、解除死亡狀態（重新開始關卡時用）。</summary>
    public void ResetHealth()
    {
        _dead = false;
        health = maxHealth;
        HealthChanged?.Invoke(health, maxHealth);
    }

    private void Die()
    {
        _dead = true;
        Debug.Log("[CoreStats] 核心被摧毀", this);
        Died?.Invoke();
        onDied?.Invoke();
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!Application.isPlaying) health = maxHealth;
    }

    [ContextMenu("Debug/Take 1000 Damage")]
    private void DebugDamage()
    {
        if (!Application.isPlaying) return;
        TakeDamage(new DamageInfo(1000f, 0f, 0f, 0f), null);
    }
#endif
}
