using System.Collections;
using UnityEngine;

public class TurretController : MonoBehaviour
{
    public Vector3 PitchPivotPosition => pitchTransform != null ? pitchTransform.position : transform.position;
    public float BulletSpeed => bulletSpeed;
    public Vector3 MuzzlePosition => muzzle != null ? muzzle.position : transform.position;

    /// <summary>
    /// 砲塔這一幀要轉向的世界座標點。
    ///
    /// 由砲塔自己每幀重新計算（見 RefreshAimPoint）。外部請透過 TrackTarget / AimAlongDirection /
    /// AimAtRest / AimAtPoint 指定「要瞄什麼」，不要直接寫這個欄位 —— 追蹤模式下會被下一幀覆蓋。
    /// </summary>
    public Vector3 targetLocation;

    // ── 瞄準模式 ────────────────────────────────────────────────
    //
    // 為什麼不讓 Brain 直接寫 targetLocation：
    // Brain 降頻後每 0.1 秒才思考一次，寫進來的是「當下」的世界座標點。
    // 站在移動的船上時，敵人、玩家、砲塔都跟著船跑，那個點卻留在原地 ——
    // 船速 20 m/s 時，兩次思考之間瞄準點會落後最多 2 公尺，砲塔整段時間都在追一個過時的點。
    // 現在 Brain 只告訴砲塔「瞄誰」，瞄準點由砲塔每幀用目標當下的位置與速度重算。
    private enum AimMode { Point, Target, Direction, Rest }
    private AimMode _aimMode = AimMode.Point;

    private Transform _trackTarget;
    private Rigidbody _trackTargetRb;
    private float _trackExpireTime;
    private Vector3 _aimDirection;

    /// <summary>目前正在追蹤的目標；沒有在追蹤目標（或已逾時）時為 null。給頭部注視之類的表現用。</summary>
    public Transform CurrentTarget =>
        (_aimMode == AimMode.Target && _trackTarget != null && Time.time <= _trackExpireTime) ? _trackTarget : null;

    [Header("Target Tracking")]
    [Tooltip("Brain 多久沒有再呼叫 TrackTarget 就停止追蹤（秒），停在最後的瞄準點。\n" +
             "Brain 每次思考（預設 0.1 秒）都會更新，這只是保險：避免 Brain 換狀態卻沒通知時，砲塔永遠黏著舊目標。")]
    [SerializeField, Min(0.1f)] private float trackTimeout = 0.5f;

    private const float DirectionAimDistance = 20f;   // 方向模式的虛擬點距離（距離不重要，方向才重要）

    public Transform pitchTransform;
    public Transform yawTransform;

    public float pitchSpeed;//degrees per second
    public float yawSpeed;//degrees per second

    [Header("Pitch Limit")]
    [SerializeField] private float minPitch = -10f;
    [SerializeField] private float maxPitch = 30f;

    [Header("Yaw Limit")]
    [SerializeField] private float yawLimit = 30f;   // 以中心為基準 ±yawLimit 度；180 = 無限制
    [SerializeField] private float yawCenter = 0f;   // 中心朝向（相對父物件的 local yaw，通常 0 = 正前方）

    [Header("Aim Raycast")]
    [SerializeField] private Transform muzzle;              // 射線起點（hierarchy 裡的 muzzle）
    [SerializeField] private float aimRayDistance = 500f;
    [SerializeField] private LayerMask aimRayMask = ~0;     // 預設打到所有 layer
    [SerializeField] private bool drawAimGizmo = true;

    // 最近一次 raycast 的結果（給 gizmo 和外部查詢用）
    public Vector3 AimPoint { get; private set; }
    public bool AimHasHit { get; private set; }
    public Transform AimHitTransform { get; private set; }

    [Header("Attack")]
    public GameObject bulletPrefab;
    public float physicalDamage; // physical damage of the bullet
    public float explosionDamage; // explosion damage of the bullet
    public float energyDamage; // energy damage of the bullet
    public float coldDamage; // cold damage of the bullet

