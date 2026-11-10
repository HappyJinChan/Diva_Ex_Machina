using System;
using UnityEngine;
using UnityEngine.Events;

/// <summary>
/// 建築的血量與受傷處理。掛在建築 prefab 的根物件上。
///
/// 敵人的 Bullet / MeleeHitbox 都是用 GetComponentInParent&lt;IDamageable&gt;() 找目標，
/// 所以子物件上的 Collider 被打到也會算到這裡。
///
/// 要讓敵人真的打得到，另外還需要：
///   1. 建築的 Collider 所在層要包含在敵人武器的目標層裡
///      （TurretController.bulletTargetLayer / MeleeHitData.hittableLayers）。
///   2. 想讓敵人主動把它當目標，建築的 tag 設成 "Defence Fortifications"（EnemyDetection 用）。
///
/// 防禦公式與 ModularEntityStats / PlayerStats 相同（DefenseFormula）：
/// 減傷 = 防禦 ÷ (防禦 + 1000)，防禦 1000 = 減傷 50%，永遠不會到 100%。
/// </summary>
[DisallowMultipleComponent]
public class BuildingStats : MonoBehaviour, IDamageable
{
    [Header("Health")]
    [Min(1f)] public float maxHealth = 500f;
    [SerializeField] private float health;

    [Header("Defense（1000 = 減傷 50%）")]
    public float physicalDefense;
    public float explosionDefense;
    public float energyDefense;
    public float coldDefense;

    [Header("Feedback (可留空)")]
    [Tooltip("受傷回饋。留空就不播放。")]
    public ModularEntityEffectManager effectManager;

    [Tooltip("被摧毀時在原地生成的特效（爆炸、瓦礫等）。會走 PrefabPool。")]
    public GameObject destroyEffectPrefab;

    [Header("Destroy")]
    [Tooltip("被摧毀時一起刪掉 ghost ship 上的分身，避免 NavMesh 上的洞留著。")]
    public bool destroyGhostOnDeath = true;

    [Tooltip("血量歸零後延遲多久才真正 Destroy（給死亡動畫用）。0 = 立即。")]
    [Min(0f)] public float destroyDelay = 0f;

    [Header("Debug")]
    [SerializeField] private bool logDamage = true;

    [Header("Events")]
    public UnityEvent<float> onDamaged;   // 參數：實際扣掉的血量
    public UnityEvent onDestroyed;

    /// <summary>程式端訂閱用。(實際傷害, 攻擊者)</summary>
    public event Action<float, GameObject> Damaged;
    public event Action<BuildingStats> Destroyed;

    public float Health => health;
    public float HealthNormalized => maxHealth > 0f ? health / maxHealth : 0f;
    public bool IsDestroyed => _dead;

    private bool _dead;

    private void Awake()
    {
        health = maxHealth;
    }

    public void TakeDamage(DamageInfo dmg, GameObject attacker)
    {
        // Destroy 要到這一幀結束才生效，同一幀的後續命中直接擋掉，避免死亡觸發多次
        if (_dead) return;

        float amount = DefenseFormula.Apply(dmg, physicalDefense, explosionDefense, energyDefense, coldDefense);

        if (amount <= 0f) return;

        health = Mathf.Max(0f, health - amount);

        if (logDamage)
            Debug.Log($"[BuildingStats] {GetInstanceID()} '{name}' took {amount:F1} from {(attacker != null ? attacker.name : "null")}, HP {health:F0}/{maxHealth:F0}", this);

        // 先扣血、先判死，回饋放後面 —— 回饋出錯也不會讓建築變成不死
        if (health <= 0f) Die();

        // 傷害數字和閃光各自可以留空，由 PlayDamageFeedback 自己判斷
        if (effectManager != null)
            effectManager.PlayDamageFeedback(amount);

        Damaged?.Invoke(amount, attacker);
        onDamaged?.Invoke(amount);
    }

    /// <summary>修理。已被摧毀的建築不能修。</summary>
    public void Repair(float amount)
    {
        if (_dead || amount <= 0f) return;
        health = Mathf.Min(maxHealth, health + amount);
    }

    private void Die()
    {
        _dead = true;

        // 先找 ghost 再 Unregister，不然配對就查不到了
        Transform ghost = destroyGhostOnDeath ? FindGhost() : null;
        BuildingRegistry.Unregister(transform);

        if (destroyEffectPrefab != null)
            PrefabPool.Spawn(destroyEffectPrefab, transform.position, transform.rotation);

        Destroyed?.Invoke(this);
        onDestroyed?.Invoke();

        // 把 grid 上佔用的格子還回去，玩家才能在原地重蓋。
        // 這裡就釋放、不等 destroyDelay：死亡動畫還在播的時候格子已經可以再用。
        // 沒登記過的建築（事先擺在場景裡的）是 no-op。
        BuildingGrid.ReleaseFromAnyGrid(gameObject);

        if (ghost != null) Destroy(ghost.gameObject);
        Destroy(gameObject, destroyDelay);
    }

    private Transform FindGhost()
    {
        var entries = BuildingRegistry.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].real == transform)
                return entries[i].ghost;
        }
        return null;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (!Application.isPlaying) health = maxHealth;
    }
#endif
}
