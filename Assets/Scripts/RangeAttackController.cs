using System.Collections;
using UnityEngine;

/// <summary>
/// 遠程攻擊的所有行為：開火、彈道、擴散、換彈、射速冷卻。
///
/// 從 AttackManager 拆出來，職責邊界是「開火之後的事」。
/// 武器資料的同步（ApplyHand / ApplyShoulder）、彈藥 UI、船上狀態
/// 都還在 AttackManager，這裡透過 attackManager 參考存取。
///
/// 掛在跟 AttackManager 同一個 GameObject 上。
/// </summary>
[RequireComponent(typeof(AttackManager))]
public class RangeAttackController : MonoBehaviour
{
    [SerializeField] private AttackManager attackManager;

    [Tooltip("玩家特效（槍口、換彈回饋）。留空會從 attackManager.playerAnimation 的物件上自動抓。")]
    [SerializeField] private PlayerEffectManager playerEffects;
    private PlayerEffectManager Effects
    {
        get
        {
            if (playerEffects == null && attackManager != null && attackManager.playerAnimation != null)
                playerEffects = attackManager.playerAnimation.GetComponent<PlayerEffectManager>();
            return playerEffects;
        }
    }

    [Header("Just for testing")]
    [Tooltip("武器沒有指定 bullet 時的後備彈藥 prefab")]
    [SerializeField] private GameObject testBulletPrefab;

    [Header("Recoil")]
    [Tooltip("判定「停火」的門檻，單位是該武器 timeBetweenShooting 的倍數。\n" +
             "超過這個時間沒開火才開始衰減。\n\n" +
             "用倍數而非固定秒數，門檻才會自動隨武器射速縮放 ——\n" +
             "衝鋒槍約 0.1 秒、火砲約 3 秒，不需要各自調參數。")]
    [SerializeField] private float ceaseFireIntervalMultiplier = 1.5f;

    [Header("Aim Override (測試用)")]
    [Tooltip("不為 null 時，所有開火都瞄準這個物件（Collider 中心），忽略準星與鎖定。\n" +
             "給 WeaponBalanceBot 自動測試用 —— 後座偏移與散佈照常套用，只有「瞄準」是理想化的。\n" +
             "平常保持空白。")]
    public Transform aimOverride;

    [Tooltip("aimOverride 目標的速度（世界座標）。不為零時用攔截預測算提前量，跟鎖定瞄準同一套算法。\n" +
             "由 WeaponBalanceBot 每幀寫入。")]
    [HideInInspector] public Vector3 aimOverrideVelocity;

    [Header("Debug")]
    [Tooltip("開火時在 Scene 視圖畫出：黃 = 準星 ray（相機出發）、紅 = 子彈實際方向（槍口出發），持續 1 秒。")]
    [SerializeField] private bool debugDrawFireRay = false;

    private void Awake()
    {
        if (attackManager == null)
            attackManager = GetComponent<AttackManager>();
    }

    private void Reset()
    {
        attackManager = GetComponent<AttackManager>();
    }

    private void Update()
    {
        // 偏移持續衰減 —— 開火期間也在扣。
        float dt = Time.deltaTime;

        DecayDeviation(attackManager.leftHandWeapon, dt);
        DecayDeviation(attackManager.rightHandWeapon, dt);
        DecayDeviation(attackManager.leftShoulderWeapon, dt);
        DecayDeviation(attackManager.rightShoulderWeapon, dt);
    }

    private void DecayDeviation(Weapon w, float dt)
    {
        if (w == null) return;
        if (w.rangeRuntime.accumulatedDeviation <= 0f) return;

        // 只在停火後衰減。射擊期間偏移只會往天花板爬，不會被自己的射擊間隔
        // 抵銷掉一部分 —— 那會讓射速慢的武器變相獲得優勢。
        float interval = Mathf.Max(0.01f, w.range.timeBetweenShooting);
        float ceaseFireDelay = interval * Mathf.Max(1f, ceaseFireIntervalMultiplier);

        if (Time.time < w.rangeRuntime.lastShotTime + ceaseFireDelay) return;

        w.rangeRuntime.accumulatedDeviation = Mathf.Max(
            0f, w.rangeRuntime.accumulatedDeviation - w.range.deviationDecayPerSecond * dt);
    }

