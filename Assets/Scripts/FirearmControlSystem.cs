using UnityEngine;

[System.Serializable]
public class Firearm
{
    public GameObject turret;
    public GameObject barrel;

    public FiringMode firingMode; // Salvo / ShootingInTurn

    public float turretRotationSpeed;
    public float barrelElevationSpeed;
    public float turretRotationLimit; // if angle is 360, no limit
    public float barrelElevationLimitUp;
    [Tooltip("炮管最多能往水平以下壓幾度（例如 10 → 最低 -10°）。\n" +
             "填負數 = 最低仰角在水平以上：例如 -60 → 炮管最低只到 60°（高射砲用）。")]
    public float barrelElevationLimitDown;

    public Transform[] muzzles; // array of muzzle transforms for firing projectiles
    public Transform muzzleCenterPoint;// central point of muzzles for calculating intersection with targets

    public GameObject bulletPrefab; // prefab of the bullet to be fired
    public float physicalDamage; // physical damage of the bullet
    public float explosionDamage; // explosion damage of the bullet
    public float energyDamage; // energy damage of the bullet
    public float coldDamage; // cold damage of the bullet
    [Tooltip("衝擊值：命中時扣目標的 ACS，扣到 0 觸發失衡。跟傷害一樣吃子彈的距離衰減。")]
    public float impact; // impact of the bullet (ACS damage)

    public float bulletSpeed; // speed of the bullet
    public float bulletSpread; // spread angle for inaccuracy
    public float bulletPerRound = 1; // number of bullets fired per Round
    public float roundsPerFire = 1; // number of round per firing action
    public float timeBetweenShots = 0; // time delay between shots
    public float timeBetweenShooting = 1f;
    public float reloadTime = 5f;
    public float reloadTimer = 0f;
    public int magazineSize = 5; // number of rounds per magazine

    [Header("Per-firearm target (optional)")]
    public Transform target;               // each firearm can have its own target
    public Rigidbody targetRigidbody;      // optional; if null will estimate from position delta
}

public class FirearmControlSystem : MonoBehaviour
{
    [Header("Default target (fallback)")]
    public Transform defaultTarget;

    [Header("Default target velocity source (fallback)")]
    public Rigidbody defaultTargetRigidbody; // optional; if null will estimate from position delta

    public Firearm[] firearms;

    private Vector3[] _lastTargetPos;
    private Vector3[] _targetVel;
    private bool[] _hasLastTargetPos;

    // �O���G�p��W�@�� aimPoint�A���ѵ� IsWithinFireCone �ϥ�
    private Vector3[] _lastAimPoint;

    private struct Runtime
    {
        public Quaternion turretInitialLocalRot;
        public Quaternion barrelInitialLocalRot;

        // yaw/pitch deltas from initial pose
        public float yawDeltaDeg;
        public float pitchDeg; // positive = up

        public bool inited;
    }

    private Runtime[] _rt;

    // ----------------------------
    // Firing runtime (per FireArm)
    // ----------------------------
    private int[] _ammo;                 // currentAmmo in magazine
    private float[] _cooldown;           // time remaining until next firing action allowed
    private bool[] _burstActive;         // currently executing burst
    private int[] _burstRemaining;       // remaining shot events in current burst
    private float[] _burstTimer;         // time until next shot event in burst
    private int[] _nextMuzzleIndex;      // for ShootingInTurn
    private int[] _burstMuzzleIndex;     // fixed muzzle during current burst

    private void Awake()
    {
        if (firearms == null) firearms = new Firearm[0];

        int n = firearms.Length;

        _rt = new Runtime[n];

        _ammo = new int[n];
        _cooldown = new float[n];
        _burstActive = new bool[n];
        _burstRemaining = new int[n];
        _burstTimer = new float[n];
        _nextMuzzleIndex = new int[n];
        _burstMuzzleIndex = new int[n];

        _lastAimPoint = new Vector3[n];

        _lastTargetPos = new Vector3[n];
        _targetVel = new Vector3[n];
        _hasLastTargetPos = new bool[n];
    }

