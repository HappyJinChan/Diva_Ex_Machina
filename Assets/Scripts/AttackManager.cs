using System.Collections.Generic;
using UnityEngine;

public class AttackManager : MonoBehaviour
{
    // ───── 後座力 ─────
    //
    // 設計師只設定 RecoilPerShooting —— 每次扣扳機造成的偏移角度（度）。
    // 它本身就是角度，不需要任何換算係數。
    //
    // 玩家看到的 Recoil 是
    //     Recoil = RecoilPerShooting / TimeBetweenShooting × 1000
    // 也就是每秒偏移量再放大 1000 倍。射速已經吃進去，衝鋒槍（0.1 度/發 × 14 發/秒）和
    // 火砲（4 度/發 × 0.5 發/秒）可以直接對比，玩家不必在腦中乘一次射速。
    //
    // ×1000 純粹是為了讓 Recoil 與防具上的 RecoilControl 落在同一個量級
    // （數百 ~ 數千）。ratio 是兩者相除，所以這個縮放對比值沒有影響 ——
    // 只是讓玩家看到 1429 而不是 1.429。
    //
    // 玩家的判讀基準（ratio = Recoil / RecoilControl）：
    //     ratio ≈ 0.5 → 精準
    //     ratio ≈ 1.0 → 可用
    //     ratio ≈ 2.0 → 打不中
    //
    // 用曲線而不是公式：ratio ≤ 1 的區段是線性的（控制力足夠時線性回報），
    // 超過 1 之後才變陡（不足時懲罰加速）。單一公式表達不了這個轉折，
    // 而且曲線可以直接在 Inspector 拉，不用改程式碼。

    [Header("Recoil Curves")]
    [Tooltip("X = ratio (Recoil / RecoilControl)，Y = 偏移天花板（度）。\n" +
             "錨點：0.1→0.2  0.5→1  1.0→2  2.0→5")]
    [SerializeField]
    private AnimationCurve maxDeviationByRatio = new AnimationCurve(
        new Keyframe(0.1f, 0.2f), new Keyframe(0.5f, 1f),
        new Keyframe(1f, 2f), new Keyframe(2f, 5f));

    [Tooltip("X = ratio，Y = 從天花板恢復到 0 所需秒數。\n" +
             "錨點：0.1→0.3s  0.5→0.6s  1.0→1.0s  2.0→2.0s")]
    [SerializeField]
    private AnimationCurve recoveryTimeByRatio = new AnimationCurve(
        new Keyframe(0.1f, 0.3f), new Keyframe(0.5f, 0.6f),
        new Keyframe(1f, 1f), new Keyframe(2f, 2f));

    [Tooltip("timeBetweenShooting 的下限，避免除零讓 Recoil 變成無限大")]
    [SerializeField] private float minShotInterval = 0.01f;

    // Recoil 的顯示縮放。只影響顯示數字與 RecoilControl 的量級對齊，
    // 不影響 ratio（兩者相除時約掉）。
    private const float RecoilDisplayScale = 1000f;

    public Rigidbody shipRB;
    public bool onShip;

    public PlayerAnimation playerAnimation;

    [Header("Sub Controllers")]
    [Tooltip("遠程攻擊的實作。掛在同一個 GameObject 上，Awake 會自動抓。")]
    [SerializeField] private RangeAttackController rangeController;

    [Tooltip("近戰連段的實作。掛在同一個 GameObject 上，Awake 會自動抓。")]
    [SerializeField] private MeleeAttackController meleeController;

    public Weapon leftHandWeapon;
    public Weapon rightHandWeapon;
    public Weapon leftShoulderWeapon;
    public Weapon rightShoulderWeapon;

    // 用來判斷「是否換了一把武器」，以便重置子彈等 runtime state
    private RangeWeaponInstance _leftRangeWeaponSource;
    private RangeWeaponInstance _rightRangeWeaponSource;