    public bool TryStartShoot(Weapon w)
    {
        if (w == null) return false;
        if (w.kind == HandWeaponKind.Melee) return false;

        // 冷卻：readyToShoot = 沒有正在進行的 Shoot()；nextShootTime = 冷卻結束的時間。
        // 直接在這裡比時間，而不是等協程把 readyToShoot 設回 true ——
        // 協程在 Update 之後才跑，冷卻結束的那一幀 Update 已經過了，會白白多等一幀。
        bool ready = w.rangeRuntime.readyToShoot && Time.time >= w.rangeRuntime.nextShootTime;

        // 沒子彈就嘗試換彈（跟 HandleAttack 同邏輯）
        if (ready && !w.rangeRuntime.reloading && w.rangeRuntime.bulletsLeft <= 0)
            StartReload(w);

        // 可以射擊才真正開火
        if (ready && !w.rangeRuntime.reloading && w.rangeRuntime.bulletsLeft > 0)
        {
            // 冷卻是在兩幀之間結束的，這一幀才輪到開火 —— 晚了不到一幀。
            // 把這一發當成「準時」在 nextShootTime 開火，下一發從那個時間往後排，
            // 晚掉的時間就會在下一發補回來，平均射速等於面板數值，不受幀率影響。
            // 晚超過一幀（停火、剛按下扳機）就從現在開始算，不會一次補好幾發。
            float now = Time.time;
            float late = now - w.rangeRuntime.nextShootTime;
            float scheduledStart = (late > 0f && late <= Time.deltaTime) ? w.rangeRuntime.nextShootTime : now;

            w.rangeRuntime.bulletsLeft = Mathf.Max(0, w.rangeRuntime.bulletsLeft);
            w.rangeRuntime.readyToShoot = false;
            StartCoroutine(Shoot(w, PlayerAiming.Instance.GetRay(), scheduledStart));
            return true;
        }

        return false;
    }