    private void Start()
    {
        for (int i = 0; i < firearms.Length; i++)
        {
            var fa = firearms[i];
            if (fa == null || fa.turret == null || fa.barrel == null) continue;

            _rt[i].turretInitialLocalRot = fa.turret.transform.localRotation;
            _rt[i].barrelInitialLocalRot = fa.barrel.transform.localRotation;
            _rt[i].yawDeltaDeg = 0f;

            // 開場就把炮管放進允許範圍（高射砲最低 60° 時，一開始就是抬起來的）
            GetPitchRange(fa, out float minPitch, out float maxPitch);
            _rt[i].pitchDeg = Mathf.Clamp(0f, minPitch, maxPitch);
            fa.barrel.transform.localRotation =
                Quaternion.AngleAxis(-_rt[i].pitchDeg, Vector3.right) * _rt[i].barrelInitialLocalRot;

            _rt[i].inited = true;

            _ammo[i] = Mathf.Max(0, fa.magazineSize);
            _cooldown[i] = 0f;
            _burstActive[i] = false;
            _burstRemaining[i] = 0;
            _burstTimer[i] = 0f;
            _nextMuzzleIndex[i] = 0;
            _burstMuzzleIndex[i] = 0;

            fa.reloadTimer = Mathf.Max(0f, fa.reloadTimer);
            if (fa.reloadTimer > 0f) _ammo[i] = 0;

            var t = ResolveTarget(fa);
            if (t != null)
            {
                _lastTargetPos[i] = t.position;
                _hasLastTargetPos[i] = true;
            }
        }
    }

    private void Update()
    {
        if (firearms == null) return;
        for (int i = 0; i < firearms.Length; i++)
        {
            var fa = firearms[i];
            if (fa == null) continue;
            UpdateTargetVelocity(i, fa);
            TickFiring(i, fa); // ���u�B�N�o�˼Ưd�b Update
        }
    }

    private void LateUpdate() // �� �˷ǧ�� LateUpdate
    {
        if (firearms == null) return;
        for (int i = 0; i < firearms.Length; i++)
        {
            var fa = firearms[i];
            if (fa == null) continue;
            var t = ResolveTarget(fa);
            if (t != null)
                AimOne(i, fa, t, _targetVel[i]); // ���� defaultTarget �w�Q EnemyBrain ��s����
        }
    }

    private Transform ResolveTarget(Firearm fa)
    {
        if (fa != null && fa.target != null) return fa.target;
        return defaultTarget;
    }

    private Rigidbody ResolveTargetRigidbody(Firearm fa)
    {
        if (fa != null && fa.targetRigidbody != null) return fa.targetRigidbody;
        return defaultTargetRigidbody;
    }

    private void UpdateTargetVelocity(int idx, Firearm fa)
    {
        Transform t = ResolveTarget(fa);
        if (t == null)
        {
            _targetVel[idx] = Vector3.zero;
            _hasLastTargetPos[idx] = false;
            return;
        }

        Rigidbody rb = ResolveTargetRigidbody(fa);
        if (rb != null)
        {
            _targetVel[idx] = rb.linearVelocity;
            _lastTargetPos[idx] = t.position;
            _hasLastTargetPos[idx] = true;
            return;
        }

        float dt = Time.deltaTime;
        if (dt <= 0.0001f) return;

        Vector3 now = t.position;

        if (!_hasLastTargetPos[idx])
        {
            _lastTargetPos[idx] = now;
            _targetVel[idx] = Vector3.zero;
            _hasLastTargetPos[idx] = true;
            return;
        }

        _targetVel[idx] = (now - _lastTargetPos[idx]) / dt;
        _lastTargetPos[idx] = now;
    }

    private void AimOne(int idx, Firearm fa, Transform target, Vector3 targetVel)
    {
        if (fa == null) return;
        if (fa.turret == null || fa.barrel == null) return;
        if (!_rt[idx].inited) return;

        Transform turretT = fa.turret.transform;
        Transform barrelT = fa.barrel.transform;

        Vector3 origin = (fa.muzzleCenterPoint != null) ? fa.muzzleCenterPoint.position : barrelT.position;

        // 瞄 AimPoint（身體中心），不是 target.position（pivot 在腳底 → 打腿）
        Vector3 targetPos = AimPoint.PositionOf(target);
        Vector3 aimPoint = targetPos;
        if (fa.bulletSpeed > 0.01f)
        {
            if (MathToolKit.InterceptionPoint(targetPos, origin, targetVel, fa.bulletSpeed, out var c))
                aimPoint = c;
        }

        _lastAimPoint[idx] = aimPoint;

        UpdateTurretYaw(idx, fa, turretT, aimPoint);
        UpdateBarrelPitch(idx, fa, turretT, barrelT, aimPoint);
    }

