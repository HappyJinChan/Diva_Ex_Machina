using UnityEngine;
using UnityEngine.Pool;
using System;
using System.Collections;
using System.Collections.Generic;

public enum EntitySize
{
    Small,
    Medium,
    Large,
}

public class ModularEntityStats : MonoBehaviour, IDamageable
{
    public EntitySize entitySize;
    public float maxHealth;
    public float health;
    // 防禦值（不是百分比）。減傷 = 防禦 ÷ (防禦 + 1000)，詳見 DefenseFormula。
    public float physicalDefense;
    public float explosionDefense;
    public float energyDefense;
    public float coldDefense;

    public float sprintSpeed;
    public float accelerationSpeed;
    public float decelerationSpeed;
    public float dashSpeed;
    public float rotationSpeed;
    // Optional events
    public event Action OnDeath;

    public ModularEntityEffectManager modularEntityEffectManager;

    public float jumpHeight;

    [Header("Death")]
    [Tooltip("死亡時在原地生成的特效（例如 Explosion Builder 做的爆炸）。留空就沒有。會走 PrefabPool。")]
    public GameObject deathEffect;

    [Tooltip("特效上沒有 ExplosionEffect（沒辦法自己算出要播多久）時，幾秒後回收。")]
    [Min(0.1f)] public float deathEffectLifetime = 3f;

    [Header("Death Explosion")]
    [Tooltip("死亡時對半徑內的其他敵人造成爆炸傷害。")]
    public bool deathExplosion = true;

    [Tooltip("爆炸傷害 = 自己的最大生命值 × 這個比例。屬於爆炸傷害，會被對方的爆炸防禦減免。")]
    [Range(0f, 1f)] public float deathExplosionHealthRatio = 0.1f;

    [Tooltip("爆炸半徑（公尺）。選取物件時在 Scene 視圖裡會畫出橘色的球。")]
    [Min(0f)] public float deathExplosionRadius = 6f;

    [Tooltip("要掃描的層。留空 = Enemy。只有掛著 ModularEntityStats 的物件（也就是其他敵人）會受傷，玩家和建築不受影響。")]
    public LayerMask deathExplosionLayers;

    [Tooltip("被死亡爆炸炸死的敵人，延遲幾秒後才死亡並引爆，讓連鎖看起來是一個接一個炸開。0 = 同一幀全部爆開。")]
    [Min(0f)] public float chainExplosionDelay = 0.1f;

    // Destroy 要到這一幀結束才生效；同一幀的後續命中直接擋掉，
    // 否則同時中好幾發會觸發好幾次死亡（好幾個爆炸、OnDeath 叫好幾次）。
    // 連鎖延遲期間也是 true：已經注定要死，不再吃傷害。
    private bool _dead;

    public bool IsDead => _dead;

    // 受傷時通知 Brain 拉仇恨。第一次受傷才找，之後沿用。
    private ModularEntityBrain _brain;
    private bool _brainSearched;

    // 目前是否正在結算死亡爆炸的傷害。TakeDamage 用它判斷「這次死亡是不是被爆炸連鎖炸死的」。
    // 用計數而不是 bool：chainExplosionDelay = 0 時爆炸會在同一個呼叫堆疊裡巢狀觸發。
    private static int s_ExplosionDepth;

    private static readonly Collider[] s_ExplosionHits = new Collider[128];

    // 特殊狀態（失衡等）。沒有掛的話第一次用到時自動補上，見 EntityStatusEffects。
    private EntityStatusEffects _status;

    /// <summary>這個實體的特殊狀態管理（失衡、ACS…）。</summary>
    public EntityStatusEffects Status
    {
        get
        {
            if (_status == null)
            {
                _status = GetComponent<EntityStatusEffects>();
                if (_status == null) _status = gameObject.AddComponent<EntityStatusEffects>();
            }
            return _status;
        }
    }

    public void TakeDamage(DamageInfo dmg, GameObject attacker)
    {
        if (_dead) return;

        EntityStatusEffects status = Status;

        // 失衡中受到的傷害增加。倍率看的是「這一擊之前」的狀態 ——
        // 打到失衡的那一擊本身不加成。
        float amount = DefenseFormula.Apply(dmg, physicalDefense, explosionDefense, energyDefense, coldDefense)
                       * status.DamageTakenMultiplier;

        if (amount > 0f)
            ApplyDamage(amount, attacker);

        // 衝擊：扣 ACS。只有衝擊、沒有傷害的攻擊也算。這一擊打死的話就不用了。
        if (!_dead && dmg.impact > 0f)
            status.ApplyImpact(dmg.impact);
    }

    private void ApplyDamage(float amount, GameObject attacker)
    {
        health -= amount;
        modularEntityEffectManager.PlayDamageFeedback(amount);

        // 被打就拉仇恨：用減防後的實際傷害。這一擊致命的話就不用了。
        if (health > 0f)
        {
            ModularEntityBrain brain = GetBrain();
            if (brain != null) brain.OnDamaged(amount, maxHealth, attacker);
        }

        if (health <= 0f)
        {
            if (s_ExplosionDepth > 0 && chainExplosionDelay > 0f)
            {
                // 被死亡爆炸炸死：先標記死亡（不再吃傷害），稍後才真正爆開
                _dead = true;
                health = 0f;
                StartCoroutine(DieAfter(chainExplosionDelay));
            }
            else
            {
                Die();
            }
        }
    }