    // Melee Weapon Source cache
    private MeleeWeaponInstance _leftMeleeWeaponSource;
    private MeleeWeaponInstance _rightMeleeWeaponSource;

    // Shoulder Weapon Scource cache
    private ShoulderWeaponInstance _leftShoulderWeaponSource;
    private ShoulderWeaponInstance _rightShoulderWeaponSource;

    [Header("Melee")]
    [Tooltip("blade × handle → 武器類型 的組合規則表。鍛造 UI 與戰鬥系統共用同一份資產。")]
    [SerializeField] private MeleeStanceRules stanceRules;

    [Header("Optional: used to add player velocity to sword slash")]
    [SerializeField] public Rigidbody playerRb;


    private void Awake()
    {
        if (playerRb == null)
            playerRb = GetComponentInParent<Rigidbody>();

        if (rangeController == null)
            rangeController = GetComponent<RangeAttackController>();

        if (meleeController == null)
            meleeController = GetComponent<MeleeAttackController>();
    }
    private void OnEnable()
    {
        if (PlayerStats.Instance != null)
            PlayerStats.Instance.OnHandWeaponDataChanged += SyncFromPlayerStats;

        SyncFromPlayerStats();
        PushAmmoUI();
    }

    private void OnDisable()
    {
        if (PlayerStats.Instance != null)
            PlayerStats.Instance.OnHandWeaponDataChanged -= SyncFromPlayerStats;
    }

    private void SyncFromPlayerStats()
    {
        var stats = PlayerStats.Instance;
        if (stats == null) return;

        // 注意：左右手一起重算。握持方式（單手 / 雙手）取決於「另一隻手」，
        // 所以卸下副手時主手必須跟著重算，這裡順序無關但兩邊都要跑。
        ApplyHand(stats.leftHand, leftHandWeapon, true, stanceRules,
                  ref _leftRangeWeaponSource, ref _leftMeleeWeaponSource);
        ApplyHand(stats.rightHand, rightHandWeapon, false, stanceRules,
                  ref _rightRangeWeaponSource, ref _rightMeleeWeaponSource);


        ApplyShoulder(stats.leftShoulder, leftShoulderWeapon, ref _leftShoulderWeaponSource);
        ApplyShoulder(stats.rightShoulder, rightShoulderWeapon, ref _rightShoulderWeaponSource);

        PushAmmoUI();
    }

    private static void ClearRangeOutput(Weapon outWeapon, ref RangeWeaponInstance cachedSource)
    {
        cachedSource = null;

        outWeapon.bullet = null;
        outWeapon.muzzle = null;

        outWeapon.damage.physicalDamage = 0f;
        outWeapon.damage.explosionDamage = 0f;
        outWeapon.damage.energyDamage = 0f;
        outWeapon.damage.coldDamage = 0f;
        outWeapon.damage.criticalChance = 0f;
        outWeapon.damage.criticalMultiplier = 1f;

        outWeapon.range.reloadTime = 0f;
        outWeapon.range.bulletPerShot = 0;
        outWeapon.range.roundPerTap = 0;
        outWeapon.range.timeBetweenShooting = 0f;
        outWeapon.range.timeBetweenShots = 0f;
        outWeapon.range.spread = 0f;
        outWeapon.range.recoilPerShooting = 0f;
        outWeapon.range.recoil = 0f;
        outWeapon.range.recoilControl = 0f;
        outWeapon.range.recoilRatio = 0f;
        outWeapon.range.maxDeviation = 0f;
        outWeapon.range.deviationDecayPerSecond = 0f;
        outWeapon.rangeRuntime.accumulatedDeviation = 0f;
        outWeapon.rangeRuntime.accumulatedDeviation = 0f;
        outWeapon.range.magazineSize = 0;
        outWeapon.range.bulletSpeed = 0f;
        outWeapon.range.firingMode = 0;

        // runtime 不一定要清，但清咗 Inspector 會更直觀
        outWeapon.rangeRuntime.bulletsLeft = 0;
        outWeapon.rangeRuntime.shooting = false;
        outWeapon.rangeRuntime.reloading = false;
        outWeapon.rangeRuntime.readyToShoot = false;
        outWeapon.rangeRuntime.allowInvoke = false;
    }