    private void UpdateTurretYaw(int idx, Firearm fa, Transform turretT, Vector3 aimPoint)
    {
        Transform parent = turretT.parent;

        Vector3 toAimWorld = aimPoint - turretT.position;
        toAimWorld.y = 0f;
        if (toAimWorld.sqrMagnitude < 0.0001f) return;

        Vector3 dirParent =
            parent != null ? parent.InverseTransformDirection(toAimWorld.normalized) : toAimWorld.normalized;

        float desiredYawDeg = Mathf.Atan2(dirParent.x, dirParent.z) * Mathf.Rad2Deg;

        Vector3 initialFwdWorld = turretT.parent != null
            ? turretT.parent.TransformDirection((_rt[idx].turretInitialLocalRot * Vector3.forward))
            : (_rt[idx].turretInitialLocalRot * Vector3.forward);

        Vector3 initialFwdParent =
            parent != null ? parent.InverseTransformDirection(initialFwdWorld) : initialFwdWorld;

        initialFwdParent.y = 0f;
        if (initialFwdParent.sqrMagnitude < 0.0001f) initialFwdParent = Vector3.forward;

        float initialYawDeg = Mathf.Atan2(initialFwdParent.x, initialFwdParent.z) * Mathf.Rad2Deg;

        float rawDelta = Mathf.DeltaAngle(initialYawDeg, desiredYawDeg);

        float limit = fa.turretRotationLimit;
        if (limit > 0f && limit < 360f)
        {
            float half = limit * 0.5f;
            rawDelta = Mathf.Clamp(rawDelta, -half, half);
        }

        float maxStep = Mathf.Max(0f, fa.turretRotationSpeed) * Time.deltaTime;
        _rt[idx].yawDeltaDeg = Mathf.MoveTowardsAngle(_rt[idx].yawDeltaDeg, rawDelta, maxStep);

        turretT.localRotation = Quaternion.AngleAxis(_rt[idx].yawDeltaDeg, Vector3.up) * _rt[idx].turretInitialLocalRot;
    }

    // 炮管仰角範圍（度，正 = 往上）。
    // 最低仰角 = -barrelElevationLimitDown：正數 = 可往下壓；負數 = 最低仰角在水平以上（高射砲）。
    // 以前用 Mathf.Max(0, limitDown) 把負數吃掉，最低永遠只能到水平。
    private static void GetPitchRange(Firearm fa, out float minPitch, out float maxPitch)
    {
        maxPitch = Mathf.Max(0f, fa.barrelElevationLimitUp);
        minPitch = Mathf.Min(-fa.barrelElevationLimitDown, maxPitch); // 最低不能高過最高
    }

    private void UpdateBarrelPitch(int idx, Firearm fa, Transform turretT, Transform barrelT, Vector3 aimPoint)
    {
        Vector3 toAimWorld = aimPoint - barrelT.position;
        if (toAimWorld.sqrMagnitude < 0.0001f) return;

        Vector3 dirTurret = turretT.InverseTransformDirection(toAimWorld.normalized);

        float desiredPitchUpDeg = Mathf.Atan2(dirTurret.y, Mathf.Max(0.0001f, dirTurret.z)) * Mathf.Rad2Deg;

        GetPitchRange(fa, out float minPitch, out float maxPitch);
        desiredPitchUpDeg = Mathf.Clamp(desiredPitchUpDeg, minPitch, maxPitch);

        float maxStep = Mathf.Max(0f, fa.barrelElevationSpeed) * Time.deltaTime;
        _rt[idx].pitchDeg = Mathf.MoveTowards(_rt[idx].pitchDeg, desiredPitchUpDeg, maxStep);

        barrelT.localRotation = Quaternion.AngleAxis(-_rt[idx].pitchDeg, Vector3.right) * _rt[idx].barrelInitialLocalRot;
    }