    private ModularEntityBrain GetBrain()
    {
        if (!_brainSearched)
        {
            _brainSearched = true;
            _brain = GetComponent<ModularEntityBrain>();
            if (_brain == null) _brain = GetComponentInChildren<ModularEntityBrain>();
        }
        return _brain;
    }

    private IEnumerator DieAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        Die();
    }

    private void Die()
    {
        _dead = true;
        health = 0f;

        SpawnDeathEffect();
        DealDeathExplosionDamage();
        OnDeath?.Invoke();
        Destroy(gameObject);
    }

    /// <summary>
    /// 對半徑內的其他敵人造成「自己最大生命值 × 比例」的爆炸傷害。
    /// 半徑內一律全額，不隨距離衰減；防禦照常由對方的 TakeDamage 套用。
    /// </summary>
    private void DealDeathExplosionDamage()
    {
        if (!deathExplosion) return;

        float damage = maxHealth * deathExplosionHealthRatio;
        if (damage <= 0f || deathExplosionRadius <= 0f) return;

        int mask = deathExplosionLayers.value != 0 ? deathExplosionLayers.value : LayerMask.GetMask("Enemy");
        Vector3 center = transform.position;

        int count = Physics.OverlapSphereNonAlloc(
            center, deathExplosionRadius, s_ExplosionHits, mask, QueryTriggerInteraction.Collide);

        // 先把受害者收集到自己的清單裡再結算：
        //   1) 一隻敵人有好幾個 collider 時只算一次
        //   2) 連鎖延遲為 0 時，對方會在 TakeDamage 裡立刻爆炸、重用 s_ExplosionHits，
        //      直接邊掃邊結算的話這個共用陣列會被蓋掉
        List<ModularEntityStats> victims = ListPool<ModularEntityStats>.Get();
        try
        {
            for (int i = 0; i < count; i++)
            {
                Collider col = s_ExplosionHits[i];
                if (col == null) continue;

                ModularEntityStats other = col.GetComponentInParent<ModularEntityStats>();
                if (other == null || other == this || other._dead) continue;
                if (victims.Contains(other)) continue;

                victims.Add(other);
            }

            var info = new DamageInfo(0f, damage, 0f, 0f);

            s_ExplosionDepth++;
            try
            {
                for (int i = 0; i < victims.Count; i++)
                {
                    ModularEntityStats v = victims[i];
                    if (v != null && !v._dead)
                        v.TakeDamage(info, gameObject);
                }
            }
            finally
            {
                s_ExplosionDepth--;
            }
        }
        finally
        {
            ListPool<ModularEntityStats>.Release(victims);
        }
    }

    private void OnDrawGizmosSelected()
    {
        if (!deathExplosion || deathExplosionRadius <= 0f) return;
        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, deathExplosionRadius);
    }

    /// <summary>
    /// 在原地生成死亡特效，並排程回收。
    ///
    /// 站在船上死亡時，特效掛到船底下跟著船走（跟 Bullet 的命中特效同一個做法），
    /// 否則船開走之後，爆炸會停在船後方的半空中。
    /// </summary>
    private void SpawnDeathEffect()
    {
        if (deathEffect == null) return;

        Transform follow = null;
        ShipPassenger passenger = GetComponentInParent<ShipPassenger>();
        if (passenger == null) passenger = GetComponentInChildren<ShipPassenger>();
        if (passenger != null && passenger.isOnShip && passenger.PlatformRigidbody != null)
            follow = passenger.PlatformRigidbody.transform;

        GameObject fx = PrefabPool.Spawn(deathEffect, transform.position, Quaternion.identity, follow);
        if (fx == null) return;

        // 掛到船底下會繼承船的縮放 → 反算回去，讓特效維持 prefab 設定的世界大小
        if (follow != null)
        {
            Vector3 want = deathEffect.transform.localScale;
            Vector3 p = follow.lossyScale;
            fx.transform.localScale = new Vector3(
                Mathf.Approximately(p.x, 0f) ? want.x : want.x / p.x,
                Mathf.Approximately(p.y, 0f) ? want.y : want.y / p.y,
                Mathf.Approximately(p.z, 0f) ? want.z : want.z / p.z);
        }

        // 回收：ExplosionEffect 勾了 Despawn When Finished 就交給它自己；
        // 否則由這裡排程（兩邊都排會互相干擾）
        ExplosionEffect explosion = fx.GetComponent<ExplosionEffect>();
        if (explosion != null)
        {
            if (!explosion.DespawnWhenFinished)
                PrefabPool.Despawn(fx, explosion.GetDuration());
        }
        else
        {
            PrefabPool.Despawn(fx, deathEffectLifetime);
        }
    }
}