    public float bulletSpeed; // speed of the bullet
    public float bulletSpread; // spread angle for inaccuracy
    public float bulletPerRound = 1; // number of bullets fired per Round
    public float roundsPerFire = 1; // number of round per firing action
    public float timeBetweenShots = 0; // round 與 round 之間間隔
    public float timeBetweenShooting = 1f; // fire 與 fire 之間間隔
    public float reloadTime = 5f; //number of second to reload
    public float reloadTimer = 0f;
    public int magazineSize = 5; // number of rounds per magazine

    [Header("Attack - Bullet Setup")]
    [Tooltip("子彈允許打到的層（turret 是敵人 → 設成玩家層），會寫進 Bullet.enemyLayer")]
    [SerializeField] private LayerMask bulletTargetLayer;
    [Tooltip("子彈要忽略的層（通常是自己/其他敵人），會寫進 Bullet.ignoreLayer")]
    [SerializeField] private LayerMask bulletIgnoreLayer;
    [Tooltip("這座砲塔的擁有者，傳給 Bullet.attacker 供傷害結算/友軍判定")]
    [SerializeField] private GameObject attacker;

    // ── 射擊狀態閘門 ──────────────────────────────────────────
    private int ammoInMagazine;      // 目前彈匣剩餘 round 數
    private bool isFiring;           // 正在執行一次 fire 的連發序列
    private bool isReloading;        // 正在換彈
    private float nextFireAllowedTime;   // 下一次允許 fire 的時間（timeBetweenShooting 冷卻）

    public bool IsReloading => isReloading;
    public int AmmoInMagazine => ammoInMagazine;

    private void Start()
    {
        ammoInMagazine = magazineSize;
        if (attacker == null) attacker = gameObject;   // 沒指定就當作自己
    }

    public void Update()
    {
        RefreshAimPoint();
        Yaw();
        Pitch();
        AimRaycast();
    }

    // ============================================================
    // 瞄準指定：由 Brain 呼叫。只設定「瞄什麼」，實際瞄準點每幀在 RefreshAimPoint 重算。
    // ============================================================

    /// <summary>
    /// 追蹤目標：每幀用目標當下的位置（collider 中心）與速度，以自己的彈速算攔截點。
    /// Brain 每次思考都要呼叫一次續約，超過 trackTimeout 沒續約就停在最後的瞄準點。
    /// </summary>
    public void TrackTarget(Transform target)
    {
        if (target == null) return;

        if (target != _trackTarget)
        {
            _trackTarget = target;
            _trackTargetRb = target.GetComponentInParent<Rigidbody>();
        }

        _aimMode = AimMode.Target;
        _trackExpireTime = Time.time + trackTimeout;

        UpdateTrackedAim();   // 立刻算一次：同一次思考裡緊接著的 HasLineOfSightTo / Shoot 用的就是最新的點
    }

    /// <summary>沿某個水平方向放平（例如移動方向）。點每幀跟著砲塔位置走，船開多快都不會落後。</summary>
    public void AimAlongDirection(Vector3 worldDirection)
    {
        worldDirection.y = 0f;
        if (worldDirection.sqrMagnitude < 0.0001f)
        {
            AimAtRest();
            return;
        }

        ClearTrackTarget();
        _aimMode = AimMode.Direction;
        _aimDirection = worldDirection.normalized;
        targetLocation = PitchPivotPosition + _aimDirection * DirectionAimDistance;
    }

    /// <summary>回到中立朝向（RestAimPoint），每幀重算。</summary>
    public void AimAtRest()
    {
        ClearTrackTarget();
        _aimMode = AimMode.Rest;
        targetLocation = RestAimPoint;
    }

    /// <summary>瞄準一個固定的世界座標點（不會跟著任何東西移動）。</summary>
    public void AimAtPoint(Vector3 worldPoint)
    {
        ClearTrackTarget();
        _aimMode = AimMode.Point;
        targetLocation = worldPoint;
    }

    private void ClearTrackTarget()
    {
        _trackTarget = null;
        _trackTargetRb = null;
    }