    private static void ClearMeleeOutput(Weapon outWeapon, ref MeleeWeaponInstance cachedSource)
    {
        cachedSource = null;

        outWeapon.melee.weaponClass = default;
        outWeapon.melee.grip = default;
        outWeapon.melee.meleeOutput = 1f;
        outWeapon.melee.meleeSpeed = 1f;
        outWeapon.melee.reloadTime = 0f;
        outWeapon.melee.slashVfx = null;
        outWeapon.melee.hitbox = null;

        outWeapon.meleeRuntime.reloading = false;
        outWeapon.meleeRuntime.attacking = false;
        outWeapon.meleeRuntime.comboIndex = -1;
        outWeapon.meleeRuntime.cooldownNormalized = 0f;
    }

    private static void ClearWeaponOutput(Weapon outWeapon, ref ShoulderWeaponInstance cachedSource)
    {
        cachedSource = null;

        outWeapon.bullet = null;
        outWeapon.muzzle = null;

        outWeapon.damage.physicalDamage = 0f;
        outWeapon.damage.explosionDamage = 0f;
        outWeapon.damage.energyDamage = 0f;
        outWeapon.damage.coldDamage = 0f;
        outWeapon.damage.criticalChance = 0f;
        outWeapon.damage.criticalMultiplier = 1f;

        outWeapon.range.reloadTime = 0f;
        outWeapon.range.bulletPerShot = 0;
        outWeapon.range.roundPerTap = 0;
        outWeapon.range.timeBetweenShooting = 0f;
        outWeapon.range.timeBetweenShots = 0f;
        outWeapon.range.spread = 0f;
        outWeapon.range.recoilPerShooting = 0f;
        outWeapon.range.recoil = 0f;
        outWeapon.range.recoilControl = 0f;
        outWeapon.range.recoilRatio = 0f;
        outWeapon.range.maxDeviation = 0f;
        outWeapon.range.deviationDecayPerSecond = 0f;
        outWeapon.rangeRuntime.accumulatedDeviation = 0f;
        outWeapon.rangeRuntime.accumulatedDeviation = 0f;
        outWeapon.range.magazineSize = 0;
        outWeapon.range.bulletSpeed = 0f;
        outWeapon.range.firingMode = 0;

        // runtime 不一定要清，但清咗 Inspector 會更直觀
        outWeapon.rangeRuntime.bulletsLeft = 0;
        outWeapon.rangeRuntime.shooting = false;
        outWeapon.rangeRuntime.reloading = false;
        outWeapon.rangeRuntime.readyToShoot = false;
        outWeapon.rangeRuntime.allowInvoke = false;
    }
    // 暴擊：以武器本體為基礎（覆蓋玩家基礎暴擊）+ 零件 / 手甲 / 頭盔的武器側 buff。開火 / 揮刀時直接讀 outWeapon.damage。
    private static void ApplyCritical(List<EquipmentBuff> weaponBuffs, Weapon outWeapon)
    {
        var ps = PlayerStats.Instance;
        outWeapon.damage.criticalChance = (ps != null) ? ps.GetCriticalChanceFor(weaponBuffs) : 0f;
        outWeapon.damage.criticalMultiplier = (ps != null) ? ps.GetCriticalMultiplierFor(weaponBuffs) : 1f;
    }