    private void TickFiring(int idx, Firearm fa)
    {
        if (fa == null) return;

        float dt = Time.deltaTime;

        if (fa.reloadTimer > 0f)
        {
            fa.reloadTimer -= dt;
            if (fa.reloadTimer <= 0f)
            {
                fa.reloadTimer = 0f;
                _ammo[idx] = Mathf.Max(0, fa.magazineSize);
            }
            return;
        }

        if (_cooldown[idx] > 0f)
        {
            _cooldown[idx] -= dt;
            if (_cooldown[idx] < 0f) _cooldown[idx] = 0f;
        }

        if (!_burstActive[idx]) return;

        if (_burstTimer[idx] > 0f)
        {
            _burstTimer[idx] -= dt;
            if (_burstTimer[idx] > 0f) return;
            _burstTimer[idx] = 0f;
        }

        if (_ammo[idx] <= 0)
        {
            AbortBurstAndStartReload(idx, fa);
            return;
        }

        DoShotEvent(idx, fa);

        _ammo[idx] = Mathf.Max(0, _ammo[idx] - 1);
        _burstRemaining[idx]--;

        if (_burstRemaining[idx] <= 0)
        {
            EndBurst(idx, fa);
        }
        else
        {
            _burstTimer[idx] = Mathf.Max(0f, fa.timeBetweenShots);
        }
    }

    public void FireWeapon(Firearm fireArm)
    {
        if (fireArm == null) return;

        int idx = -1;
        for (int i = 0; i < firearms.Length; i++)
        {
            if (ReferenceEquals(firearms[i], fireArm))
            {
                idx = i;
                break;
            }
        }
        if (idx < 0) return;

        var fa = fireArm;

        if (fa.bulletPrefab == null) return;
        if (fa.muzzles == null || fa.muzzles.Length == 0) return;

        if (_burstActive[idx]) return;
        if (_cooldown[idx] > 0f) return;
        if (fa.reloadTimer > 0f) return;

        int roundsPerFire = Mathf.Max(1, Mathf.RoundToInt(fa.roundsPerFire));

        if (_ammo[idx] < roundsPerFire)
        {
            StartReload(idx, fa);
            return;
        }

        if (fa.firingMode == FiringMode.ShootingInTurn)
        {
            int n = fa.muzzles.Length;
            if (n <= 0) return;
            int next = _nextMuzzleIndex[idx];
            if (next < 0) next = 0;
            if (next >= n) next = next % n;
            _burstMuzzleIndex[idx] = next;
        }
        else
        {
            _burstMuzzleIndex[idx] = 0;
        }

        _burstActive[idx] = true;
        _burstRemaining[idx] = roundsPerFire;
        _burstTimer[idx] = 0f;

        DoShotEvent(idx, fa);
        _ammo[idx] = Mathf.Max(0, _ammo[idx] - 1);
        _burstRemaining[idx]--;

        if (_burstRemaining[idx] <= 0)
        {
            EndBurst(idx, fa);
        }
        else
        {
            _burstTimer[idx] = Mathf.Max(0f, fa.timeBetweenShots);
        }
    }

    private void DoShotEvent(int idx, Firearm fa)
    {
        int pellets = Mathf.Max(1, Mathf.RoundToInt(fa.bulletPerRound));
        float spread = Mathf.Max(0f, fa.bulletSpread);
        float speed = Mathf.Max(0f, fa.bulletSpeed);

        if (fa.firingMode == FiringMode.Salvo)
        {
            for (int m = 0; m < fa.muzzles.Length; m++)
            {
                var muzzle = fa.muzzles[m];
                if (muzzle == null) continue;

                SpawnProjectilesFromMuzzle(muzzle, fa);
            }
        }
        else
        {
            int mi = _burstMuzzleIndex[idx];
            if (mi < 0 || fa.muzzles == null || mi >= fa.muzzles.Length) return;

            var muzzle = fa.muzzles[mi];
            if (muzzle == null) return;

            SpawnProjectilesFromMuzzle(muzzle, fa);
        }
    }