    /// <param name="scheduledStart">這次扣扳機「排定」的開火時間（見 TryStartShoot），冷卻從它往後排。</param>
    private IEnumerator Shoot(Weapon w, Ray ray, float scheduledStart)
    {
        w.rangeRuntime.readyToShoot = false;
        int shotsToFire = Mathf.Min(w.range.roundPerTap, w.rangeRuntime.bulletsLeft);

        var bulletPrefab = (w.bullet != null) ? w.bullet : testBulletPrefab;
        var muzzle = w.muzzle;
        if (bulletPrefab == null || muzzle == null)
            yield break;

        // ★ 偏移累加：每呼叫一次 Shoot() 加一次，刻意放在所有迴圈之外。
        //   霰彈槍（bulletPerShot = 8）和點射步槍（roundPerTap = 3）不會一次跳好幾級 ——
        //   一次扣扳機就是一次後座。
        //
        //   天花板由 maxDeviation（ratio 查曲線）決定，所以射速快只是更快到頂，
        //   到不了更高的地方。火砲一發就能打滿，這是刻意的。
        //   肩武器是固定式武裝，recoilPerShooting 恆為 0，這段對它無作用。
        w.rangeRuntime.lastShotTime = Time.time;

        if (!w.isShoulder && w.range.recoilPerShooting > 0f)
        {
            w.rangeRuntime.accumulatedDeviation = Mathf.Min(
                w.range.maxDeviation,
                w.rangeRuntime.accumulatedDeviation + w.range.recoilPerShooting);
        }

        // 偏移的「大小」整次扣扳機共用；「方向」每一輪（Round）骰一次，見下面的 roundDeviation。
        float deviationDeg = w.isShoulder ? 0f : w.rangeRuntime.accumulatedDeviation;

        bool isLeftShoulder = (w == attackManager.leftShoulderWeapon);
        bool isRightShoulder = (w == attackManager.rightShoulderWeapon);

        // 這一輪「排定」的開火時間。點射的間隔和打完之後的冷卻都從它往後排，
        // 不用 WaitForSeconds（它會把每一段等待都進位到整數幀，射速越快損失越多）。
        float scheduled = scheduledStart;

        for (int i = 0; i < shotsToFire; i++)
        {
            // 肩武器：先觸發開火動畫，等 Attack_Fire 開始播放才生成這一輪的子彈。
            // 沒有目標時武器是收著的，這段等待也涵蓋「迅速轉向前方」的時間。
            if ((isLeftShoulder || isRightShoulder) && attackManager.playerAnimation != null)
            {
                yield return attackManager.playerAnimation.FireShoulderAndWait(isLeftShoulder);
                scheduled = Mathf.Max(scheduled, Time.time);   // 等動畫是真的延遲，不補回來
            }

            // 這一輪的後座偏移（圖上的紫點）。只在這一輪的第一顆彈丸骰一次，
            // 之後的彈丸沿用同一個旋轉 —— 霰彈槍整組彈丸一起被推開，再各自散開。
            // 以前是每顆彈丸各骰一次，後座偏移變成第二層散佈：彈丸圓圈中心永遠在準星上，
            // 只是越打越大（散佈 + 偏移），而不是整組被推離準星。
            Quaternion roundDeviation = Quaternion.identity;
            bool hasRoundDeviation = false;

            for (int x = 0; x < w.range.bulletPerShot; x++)
            {
                bool isLeft = (w == attackManager.leftHandWeapon);
                bool isRight = (w == attackManager.rightHandWeapon);

                if (isLeft)
                {
                    if (Effects != null) Effects.PlayLeftMuzzleFeedback();
                    attackManager.playerAnimation.TriggerFireLeft();
                }
                else if (isRight)
                {
                    if (Effects != null) Effects.PlayRightMuzzleFeedback();
                    attackManager.playerAnimation.TriggerFireRight();
                }

                Vector3 targetPoint;

                Vector3 movingPlatformOffset = Vector3.zero;

                // Burst（roundPerTap > 1）每一發都改用「當幀最新」的準星 ray；
                // 原本整個 burst 沿用扣扳機那一刻的舊 ray，相機一轉就會歪。
                Ray fireRay = (PlayerAiming.Instance != null) ? PlayerAiming.Instance.GetRay() : ray;

                bool constrained = PlayerAiming.Instance.lockOn;
                if (aimOverride != null)
                {
                    // 測試機器人：瞄目標中心；目標在動時跟鎖定一樣算攔截點（提前量）
                    Vector3 aimPos = AimPointOf(aimOverride);
                    if (aimOverrideVelocity.sqrMagnitude > 0.0001f &&
                        MathToolKit.InterceptionPoint(aimPos, muzzle.position, aimOverrideVelocity, w.range.bulletSpeed, out var leadPoint))
                        targetPoint = leadPoint;
                    else
                        targetPoint = aimPos;
                }
                else if (constrained)
                {
                    var targetRb = PlayerAiming.Instance.GetTargetRigidbody();
                    if (TrySampleTarget(targetRb, out Vector3 tgtPos, out Vector3 tgtVel))
                    {
                        // 位置改用鎖定的瞄準點（AimPoint）；Rigidbody 只拿來取速度做提前量。
                        // rb.position 是敵人的原點，通常在腳底，直接用會整排打在地上。
                        if (PlayerAiming.Instance.TryGetLockedAimPoint(out Vector3 lockedAim))
                            tgtPos = lockedAim;

                        if (MathToolKit.InterceptionPoint(tgtPos, muzzle.position, tgtVel, w.range.bulletSpeed, out var predicted))
                            targetPoint = predicted;
                        else
                            targetPoint = tgtPos;
                    }
                    else
                    {
                        targetPoint = PlayerAiming.Instance.GetAimPointAlongRay(fireRay);
                        if (attackManager.shipRB != null && attackManager.onShip == true)
                        {
                            movingPlatformOffset = attackManager.shipRB.linearVelocity;
                        }
                    }
                }
                else
                {
                    targetPoint = PlayerAiming.Instance.GetAimPointAlongRay(fireRay);
                    if (attackManager.shipRB != null && attackManager.onShip == true)
                    {
                        movingPlatformOffset = attackManager.shipRB.linearVelocity;
                    }
                }

                Vector3 dirNoSpread = (targetPoint - muzzle.position).normalized;

                // 兩層圓錐取樣：
                //   綠錐（deviation）擾動出「本輪的瞄準方向」= 圖上的紫點（每輪骰一次，整組彈丸共用）
                //   紅錐（spread）  再從紫點往外散開          = 圖上的藍點（每顆彈丸各骰一次）
                // 關鍵是紅錐疊在紫方向上而不是原始方向上 —— 後座把準星推開之後，
                // 散佈是從被推開的位置再往外擴，兩者是累加的。
                //
                // 共用的是「旋轉」而不是方向本身：同一輪的彈丸瞄的是同一個點，
                // 用旋轉套用也不怕 dirNoSpread 之後有微小差異。
                //
                // 鎖定時 dirNoSpread 是攔截預測方向，deviation 照樣生效 ——
                // 後座力對自動鎖定同樣有懲罰，這是 RecoilControl 的價值所在。
                if (!hasRoundDeviation)
                {
                    Vector3 deviated = ApplyConeSpread(dirNoSpread, deviationDeg);
                    roundDeviation = Quaternion.FromToRotation(dirNoSpread, deviated);
                    hasRoundDeviation = true;
                }
                Vector3 dirAfterDeviation = roundDeviation * dirNoSpread;
                Vector3 dirWithSpread = ApplyConeSpread(dirAfterDeviation, w.range.spread);

                if (debugDrawFireRay)
                {
                    Debug.DrawRay(fireRay.origin, fireRay.direction * 100f, Color.yellow, 1f);
                    Debug.DrawRay(muzzle.position, dirNoSpread * 100f, Color.red, 1f);
                }

                // 物件池取代 Instantiate。池子缺席時內部會退回 Instantiate，行為不變。
                var currentBullet = PrefabPool.Spawn(bulletPrefab, muzzle.position , Quaternion.identity);
                if (currentBullet == null) continue;

                var bulletComp = currentBullet.GetComponent<Bullet>();

                // ★ 以下的欄位寫入必須在 Spawn 之後 —— Bullet 在被啟用時會把所有欄位
                //   還原成 prefab 值，先寫會被蓋掉。順序跟原本的 Instantiate 版本一致。
                //
                //   注意這裡不寫 enemyLayer / ignoreLayer，吃的是 prefab 上的值。
                //   池化之後這依然成立，靠的正是 Bullet 會還原預設值 ——
                //   否則回收自砲塔的子彈會帶著「敵人視角的敵我層」過來，玩家會被自己的子彈打到。
                if (bulletComp != null)
                {
                    // 原本 attacker 這行寫在 null 檢查外面，bulletComp 為 null 時會丟例外。
                    bulletComp.attacker = attackManager.playerRb.gameObject;

                    // 1) 傷害（已由 ApplyHand / ApplyShoulder 同步到 w.damage）
                    bulletComp.physicalDamage = w.damage.physicalDamage;
                    bulletComp.explosionDamage = w.damage.explosionDamage;
                    bulletComp.energyDamage = w.damage.energyDamage;
                    bulletComp.coldDamage = w.damage.coldDamage;
                    bulletComp.impact = w.damage.impact;   // 衝擊值（失衡用），命中時跟傷害一起受距離衰減

                    // 2) 暴擊：以武器本體為基礎 + 零件 / 手甲 / 頭盔的加成（由 AttackManager 算好）
                    bulletComp.criticalChance = w.damage.criticalChance;
                    bulletComp.criticalMultiplier = w.damage.criticalMultiplier;

                    // 3) 穿透：由武器的 piercing 屬性決定，蓋掉子彈 prefab 上的 penetration。
                    //    （敵人的子彈不走這裡，仍然吃 prefab 上的值）
                    bulletComp.penetration = w.range.piercing;

                    // 3.5) 距離衰減：全傷距離由武器的 EffectiveRange 決定（0 = 不衰減）。
                    //      繼承的船速告訴子彈，算飛行距離時扣掉（距離 = 彈速 × 飛行時間）。
                    bulletComp.effectiveRange = w.range.effectiveRange;
                    bulletComp.SetInheritedVelocity(movingPlatformOffset);

                    // 4) 發射平台：只給曳光（BulletTracer）用，讓站在船上開火時曳光從槍口延伸出去（見 Bullet.SetPlatform）。
                    //    不看 movingPlatformOffset —— 鎖定目標時它是 0（子彈不繼承船速），
                    //    但鏡頭仍然跟著船走，拖尾一樣要補償。
                    bulletComp.SetPlatform((attackManager.shipRB != null && attackManager.onShip)
                        ? attackManager.shipRB
                        : null);
                }

                var rb = currentBullet.GetComponent<Rigidbody>();
                if (rb) rb.linearVelocity = (dirWithSpread * w.range.bulletSpeed) + movingPlatformOffset;
                //make bullet face the direction it's moving
                // 用方向本身，不要乘上速度：bulletSpeed 為 0 時 forward 會收到零向量，
                // Unity 會忽略這次賦值，子彈就維持 Quaternion.identity 的朝向。
                if (dirWithSpread.sqrMagnitude > 0.0001f)
                {
                    currentBullet.transform.rotation = Quaternion.LookRotation(dirWithSpread.normalized, Vector3.up);
                    if (rb) rb.rotation = currentBullet.transform.rotation;   // 插值的 Rigidbody 要連物理姿態一起設，否則第一幀會被插值蓋回去
                }

                // ★ 站在船上開火：把子彈放到「船的物理姿態」上的槍口位置（見 ToPlatformPhysicsSpace）。
                //   不這樣做，船開得越快，子彈看起來越像是從槍口後方（船行進的反方向）射出來。
                if (rb && attackManager.shipRB != null && attackManager.onShip)
                {
                    Vector3 physicsMuzzle = ToPlatformPhysicsSpace(attackManager.shipRB, muzzle.position);
                    currentBullet.transform.position = physicsMuzzle;
                    rb.position = physicsMuzzle;
                }


            }

            w.rangeRuntime.bulletsLeft--;
            attackManager.PushAmmoUI();

            if (i < shotsToFire - 1)
            {
                // 點射間隔：從上一發的排定時間往後排。至少等一幀（跟原本 WaitForSeconds 一樣，同一幀不會連發）。
                scheduled += Mathf.Max(0f, w.range.timeBetweenShots);
                do { yield return null; } while (Time.time < scheduled);
            }
        }

        // 冷卻：從最後一發的排定時間往後排（冷卻在點射結束後才開始，跟原本一樣）。
        // readyToShoot 立刻放開，實際能不能開火由 TryStartShoot 比對 nextShootTime。
        w.rangeRuntime.nextShootTime = scheduled + Mathf.Max(0f, w.range.timeBetweenShooting);
        w.rangeRuntime.readyToShoot = true;
    }

