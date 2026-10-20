using System.Collections.Generic;
using MoreMountains.Feedbacks;
using UnityEngine;

/// <summary>
/// 換裝爆炸：支援艙命中 Bradamant、換上新套裝的那一刻，她對周圍產生的能量爆炸。
/// 掛在 Bradamant 的根物件上（Tag 為 "Player" 的那個，或它的父物件）。
///
/// 由 SupportTurretBrain 在艙命中時呼叫 Burst()，順序是「先爆炸、再換裝」——
/// 閃光剛好蓋住換模型的那一幀。只有被射中換裝時才會爆炸，在裝備介面點分頁不會。
///
/// 做三件事：
///   1. 傷害：範圍內一律全額（不隨距離遞減），每個目標只算一次，只打 Enemy Layer，不打自己。
///      能量傷害 + 衝擊值（扣 ACS），數值固定。攻擊者 = Bradamant。
///   2. 清子彈：範圍內「打得到 Bradamant」的子彈直接消失（不會引爆）。
///   3. 特效：Explosion Prefab（建議用 Explosion Builder 的 Lightning）+ Burst Feedback（鏡頭震動、手把震動、音效）。
///      特效大小跟著 Radius 縮放，並掛在 Bradamant 身上跟著她移動
///      （Local 的圖層 —— 閃光、電漿、電弧、衝擊環 —— 跟著走；World 的火花、離子會留在原地飄散）。
///      被打到的敵人自己會播受擊回饋（ModularEntityEffectManager）。不做全畫面凍結。
///
/// 爆炸中心 = Bradamant 的瞄準點（跟支援艙瞄的是同一個點），不是艙消失的位置。
///
/// 換裝硬直（GearChange）：同一刻由 SupportTurretBrain 呼叫。
///   速度降到 Freeze Speed Ratio（預設原速的 10%）、暫停移動 Freeze Duration 秒（PlayerMovement.FreezeMovement，期間不接受操作），
///   並在 Animator 播放 Gear Change State（指定圖層，CrossFade）。
///   動畫離開 Gear Change 的方式由 Animator 決定（例如 Gear Change → Exit，Has Exit Time）。
/// </summary>
[DisallowMultipleComponent]
public class SupplyBurst : MonoBehaviour
{
    [Header("Damage")]
    [Tooltip("爆炸半徑（公尺）。")]
    [SerializeField, Min(0f)] private float radius = 15f;

    [Tooltip("能量傷害（固定值，範圍內全額）。")]
    [SerializeField, Min(0f)] private float energyDamage = 100f;

    [Tooltip("衝擊值：扣目標的 ACS，扣到 0 觸發失衡。")]
    [SerializeField, Min(0f)] private float impact = 50f;

    [Tooltip("會被炸到的層（敵人）。不要勾船和玩家。")]
    [SerializeField] private LayerMask enemyLayer;

    [Header("Bullet Clear")]
    [Tooltip("清除範圍內打得到 Bradamant 的子彈。")]
    [SerializeField] private bool clearEnemyBullets = true;

    [Tooltip("清子彈的半徑。0 = 跟爆炸半徑一樣。")]
    [SerializeField, Min(0f)] private float bulletClearRadius = 0f;

    [Tooltip("子彈所在的層。預設 \"Bullet\"。")]
    [SerializeField] private LayerMask bulletLayers;

    [Tooltip("Bradamant 的受擊層。子彈的 Enemy Layer 跟這裡有交集 = 打得到她 = 要清掉。\n" +
             "留空 = 開始時自動用她身上所有碰撞箱的層（Console 會印出來）。")]
    [SerializeField] private LayerMask playerHurtLayers;

    [Tooltip("（可留空）每顆被清掉的子彈位置生成的小特效。")]
    [SerializeField] private GameObject bulletClearEffect;

    [Tooltip("小特效幾秒後回池子。")]
    [SerializeField, Min(0.1f)] private float bulletClearEffectLifetime = 1f;

    [Header("Effects")]
    [Tooltip("爆炸特效（建議 Explosion_Lightning）。走 PrefabPool。")]
    [SerializeField] private GameObject explosionPrefab;

    [Tooltip("特效大小跟著 Radius 縮放。")]
    [SerializeField] private bool scaleEffectWithRadius = true;