    private void SpawnProjectilesFromMuzzle(Transform muzzle, Firearm fa)
    {
        Vector3 baseDir = muzzle.forward;
        int pellets = Mathf.Max(1, Mathf.RoundToInt(fa.bulletPerRound));
        float spread = Mathf.Max(0f, fa.bulletSpread);
        float speed = Mathf.Max(0f, fa.bulletSpeed);
        for (int i = 0; i < pellets; i++)
        {
            Vector3 dir = baseDir;

            if (spread > 0.0001f)
            {
                float yaw = Random.Range(-spread, spread);
                float pitch = Random.Range(-spread, spread);
                dir = Quaternion.Euler(pitch, yaw, 0f) * baseDir;
                dir.Normalize();
            }

            Quaternion rot = Quaternion.LookRotation(dir, muzzle.up);
            // 物件池取代 Instantiate（跟 TurretController / RangeAttackController 一樣）。池子缺席時內部會退回 Instantiate。
            // ★ 欄位寫入必須在 Spawn 之後：Bullet 被啟用時會把欄位還原成 prefab 值，先寫會被蓋掉。
            GameObject b = PrefabPool.Spawn(fa.bulletPrefab, muzzle.position, rot);
            if (b == null) continue;

            var bulletComp = b.GetComponent<Bullet>();
            if (bulletComp != null)
            {
                bulletComp.attacker = gameObject;
                bulletComp.physicalDamage = fa.physicalDamage;
                bulletComp.explosionDamage = fa.explosionDamage;
                bulletComp.energyDamage = fa.energyDamage;
                bulletComp.coldDamage = fa.coldDamage;
                bulletComp.impact = fa.impact;
            }
            Rigidbody rb = b.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.linearVelocity = dir * speed;
            }
        }
    }

    private void EndBurst(int idx, Firearm fa)
    {
        _burstActive[idx] = false;
        _burstRemaining[idx] = 0;
        _burstTimer[idx] = 0f;

        if (fa.firingMode == FiringMode.ShootingInTurn && fa.muzzles != null && fa.muzzles.Length > 0)
        {
            _nextMuzzleIndex[idx] = (_nextMuzzleIndex[idx] + 1) % fa.muzzles.Length;
        }

        _cooldown[idx] = Mathf.Max(0f, fa.timeBetweenShooting);
    }

    private void StartReload(int idx, Firearm fa)
    {
        fa.reloadTimer = Mathf.Max(0f, fa.reloadTime);
        _ammo[idx] = 0;

        _burstActive[idx] = false;
        _burstRemaining[idx] = 0;
        _burstTimer[idx] = 0f;
    }

    private void AbortBurstAndStartReload(int idx, Firearm fa)
    {
        _burstActive[idx] = false;
        _burstRemaining[idx] = 0;
        _burstTimer[idx] = 0f;
        StartReload(idx, fa);
    }

    public void RequestFireAll(float coneDeg)
    {
        if (firearms == null) return;

        for (int i = 0; i < firearms.Length; i++)
        {
            var fa = firearms[i];
            if (fa == null) continue;

            var t = ResolveTarget(fa);
            if (t == null) continue;

            if (!IsWithinFireCone(i, fa, coneDeg))
                continue;

            FireWeapon(fa);
        }
    }

    private bool IsWithinFireCone(int idx, Firearm fa, float coneDeg)
    {
        if (fa == null) return false;

        Transform dirT = fa.muzzleCenterPoint != null ? fa.muzzleCenterPoint.transform : null;
        if (dirT == null && fa.barrel != null) dirT = fa.barrel.transform;
        if (dirT == null) return false;

        Vector3 origin = (fa.muzzleCenterPoint != null) ? fa.muzzleCenterPoint.position : dirT.position;
        Vector3 toAim = _lastAimPoint[idx] - origin;
        if (toAim.sqrMagnitude < 0.0001f) return false;

        Vector3 fwd = dirT.forward;
        fwd.y = 0f;
        toAim.y = 0f;

        if (fwd.sqrMagnitude < 0.0001f) return false;
        if (toAim.sqrMagnitude < 0.0001f) return false;

        float angle = Vector3.Angle(fwd.normalized, toAim.normalized);
        return angle <= Mathf.Max(0f, coneDeg);
    }
}