    /// <summary>
    /// 把「畫面上」的一個點，換算成船在物理模擬中的對應位置。
    ///
    /// 船是插值的 Rigidbody：畫面上的船（transform）比物理位置（rb.position）落後最多一個 physics step。
    /// 槍口跟著畫面上的船走，但子彈是從物理位置開始模擬、之後再以同樣的落後量被畫出來。
    /// 直接拿槍口的畫面位置當子彈的物理起點，等於在船的座標系裡往後退了「船速 × 落後時間」，
    /// 子彈就會畫在槍口後方。換算到船的物理姿態上之後，子彈被畫出來時剛好落在槍口。
    /// 船沒有開插值時兩者相同，換算結果不變。
    /// </summary>
    private static Vector3 ToPlatformPhysicsSpace(Rigidbody platform, Vector3 renderedWorldPoint)
    {
        Transform t = platform.transform;
        Vector3 local = t.InverseTransformPoint(renderedWorldPoint);
        return Matrix4x4.TRS(platform.position, platform.rotation, t.lossyScale).MultiplyPoint3x4(local);
    }

    public void StartReload(Weapon w)
    {
        if (w == null) return;
        if (w.kind == HandWeaponKind.Melee) return;   // 近戰不走換彈流程
        if (w.rangeRuntime.reloading) return;

        w.rangeRuntime.reloading = true;
        w.reloadNormalized = 0f;
        attackManager.PushAmmoUI();
        if (Effects != null) Effects.AnimEvent_Reload();

        if (w.range.reloadTime <= 0f)
        {
            w.reloadNormalized = 1f;
            attackManager.PushAmmoUI();
            FinishReload(w);
            attackManager.PushAmmoUI();
            return;
        }

        StartCoroutine(ReloadCoroutine(w));
    }

