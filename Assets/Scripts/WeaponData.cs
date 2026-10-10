using UnityEngine;

[System.Serializable]
public class WeaponDamage
{
    public float physicalDamage;
    public float explosionDamage;
    public float energyDamage;
    public float coldDamage;

    // 暴擊：以武器本體為基礎（覆蓋玩家基礎暴擊）+ 零件 / 手甲 / 頭盔的加成。
    // 由 AttackManager 在裝備變動時寫入，子彈與近戰 hitbox 直接讀這裡。
    public float criticalChance;
    public float criticalMultiplier = 1f;
}

[System.Serializable]
public class RangeWeaponSettings
{
    public float reloadTime;
    public int bulletPerShot;
    public int roundPerTap;
    public float timeBetweenShooting;
    public float timeBetweenShots;
    public float spread;

    // ───── 後座力 ─────
    //
    // 設計師只設定 recoilPerShooting；其餘全部是 AttackManager 推導出來的。

    // 每次扣扳機造成的偏移角度（度）。武器側 Attribute，設計師直接設定。
    // 它本身就是角度，不需要換算係數 —— 開火時直接加到 accumulatedDeviation 上。
    // 點射武器一次扣扳機打 3 發也只算一次。
    public float recoilPerShooting;

    // 玩家看到的「後座力」= recoilPerShooting / timeBetweenShooting，也就是每秒後座量。
    // 把射速算進去之後，衝鋒槍（單發低但射速快）和火砲（單發高但射速慢）才能直接比較。
    public float recoil;

    // 後座力控制。只出現在手甲 / 頭盔上，含 baseStats 基礎值。同樣顯示給玩家。
    public float recoilControl;

    // ratio = recoil / recoilControl。玩家的判讀基準：
    //   ratio ≈ 0.5 → 精準    ratio ≈ 1 → 可用    ratio ≈ 2 → 打不中
    public float recoilRatio;

    // 偏移的天花板（度），由 ratio 查曲線得出。射速快只是更快到頂，天花板不變。
    public float maxDeviation;

    // 停火後的恢復速度（度/秒）= maxDeviation / 恢復時間。
    // 恢復時間同樣由 ratio 查曲線，所以 recoilControl 同時控制天花板與恢復。
    public float deviationDecayPerSecond;
    public int magazineSize;
    public float bulletSpeed;
    public int firingMode; // 0:Single, 1:Auto, 2:Charge

    // 穿透：0 = 命中即消失；N = 可貫穿 N 個敵人，命中第 N+1 個時消失；-1 = 無限穿透。
    // 由 AttackManager 從 Attributes.piercing 算出，開火時寫進 Bullet.penetration。
    public int piercing;
}

[System.Serializable]
public class RangeWeaponRuntimeState
{
    public int bulletsLeft;
    public bool shooting;
    public bool reloading;
    public bool readyToShoot;
    public bool allowInvoke;

    // 目前累積的準星偏移半角（度）。每呼叫一次 Shoot() 累加一次 ——
    // 不管那次打出幾發、幾顆彈丸，所以霰彈槍和點射步槍不會一次跳好幾級。
    // 每把武器各自累積：左手打完 10 發不影響右手，肩武器也獨立。
    public float accumulatedDeviation;

    // 最後一次開火的時間。用來判斷「停火了沒」—— 衰減只在停火後進行。
    public float lastShotTime;
}

[System.Serializable]
public class MeleeWeaponSettings
{
    public MeleeWeaponClass weaponClass; // 由 blade × handle 經 MeleeStanceRules 推導
    public MeleeGrip grip;               // 由另一隻手是否空著推導（runtime）

    public float meleeOutput = 1f;  // 傷害倍率（已含 buff）
    public float meleeSpeed = 1f;   // 動畫 / 連段速度倍率（已含 buff）
    public float dashDistance;      // 單段突進基礎距離（已含 buff）
    public float reloadTime;        // 連段結束後的硬直冷卻（已含 buff）

    public GameObject slashVfx;     // 來自 MeleeWeapon.swordSlash

    public MeleeHitbox hitbox;      // 裝備時由 EquipmentManager 註冊到 instance 上
}

[System.Serializable]
public class MeleeWeaponRuntimeState
{
    public bool reloading;              // 冷卻中
    public bool attacking;              // 揮擊中
    public int comboIndex = -1;         // 目前第幾段，-1 = 不在連段
    public float cooldownNormalized;    // 0~1，冷卻進度

    // 彈藥條的顯示值（0~1）。語意是「這隻手還剩幾段可打 / 總段數」。
    //
    // 這隻手沒在揮時維持不動 —— 另一隻手在打不會影響它。
    // 接手的瞬間才依當下的共用 comboIndex 一次跳到對應值，
    // 所以「跳到 1 格」本身就是在告訴玩家：你接的是最後一段。
    public float barFill = 1f;
}

[System.Serializable]
public class Weapon
{

    public Transform muzzle;
    public GameObject bullet;

    // 目前這個槽位是遠程還是近戰（給 PlayerMovement / UI 分流用）
    public HandWeaponKind kind = HandWeaponKind.None;

    // 肩武器是固定式武裝：只有 spread，不受後座力影響。
    // 由 ApplyShoulder 設為 true，ApplyHand 設為 false。
    public bool isShoulder;

    // Damage Type
    public WeaponDamage damage = new WeaponDamage();

    // Melee Weapon Specific (Foldout)
    public MeleeWeaponSettings melee = new MeleeWeaponSettings();
    // Runtime State (Foldout)
    public MeleeWeaponRuntimeState meleeRuntime = new MeleeWeaponRuntimeState();

    // Range Weapon Specific (Foldout)
    public RangeWeaponSettings range = new RangeWeaponSettings();

    // Runtime State (Foldout)
    public RangeWeaponRuntimeState rangeRuntime = new RangeWeaponRuntimeState();

    // Reload UI runtime (0~1). When reloading, ammo bar shows this value.
    [HideInInspector] public float reloadNormalized;
}