    private void ApplyHand(WeaponStats hand, Weapon outWeapon, bool isLeftHand,
                                  MeleeStanceRules stanceRules,
                                  ref RangeWeaponInstance cachedRange,
                                  ref MeleeWeaponInstance cachedMelee)
    {
        if (outWeapon == null) return;

        // 沒武器：兩邊都清
        if (hand == null || hand.weaponKind == HandWeaponKind.None || !hand.HasWeapon)
        {
            outWeapon.kind = HandWeaponKind.None;
            ClearRangeOutput(outWeapon, ref cachedRange);
            ClearMeleeOutput(outWeapon, ref cachedMelee);
            return;
        }

        outWeapon.kind = hand.weaponKind;
        outWeapon.isShoulder = false;

        // 分流：遠程 / 近戰
        if (hand.weaponKind == HandWeaponKind.Range)
        {
            ClearMeleeOutput(outWeapon, ref cachedMelee);   // 從刀換回槍，清掉近戰殘留

            if (hand.rangeweapon == null)
            {
                ClearRangeOutput(outWeapon, ref cachedRange);
                return;
            }

            // 如果換武器：重置 runtime state（子彈/裝填狀態等）
            bool changedWeapon = cachedRange != hand.rangeweapon;
            cachedRange = hand.rangeweapon;

            // 1) 子彈 prefab / muzzle
            var rw = hand.rangeweapon.item as RangeWeapon;
            outWeapon.bullet = (rw != null) ? rw.bullet : null;
            outWeapon.muzzle = hand.rangeweapon.muzzlePoint;

            // 2) 把該手「總 Buff」轉成射擊數值
            outWeapon.damage.physicalDamage = hand.GetAttribute(Attributes.PhysicalDamage);
            outWeapon.damage.explosionDamage = hand.GetAttribute(Attributes.ExplosionDamage);
            outWeapon.damage.energyDamage = hand.GetAttribute(Attributes.EnergyDamage);
            outWeapon.damage.coldDamage = hand.GetAttribute(Attributes.ColdDamage);
            ApplyCritical(hand.buffs, outWeapon);

            outWeapon.range.reloadTime = hand.GetAttribute(Attributes.ReloadTime);
            outWeapon.range.bulletPerShot = Mathf.Max(1, Mathf.RoundToInt(hand.GetAttribute(Attributes.BulletPerShot)));
            outWeapon.range.roundPerTap = Mathf.Max(1, Mathf.RoundToInt(hand.GetAttribute(Attributes.RoundPerPull)));
            outWeapon.range.timeBetweenShooting = hand.GetAttribute(Attributes.TimeBetweenShooting);
            outWeapon.range.timeBetweenShots = hand.GetAttribute(Attributes.TimeBetweenShots);
            outWeapon.range.spread = hand.GetAttribute(Attributes.Spread);
            outWeapon.range.recoilPerShooting = hand.GetAttribute(Attributes.RecoilPerShooting);
            outWeapon.range.recoilControl = (PlayerStats.Instance != null)
                ? PlayerStats.Instance.GetRecoilControlForHand(isLeftHand)
                : 0f;

            // 衍生值：在裝備變動時算一次即可，開火時直接讀
            ApplyRecoilDerivedValues(outWeapon.range);
            outWeapon.range.magazineSize = Mathf.Max(1, Mathf.RoundToInt(hand.GetAttribute(Attributes.MagazineSize)));
            outWeapon.range.bulletSpeed = hand.GetAttribute(Attributes.BulletSpeed);
            outWeapon.range.firingMode = Mathf.RoundToInt(hand.GetAttribute(Attributes.FiringMode));

            if (changedWeapon)
            {
                outWeapon.rangeRuntime.bulletsLeft = outWeapon.range.magazineSize;
                outWeapon.rangeRuntime.shooting = false;
                outWeapon.rangeRuntime.reloading = false;
                outWeapon.rangeRuntime.readyToShoot = true;
                outWeapon.rangeRuntime.allowInvoke = true;
                outWeapon.reloadNormalized = 0f;
            }
            return;
        }
        // ───── 近戰 ─────
        if (hand.weaponKind == HandWeaponKind.Melee)
        {
            // ★ 這就是原本的 bug：換成刀之後舊槍的 damage / runtime 會殘留
            ClearRangeOutput(outWeapon, ref cachedRange);

            if (hand.meleeWeapon == null || hand.meleeWeapon.item is not MeleeWeapon mw)
            {
                ClearMeleeOutput(outWeapon, ref cachedMelee);
                return;
            }

            bool changedWeapon = cachedMelee != hand.meleeWeapon;
            cachedMelee = hand.meleeWeapon;

            // 1) 傷害：跟遠程走同一條 buff 管線
            outWeapon.damage.physicalDamage = hand.GetAttribute(Attributes.PhysicalDamage);
            outWeapon.damage.explosionDamage = hand.GetAttribute(Attributes.ExplosionDamage);
            outWeapon.damage.energyDamage = hand.GetAttribute(Attributes.EnergyDamage);
            outWeapon.damage.coldDamage = hand.GetAttribute(Attributes.ColdDamage);
            ApplyCritical(hand.buffs, outWeapon);

            // 2) 武器類型：每次現算，不存進 Instance。
            //    這樣之後調整 MeleeStanceRules 時，存檔裡的舊武器不會停在舊值。
            outWeapon.melee.weaponClass = (stanceRules != null)
                ? stanceRules.ResolveClass(hand.meleeWeapon)
                : MeleeWeaponClass.Sword;

            // 3) 握持方式：另一隻手空著就是雙手持
            outWeapon.melee.grip = MeleeStanceResolver.ResolveGrip(isLeftHand);

            // 4) 近戰數值
            var ps = PlayerStats.Instance;
            outWeapon.melee.slashVfx = mw.swordSlash;
            outWeapon.melee.hitbox = hand.meleeWeapon.hitbox;
            if (ps != null)
            {
                outWeapon.melee.meleeOutput = ps.GetMeleeOutputForHand(isLeftHand);
                outWeapon.melee.meleeSpeed = Mathf.Max(0.01f, ps.GetMeleeSpeedForHand(isLeftHand));
                outWeapon.melee.reloadTime = Mathf.Max(0f, ps.GetMeleeReloadTimeForHand(isLeftHand));
            }

            if (changedWeapon)
            {
                outWeapon.meleeRuntime.reloading = false;
                outWeapon.meleeRuntime.attacking = false;
                outWeapon.meleeRuntime.comboIndex = -1;
                outWeapon.meleeRuntime.cooldownNormalized = 0f;
            }
            return;
        }
    }
    // 由「每秒後座力」推導出每發偏移與每秒衰減。
    // 由 recoilPerShooting + 射速 + recoilControl 推導出所有後座力衍生值。
    //
    //   recoil       = recoilPerShooting / timeBetweenShooting × 1000   ← 顯示給玩家
    //   ratio        = recoil / recoilControl
    //   maxDeviation = 曲線A(ratio)                              ← 天花板
    //   decayPerSec  = maxDeviation / 曲線B(ratio)               ← 停火後的恢復
    //
    // 每發累加量不在這裡算 —— recoilPerShooting 本身就是角度，開火時直接用。
    //
    // recoilControl 因此同時控制天花板高度與恢復速度，玩家只要看一個比值就懂。
    // 點射武器一次扣扳機只累加一次，roundPerTap 不參與計算。
    private void ApplyRecoilDerivedValues(RangeWeaponSettings range)
    {
        float perShooting = Mathf.Max(0f, range.recoilPerShooting);
        float interval = Mathf.Max(minShotInterval, range.timeBetweenShooting);

        // ×1000 讓 Recoil 與防具上的 RecoilControl 同量級（數百 ~ 數千）
        range.recoil = perShooting / interval * RecoilDisplayScale;

        if (perShooting <= 0f)
        {
            range.recoilRatio = 0f;
            range.maxDeviation = 0f;
            range.deviationDecayPerSecond = 0f;
            return;
        }

        float control = Mathf.Max(1f, range.recoilControl);
        float ratio = range.recoil / control;

        range.recoilRatio = ratio;
        range.maxDeviation = Mathf.Max(0f, maxDeviationByRatio.Evaluate(ratio));

        float recovery = Mathf.Max(0.01f, recoveryTimeByRatio.Evaluate(ratio));
        range.deviationDecayPerSecond = range.maxDeviation / recovery;
    }