    private void RefreshAimPoint()
    {
        switch (_aimMode)
        {
            case AimMode.Target:
                // 目標被摧毀 / Brain 太久沒續約 → 停在最後算出的點（跟以前「沒人更新就停住」的行為一樣）
                if (_trackTarget == null || Time.time > _trackExpireTime)
                {
                    ClearTrackTarget();
                    _aimMode = AimMode.Point;
                    break;
                }
                UpdateTrackedAim();
                break;

            case AimMode.Direction:
                targetLocation = PitchPivotPosition + _aimDirection * DirectionAimDistance;
                break;

            case AimMode.Rest:
                targetLocation = RestAimPoint;
                break;

            // AimMode.Point：固定點，不用重算
        }
    }

    /// <summary>
    /// 以目標當下的位置與速度、自己的彈速算攔截點。
    /// 算法跟原本 Brain 裡的完全相同，只是從「每次思考算一次」變成「每幀算一次」。
    /// </summary>
    private void UpdateTrackedAim()
    {
        // 瞄 collider 中心而不是 pivot（砲塔 / 建築的 pivot 在地面，會打到基座）
        Vector3 aimPoint = GetAimPoint(_trackTarget);
        Vector3 targetVelocity = _trackTargetRb != null ? _trackTargetRb.linearVelocity : Vector3.zero;

        if (bulletSpeed > 0f &&
            MathToolKit.InterceptionPoint(aimPoint, MuzzlePosition, targetVelocity, bulletSpeed, out Vector3 intercept))
        {
            targetLocation = intercept;
        }
        else
        {
            // 解不出攔截（目標太快 / 彈太慢）→ 直接瞄準目標現在的位置
            targetLocation = aimPoint;
        }
    }

    // ============================================================
    // 開火入口：由 brain 每幀呼叫。內部走閘門，重複呼叫不會疊放。
    // 條件不滿足（連發中 / 換彈中 / 冷卻未到 / 沒子彈）就安靜跳過。
    // ============================================================
    public void Shoot()
    {
        if (isFiring || isReloading) return;              // 正在連發或換彈 → 忽略
        if (Time.time < nextFireAllowedTime) return;      // fire 冷卻未到 → 忽略

        if (ammoInMagazine <= 0)                          // 沒子彈 → 自動換彈
        {
            Reload();
            return;
        }

        StartCoroutine(FireSequence());
    }

    // 一次 fire：連續打 roundsPerFire 個 round，round 之間隔 timeBetweenShots
    private IEnumerator FireSequence()
    {
        isFiring = true;

        int rounds = Mathf.Max(1, Mathf.RoundToInt(roundsPerFire));
        for (int i = 0; i < rounds; i++)
        {
            if (ammoInMagazine <= 0) break;   // 連發途中打空就停

            FireOneRound();
            ammoInMagazine--;

            // 最後一個 round 後不用再等 round 間隔
            if (i < rounds - 1 && timeBetweenShots > 0f)
                yield return new WaitForSeconds(timeBetweenShots);
        }

        // 這次 fire 結束 → 設定下一次 fire 的冷卻
        nextFireAllowedTime = Time.time + timeBetweenShooting;
        isFiring = false;

        // 打空了就接著換彈
        if (ammoInMagazine <= 0)
            Reload();
    }

    // 一個 round：同時噴 bulletPerRound 顆子彈（霰彈感）
    private void FireOneRound()
    {
        if (bulletPrefab == null || muzzle == null) return;

        int pellets = Mathf.Max(1, Mathf.RoundToInt(bulletPerRound));
        for (int i = 0; i < pellets; i++)
            SpawnBullet();
    }

