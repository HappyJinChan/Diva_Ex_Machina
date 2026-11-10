using System.Collections;
using UnityEngine;

/// <summary>
/// 掛在槍口 / 砲口物件上的武器：一種彈藥、可選的蓄力。往這個物件的 forward 開火。
///
/// 放在砲塔 Pitch 骨架底下就會跟著 TurretController 的瞄準轉 —— 武器本身不管瞄準。
/// 一座砲塔可以掛好幾個（需要不同彈藥時）。蓄力粒子可以放好幾個（例如槍口和砲管各一個）。TurretController 原本的射擊不受影響。
///
/// 用法（由能力，例如 StationaryChargeAttack 呼叫）：
///   BeginCharge()  播蓄力粒子
///   Fire()         停掉蓄力粒子、播開火粒子、射出 Rounds Per Fire 個 round
///   CancelCharge() 停掉蓄力粒子，不開火（被失衡打斷時）
/// 機體失衡時不會開火。
/// </summary>
[DisallowMultipleComponent]
public class TurretWeapon : MonoBehaviour
{
    [Header("Muzzle")]
    [Tooltip("子彈生成點與方向（forward）。留空 = 這個物件。")]
    [SerializeField] private Transform muzzle;

    [Header("Ammo")]
    public GameObject bulletPrefab;
    public float physicalDamage;
    public float explosionDamage;
    public float energyDamage;
    public float coldDamage;
    public float bulletSpeed = 200f;
    [Tooltip("錐形散布角度（度）。")]
    public float bulletSpread = 0f;
    [Tooltip("每個 round 同時射出幾顆（霰彈）。")]
    public int bulletPerRound = 1;
    [Tooltip("一次開火打幾個 round。")]
    public int roundsPerFire = 1;
    [Tooltip("round 之間的間隔（秒）。")]
    public float timeBetweenShots = 0.1f;

    [Header("Bullet Setup")]
    [Tooltip("子彈允許打到的層（玩家層），寫進 Bullet.enemyLayer。")]
    [SerializeField] private LayerMask bulletTargetLayer;
    [Tooltip("子彈要忽略的層，寫進 Bullet.ignoreLayer。")]
    [SerializeField] private LayerMask bulletIgnoreLayer;
    [Tooltip("傳給 Bullet.attacker。留空 = 根物件。")]
    [SerializeField] private GameObject attacker;

    [Header("VFX")]
    [Tooltip("蓄力期間播放（建議勾 Looping）。可以放好幾個，例如槍口一個、砲管一個。")]
    [SerializeField] private ParticleSystem[] chargeVfx;
    [Tooltip("開火瞬間播放一次（槍口火光），可留空。")]
    [SerializeField] private ParticleSystem fireVfx;

    [Tooltip("開火特效先播，過幾秒子彈才射出（秒）。子彈很快時，玩家才看得到開火的瞬間、有時間反應。0 = 同時。")]
    [SerializeField, Min(0f)] private float fireDelay = 0.5f;

    public bool IsCharging { get; private set; }
    public bool IsFiring { get; private set; }

    /// <summary>開火時（第一個 round 射出前）。</summary>
    public event System.Action<TurretWeapon> OnFired;

    private ModularEntityStats _ownerStats;
    private TurretController _turret;   // 蓄力 / 開火期間讓砲塔用這把武器的彈速做預判

    private void Awake()
    {
        if (muzzle == null) muzzle = transform;
        if (attacker == null) attacker = transform.root.gameObject;
        _ownerStats = GetComponentInParent<ModularEntityStats>();
        _turret = GetComponentInParent<TurretController>();
        StopVfx(chargeVfx);
    }

    private void OnDisable()
    {
        IsCharging = false;
        IsFiring = false;
        StopVfx(chargeVfx);
        ReleaseLead();
    }

    private bool OwnerStaggered => _ownerStats != null && _ownerStats.Status.IsStaggered;

    public void BeginCharge()
    {
        IsCharging = true;
        if (_turret != null && bulletSpeed > 0f) _turret.SetLeadSpeedOverride(bulletSpeed);
        if (chargeVfx == null) return;
        foreach (ParticleSystem ps in chargeVfx)
            if (ps != null) { ps.Clear(true); ps.Play(true); }
    }

    public void CancelCharge()
    {
        IsCharging = false;
        StopVfx(chargeVfx);
        ReleaseLead();
    }

    public void Fire()
    {
        IsCharging = false;
        StopVfx(chargeVfx);
        if (OwnerStaggered || IsFiring || !isActiveAndEnabled) { ReleaseLead(); return; }
        if (_turret != null && bulletSpeed > 0f) _turret.SetLeadSpeedOverride(bulletSpeed);   // 沒蓄力直接開火也要
        StartCoroutine(FireSequence());
    }

    private IEnumerator FireSequence()
    {
        IsFiring = true;
        OnFired?.Invoke(this);
        if (fireVfx != null) fireVfx.Play(true);

        // 先讓玩家看到開火特效，再射出子彈（期間砲塔仍照這把武器的彈速預判瞄準）
        if (fireDelay > 0f)
        {
            yield return new WaitForSeconds(fireDelay);
            if (OwnerStaggered) { IsFiring = false; ReleaseLead(); yield break; }
        }

        int rounds = Mathf.Max(1, roundsPerFire);
        for (int i = 0; i < rounds; i++)
        {
            if (OwnerStaggered) break;

            int pellets = Mathf.Max(1, bulletPerRound);
            for (int p = 0; p < pellets; p++) SpawnBullet();

            if (i < rounds - 1 && timeBetweenShots > 0f)
                yield return new WaitForSeconds(timeBetweenShots);
        }
        IsFiring = false;
        ReleaseLead();
    }

    private void ReleaseLead()
    {
        if (_turret != null) _turret.ClearLeadSpeedOverride();
    }

    // 跟 TurretController.SpawnBullet 相同：欄位一定要在 Spawn 之後寫（Bullet 啟用時會還原成 prefab 值）
    private void SpawnBullet()
    {
        if (bulletPrefab == null) return;

        Quaternion spreadRot = Quaternion.Euler(
            Random.Range(-bulletSpread, bulletSpread),
            Random.Range(-bulletSpread, bulletSpread),
            0f);
        Vector3 dir = spreadRot * muzzle.forward;

        GameObject go = PrefabPool.Spawn(bulletPrefab, muzzle.position, Quaternion.LookRotation(dir));
        if (go == null) return;

        Bullet bullet = go.GetComponent<Bullet>();
        if (bullet == null) return;

        bullet.attacker = attacker;
        bullet.physicalDamage = physicalDamage;
        bullet.explosionDamage = explosionDamage;
        bullet.energyDamage = energyDamage;
        bullet.coldDamage = coldDamage;
        bullet.enemyLayer = bulletTargetLayer;
        bullet.ignoreLayer = bulletIgnoreLayer;

        Rigidbody rb = go.GetComponent<Rigidbody>();
        if (rb != null) rb.linearVelocity = dir.normalized * bulletSpeed;
    }

    private static void StopVfx(ParticleSystem[] list)
    {
        if (list == null) return;
        foreach (ParticleSystem ps in list)
            if (ps != null) ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private void OnDrawGizmosSelected()
    {
        Transform m = muzzle != null ? muzzle : transform;
        Gizmos.color = Color.red;
        Gizmos.DrawRay(m.position, m.forward * 5f);
    }
}