    private void ApplyShoulder(ShoulderWeaponStats shoulder, Weapon outWeapon, ref ShoulderWeaponInstance cachedSource)
    {
        if (outWeapon == null) return;

        outWeapon.isShoulder = true;
        // 沒武器：清空輸出（也可以改成 disable 該手射擊）
        if (shoulder == null || shoulder.weaponKind == ShoulderWeaponKind.None || (!shoulder.HasWeapon))
        {
            ClearWeaponOutput(outWeapon, ref cachedSource);
            return;
        }
        // 分流：遠程 
        if (shoulder.weaponKind == ShoulderWeaponKind.Range)
        {
            if (shoulder.shoulderweapon == null)
            {
                ClearWeaponOutput(outWeapon, ref cachedSource);
                return;
            }

            // 如果換武器：重置 runtime state（子彈/裝填狀態等）
            bool changedWeapon = cachedSource != shoulder.shoulderweapon;
            cachedSource = shoulder.shoulderweapon;

            // 1) 子彈 prefab / muzzle
            var rw = shoulder.shoulderweapon.item as ShoulderWeapon;
            outWeapon.bullet = (rw != null) ? rw.bullet : null;
            outWeapon.muzzle = shoulder.shoulderweapon.muzzlePoint;

            // 2) 把該手「總 Buff」轉成射擊數值
            outWeapon.damage.physicalDamage = shoulder.GetAttribute(Attributes.PhysicalDamage);
            outWeapon.damage.explosionDamage = shoulder.GetAttribute(Attributes.ExplosionDamage);
            outWeapon.damage.energyDamage = shoulder.GetAttribute(Attributes.EnergyDamage);
            outWeapon.damage.coldDamage = shoulder.GetAttribute(Attributes.ColdDamage);
            ApplyCritical(shoulder.buffs, outWeapon);

            outWeapon.range.reloadTime = shoulder.GetAttribute(Attributes.ReloadTime);
            outWeapon.range.bulletPerShot = Mathf.Max(1, Mathf.RoundToInt(shoulder.GetAttribute(Attributes.BulletPerShot)));
            outWeapon.range.roundPerTap = Mathf.Max(1, Mathf.RoundToInt(shoulder.GetAttribute(Attributes.RoundPerPull)));
            outWeapon.range.timeBetweenShooting = shoulder.GetAttribute(Attributes.TimeBetweenShooting);
            outWeapon.range.timeBetweenShots = shoulder.GetAttribute(Attributes.TimeBetweenShots);
            outWeapon.range.spread = shoulder.GetAttribute(Attributes.Spread);
            // 肩武器是固定式武裝：只有 spread，沒有後座力累積
            outWeapon.range.recoilPerShooting = 0f;
            outWeapon.range.recoil = 0f;
            outWeapon.range.recoilControl = 0f;
            outWeapon.range.recoilRatio = 0f;
            outWeapon.range.maxDeviation = 0f;
            outWeapon.range.deviationDecayPerSecond = 0f;
            outWeapon.range.magazineSize = Mathf.Max(1, Mathf.RoundToInt(shoulder.GetAttribute(Attributes.MagazineSize)));
            outWeapon.range.bulletSpeed = shoulder.GetAttribute(Attributes.BulletSpeed);
            outWeapon.range.firingMode = Mathf.RoundToInt(shoulder.GetAttribute(Attributes.FiringMode));

            if (changedWeapon)
            {
                outWeapon.rangeRuntime.bulletsLeft = outWeapon.range.magazineSize;
                outWeapon.rangeRuntime.shooting = false;
                outWeapon.rangeRuntime.reloading = false;
                outWeapon.rangeRuntime.readyToShoot = true;
                outWeapon.rangeRuntime.allowInvoke = true;
                outWeapon.reloadNormalized = 0f;
            }
            return;
        }
    }