    // spawn 單顆子彈：套用散布、設定傷害/層/速度，用 Rigidbody 推進（配合 Bullet.cs）
    private void SpawnBullet()
    {
        // 基準方向：槍管前方；bulletSpread 為錐形散布角度
        Quaternion spreadRot = Quaternion.Euler(
            Random.Range(-bulletSpread, bulletSpread),
            Random.Range(-bulletSpread, bulletSpread),
            0f
        );
        Vector3 dir = spreadRot * muzzle.forward;

        // 物件池取代 Instantiate。池子缺席時內部會退回 Instantiate，行為不變。
        GameObject go = PrefabPool.Spawn(bulletPrefab, muzzle.position, Quaternion.LookRotation(dir));
        if (go == null) return;

        // ★ 以下的欄位寫入必須在 Spawn 之後。
        //   Bullet 在被啟用時會把所有欄位還原成 prefab 值（含 penetration、
        //   collisionDetectionMode），先寫會被蓋掉。順序跟原本的 Instantiate 版本一致。
        //
        //   順帶一提：這裡不寫 criticalChance / criticalMultiplier，所以砲塔子彈吃的是
        //   prefab 上的暴擊值。在池化之前這是理所當然的；池化之後是因為 Bullet 會還原
        //   預設值才依然成立 —— 否則回收自玩家的子彈會帶著玩家的暴擊率過來。
        Bullet bullet = go.GetComponent<Bullet>();
        if (bullet != null)
        {
            bullet.attacker = attacker;

            bullet.physicalDamage = physicalDamage;
            bullet.explosionDamage = explosionDamage;
            bullet.energyDamage = energyDamage;
            bullet.coldDamage = coldDamage;

            // enemyLayer = 「這顆子彈允許打到的層」→ turret 是敵人，設成玩家層
            bullet.enemyLayer = bulletTargetLayer;
            bullet.ignoreLayer = bulletIgnoreLayer;

            // 用 Rigidbody 速度推進（Bullet 靠 rb.linearVelocity 飛 + 預測 linecast）
            Rigidbody rb = go.GetComponent<Rigidbody>();
            if (rb != null)
                rb.linearVelocity = dir.normalized * bulletSpeed;
        }
    }

    // ============================================================
    // 換彈：由 Shoot() 在打空時自動觸發，也可由 brain 主動呼叫（例如提前換彈）
    // ============================================================
    public void Reload()
    {
        if (isReloading) return;
        if (ammoInMagazine >= magazineSize) return;   // 滿彈不用換
        StartCoroutine(ReloadSequence());
    }

    private IEnumerator ReloadSequence()
    {
        isReloading = true;
        reloadTimer = 0f;

        // 用計時器跑，方便你之後接 UI 進度條（reloadTimer / reloadTime）
        while (reloadTimer < reloadTime)
        {
            reloadTimer += Time.deltaTime;
            yield return null;
        }

        ammoInMagazine = magazineSize;
        reloadTimer = 0f;
        isReloading = false;
    }

    // 從砲口沿槍管前方打一條射線，得出「槍管實際指向的位置」
    public void AimRaycast()
    {
        if (muzzle == null) return;

        Vector3 origin = muzzle.position;
        Vector3 dir = muzzle.forward;   // 槍管的前方

        // QueryTriggerInteraction.Ignore：不要被自己的視界 trigger 之類的東西擋住
        if (Physics.Raycast(origin, dir, out RaycastHit hit, aimRayDistance, aimRayMask, QueryTriggerInteraction.Ignore))
        {
            AimPoint = hit.point;
            AimHasHit = true;
            AimHitTransform = hit.transform;
        }
        else
        {
            // 沒打到東西：回傳射線盡頭，這樣 AimPoint 永遠有意義
            AimPoint = origin + dir * aimRayDistance;
            AimHasHit = false;
            AimHitTransform = null;
        }
    }
    /// <summary>
    /// 目標的瞄準點：目標身上有 Collider 就打它的中心，否則退回 pivot。
    /// 建築 / 砲塔的 pivot 通常在地面，直接瞄 position 會打到基座。
    /// </summary>
    public static Vector3 GetAimPoint(Transform target)
    {
        if (target == null) return Vector3.zero;
        if (target.TryGetComponent(out Collider col) && col.enabled)
            return col.bounds.center;
        return target.position;
    }