    private IEnumerator ReloadCoroutine(Weapon w)
    {
        float elapsed = 0f;

        while (elapsed < w.range.reloadTime)
        {
            elapsed += Time.deltaTime;
            w.reloadNormalized = Mathf.Clamp01(elapsed / w.range.reloadTime);
            attackManager.PushAmmoUI();
            yield return null;
        }

        w.reloadNormalized = 1f;
        attackManager.PushAmmoUI();

        FinishReload(w);
        attackManager.PushAmmoUI();
    }

    private void FinishReload(Weapon w)
    {
        w.rangeRuntime.bulletsLeft = w.range.magazineSize;
        w.rangeRuntime.reloading = false;
        w.reloadNormalized = 0f;
    }

    // 在以 dir 為軸、半角 halfAngleDeg 的圓錐內均勻取一個方向。
    //
    // cosAlpha 用 Lerp(1, cosMax, random) 而非直接對角度取樣 —— 那樣會讓
    // 靠近中心的區域過度密集。這是球冠上的均勻分佈。
    private static Vector3 ApplyConeSpread(Vector3 dir, float halfAngleDeg)
    {
        if (halfAngleDeg <= 0f) return dir;

        Vector3 right = Vector3.Cross(dir, Vector3.up);
        if (right.sqrMagnitude < 0.0001f)
            right = Vector3.Cross(dir, Vector3.right);
        right.Normalize();

        Vector3 up = Vector3.Cross(right, dir);

        float cosMax = Mathf.Cos(halfAngleDeg * Mathf.Deg2Rad);
        float cosAlpha = Mathf.Lerp(1f, cosMax, Random.value);
        float sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - cosAlpha * cosAlpha));
        float phi = Random.Range(0f, Mathf.PI * 2f);

        Vector3 lateral = right * Mathf.Cos(phi) + up * Mathf.Sin(phi);
        return (dir * cosAlpha + lateral * sinAlpha).normalized;
    }

    // 目標的瞄準點：有 Collider 就用 bounds 中心，比 pivot（常在腳底）準
    private static Vector3 AimPointOf(Transform t)
    {
        var col = t.GetComponentInChildren<Collider>();
        return (col != null) ? col.bounds.center : t.position;
    }

    private bool TrySampleTarget(Rigidbody rb, out Vector3 pos, out Vector3 vel)
    {
        pos = default;
        vel = default;

        if (!rb) return false;

        try
        {
            pos = rb.position;
            vel = rb.linearVelocity;
            return true;
        }
        catch (MissingReferenceException)
        {
            return false;
        }
    }
}