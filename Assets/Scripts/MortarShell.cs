using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 迫擊砲彈。由 BallisticTurretController（Fixed Speed High Arc 模式）發射，經 IBallisticProjectile.Launch 啟動。
/// 不繼承任何東西，也不使用 Bullet.cs。
///
/// 飛行：
///   · 自己的重力 = Physics.gravity × Gravity Multiplier（由砲塔傳進來，跟瞄準計算一致）。
///   · 弱誘導：過了最高點才開始，用有上限的加速度（Max Guidance Accel）把速度往
///     「剩下的時間內只靠重力打到目標預測位置」修正。數值很小 —— 主要靠落地爆炸和飽和炮擊命中。
///   · 散布：每顆砲彈發射時抽一個水平偏移（Scatter Radius 以內，面積均勻），誘導瞄的是「目標 + 偏移」。
///     偏移跟著目標走（追移動中的敵人、甲板上的點時一樣散開）。上升段照原本的拋物線，下降段才偏過去。
///     需要誘導（Max Guidance Accel > 0）才會散開。
///   · 有目標就追目標當下的位置；目標消失（被打死）就打它最後的位置；沒有目標就打指定的點。
///     指定的點黏在會動的物體上（例如甲板，FollowSpot）時，追那一點當下的位置。
///
/// 撞擊判定：每一步檢查「上一步到這一步的線段」有沒有碰到 Impact Layers（射線，不靠碰撞），
///   砲彈速度很快也不會穿過去。Arm At Apex：過了最高點才開始判定，從船上射出時不會撞到自己的船身。
///
/// 爆炸：範圍內一律全額，每個目標只算一次，只打 Enemy Layer（沒有誤傷）。特效走 PrefabPool，可跟著 Radius 縮放。
///
/// Prefab：模型 + 這個元件（會自動加 Rigidbody）。不需要 Collider；有的話會在 Awake 被關掉。
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class MortarShell : MonoBehaviour, IBallisticProjectile
{
    [Header("Damage")]
    [Tooltip("爆炸半徑（公尺）。")]
    [SerializeField, Min(0f)] private float radius = 12f;

    [SerializeField, Min(0f)] private float physicalDamage = 0f;
    [SerializeField, Min(0f)] private float explosionDamage = 80f;
    [SerializeField, Min(0f)] private float energyDamage = 0f;
    [SerializeField, Min(0f)] private float coldDamage = 0f;

    [Tooltip("衝擊值：扣目標的 ACS，扣到 0 觸發失衡。")]
    [SerializeField, Min(0f)] private float impact = 40f;

    [Tooltip("會被炸到的層（敵人）。不要勾玩家和船。")]
    [SerializeField] private LayerMask enemyLayer;

    [Tooltip("攻擊者（傷害結算 / 擊殺統計）。MortarBrain 發射時會填入；留空就是砲彈自己。")]
    public GameObject attacker;

    [Header("Impact")]
    [Tooltip("碰到這些層就爆炸（地面、建築、敵人、船…）。不要勾 Bullet 和玩家。")]
    [SerializeField] private LayerMask impactLayers = ~0;

    [Tooltip("過了最高點才開始判定撞擊：從船上射出時不會撞到自己的船身。")]
    [SerializeField] private bool armAtApex = true;

    [Tooltip("離目標這個距離以內就提前引爆（近炸引信）。0 = 只靠撞擊。")]
    [SerializeField, Min(0f)] private float proximityFuse = 0f;

    [Header("Scatter（散布）")]
    [Tooltip("落點散布半徑（公尺）：每顆砲彈落在目標周圍這個半徑以內的隨機位置。0 = 全部瞄正中央。")]
    [SerializeField, Min(0f)] private float scatterRadius = 30f;

    [Header("Guidance（弱誘導）")]
    [Tooltip("修正的最大加速度（m/s²，不含重力）。0 = 不誘導。數值小 = 只做微調。")]
    [SerializeField, Min(0f)] private float maxGuidanceAccel = 8f;

    [Tooltip("修正的反應時間（秒）。越小越積極。")]
    [SerializeField, Min(0.05f)] private float guidanceResponse = 1f;

    [Tooltip("預計落地前這麼多秒就停止修正（避免最後一刻急轉）。")]
    [SerializeField, Min(0f)] private float guidanceStopTime = 0.3f;

    [Tooltip("追蹤敵人時（迫擊砲鎖定目標，FollowTarget）用的最大修正加速度（m/s²）。\n" +
             "比 Max Guidance Accel 大：要追得上移動中的敵人。打點 / 範圍炮擊仍用 Max Guidance Accel。")]
    [SerializeField, Min(0f)] private float targetGuidanceAccel = 30f;

    [Header("Effects")]
    [Tooltip("爆炸特效。走 PrefabPool。")]
    [SerializeField] private GameObject explosionPrefab;

    [Tooltip("特效大小跟著 Radius 縮放。")]
    [SerializeField] private bool scaleEffectWithRadius = true;

    [Tooltip("特效大小倍率（越大越大）。1 = 特效的 Radius 等於傷害半徑；\n" +
             "Explosion Builder 的衝擊環會擴散到約 1.6 倍，所以約 0.6 時衝擊環外緣 ≈ 傷害半徑。")]
    [SerializeField, Min(0.01f)] private float effectSizeMultiplier = 1f;

    [Tooltip("（進階，通常保持 0）Explosion Prefab 在 Explosion Builder 裡設定的 Radius（公尺）。\n" +
             "0 = 自動：prefab 燈光的 Range ÷ 4。放大倍率 = 傷害半徑 ÷ 這個值 × Effect Size Multiplier。")]
    [SerializeField, Min(0f)] private float effectBaseRadius = 0f;

    [Tooltip("特效沒有開 ExplosionEffect 的「播完自動回收」時，幾秒後回池子。")]
    [SerializeField, Min(0.1f)] private float explosionFallbackLifetime = 5f;

    [Header("Lifetime")]
    [Tooltip("飛行超過這個秒數就直接消失（不爆炸）。")]
    [SerializeField, Min(1f)] private float maxLifetime = 60f;

    [SerializeField] private bool logExplosion = false;

    [Header("Debug")]
    [Tooltip("爆炸時畫出傷害半徑（三個互相垂直的圓），用來對照特效大小。\n" +
             "Scene 視窗一定看得到；Game 視窗要打開右上角的 Gizmos。")]
    [SerializeField] private bool debugDrawRadius = false;

    [Tooltip("圓圈留在畫面上的秒數。")]
    [SerializeField, Min(0.1f)] private float debugDrawDuration = 3f;

    [SerializeField] private Color debugDrawColor = new Color(1f, 0.2f, 0.2f, 1f);

    /// <summary>爆炸時觸發。參數：這顆砲彈、爆炸位置。</summary>
    public event Action<MortarShell, Vector3> OnExploded;

    /// <summary>爆炸半徑（公尺）。</summary>
    public float Radius => radius;

    /// <summary>落點散布半徑（公尺）。</summary>
    public float ScatterRadius => scatterRadius;

    /// <summary>現在還在空中的砲彈數（已發射、還沒爆炸 / 消失）。給炮擊範圍顯示之類的用。</summary>
    public static int LiveCount { get; private set; }

    private Rigidbody _rb;
    private bool _launched;
    private bool _exploded;
    private bool _pastApex;
    private float _launchTime;
    private float _arrivalTime;
    private Vector3 _gravity;
    private Vector3 _prevPosition;

    private Transform _target;
    private Rigidbody _targetRb;
    private Vector3 _targetPoint;   // 目標最後的位置 / 指定的點
    private AimSpot _spot;          // 會動的指定點（例如甲板上的一點）；沒有就是 null
    private Vector3 _scatter;       // 這顆砲彈的落點偏移（水平）

    private static readonly Collider[] _overlapBuffer = new Collider[128];
    private static readonly HashSet<int> _seen = new HashSet<int>();

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _rb.useGravity = false;                 // 重力自己加（倍率）
        _rb.detectCollisions = false;           // 撞擊用射線判定，不讓物理把它彈開
        _rb.interpolation = RigidbodyInterpolation.Interpolate;

        // 自己身上的 Collider 關掉：撞擊射線的終點就在砲彈身上，不關會打到自己
        foreach (Collider c in GetComponentsInChildren<Collider>(true))
            c.enabled = false;
    }

    private void Reset()
    {
        // 預設：除了子彈、玩家以外都會引爆（名稱不存在的層會被略過）
        impactLayers = ~LayerMask.GetMask("Bullet", "Player", "Player Hurtbox", "Ignore Raycast");
    }

    // ── IBallisticProjectile ──────────────────────────────────

    public void Launch(Vector3 velocity, float gravityMultiplier, float expectedFlightTime, Transform target, Vector3 targetPoint)
    {
        if (_rb == null) _rb = GetComponent<Rigidbody>();

        transform.SetParent(null, true);
        _rb.isKinematic = false;
        _rb.linearVelocity = velocity;
        if (velocity.sqrMagnitude > 0.0001f)
            transform.rotation = Quaternion.LookRotation(velocity);

        _gravity = Physics.gravity * Mathf.Max(0.01f, gravityMultiplier);
        _launchTime = Time.time;
        _arrivalTime = Time.time + Mathf.Max(0f, expectedFlightTime);

        _target = target;
        _targetRb = target != null ? target.GetComponentInParent<Rigidbody>() : null;
        _targetPoint = target != null ? TurretController.GetAimPoint(target) : targetPoint;

        _spot = null;

        // 散布：圓盤內面積均勻分布（insideUnitCircle 本身就是均勻的，不會擠在中間）
        Vector2 disk = UnityEngine.Random.insideUnitCircle * scatterRadius;
        _scatter = new Vector3(disk.x, 0f, disk.y);

        _prevPosition = _rb.position;
        _pastApex = false;
        _exploded = false;
        if (!_launched) LiveCount++;
        _launched = true;

        Destroy(gameObject, maxLifetime);
    }

    /// <summary>
    /// 改用另一個散布半徑，重新抽一次落點偏移。在 Launch 之後呼叫（MortarBrain 的範圍炮擊用）。
    /// </summary>
    public void SetScatterRadius(float r)
    {
        Vector2 disk = UnityEngine.Random.insideUnitCircle * Mathf.Max(0f, r);
        _scatter = new Vector3(disk.x, 0f, disk.y);
    }

    /// <summary>
    /// 沒有目標時，改追一個會動的點（例如甲板上的一點）。在 Launch 之後呼叫（MortarBrain 會處理）。
    /// </summary>
    /// <summary>
    /// 追蹤一個敵人：過了最高點之後，誘導改追它「當下」的位置（用 Target Guidance Accel）。
    /// 在 Launch 之後呼叫（MortarBrain 鎖定敵人時會處理）。敵人死掉就打它最後的位置。
    /// </summary>
    public void FollowTarget(Transform target)
    {
        if (target == null || !target.gameObject.activeInHierarchy) return;
        _target = target;
        _targetRb = target.GetComponentInParent<Rigidbody>();
        _targetPoint = TurretController.GetAimPoint(target);
        _spot = null;
    }

    public void FollowSpot(AimSpot spot)
    {
        if (_target != null || spot == null) return;
        _spot = spot;
        _targetPoint = spot.Position;
    }

    // ── 飛行 ─────────────────────────────────────────────────

    private void FixedUpdate()
    {
        if (!_launched || _exploded) return;

        // 目標還在就更新最後的位置；被打死（Destroy / 停用）就一直打最後的位置
        if (_target != null && _target.gameObject.activeInHierarchy)
            _targetPoint = TurretController.GetAimPoint(_target);
        else
        {
            // 目標剛被打死：最後的位置如果在甲板上，就黏在甲板上跟著船走
            // （ReferenceEquals：被 Destroy 的物件 == null 也會是 true，要看參照本身還在不在）
            if (!ReferenceEquals(_target, null))
                _spot = AimSpot.AnchoredBelow(_targetPoint);

            _target = null;
            _targetRb = null;
            if (_spot != null) _targetPoint = _spot.Position;   // 甲板上的點：跟著船走
        }

        Vector3 pos = _rb.position;
        Vector3 v = _rb.linearVelocity;
        if (!_pastApex && v.y <= 0f) _pastApex = true;

        // 撞擊 / 近炸：上一步到這一步的線段
        if (!armAtApex || _pastApex)
        {
            if (CheckImpact(_prevPosition, pos)) return;
        }

        Guide(pos, v);
        _rb.AddForce(_gravity, ForceMode.Acceleration);

        if (v.sqrMagnitude > 0.01f)
            _rb.MoveRotation(Quaternion.LookRotation(v));

        _prevPosition = pos;
    }

    private void Guide(Vector3 pos, Vector3 v)
    {
        float maxAccel = _target != null ? targetGuidanceAccel : maxGuidanceAccel;
        if (maxAccel <= 0f || !_pastApex) return;

        float tgo = _arrivalTime - Time.time;
        if (tgo <= guidanceStopTime) return;

        Vector3 targetVel = _targetRb != null ? _targetRb.linearVelocity
                          : _spot != null ? _spot.Velocity
                          : Vector3.zero;
        Vector3 predicted = _targetPoint + _scatter + targetVel * tgo;

        Vector3 required = MathToolKit.BallisticVelocityToReach(pos, predicted, tgo, _gravity);
        Vector3 accel = Vector3.ClampMagnitude((required - v) / guidanceResponse, maxAccel);
        _rb.AddForce(accel, ForceMode.Acceleration);
    }

    private bool CheckImpact(Vector3 from, Vector3 to)
    {
        Vector3 seg = to - from;
        float len = seg.magnitude;

        if (len > 0.0001f &&
            Physics.Raycast(from, seg / len, out RaycastHit hit, len, impactLayers, QueryTriggerInteraction.Ignore))
        {
            Explode(hit.point);
            return true;
        }

        if (proximityFuse > 0f)
        {
            float len2 = seg.sqrMagnitude;
            float u = len2 > 0f ? Mathf.Clamp01(Vector3.Dot(_targetPoint - from, seg) / len2) : 0f;
            Vector3 closest = from + seg * u;
            if ((_targetPoint - closest).sqrMagnitude <= proximityFuse * proximityFuse)
            {
                Explode(closest);
                return true;
            }
        }
        return false;
    }

    // ── 爆炸 ─────────────────────────────────────────────────

    private void Explode(Vector3 position)
    {
        if (_exploded) return;
        _exploded = true;

        SpawnExplosionEffect(position);
        int hits = DealDamage(position);
        if (debugDrawRadius) DrawRadius(position);

        if (logExplosion)
            Debug.Log($"MortarShell: 爆炸，飛行 {Time.time - _launchTime:0.00} 秒，命中 {hits} 個目標，" +
                      $"離目標 {Vector3.Distance(position, _targetPoint):0.0} m（散布點 {_scatter.magnitude:0.0} m）", this);

        OnExploded?.Invoke(this, position);
        Destroy(gameObject);
    }

    private int DealDamage(Vector3 center)
    {
        if (radius <= 0f || enemyLayer.value == 0) return 0;

        var dmg = new DamageInfo(physicalDamage, explosionDamage, energyDamage, coldDamage, impact);
        if (dmg.physical <= 0f && dmg.explosion <= 0f && dmg.energy <= 0f && dmg.cold <= 0f && dmg.impact <= 0f)
            return 0;

        int count = Physics.OverlapSphereNonAlloc(
            center, radius, _overlapBuffer, enemyLayer.value, QueryTriggerInteraction.Collide);

        GameObject source = attacker != null ? attacker : gameObject;
        _seen.Clear();
        int hits = 0;

        for (int i = 0; i < count; i++)
        {
            Collider col = _overlapBuffer[i];
            if (col == null) continue;

            var target = col.GetComponentInParent<IDamageable>();
            if (target == null) continue;

            var comp = target as Component;
            if (comp == null) continue;

            if (!_seen.Add(comp.GetInstanceID())) continue;   // 多個碰撞箱只算一次
            if (attacker != null && comp.transform.IsChildOf(attacker.transform)) continue;

            target.TakeDamage(dmg, source);
            hits++;
        }
        return hits;
    }

    private void SpawnExplosionEffect(Vector3 position)
    {
        if (explosionPrefab == null) return;

        GameObject fx = PrefabPool.Spawn(explosionPrefab, position, Quaternion.identity);
        if (fx == null) return;

        float k = (scaleEffectWithRadius ? radius / BaseRadius : 1f) * effectSizeMultiplier;
        fx.transform.localScale = explosionPrefab.transform.localScale * k;

        var ee = fx.GetComponent<ExplosionEffect>();
        if (ee != null) ee.ScaleLightRange(k);   // 回池子時 ExplosionEffect 會還原縮放與燈光範圍

        if (ee != null && ee.DespawnWhenFinished) return;
        PrefabPool.Despawn(fx, ee != null ? Mathf.Max(ee.GetDuration(), 0.1f) : explosionFallbackLifetime);
    }

    // 傷害半徑：水平一圈（地面上最好對照）＋兩個直立的圈
    private void DrawRadius(Vector3 center)
    {
        const int seg = 48;
        Vector3 prevH = center + new Vector3(radius, 0f, 0f);
        Vector3 prevX = center + new Vector3(radius, 0f, 0f);
        Vector3 prevZ = center + new Vector3(0f, 0f, radius);
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            float c = Mathf.Cos(a) * radius;
            float s = Mathf.Sin(a) * radius;

            Vector3 h = center + new Vector3(c, 0f, s);
            Vector3 x = center + new Vector3(c, s, 0f);
            Vector3 z = center + new Vector3(0f, s, c);

            Debug.DrawLine(prevH, h, debugDrawColor, debugDrawDuration);
            Debug.DrawLine(prevX, x, debugDrawColor, debugDrawDuration);
            Debug.DrawLine(prevZ, z, debugDrawColor, debugDrawDuration);
            prevH = h; prevX = x; prevZ = z;
        }
    }

    private float BaseRadius
    {
        get
        {
            if (effectBaseRadius > 0f) return effectBaseRadius;
            var light = explosionPrefab != null ? explosionPrefab.GetComponentInChildren<Light>(true) : null;
            return (light != null && light.range > 0f) ? light.range / 4f : 2f;   // Explosion Builder：燈光範圍 = 4 × Radius
        }
    }

    private void OnDestroy()
    {
        if (_launched)
        {
            _launched = false;
            LiveCount = Mathf.Max(0, LiveCount - 1);
        }
    }

    // 進入 Play 時歸零（關掉 Domain Reload 時靜態欄位不會自動清掉）
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => LiveCount = 0;

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(1f, 0.5f, 0.1f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, radius);
        if (_launched)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawWireSphere(_targetPoint, 2f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(_targetPoint + _scatter, 1.5f);   // 這顆砲彈實際瞄的點
        }
    }
#endif
}