    public bool HasLineOfSightTo(Transform target)
    {
        if (target == null || muzzle == null) return false;

        Vector3 origin = muzzle.position;
        Vector3 toTarget = GetAimPoint(target) - origin;
        float dist = toTarget.magnitude;
        if (dist < 0.001f) return true;   // 貼在一起,視為可見

        if (Physics.Raycast(origin, toTarget / dist, out RaycastHit hit, dist, aimRayMask, QueryTriggerInteraction.Ignore))
        {
            // 打到的東西是目標自己(或目標的子 collider)→ 視線通
            if (hit.transform == target || hit.transform.IsChildOf(target)) return true;

            // 打到同一個「可受傷物件」的其他部位也算視線通。
            // 例：鎖定的是砲塔頭部，射線先碰到同一座砲塔的基座 ——
            // 基座不是頭部的子物件，但打下去一樣會扣到同一個 BuildingStats。
            IDamageable targetOwner = target.GetComponentInParent<IDamageable>();
            if (targetOwner != null && hit.collider.GetComponentInParent<IDamageable>() == targetOwner)
                return true;

            return false;
        }

        // 射線全程沒打到任何東西 → 中間沒障礙,視線通
        return true;
    }
    public void Yaw()
    {
        if (yawTransform == null) return;

        Vector3 dir = targetLocation - yawTransform.position;

        Transform parent = yawTransform.parent;
        Vector3 localDir = parent != null ? parent.InverseTransformDirection(dir) : dir;

        float targetYaw = Mathf.Atan2(localDir.x, localDir.z) * Mathf.Rad2Deg;

        // ★ 夾在中心 ±yawLimit 內（180 視為無限制，直接跳過）
        if (yawLimit < 180f)
        {
            // 目標相對「中心」偏了幾度（DeltaAngle 會處理 359°→1° 的環繞，給出 -180~180）
            float offset = Mathf.DeltaAngle(yawCenter, targetYaw);
            offset = Mathf.Clamp(offset, -yawLimit, yawLimit);
            targetYaw = yawCenter + offset;
        }

        float currentYaw = yawTransform.localEulerAngles.y;
        float newYaw = Mathf.MoveTowardsAngle(currentYaw, targetYaw, yawSpeed * Time.deltaTime);

        Vector3 e = yawTransform.localEulerAngles;
        yawTransform.localEulerAngles = new Vector3(e.x, newYaw, e.z);
    }

    public void Pitch()
    {
        if (pitchTransform == null) return;

        Vector3 dir = targetLocation - pitchTransform.position;

        Transform parent = pitchTransform.parent;
        Vector3 localDir = parent != null ? parent.InverseTransformDirection(dir) : dir;

        float horizontal = new Vector2(localDir.x, localDir.z).magnitude;
        float targetPitch = -Mathf.Atan2(localDir.y, horizontal) * Mathf.Rad2Deg;

        targetPitch = Mathf.Clamp(targetPitch, minPitch, maxPitch);

        float currentPitch = pitchTransform.localEulerAngles.x;
        float newPitch = Mathf.MoveTowardsAngle(currentPitch, targetPitch, pitchSpeed * Time.deltaTime);

        Vector3 e = pitchTransform.localEulerAngles;
        pitchTransform.localEulerAngles = new Vector3(newPitch, e.y, e.z);
    }

    private void OnDrawGizmos()
    {
        if (!drawAimGizmo || muzzle == null) return;

        // 命中 = 紅色實線到命中點 + 命中點畫球；未命中 = 黃色虛擬射線到盡頭
        if (AimHasHit)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(muzzle.position, AimPoint);
            Gizmos.DrawSphere(AimPoint, 0.25f);
        }
        else
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(muzzle.position, muzzle.position + muzzle.forward * aimRayDistance);
        }

        // 目標點畫成綠色，方便對照「槍管實際指向」vs「想指向的目標」
        Gizmos.color = Color.green;
        Gizmos.DrawWireSphere(targetLocation, 0.5f);
    }

    public Vector3 RestAimPoint
    {
        get
        {
            // 用 yaw 的父空間定義「中心朝向」——跟 Yaw() 的計算基準一致
            Transform basis = yawTransform != null ? yawTransform.parent : transform;
            if (basis == null) basis = transform;

            Vector3 centerDir = basis.rotation * Quaternion.Euler(0f, yawCenter, 0f) * Vector3.forward;

            // 起點用 pitch 軸的位置：目標與它同高 → localDir.y = 0 → pitch 歸零（放平）
            Vector3 origin = pitchTransform != null ? pitchTransform.position : transform.position;

            return origin + centerDir * 20f;   // 20m 遠處的虛擬點，距離不重要，方向才重要
        }
    }
}