    [Tooltip("特效大小倍率（越大越大）。1 = 特效的 Radius 等於傷害半徑；\n" +
             "Explosion Builder 的衝擊環會擴散到約 1.6 倍，所以約 0.6 時衝擊環外緣 ≈ 傷害半徑。")]
    [SerializeField, Min(0.01f)] private float effectSizeMultiplier = 1f;

    [Tooltip("Explosion Prefab 在 Explosion Builder 裡設定的 Radius（公尺）。特效放大倍率 = Radius ÷ 這個值。\n" +
             "0 = 自動：用 prefab 上燈光的 Range ÷ 4 推算（Explosion Builder 的燈光範圍 = 4 × Radius）。")]
    [SerializeField, Min(0f)] private float effectBaseRadius = 0f;   // 進階，通常保持 0

    [Tooltip("特效掛在 Bradamant 身上，跟著她移動。")]
    [SerializeField] private bool effectFollowsPlayer = true;

    [Tooltip("特效沒有開 ExplosionEffect 的「播完自動回收」時，幾秒後回池子。")]
    [SerializeField, Min(0.1f)] private float explosionFallbackLifetime = 5f;

    [Tooltip("爆炸時播放（鏡頭震動、手把震動、音效…）。")]
    [SerializeField] private MMF_Player burstFeedback;

    [Header("Gear Change（換裝硬直）")]
    [Tooltip("速度歸零、暫停移動的秒數。0 = 不暫停。")]
    [SerializeField, Min(0f)] private float freezeDuration = 0.5f;

    [Tooltip("硬直期間的速度 = 命中當下速度 × 這個比例（方向不變，等速）。0 = 完全停住；0.1 = 原速的 10%。")]
    [SerializeField, Range(0f, 1f)] private float freezeSpeedRatio = 0.1f;

    [Tooltip("Bradamant 的 Animator。留空就往子物件找。")]
    [SerializeField] private Animator animator;

    [Tooltip("要播放的 State 名稱。")]
    [SerializeField] private string gearChangeState = "Gear Change";

    [Tooltip("State 所在的 Animator 圖層名稱。")]
    [SerializeField] private string gearChangeLayer = "Hit_Reaction";

    [Tooltip("CrossFade 的過渡時間（秒）。")]
    [SerializeField, Min(0f)] private float gearChangeFade = 0.1f;

    [Header("Debug")]
    [SerializeField] private bool logBurst = true;

    private static readonly Collider[] _enemyBuffer = new Collider[128];
    private static readonly Collider[] _bulletBuffer = new Collider[256];
    private readonly HashSet<int> _seen = new HashSet<int>();
    private float _resolvedBaseRadius = -1f;
    private PlayerMovement _movement;
    private PlayerEffectManager _effects;

    public float Radius => radius;

    private void Reset()
    {
        bulletLayers = LayerMask.GetMask("Bullet");
    }

    private void Start()
    {
        if (clearEnemyBullets && playerHurtLayers.value == 0)
        {
            int mask = 0;
            foreach (var col in GetComponentsInChildren<Collider>(true))
                mask |= 1 << col.gameObject.layer;
            playerHurtLayers = mask;
            Debug.Log($"SupplyBurst: Player Hurt Layers 留空，自動使用 {LayerMaskNames(mask)}", this);
        }

        if (enemyLayer.value == 0)
            Debug.LogWarning("SupplyBurst: Enemy Layer 沒有設定，爆炸不會傷害任何東西", this);
    }

    /// <summary>爆炸中心：Bradamant 的瞄準點。</summary>
    public Vector3 Center => TurretController.GetAimPoint(transform);

    /// <summary>在 Bradamant 身上爆炸。</summary>
    [ContextMenu("Test Burst")]
    public void Burst()
    {
        Vector3 center = Center;

        SpawnExplosionEffect(center);
        if (burstFeedback != null) burstFeedback.PlayFeedbacks(center);

        int hits = DealDamage(center);
        int cleared = clearEnemyBullets ? ClearBullets(center) : 0;

        if (logBurst)
            Debug.Log($"SupplyBurst: 爆炸，命中 {hits} 個目標，清除 {cleared} 顆子彈", this);
    }

