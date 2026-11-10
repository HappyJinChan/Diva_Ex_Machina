using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class Bullet : MonoBehaviour, IPooled
{
    public GameObject attacker;

    public float physicalDamage = 0;
    public float explosionDamage = 0;
    public float energyDamage = 0;
    public float coldDamage = 0;

    // 衝擊值：扣目標的 ACS，扣到 0 觸發失衡。受距離衰減，不吃暴擊。
    // 玩家的子彈開火時由 RangeAttackController 用武器的 Impact 蓋掉；敵人的子彈吃 prefab 上的值。
    public float impact = 0;

    public float criticalChance = 0.05f;
    public float criticalMultiplier = 1.5f;

    public float lifespan = 5f;
    public Rigidbody rb;

    public LayerMask ignoreLayer;
    public LayerMask enemyLayer;
    public bool ignoreObstacles = false;

    // ─────────────────────────── Hit Effect ───────────────────────────
    [Header("Hit Effect")]
    [Tooltip("命中時在命中點生成的特效。根物件底下可以掛任意多個 Particle System。\n" +
             "走 PrefabPool，播完會自動回池子，不需要在特效上掛任何腳本。")]
    public GameObject hitEffect;

    [Tooltip("特效存活幾秒後回池子。\n0 = 自動推算：取所有 Particle System 中 (duration + 最長粒子壽命) 的最大值。")]
    [Min(0f)] public float hitEffectLifetime = 0f;

    [Tooltip("特效沿命中面法線往外推幾公尺，避免跟牆面 z-fighting 或被切掉一半。")]
    [Min(0f)] public float hitEffectSurfaceOffset = 0.1f;

    [Tooltip("只有打中目標（敵人 / 可受傷的物件）時才額外播放的特效，例如火花。\n" +
             "打中目標時會和上面的 Hit Effect 一起播；打到牆壁、地面、建築時只播 Hit Effect。留空 = 不用。")]
    public GameObject targetHitEffect;

    [Tooltip("目標命中特效存活幾秒後回池子。0 = 自動推算（同 Hit Effect Lifetime 的算法）。")]
    [Min(0f)] public float targetHitEffectLifetime = 0f;
    // ──────────────────────────────────────────────────────────────

    [Header("Flight")]
    [Tooltip("讓子彈模型朝向飛行方向")]
    public bool alignToVelocity = true;
    // ──────────────────────────────────────────────────────────────

    // Your semantics:
    // 0  = destroy after 1st enemy impact
    // 1  = pass 1 enemy, destroy after 2nd enemy impact
    // 2  = pass 2 enemies, destroy after 3rd enemy impact
    // -1 = infinite
    //
    // ★ 注意：這個欄位是「就地遞減」的 —— 打穿一個目標就 --。
    //   所以它必須進 prefab 快照，回收重用時還原，否則穿透彈只有第一輪有穿透。
    public int penetration = 0;

    // ── 距離衰減 ──
    //
    //   d ≤ R          ：100%
    //   R < d < E      ：從 100% 線性降到 falloffMinDamage（E = R × falloffEndMultiplier）
    //   d ≥ E          ：固定 falloffMinDamage
    //
    //   d = 子彈「相對發射者」的累計飛行距離（彈速 × 飛行時間），不是世界座標的位移：
    //   在行進中的船上開火時，子彈繼承的船速不算進距離，否則船越快衰減越早開始。
    //   追蹤彈、貫穿後繼續飛的子彈，路徑變長，距離也跟著變長。
    //
    //   玩家的子彈：開火時由 RangeAttackController 用武器的 EffectiveRange 蓋掉。
    //   敵人的子彈：吃 prefab 上的值（砲塔不覆寫）。
    [Header("Damage Falloff")]
    [Tooltip("全傷距離 R（公尺）。0 = 不衰減。\n玩家子彈開火時會被武器的 EffectiveRange 蓋掉；敵人子彈吃這裡的值。")]
    [Min(0f)] public float effectiveRange = 0f;

    [Tooltip("衰減結束的距離 = R × 這個倍率。超過之後傷害固定在最低值。")]
    [Min(1f)] public float falloffEndMultiplier = 2f;

    [Tooltip("衰減到底時的傷害比例（0.3 = 30%）。")]
    [Range(0f, 1f)] public float falloffMinDamage = 0.3f;

    // 發射平台（玩家站在行進中的船上開火時，就是那艘船的 Rigidbody）與發射點。
    // 只給曳光（BulletTracer）用，不影響飛行與命中。
    //
    // 鏡頭跟著船走，所以曳光要畫成「相對於船」的樣子才會從槍口延伸出去：
    //   方向 = 子彈速度 − 船速（相對於船的飛行方向）
    //   尾端 = 不超過發射點（發射點記在船的座標系裡，跟著船一起移動）
    //
    // 由發射端（RangeAttackController）在 Spawn 之後呼叫 SetPlatform；每次重新上場清空。
    private Rigidbody _platform;
    private Vector3 _spawnWorld;     // 沒有平台時的發射點（世界座標）
    private Vector3 _spawnLocal;     // 有平台時的發射點（平台座標）

    /// <summary>指定發射平台（站在船上開火時傳船的 Rigidbody；否則傳 null）。</summary>
    public void SetPlatform(Rigidbody platform)
    {
        _platform = platform;
        if (platform != null) _spawnLocal = platform.transform.InverseTransformPoint(_spawnWorld);
    }

    /// <summary>發射平台的速度。不在船上開火時是 0。</summary>
    public Vector3 PlatformVelocity => _platform != null ? _platform.linearVelocity : Vector3.zero;

    /// <summary>發射點目前在世界中的位置（在船上開火時會跟著船移動）。</summary>
    public Vector3 SpawnPoint => _platform != null ? _platform.transform.TransformPoint(_spawnLocal) : _spawnWorld;

    // 距離衰減用的 runtime 狀態（每次上場歸零）
    private float _flightDistance;        // 相對發射者的累計飛行距離
    private Vector3 _inheritedVelocity;   // 發射時繼承的速度（例如船速），計算飛行距離時扣掉

    /// <summary>
    /// 發射時疊加在子彈上的速度（例如站在行進中的船上開火時的船速）。
    /// 只用來計算距離衰減：飛行距離扣掉這部分，才是「彈速 × 飛行時間」。沒繼承任何速度時不用呼叫。
    /// </summary>
    public void SetInheritedVelocity(Vector3 velocity) => _inheritedVelocity = velocity;

    /// <summary>目前累計的飛行距離（公尺，相對發射者）。</summary>
    public float FlightDistance => _flightDistance;

    /// <summary>在距離 distance 命中時的傷害倍率（距離衰減）。</summary>
    public float GetFalloffMultiplier(float distance)
    {
        if (effectiveRange <= 0f || distance <= effectiveRange) return 1f;

        float end = effectiveRange * Mathf.Max(1f, falloffEndMultiplier);
        if (end <= effectiveRange || distance >= end) return Mathf.Clamp01(falloffMinDamage);

        float t = (distance - effectiveRange) / (end - effectiveRange);
        return Mathf.Lerp(1f, Mathf.Clamp01(falloffMinDamage), t);
    }

    // _live：這顆子彈是否「在場上活著」。
    // 取代原本的 _destroyed —— 池化之後子彈不會真的被銷毀，只會在 live / 待命之間切換。
    private bool _live;
    private Collider _selfCol;

    // Prevent multi-collider enemies from taking damage multiple times per bullet
    private readonly HashSet<int> _hitEnemyIds = new HashSet<int>();

    // Predict 的 Linecast 記下的命中點與命中面法線。
    //
    // ★ 位置一定要在這裡記，不能等到結算時才讀 transform.position。
    //   Predict 只是「登記」命中，真正的結算在下一個 physics step；
    //   中間物理引擎已經把子彈往前推了 velocity × fixedDeltaTime，
    //   砲塔子彈一步就是好幾公尺 —— 那時的位置早就在牆的另一側了。
    //
    // Trigger 回呼本身拿不到這兩個資訊，所以只有走預測那條路的命中才有；
    // 沒有時退回子彈當下的位置與「來向的反方向」。
    private Vector3 _pendingHitPoint;
    private Vector3 _pendingHitNormal;
    private bool _hasPendingHitPoint;

    // 壽命：原本是 Destroy(gameObject, lifespan)，那個排程綁在 GameObject 上、
    // 回收重用時取消不掉（重生後的子彈可能被上一世的計時器殺掉）。改成自己算。
    private float _despawnTime;

    // Predict 的待結算命中。原本用 coroutine + yield return null 延一幀，
    // 那等於每個 physics step、每顆子彈都配置一個 iterator。改成旗標。
    private Collider _pendingHit;
    private bool _hasPendingHit;

    // 視覺元件快取（回收時要清乾淨，否則會看到殘留的拖尾與粒子）
    private TrailRenderer[] _trails;
    private bool[] _trailEmitDefaults;
    private ParticleSystem[] _particles;

    // 子彈本體的模型（不含 TrailRenderer / LineRenderer / 粒子）。
    // 剛生成時先藏起來，等第一個 physics step 跑完才顯示 —— 見 IsRevealed。
    private Renderer[] _bodyRenderers;
    private bool[] _bodyRendererDefaults;

    // 生成後是否已經跑過 physics step；跑過之前子彈先不顯示。
    private bool _steppedSinceSpawn;
    private bool _revealed;

    /// <summary>
    /// 子彈是否已經可以顯示。生成後到第一個 physics step 之前為 false。
    ///
    /// 為什麼要藏：子彈是插值的 Rigidbody。生成的那一幀，畫面上的位置是我們直接寫進去的物理位置；
    /// 站在船上開火時，為了讓之後的每一幀都對準槍口，這個物理位置是「船的物理姿態」上的槍口，
    /// 比畫面上的船超前一點（船速 × 插值落後時間），所以那一幀會畫在槍口旁邊。
    /// 另外，物件池重用的子彈在第一個 physics step 之前，插值可能還拿上一世的位置來算。
    /// 等第一個 step 跑完，插值的前後兩個姿態都是這一世的，畫面位置才可靠。
    /// 這段時間最多一兩幀，而且槍口火焰正好蓋住，看不出缺了子彈。
    /// </summary>
    public bool IsRevealed => _revealed;

    // 共用暫存 buffer（同一 frame 內同步使用，不會互相干擾）
    private static readonly Collider[] _overlapBuffer = new Collider[64];

    /// <summary>這顆子彈是否在場上飛行中。</summary>
    public bool IsLive => _live;

    // ─────────────────────────── Explosion ───────────────────────────
    //
    // 爆炸傷害 = 這顆子彈的「總傷害」× 各屬性的轉換倍率。
    //   總傷害 = physicalDamage + explosionDamage + energyDamage + coldDamage
    //   用的是子彈身上的原始值：已經包含武器、零件的 buff，但「暴擊前、距離衰減前」。
    //   → 爆炸不吃距離衰減，也不吃暴擊。
    //
    //   例：physical 3、cold 5、coldExplosionDamageConversionRate 2.5
    //       直擊 8 點（再乘暴擊與衰減），爆炸 (3 + 5) × 2.5 = 20 點冷凍傷害。
    //
    // 什麼時候爆炸：
    //   · 每命中一個敵人爆一次（貫穿彈每穿過一個就爆一次）。直擊的目標也在範圍內，也會吃到爆炸。
    //   · 撞到牆壁、地面時爆炸。
    //   · 壽命到了、在空中消失時也爆炸。
    // 範圍內均一傷害（不隨距離遞減），每個目標只算一次（多碰撞箱去重），只打 enemyLayer。
    //
    // 所有欄位都會在物件池重用時還原成 prefab 值（見 Defaults）。
    [Header("Explosion")]
    [Tooltip("總傷害轉換成「物理」爆炸傷害的倍率。0 = 不轉換。")]
    [Min(0f)] public float physicalExplosionDamageConversionRate = 0f;

    [Tooltip("總傷害轉換成「爆炸」屬性爆炸傷害的倍率。0 = 不轉換。")]
    [Min(0f)] public float explosionDamageConversionRate = 0f;

    [Tooltip("總傷害轉換成「能量」爆炸傷害的倍率。0 = 不轉換。")]
    [Min(0f)] public float energyExplosionDamageConversionRate = 0f;

    [Tooltip("總傷害轉換成「冷凍」爆炸傷害的倍率。0 = 不轉換。")]
    [Min(0f)] public float coldExplosionDamageConversionRate = 0f;

    [Tooltip("總傷害轉換成爆炸「衝擊值」（扣 ACS）的倍率。0 = 爆炸不造成衝擊。")]
    [Min(0f)] public float explosionACSConversion = 0f;

    [Tooltip("爆炸半徑（公尺）。0 = 不造成爆炸傷害（只播特效）。")]
    [Min(0f)] public float explosionRadius = 0f;

    [Tooltip("爆炸時生成的特效。走 PrefabPool。\n" +
             "特效上有 ExplosionEffect 且開了 Despawn When Finished 時由它自己回收，否則由子彈排程回收。")]
    [FormerlySerializedAs("explodePrefab")]
    public GameObject explosionPrefab;

    // 爆炸範圍內的去重（同一個目標多個碰撞箱只算一次）
    private readonly HashSet<int> _explosionSeen = new HashSet<int>();

    // ═══════════════ Prefab 預設值快照 ═══════════════
    //
    // 為什麼要整組快照，而不是只重設「看起來像 runtime state」的欄位：
    //
    // 兩個 spawn 點寫入的欄位是不重疊的 ——
    //   RangeAttackController：寫 damage + crit，不寫 enemyLayer / ignoreLayer
    //   TurretController     ：寫 damage + layer，不寫 crit
    // 用 Instantiate 時沒寫到的欄位一定是 prefab 值，所以沒事；
    // 一旦回收重用，沒寫到的欄位會留著上一手的值 ——
    // 玩家子彈被砲塔用過之後 enemyLayer 會變成玩家層，玩家就會被自己的子彈打到。
    //
    // Awake() 在 Instantiate 期間就跑完，早於任何外部寫入，
    // 所以這裡拍到的必然是 prefab 上的值。

    private struct Defaults
    {
        public float physicalDamage, explosionDamage, energyDamage, coldDamage;
        public float impact;
        public float criticalChance, criticalMultiplier;
        public float lifespan;
        public LayerMask ignoreLayer, enemyLayer;
        public bool ignoreObstacles;

        public bool alignToVelocity;

        public int penetration;
        public float effectiveRange, falloffEndMultiplier, falloffMinDamage;
        public CollisionDetectionMode collisionDetectionMode;

        public GameObject hitEffect;
        public GameObject targetHitEffect;
        public float targetHitEffectLifetime;
        public float hitEffectLifetime;
        public float hitEffectSurfaceOffset;

        public float physicalExplosionDamageConversionRate, explosionDamageConversionRate;
        public float energyExplosionDamageConversionRate, coldExplosionDamageConversionRate;
        public float explosionACSConversion, explosionRadius;
        public GameObject explosionPrefab;
    }

    private Defaults _defaults;

    private void CaptureDefaults()
    {
        _defaults.physicalDamage = physicalDamage;
        _defaults.explosionDamage = explosionDamage;
        _defaults.energyDamage = energyDamage;
        _defaults.coldDamage = coldDamage;
        _defaults.impact = impact;

        _defaults.criticalChance = criticalChance;
        _defaults.criticalMultiplier = criticalMultiplier;

        _defaults.lifespan = lifespan;
        _defaults.ignoreLayer = ignoreLayer;
        _defaults.enemyLayer = enemyLayer;
        _defaults.ignoreObstacles = ignoreObstacles;

        _defaults.hitEffect = hitEffect;
        _defaults.targetHitEffect = targetHitEffect;
        _defaults.targetHitEffectLifetime = targetHitEffectLifetime;
        _defaults.hitEffectLifetime = hitEffectLifetime;
        _defaults.hitEffectSurfaceOffset = hitEffectSurfaceOffset;

        _defaults.alignToVelocity = alignToVelocity;

        _defaults.penetration = penetration;
        _defaults.effectiveRange = effectiveRange;
        _defaults.falloffEndMultiplier = falloffEndMultiplier;
        _defaults.falloffMinDamage = falloffMinDamage;

        _defaults.physicalExplosionDamageConversionRate = physicalExplosionDamageConversionRate;
        _defaults.explosionDamageConversionRate = explosionDamageConversionRate;
        _defaults.energyExplosionDamageConversionRate = energyExplosionDamageConversionRate;
        _defaults.coldExplosionDamageConversionRate = coldExplosionDamageConversionRate;
        _defaults.explosionACSConversion = explosionACSConversion;
        _defaults.explosionRadius = explosionRadius;
        _defaults.explosionPrefab = explosionPrefab;

        // Predict() 命中時會把 CCD 模式改成 ContinuousSpeculative，改了就回不去
        _defaults.collisionDetectionMode = (rb != null)
            ? rb.collisionDetectionMode
            : CollisionDetectionMode.Discrete;
    }

    private void RestoreDefaults()
    {
        attacker = null;   // prefab 上必然是 null；由 spawn 端每次重新指定
        _platform = null;   // 同上：只有站在船上開火時，發射端才會指定

        physicalDamage = _defaults.physicalDamage;
        explosionDamage = _defaults.explosionDamage;
        energyDamage = _defaults.energyDamage;
        coldDamage = _defaults.coldDamage;
        impact = _defaults.impact;

        criticalChance = _defaults.criticalChance;
        criticalMultiplier = _defaults.criticalMultiplier;

        lifespan = _defaults.lifespan;
        ignoreLayer = _defaults.ignoreLayer;
        enemyLayer = _defaults.enemyLayer;
        ignoreObstacles = _defaults.ignoreObstacles;

        hitEffect = _defaults.hitEffect;
        targetHitEffect = _defaults.targetHitEffect;
        targetHitEffectLifetime = _defaults.targetHitEffectLifetime;
        hitEffectLifetime = _defaults.hitEffectLifetime;
        hitEffectSurfaceOffset = _defaults.hitEffectSurfaceOffset;

        alignToVelocity = _defaults.alignToVelocity;

        penetration = _defaults.penetration;
        effectiveRange = _defaults.effectiveRange;
        falloffEndMultiplier = _defaults.falloffEndMultiplier;
        falloffMinDamage = _defaults.falloffMinDamage;

        physicalExplosionDamageConversionRate = _defaults.physicalExplosionDamageConversionRate;
        explosionDamageConversionRate = _defaults.explosionDamageConversionRate;
        energyExplosionDamageConversionRate = _defaults.energyExplosionDamageConversionRate;
        coldExplosionDamageConversionRate = _defaults.coldExplosionDamageConversionRate;
        explosionACSConversion = _defaults.explosionACSConversion;
        explosionRadius = _defaults.explosionRadius;
        explosionPrefab = _defaults.explosionPrefab;

        if (rb != null)
        {
            rb.collisionDetectionMode = _defaults.collisionDetectionMode;
            rb.angularVelocity = Vector3.zero;
        }
    }

    // ═══════════════════════ 生命週期 ═══════════════════════

    private void Awake()
    {
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (_selfCol == null) _selfCol = GetComponent<Collider>();

        _trails = GetComponentsInChildren<TrailRenderer>(true);
        _trailEmitDefaults = new bool[_trails.Length];
        for (int i = 0; i < _trails.Length; i++)
            _trailEmitDefaults[i] = _trails[i].emitting;

        _particles = GetComponentsInChildren<ParticleSystem>(true);

        // 只收子彈本體的模型。拖尾 / 曳光（LineRenderer，可能由 BulletTracer 動態建立）/ 粒子各有自己的控制方式
        var all = GetComponentsInChildren<Renderer>(true);
        var body = new List<Renderer>(all.Length);
        foreach (var r in all)
        {
            if (r is TrailRenderer || r is LineRenderer || r is ParticleSystemRenderer) continue;
            body.Add(r);
        }
        _bodyRenderers = body.ToArray();
        _bodyRendererDefaults = new bool[_bodyRenderers.Length];
        for (int i = 0; i < _bodyRenderers.Length; i++)
            _bodyRendererDefaults[i] = _bodyRenderers[i].enabled;

        CaptureDefaults();
    }

    // Instantiate 時、以及日後物件池 SetActive(true) 時都會走這裡。
    private void OnEnable() => HandleSpawn();

    // 物件池在 SetActive(true) 之後可以再呼叫這個（OnEnable 已經做過就會被擋掉）。
    // 接上 PrefabPool 之後，把 IPooled 介面掛到這個類別上即可，簽章已經對上。
    public void OnSpawned() => HandleSpawn();

    private void HandleSpawn()
    {
        if (_live) return;   // 同一次上場只初始化一次，避免蓋掉 spawn 端剛寫入的欄位
        _live = true;

        // 1) 先把所有設定欄位還原成 prefab 值
        RestoreDefaults();

        // 2) 再清 runtime state
        _hitEnemyIds.Clear();          // ★ 不清的話，這顆子彈永遠打不到它前世打過的目標
        _pendingHit = null;
        _hasPendingHit = false;
        _pendingHitPoint = Vector3.zero;
        _pendingHitNormal = Vector3.zero;
        _hasPendingHitPoint = false;


        _despawnTime = Time.time + Mathf.Max(0f, lifespan);

        _flightDistance = 0f;
        _inheritedVelocity = Vector3.zero;

        // 發射點：transform 已經由物件池擺到槍口了。平台由發射端稍後透過 SetPlatform 指定。
        _spawnWorld = transform.position;

        // Physics.IgnoreCollision 的配對狀態會在 collider disable/enable 時被清掉。
        // 這裡明確地 enable 一次，確保上一世 IgnoreCollision(self, other, true) 的殘留
        // 不會讓這顆子彈穿過某個敵人。
        if (_selfCol != null) _selfCol.enabled = true;

        ResetVisuals();

        OnBulletSpawned();
    }

    /// <summary>
    /// 每次上場、欄位還原成 prefab 值之後呼叫（在發射端寫入 attacker / 速度 / 目標之前）。
    /// 給繼承的子彈（例如 GuidedMissile）重置自己的狀態。子類別不要自己寫 OnEnable —— 會蓋掉這裡的初始化。
    /// </summary>
    protected virtual void OnBulletSpawned() { }

    /// <summary>
    /// 這顆子彈退場。目前直接 Destroy；接上物件池後只要換掉最後那一行。
    /// </summary>
    public void Despawn()
    {
        if (!_live) return;

        OnDespawned();

        PrefabPool.Despawn(gameObject);
    }

    // 冪等：物件池直接 SetActive(false) 時 OnDisable 也會呼叫到，重複呼叫無害。
    public void OnDespawned()
    {
        _live = false;

        _pendingHit = null;
        _hasPendingHit = false;

        if (_selfCol != null) _selfCol.enabled = false;

        StopVisuals();
    }

    private void OnDisable() => OnDespawned();

    private void LateUpdate()
    {
        if (_live && !_revealed && (_steppedSinceSpawn || rb == null))
            Reveal();
    }

    private void Reveal()
    {
        _revealed = true;

        for (int i = 0; i < _bodyRenderers.Length; i++)
            if (_bodyRenderers[i] != null) _bodyRenderers[i].enabled = _bodyRendererDefaults[i];

        // 粒子延到現在才開始放：世界座標模擬的粒子如果在藏起來那一幀就放，會留在槍口旁邊
        if (_particles != null)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i] == null) continue;
                if (_particles[i].main.playOnAwake) _particles[i].Play(true);
            }
        }
    }

    private void ResetVisuals()
    {
        // 子彈本體先藏起來，第一個 physics step 之後才顯示（見 IsRevealed）
        _steppedSinceSpawn = false;
        _revealed = false;
        for (int i = 0; i < _bodyRenderers.Length; i++)
            if (_bodyRenderers[i] != null) _bodyRenderers[i].enabled = false;

        // 順序很重要：transform 已經由 Instantiate / 物件池擺到位了，這時才 Clear，
        // 否則會看到一條從「上次死亡的位置」拉到槍口的拖尾。
        if (_trails != null)
        {
            for (int i = 0; i < _trails.Length; i++)
            {
                if (_trails[i] == null) continue;
                _trails[i].Clear();
                _trails[i].emitting = _trailEmitDefaults[i];
            }
        }

        if (_particles != null)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i] == null) continue;
                // 先停住並清空；等 Reveal() 才開始放
                _particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }

    private void StopVisuals()
    {
        if (_trails != null)
        {
            for (int i = 0; i < _trails.Length; i++)
            {
                if (_trails[i] == null) continue;
                _trails[i].emitting = false;
                _trails[i].Clear();
            }
        }

        if (_particles != null)
        {
            for (int i = 0; i < _particles.Length; i++)
            {
                if (_particles[i] == null) continue;
                _particles[i].Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }
    }

    protected virtual void FixedUpdate()
    {
        if (!_live) return;

        // 1) 上一個 physics step 由 Predict() 預測到的命中，延一步後才結算。
        //    對應原本 Predict() 裡的 yield return null。
        //
        //    ★ 一定要在「累計飛行距離」之前：Predict 已經把距離補到命中點，
        //      先累計的話，命中會多算一整個 physics step（30 Hz、300 m/s 就是 10 m）。
        if (_hasPendingHit)
        {
            _hasPendingHit = false;
            Collider pending = _pendingHit;
            _pendingHit = null;

            // 目標可能在這一步之間被銷毀了；原本的 coroutine 版在這裡會丟例外
            if (pending != null)
                OnTriggerEnterFixed(pending);

            // 預測的命中點只屬於這一次命中。不清掉的話，貫穿彈之後沒經過預測的命中，
            // 特效會出現在這個舊位置。
            _hasPendingHitPoint = false;

            if (!_live) return;
        }

        // 0) 累計飛行距離：上一個 physics step 子彈相對發射者移動的距離。
        //    剛上場的第一次 FixedUpdate 還沒經過任何 physics step，不算。
        if (_steppedSinceSpawn && rb != null)
            _flightDistance += (rb.linearVelocity - _inheritedVelocity).magnitude * Time.fixedDeltaTime;

        // 這個 FixedUpdate 之後就是這一世的第一個 physics step，下一次渲染時插值已經可靠
        _steppedSinceSpawn = true;

        // 2) 壽命：在空中消失時也會爆炸
        if (Time.time >= _despawnTime)
        {
            Explode(transform.position, null);
            Despawn();
            return;
        }

        // 3.5) 機頭對齊飛行方向
        //      原本只有追蹤轉向時才會對齊，所以沒有追蹤目標的子彈
        //      一輩子維持發射瞬間的朝向。速度一旦改變（重力下墜、被平台速度疊加、
        //      打到東西後減速），模型就跟實際飛行方向對不上。
        //      每個 physics step 對齊一次，上述情況全部涵蓋。
        AlignToVelocity();

        // 4) 下一步的碰撞預測
        Predict();
    }

    /// <summary>
    /// 把機頭轉向目前的速度方向。速度接近零時維持原朝向，
    /// 避免 LookRotation 收到零向量而跳成預設朝向。
    /// </summary>
    private void AlignToVelocity()
    {
        if (!alignToVelocity || rb == null) return;

        Vector3 vel = rb.linearVelocity;
        if (vel.sqrMagnitude < 0.0001f) return;

        transform.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);
    }

    private void OnTriggerEnter(Collider collider)
    {
        OnTriggerEnterFixed(collider);
    }

    private bool IsInEnemyLayer(Collider col)
    {
        if (col == null) return false;
        int bit = 1 << col.gameObject.layer;
        return (enemyLayer.value & bit) != 0;
    }

    // ═══════════════ 命中過濾：單一真相來源 ═══════════════
    //
    // 舊版有兩套獨立的過濾，必須手動保持一致：
    //
    //   Predict()             → GetPredictMask() & ~ignoreLayer.value，餵給 Physics.Linecast
    //   OnTriggerEnterFixed() → IsInIgnoreLayer / ignoreObstacles 兩個早期 return
    //
    // 而且它們的底層機制根本不同：
    //   Physics.Linecast 是 query，只吃 layerMask，完全不看 Layer Collision Matrix。
    //   OnTriggerEnter   則是由矩陣決定會不會被呼叫。
    //
    // 結果是「矩陣裡關掉的組合，Predict 照樣打得到」—— 子彈會在預測階段被一個
    // OnTriggerEnter 永遠不會回報的東西殺掉，表現成子彈莫名其妙消失。
    //
    // 現在兩邊都從 BuildReactMask() 導出，不可能再不同步。

    /// <summary>
    /// 這顆子彈「應該有反應」的層遮罩。
    ///
    /// Predict 直接拿它當 Linecast 的 layerMask，
    /// OnTriggerEnterFixed 透過 ShouldReactTo 拿它做早期 return。
    ///
    /// 注意這回答的是「要不要理會這個碰撞體」，不是「這是不是可以傷害的目標」。
    /// 後者仍然由 IsInEnemyLayer + IDamageable 判斷。
    /// </summary>
    protected int BuildReactMask()
    {
        int notBullet = ~LayerMask.GetMask("Bullet");

        // ignoreObstacles = true → 只對 enemyLayer 有反應，其餘一律穿過
        int mask = ignoreObstacles ? enemyLayer.value : notBullet;

        return mask & notBullet & ~ignoreLayer.value;
    }

    /// <summary>單一 collider 版本，語意跟 BuildReactMask 完全一致。</summary>
    protected bool ShouldReactTo(Collider col)
    {
        if (col == null) return false;
        return (BuildReactMask() & (1 << col.gameObject.layer)) != 0;
    }

    /// <summary>
    /// 往前掃一個 physics step 的距離，補上高速子彈的漏判。
    ///
    /// 原本是 coroutine，而且每個 FixedUpdate 都 StartCoroutine 一次 ——
    /// 大部分情況下它根本不會 yield（沒命中就直接結束），等於白白配置一個
    /// iterator + Coroutine 物件。100 顆子彈 × 50 steps/s 就是每秒五千次配置。
    /// </summary>
    protected virtual void Predict()
    {
        if (rb == null) return;
        if (_hasPendingHit) return;   // 已經有待結算的命中，不要再往前掃、也不要重複貼位置

        Vector3 from = transform.position;
        Vector3 to = from + rb.linearVelocity * Time.fixedDeltaTime;

        int layerMask = BuildReactMask();
        if (Physics.Linecast(from, to, out RaycastHit hit, layerMask))
        {
            // 距離衰減：子彈被拉回命中點，「from → 命中點」這段之後的 physics step 不會再算到，這裡補上。
            // 補完之後 _flightDistance 就是命中點的距離 —— 這一步內的 Trigger 回呼、
            // 下一個 FixedUpdate 的結算，用的都是它。換算成相對發射者的距離（扣掉繼承的船速）。
            float speed = rb.linearVelocity.magnitude;
            if (speed > 0.0001f)
                _flightDistance += hit.distance * ((rb.linearVelocity - _inheritedVelocity).magnitude / speed);

            transform.position = hit.point;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

            _pendingHit = hit.collider;

            // 給命中特效用：命中點與法線都要在這裡留下來，下一步結算時位置已經不對了
            _pendingHitPoint = hit.point;
            _pendingHitNormal = hit.normal;
            _hasPendingHitPoint = true;

            _hasPendingHit = true;
        }
    }

    // ══════════════════════════════════════════════════════════════

    protected virtual void OnTriggerEnterFixed(Collider other)
    {
        //Debug.Log($"Bullet hit: {other.name} / layer={LayerMask.LayerToName(other.gameObject.layer)}");
        if (!_live) return;
        if (other == null) return;

        // ★ 共用過濾：跟 Predict 的 Linecast layerMask 是同一個來源。
        //   舊版這裡是兩個獨立的早期 return（IsInIgnoreLayer + ignoreObstacles），
        //   跟 Predict 的遮罩各算各的。
        if (!ShouldReactTo(other))
            return;

        // 可被傷害的目標？（不再綁死 EnemyStats，改認 IDamageable 介面）
        var target = other.GetComponentInParent<IDamageable>();
        // enemyLayer 現在代表「這顆子彈允許打到的層」：
        // 玩家開的子彈設成敵人層，敵人開的子彈設成玩家層，以此分敵我、避免友軍誤傷。
        bool isTarget = (target != null) && IsInEnemyLayer(other);

        if (isTarget)
        {
            // 用被打物件的 instance id 去重（避免同一目標多 collider 重複觸發）
            var targetObj = target as Component;
            int id = (targetObj != null) ? targetObj.GetInstanceID() : other.GetInstanceID();
            if (_hitEnemyIds.Contains(id)) return;
            _hitEnemyIds.Add(id);

            // 暴擊在「攻擊方」這邊結算（暴擊是攻擊者的屬性）
            bool isCrit = Random.value < Mathf.Clamp01(criticalChance);
            float critMul = isCrit ? Mathf.Max(1f, criticalMultiplier) : 1f;

            // 距離衰減：乘在四種傷害與衝擊值上。暴擊只乘傷害，不乘衝擊值。
            float falloff = GetFalloffMultiplier(_flightDistance);
            float mul = critMul * falloff;

            // 只交出「原始四種傷害 + 衝擊值」，防禦由被打的目標自己套用
            DamageInfo dmg = new DamageInfo(
                physicalDamage * mul,
                explosionDamage * mul,
                energyDamage * mul,
                coldDamage * mul,
                impact * falloff
            );
            dmg.isCritical = isCrit;

            target.TakeDamage(dmg, attacker);

            SpawnHitEffect(other);
            SpawnHitEffect(other, targetHitEffect, targetHitEffectLifetime);   // 只有打中目標才有的特效（例如火花）

            // 爆炸：每命中一個敵人爆一次（貫穿彈每穿過一個就爆一次），直擊的目標也在範圍內
            Explode(GetImpactPoint(), other);
            if (!_live) return;

            // 避免穿過同一 collider 時重複觸發
            // （這個配對狀態會在子彈退場時由 _selfCol.enabled = false 清掉）
            if (_selfCol != null && other != null)
                Physics.IgnoreCollision(_selfCol, other, true);

            // 接著處理穿透（語意與原本相同）
            if (penetration == -1)
            {
                return; // 無限穿透
            }
            else if (penetration > 0)
            {
                penetration--; // 消耗一次穿透
                return;        // 繼續飛
            }
            else // penetration == 0
            {
                Despawn(); // 命中後退場
                return;
            }
        }

        // Not enemy:
        // - if ignoreObstacles was true, we already returned above
        // - otherwise hit obstacle => despawn
        SpawnHitEffect(other);
        Explode(GetImpactPoint(), other);   // 撞牆、撞地面也會爆炸
        Despawn();
    }

    // ═══════════════════════ 爆炸 ═══════════════════════

    /// <summary>命中點：優先用 Predict 記下來的（跟命中特效同一個位置）。</summary>
    private Vector3 GetImpactPoint()
    {
        Vector3 pos = _hasPendingHitPoint ? _pendingHitPoint : transform.position;
        if (_hasPendingHitPoint) pos += _pendingHitNormal * hitEffectSurfaceOffset;   // 從牆面往外一點，球心不要埋在牆裡
        return pos;
    }

    /// <summary>
    /// 在 position 爆炸：生成特效，並對半徑內的敵人造成均一的爆炸傷害。
    /// 傷害 = 總傷害（暴擊前、距離衰減前）× 各屬性的轉換倍率。
    /// hitCollider 只用來讓特效跟著被打中的會動物體（船、敵人）。
    /// </summary>
    private void Explode(Vector3 position, Collider hitCollider)
    {
        SpawnExplosionEffect(position, hitCollider);

        if (explosionRadius <= 0f) return;

        float total = physicalDamage + explosionDamage + energyDamage + coldDamage;
        if (total <= 0f) return;

        DamageInfo dmg = new DamageInfo(
            total * physicalExplosionDamageConversionRate,
            total * explosionDamageConversionRate,
            total * energyExplosionDamageConversionRate,
            total * coldExplosionDamageConversionRate,
            total * explosionACSConversion
        );

        // 全部都是 0 就不用掃了
        if (dmg.physical <= 0f && dmg.explosion <= 0f && dmg.energy <= 0f && dmg.cold <= 0f && dmg.impact <= 0f)
            return;

        int mask = enemyLayer.value & ~ignoreLayer.value;
        if (mask == 0) return;

        // Collide：玩家的肢體碰撞箱（Hurtbox）是 trigger（敵人的爆炸彈要打得到玩家）
        int count = Physics.OverlapSphereNonAlloc(
            position, explosionRadius, _overlapBuffer, mask, QueryTriggerInteraction.Collide);

        _explosionSeen.Clear();

        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapBuffer[i];
            if (col == null) continue;

            var target = col.GetComponentInParent<IDamageable>();
            if (target == null) continue;

            var comp = target as Component;
            if (comp == null) continue;

            // 同一個目標多個碰撞箱只算一次
            if (!_explosionSeen.Add(comp.GetInstanceID())) continue;

            // 不炸開槍者自己
            if (attacker != null && comp.transform.IsChildOf(attacker.transform)) continue;

            target.TakeDamage(dmg, attacker);
        }
    }

    private void SpawnExplosionEffect(Vector3 position, Collider hitCollider)
    {
        if (explosionPrefab == null) return;

        // 打在會動的物體（船）上就掛成它的子物件，跟著走。
        // 敵人除外：爆炸是留在空間裡的火球，不該黏在敵人身上跟著跑；
        // 而且敵人被炸死回收到物件池（SetActive false）時，掛在牠底下的爆炸會跟著被關掉。
        Rigidbody hitRb = (hitCollider != null) ? hitCollider.attachedRigidbody : null;
        Transform follow = (hitRb != null && hitRb.GetComponentInParent<ModularEntityStats>() == null) ? hitRb.transform : null;

        GameObject fx = PrefabPool.Spawn(explosionPrefab, position, Quaternion.identity, follow);
        if (fx == null) return;

        NormalizeEffectScale(fx.transform, explosionPrefab.transform.localScale);

        // ExplosionEffect 開了「播完自動回收」就交給它，兩邊都排會互相干擾
        var ee = fx.GetComponent<ExplosionEffect>();
        if (ee != null && ee.DespawnWhenFinished) return;

        PrefabPool.Despawn(fx, GetEffectDuration(fx));
    }

    // ═══════════════════════ 命中特效 ═══════════════════════

    /// <summary>
    /// 在命中點生成 hitEffect，並排程讓它播完自己回池子。
    ///
    /// 位置用 transform.position —— Predict 命中時已經把子彈貼到 hit.point，
    /// 所以那就是命中點；沒走預測那條路時，子彈也正好在接觸面附近。
    ///
    /// 特效本身不需要掛任何腳本：存活時間在這裡算好，交給 PrefabPool 延遲歸還。
    ///
    /// 被打的東西有 Rigidbody 時，特效會變成它的子物件，跟著一起移動。
    /// 不這樣做的話，打在行進中的陸行艦上，特效會停在世界座標的原地，
    /// 船開走之後就變成一條拖在船屁股後面的長痕。
    /// </summary>
    protected void SpawnHitEffect(Collider hitCollider)
    {
        SpawnHitEffect(hitCollider, hitEffect, hitEffectLifetime);
    }

    /// <summary>在命中點生成指定的特效（位置、朝向、跟隨與回收方式都和 Hit Effect 相同）。</summary>
    protected void SpawnHitEffect(Collider hitCollider, GameObject effectPrefab, float effectLifetime)
    {
        if (effectPrefab == null) return;

        // 命中點：優先用 Predict 記下來的，結算時的 transform.position 已經穿過牆面了。
        // 位置仍然沿法線往外推一點點（朝向是另一回事），避免特效跟牆面 z-fighting、
        // 或有一半被牆面裁掉。
        Vector3 pos = _hasPendingHitPoint ? _pendingHitPoint : transform.position;
        if (_hasPendingHitPoint) pos += _pendingHitNormal * hitEffectSurfaceOffset;

        // 朝向：子彈飛行方向的反方向，也就是「往來的方向噴回去」。
        // 不用命中面法線 —— 法線只看表面怎麼擺，斜射時噴出來的方向會跟子彈無關；
        // 用來向的反方向，射擊角度才會反映在特效上。
        Vector3 dir = (rb != null) ? -rb.linearVelocity : Vector3.zero;
        if (dir.sqrMagnitude < 0.0001f) dir = -transform.forward;

        Quaternion rot = Quaternion.LookRotation(dir.normalized, Vector3.up);

        // 會動的目標（船、敵人）→ 掛成它的子物件，特效跟著走。
        // 靜止的地形沒有 Rigidbody，維持世界空間即可。
        Rigidbody hitRb = (hitCollider != null) ? hitCollider.attachedRigidbody : null;
        Transform follow = (hitRb != null) ? hitRb.transform : null;

        GameObject fx = PrefabPool.Spawn(effectPrefab, pos, rot, follow);
        if (fx == null) return;

        // 掛到目標底下之後，特效會繼承目標的縮放 —— 船如果不是 1:1:1，
        // 命中特效就會跟著變大變小。這裡把 localScale 反算回去，
        // 讓特效的世界大小永遠等於 prefab 上設定的大小。
        NormalizeEffectScale(fx.transform, effectPrefab.transform.localScale);

        float life = effectLifetime > 0f ? effectLifetime : GetEffectDuration(fx);
        PrefabPool.Despawn(fx, life);
    }

    /// <summary>
    /// 抵銷父物件的縮放，讓特效維持 prefab 設定的世界大小。
    /// 沒有父物件（打在靜態地形上）時直接套用原尺寸。
    /// </summary>
    private static void NormalizeEffectScale(Transform fx, Vector3 desiredWorldScale)
    {
        Transform parent = fx.parent;
        if (parent == null)
        {
            fx.localScale = desiredWorldScale;
            return;
        }

        Vector3 p = parent.lossyScale;

        // 父物件某一軸是 0 時除不下去，那一軸維持原值
        fx.localScale = new Vector3(
            Mathf.Approximately(p.x, 0f) ? desiredWorldScale.x : desiredWorldScale.x / p.x,
            Mathf.Approximately(p.y, 0f) ? desiredWorldScale.y : desiredWorldScale.y / p.y,
            Mathf.Approximately(p.z, 0f) ? desiredWorldScale.z : desiredWorldScale.z / p.z);
    }

    /// <summary>
    /// 特效實際要播多久：所有 Particle System 中 (duration + 最長粒子壽命) 的最大值。
    /// 一個都沒有（純 Animator / AudioSource 特效）時給一個保守的 2 秒。
    /// </summary>
    private static float GetEffectDuration(GameObject fx)
    {
        ParticleSystem[] systems = fx.GetComponentsInChildren<ParticleSystem>(true);
        if (systems.Length == 0) return 2f;

        float longest = 0f;

        for (int i = 0; i < systems.Length; i++)
        {
            var main = systems[i].main;

            // Looping 的特效不會自己結束；靠 hitEffectLifetime 收尾，這裡不讓它拉長總時間
            if (main.loop) continue;

            float span = main.duration + main.startLifetime.constantMax + main.startDelay.constantMax;
            if (span > longest) longest = span;
        }

        return longest > 0f ? longest : 2f;
    }

    // IsInIgnoreLayer 已移除 —— 它是舊的第二套過濾，唯一的呼叫點已改用
    // ShouldReactTo。留著它只會讓人日後不小心又寫出跟 Predict 不同步的判斷。
}