    public void PushAmmoUI()
    {
        var ui = UIManager.Instance;
        if (ui == null) return;

        var stats = PlayerStats.Instance;

        // Hand fill
        float leftFill = CalcAmmoBarFill(leftHandWeapon);
        float rightFill = CalcAmmoBarFill(rightHandWeapon);

        bool leftReloading = IsReloadingForUI(leftHandWeapon);
        bool rightReloading = IsReloadingForUI(rightHandWeapon);

        // ✅ Shoulder fill (shoulder is range-only in current design)
        float leftShoulderFill = CalcAmmoBarFill(leftShoulderWeapon);
        float rightShoulderFill = CalcAmmoBarFill(rightShoulderWeapon);

        bool leftShoulderReloading = (leftShoulderWeapon != null && leftShoulderWeapon.rangeRuntime.reloading);
        bool rightShoulderReloading = (rightShoulderWeapon != null && rightShoulderWeapon.rangeRuntime.reloading);

        ui.SetAmmoState(
            leftFill, leftReloading,
            rightFill, rightReloading,
            leftShoulderFill, leftShoulderReloading,
            rightShoulderFill, rightShoulderReloading);
    }

    // 換彈 / 冷卻都讓彈藥條閃紅，近戰跟遠程共用同一個視覺語言
    private static bool IsReloadingForUI(Weapon w)
    {
        if (w == null) return false;

        return (w.kind == HandWeaponKind.Melee)
            ? w.meleeRuntime.reloading
            : w.rangeRuntime.reloading;
    }