    // ── 換裝硬直 ─────────────────────────────────────────────

    /// <summary>減速、暫停移動，並播放 Gear Change 動畫。</summary>
    [ContextMenu("Test Gear Change")]
    public void GearChange()
    {
        if (freezeDuration > 0f)
        {
            if (_movement == null) _movement = GetComponentInChildren<PlayerMovement>();
            if (_movement == null) _movement = GetComponentInParent<PlayerMovement>();
            if (_movement != null) _movement.FreezeMovement(freezeDuration, freezeSpeedRatio);
            else Debug.LogWarning("SupplyBurst: 找不到 PlayerMovement，不會暫停移動", this);
        }

        PlayGearChangeAnimation();
    }

    /// <summary>
    /// 被支援艙命中的全身閃光（PlayerEffectManager → MMF → EntityHitFlash）。
    /// 要在 SwitchSet 之後呼叫，新裝甲才會一起閃。
    /// </summary>
    [ContextMenu("Test Hit Flash")]
    public void PlayHitFlash()
    {
        if (_effects == null) _effects = GetComponentInChildren<PlayerEffectManager>();
        if (_effects == null) _effects = GetComponentInParent<PlayerEffectManager>();
        if (_effects != null) _effects.PlaySupplyHitFlash();
        else Debug.LogWarning("SupplyBurst: 找不到 PlayerEffectManager，不播閃光", this);
    }

    private void PlayGearChangeAnimation()
    {
        if (string.IsNullOrEmpty(gearChangeState)) return;
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (animator == null)
        {
            Debug.LogWarning("SupplyBurst: 找不到 Animator，不播 Gear Change", this);
            return;
        }

        int layer = string.IsNullOrEmpty(gearChangeLayer) ? 0 : animator.GetLayerIndex(gearChangeLayer);
        if (layer < 0)
        {
            Debug.LogWarning($"SupplyBurst: Animator 沒有圖層 '{gearChangeLayer}'", this);
            return;
        }

        int hash = Animator.StringToHash(gearChangeState);
        if (!animator.HasState(layer, hash))
        {
            Debug.LogWarning($"SupplyBurst: 圖層 '{gearChangeLayer}' 裡沒有 State '{gearChangeState}'", this);
            return;
        }

        animator.CrossFadeInFixedTime(hash, gearChangeFade, layer, 0f);
    }

    // ── 1. 傷害 ───────────────────────────────────────────────

    private int DealDamage(Vector3 center)
    {
        if (radius <= 0f || enemyLayer.value == 0) return 0;
        if (energyDamage <= 0f && impact <= 0f) return 0;

        var dmg = new DamageInfo(0f, 0f, energyDamage, 0f, impact);

        // Collide：有些敵人的受擊碰撞箱是 trigger
        int count = Physics.OverlapSphereNonAlloc(
            center, radius, _enemyBuffer, enemyLayer.value, QueryTriggerInteraction.Collide);

        _seen.Clear();
        int hits = 0;

        for (int i = 0; i < count; i++)
        {
            Collider col = _enemyBuffer[i];
            if (col == null) continue;

            var target = col.GetComponentInParent<IDamageable>();
            if (target == null) continue;

            var comp = target as Component;
            if (comp == null) continue;

            // 同一個目標多個碰撞箱只算一次
            if (!_seen.Add(comp.GetInstanceID())) continue;

            // 不炸自己
            if (comp.transform.IsChildOf(transform)) continue;

            target.TakeDamage(dmg, gameObject);
            hits++;
        }
        return hits;
    }

    // ── 2. 清子彈 ─────────────────────────────────────────────

    private int ClearBullets(Vector3 center)
    {
        float r = bulletClearRadius > 0f ? bulletClearRadius : radius;
        if (r <= 0f || bulletLayers.value == 0 || playerHurtLayers.value == 0) return 0;

        int count = Physics.OverlapSphereNonAlloc(
            center, r, _bulletBuffer, bulletLayers.value, QueryTriggerInteraction.Collide);

        int cleared = 0;
        for (int i = 0; i < count; i++)
        {
            Collider col = _bulletBuffer[i];
            if (col == null) continue;

            var bullet = col.GetComponentInParent<Bullet>();
            if (bullet == null || !bullet.IsLive) continue;

            // 打不到她的子彈（她自己的、船上友軍砲塔的）不清
            if ((bullet.enemyLayer.value & playerHurtLayers.value) == 0) continue;
            if (bullet.attacker != null && bullet.attacker.transform.IsChildOf(transform)) continue;

            Vector3 pos = bullet.transform.position;
            bullet.Despawn();   // 直接消失，不會引爆
            cleared++;

            if (bulletClearEffect != null)
            {
                GameObject fx = PrefabPool.Spawn(bulletClearEffect, pos, Quaternion.identity);
                if (fx != null) PrefabPool.Despawn(fx, bulletClearEffectLifetime);
            }
        }
        return cleared;
    }

    // ── 3. 特效 ───────────────────────────────────────────────

    private void SpawnExplosionEffect(Vector3 center)
    {
        if (explosionPrefab == null) return;

        Transform parent = effectFollowsPlayer ? transform : null;
        GameObject fx = PrefabPool.Spawn(explosionPrefab, center, Quaternion.identity, parent);
        if (fx == null) return;

        float k = (scaleEffectWithRadius ? radius / BaseRadius : 1f) * effectSizeMultiplier;
        SetWorldScale(fx.transform, explosionPrefab.transform.localScale * k);

        var ee = fx.GetComponent<ExplosionEffect>();
        if (ee != null) ee.ScaleLightRange(k);   // 燈光範圍不吃 Transform 縮放，另外放大（回池子時還原）

        // ExplosionEffect 開了「播完自動回收」就交給它
        if (ee != null && ee.DespawnWhenFinished) return;

        PrefabPool.Despawn(fx, ee != null ? Mathf.Max(ee.GetDuration(), 0.1f) : explosionFallbackLifetime);
    }

    /// <summary>特效在 Explosion Builder 裡的 Radius。</summary>
    private float BaseRadius
    {
        get
        {
            if (effectBaseRadius > 0f) return effectBaseRadius;
            if (_resolvedBaseRadius > 0f) return _resolvedBaseRadius;

            var light = explosionPrefab != null ? explosionPrefab.GetComponentInChildren<Light>(true) : null;
            if (light != null && light.range > 0f)
            {
                _resolvedBaseRadius = light.range / 4f;
            }
            else
            {
                _resolvedBaseRadius = 2f;   // Explosion Builder 的預設值
                Debug.LogWarning("SupplyBurst: Explosion Prefab 沒有燈光，推算不出原本的 Radius，暫用 2。請手動填 Effect Base Radius。", this);
            }
            if (logBurst)
                Debug.Log($"SupplyBurst: 特效原本的 Radius 推算為 {_resolvedBaseRadius:0.##} m", this);
            return _resolvedBaseRadius;
        }
    }

    // 掛在有縮放的父物件底下時，抵銷父物件的縮放，讓特效維持想要的世界大小
    private static void SetWorldScale(Transform fx, Vector3 desired)
    {
        Transform parent = fx.parent;
        if (parent == null) { fx.localScale = desired; return; }

        Vector3 p = parent.lossyScale;
        fx.localScale = new Vector3(
            Mathf.Approximately(p.x, 0f) ? desired.x : desired.x / p.x,
            Mathf.Approximately(p.y, 0f) ? desired.y : desired.y / p.y,
            Mathf.Approximately(p.z, 0f) ? desired.z : desired.z / p.z);
    }

    private static string LayerMaskNames(int mask)
    {
        var names = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) names.Add(LayerMask.LayerToName(i));
        return names.Count > 0 ? string.Join(", ", names) : "（無）";
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector3 c = Application.isPlaying ? Center : transform.position;
        Gizmos.color = new Color(0.6f, 0.3f, 1f, 0.8f);
        Gizmos.DrawWireSphere(c, radius);
        if (clearEnemyBullets && bulletClearRadius > 0f && !Mathf.Approximately(bulletClearRadius, radius))
        {
            Gizmos.color = new Color(0.3f, 0.9f, 1f, 0.6f);
            Gizmos.DrawWireSphere(c, bulletClearRadius);
        }
    }
#endif
}