    private static float CalcAmmoBarFill(Weapon w)
    {
        if (w == null) return 0f;

        // 近戰：冷卻中顯示填回進度，其餘顯示剩餘連段數。
        // 兩者都由 MeleeAttackController 寫進 barFill。
        if (w.kind == HandWeaponKind.Melee)
            return Mathf.Clamp01(w.meleeRuntime.barFill);

        // Reload mode: fillAmount means reload progress
        if (w.rangeRuntime.reloading)
            return Mathf.Clamp01(w.reloadNormalized);

        // Ammo mode: fillAmount means bullets / magazine
        if (w.range.magazineSize <= 0) return 0f;
        return Mathf.Clamp01((float)w.rangeRuntime.bulletsLeft / w.range.magazineSize);
    }

    // ────────────────────────────────────────────────
    //  統一攻擊入口
    //
    //  PlayerMovement.ProcessAttackFacingAndAttack 做完面向與時機判定之後，
    //  一律呼叫這裡，由武器種類決定交給誰。呼叫端不需要知道是刀還是槍。
    // ────────────────────────────────────────────────

    /// <summary>近戰控制器。給 PlayerMovement 查詢緩衝輸入是否還有救。</summary>
    public MeleeAttackController Melee => meleeController;

    public bool TryAttack(Weapon w)
    {
        if (w == null) return false;

        if (w.kind == HandWeaponKind.Melee)
            return (meleeController != null) && meleeController.TryStartMelee(w);

        return (rangeController != null) && rangeController.TryStartShoot(w);
    }

    // 舊名稱的轉接，讓 PlayerMovement 不用改。
    public bool TryStartShoot(Weapon w) => TryAttack(w);

    // 換彈是遠程專屬，但 PlayerController 直接呼叫這裡，所以保留轉接。
    public void StartReload(Weapon w)
    {
        if (rangeController != null)
            rangeController.StartReload(w);
    }

    public void SetOnShip(Rigidbody rb)
    {
        shipRB = rb;
        onShip = true;
    }

    public void SetOffShip()
    {
        shipRB = null;
        onShip = false;
    }
}