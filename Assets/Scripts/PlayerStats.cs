using System.Collections.Generic;
using UnityEngine;
using System;
using MoreMountains.Feedbacks;

public enum BuffApplyMode
{
    Add = 0,         // 加法：+value
    Multiplier = 1   // 倍率：(1 + value) 倍；例如 0.2 = +20%
}

public enum FiringMode
{
    Salvo,
    ShootingInTurn
}
public enum Attributes
{
    // ★ 每個成員都明確指定數值，不要依賴宣告順序。
    //
    //   Unity 序列化 enum 用的是「數值」而不是名字，所以在中間插入一個沒有
    //   指定值的成員，會讓它後面所有成員的值 +1 —— 已存檔的資產上寫著 13，
    //   意思就從 Spread 變成別的東西，而且不會有任何錯誤提示。
    //
    //   指定數值之後，宣告順序（決定 Inspector 下拉選單的排列）就跟序列化值
    //   脫鉤了：想插在哪裡就插在哪裡，只要給一個沒用過的新號碼。
    //
    //   新增時：放在語意上合適的位置，號碼取「目前最大值 + 1」。
    //   刪除時：不要重用舊號碼，留著當墓碑，避免舊資產指到新意思。

    //Damage Type
    PhysicalDamage = 0,
    ExplosionDamage = 1,
    EnergyDamage = 2,
    ColdDamage = 3,

    //Defence Type
    PhysicalDefense = 4,
    ExplosionDefense = 5,
    EnergyDefense = 6,
    ColdDefense = 7,

    //Range Weapon Specific
    ReloadTime = 8,
    BulletPerShot = 9,
    RoundPerPull = 10,
    TimeBetweenShooting = 11,
    TimeBetweenShots = 12,
    Spread = 13,
    RecoilPerShooting = 39,   // 每次扣扳機造成的偏移角度（度）。本身就是角度，不需換算
    RecoilControl = 40,       // 後座力控制。只出現在手甲 / 頭盔上，不會出現在武器上
    MagazineSize = 14,
    BulletSpeed = 15,
    FiringMode = 16,          //0:Single, 1:Auto, 2:Charge
    EffectiveRange = 42,      // 全傷距離 R（公尺）。超過後線性衰減，到 2R 剩 30%。0 = 不衰減。武器本體 Add，槍管 Multiplier
    piercing = 41,          // 子彈穿透力，0 = 無穿透，1 = 穿透 1 個目標，2 = 穿透 2 個目標，依此類推；-1 = 無限穿透

    //Energy
    MaxEnergy = 17,
    EnergyRegen = 18,
    DashEnergyCost = 19,
    FlyEnergyCost = 20,

    //Critical Attack
    CriticalChance = 21,
    CriticalMultiplier = 22,

    //Movement
    SprintSpeed = 23,
    AccelerationSpeed = 24,
    DecelerationSpeed = 25,
    DashSpeed = 26,

    //Jump/Fly
    JumpHeight = 27,
    FlySpeed = 28,
    FlyAcceleration = 29,

    //Health
    MaxHealth = 30,

    //Aiming
    LockOnRange = 31,
    AimingDistance = 32,

    //Weight
    Weight = 33,

    //Melee Weapon Specific
    MeleeOutput = 34,
    MeleeSpeed = 35,
    MeleeDashDistance = 36,   // 已停用：近戰突進距離改由 dashSpeed / sprintSpeed × 動畫長度決定。保留號碼當墓碑，不要重用
    MeleeReloadTime = 37,

    //Auto Aim Speed
    AimTurnRate = 38,         // 瞄準轉速（度/秒）：自動瞄準的最大轉速，也是突擊推進中手動轉鏡頭的上限。舊名 AutoAimSpeed
}

public enum AnimationType
{
    Bipedal,
    Hover,
}

public enum HandWeaponKind
{
    None = 0,
    Range = 1,
    Melee = 2
}
public enum ShoulderWeaponKind
{
    None = 0,
    Range = 1,
}


[System.Serializable]
public class WeaponStats
{
    // 目前這隻手拿的武器種類（None / Range / Melee）
    public HandWeaponKind weaponKind = HandWeaponKind.None;

    // 目前這隻手拿的遠程武器（如果沒有就 null）
    // 注意：目前為了保持與 AttackManager 的相容性，近戰武器時會保持 weapon == null
    public RangeWeaponInstance rangeweapon;

    // 目前這隻手拿的近戰武器（如果沒有就 null）
    public MeleeWeaponInstance meleeWeapon;

    // 目前這隻手穿的手甲（LeftHandArmor / RightHandArmor）
    public ArmorInstance handArmor;

    // 這隻手「總共」吃到的 Buff（武器本體 + 武器零件 + 手甲）
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();


    public bool HasWeapon => rangeweapon != null || meleeWeapon != null;

    public void Reset()
    {
        weaponKind = HandWeaponKind.None;
        rangeweapon = null;
        meleeWeapon = null;
        handArmor = null;
        buffs.Clear();
    }

    // 之後如果要查某一種屬性（例如 RecoilControl）可以用這個
    public float GetAttribute(Attributes attr)
    {
        float add = 0f;
        float mul = 1f;

        foreach (var b in buffs)
        {
            if (b.attribute != attr) continue;

            if (b.mode == BuffApplyMode.Add) add += b.value;
            else if (b.mode == BuffApplyMode.Multiplier) mul *= (1f + b.value);
        }

        return add * mul;
    }
}
[System.Serializable]
public class ShoulderWeaponStats
{
    // 目前這隻手拿的武器種類（None / Range / Melee）
    public ShoulderWeaponKind weaponKind = ShoulderWeaponKind.None;

    // 目前這隻手拿的遠程武器（如果沒有就 null）
    public ShoulderWeaponInstance shoulderweapon;

    // 總共吃到的 Buff
    public List<EquipmentBuff> buffs = new List<EquipmentBuff>();


    public bool HasWeapon => shoulderweapon != null;

    public void Reset()
    {
        weaponKind = ShoulderWeaponKind.None;
        shoulderweapon = null;
        buffs.Clear();
    }

    // 之後如果要查某一種屬性（例如 RecoilControl）可以用這個
    public float GetAttribute(Attributes attr)
    {
        float add = 0f;
        float mul = 1f;

        foreach (var b in buffs)
        {
            if (b.attribute != attr) continue;

            if (b.mode == BuffApplyMode.Add) add += b.value;
            else if (b.mode == BuffApplyMode.Multiplier) mul *= (1f + b.value);
        }

        return add * mul;
    }
}

[System.Serializable]
public class BaseStats
{

    [Header("Base Defense")]
    public float physicalDefense = 0f;
    public float explosionDefense = 0f;
    public float energyDefense = 0f;
    public float coldDefense = 0f;

    [Header("Base Melee")]
    public float meleeOutput = 1f; //The final damage is calculated by multiplying this value with weapon damage
    public float meleeSpeed = 1f;//The final attack speed is calculated by multiplying this value with weapon attack speed
    public float meleeReloadTime = 0.75f; //Cooldown after melee dash/attack
    [Header("Base Critical")]
    public float criticalChance = 0.05f;
    public float criticalMultiplier = 1.5f;

    [Header("Base Energy")]
    public float maxEnergy = 1000f;
    public float energyRegen = 50f;
    public float dashEnergyCost = 350f;
    public float flyEnergyCost = 30f;

    [Header("Base Movement")]
    public float sprintSpeed = 20f;
    public float accelerationSpeed = 30f;
    public float decelerationSpeed = 30f;
    public float dashSpeed = 40f;

    [Header("Base Jump / Fly")]
    public float jumpHeight = 2f;
    public float flySpeed = 0f;
    public float flyAcceleration = 0f;
    [Header("Base Health")]
    public float maxHealth = 1000f;
    [Header("Base Aiming")]
    public float lockOnRange = 300f;
    public float aimingDistance = 50f;
    // 瞄準轉速（度/秒）：自動瞄準的最大轉速，也是突擊推進中手動轉鏡頭的上限。
    // FormerlySerializedAs：舊名 autoAimSpeed，保住場景裡已經填好的數值。
    [UnityEngine.Serialization.FormerlySerializedAs("autoAimSpeed")]
    public float aimTurnRate = 60f;

    [Header("Base Recoil")]
    // 玩家不穿任何防具時也有的基礎後座力控制。
    //
    // RecoilControl 只出現在防具上，所以基礎值一定要在這裡 —— 否則裸裝時
    // GetRecoilControlForHand 會回傳 0，ratio 變成無限大，任何槍都打不中。
    //
    // 量級要跟 Recoil 對齊（AttackManager 把 Recoil 乘了 1000，落在數百~數千）。
    public float recoilControl = 400f;
}

/// <summary>
/// 重量系統的參數。
///
/// 總重量 = Diva 本體重量 + 所有裝備（防具、手持武器、肩武器、零件）的 Weight。
/// 總重量經過 curve 換算成機動倍率 m，每個移動屬性再吃 m 的 k 次方：
///   · 速度類（Sprint、加速、減速、Dash、跳躍、飛行）：數值 × m^k  —— 越重越慢
///   · 能耗類（Dash、飛行的能量消耗）：           數值 ÷ m^k  —— 越重越貴
///
/// 用次方而不是各自一個倍率，是因為很多手感是好幾個屬性相乘的結果
/// （例如飛行航程 = 飛行速度 × 能量 ÷ 飛行能耗），用次方的話 k 直接相加，
/// 一眼就能看出某種手感總共吃了多少懲罰。原則：任何一種手感的 k 總和不超過 1.5。
///
/// 想讓重量更痛時，優先調 curve，不要拉高個別的 k。
/// </summary>
[System.Serializable]
public class WeightSettings
{
    [Tooltip("Diva 本體重量（kg）。不穿任何裝備時的總重量。")]
    public float bodyWeight = 136f;

    [Tooltip("重量曲線：x = 總重量（kg，含本體），y = 機動倍率 m。\n" +
             "點與點之間線性內插；比第一個點輕取第一個點的 y，比最後一個點重取最後一個點的 y。\n" +
             "x 必須由小到大排列。")]
    public Vector2[] curve = new Vector2[]
    {
        new Vector2( 580f, 1.10f),   // 極端輕量（比 Crimson Rose 更輕）
        new Vector2( 680f, 1.00f),   // 輕型基準：Crimson Rose + 雙持衝鋒槍
        new Vector2(1000f, 0.92f),   // 輕量 → 中量（Emerald Storm + 雙手武器約 1044）
        new Vector2(1400f, 0.80f),   // 中量的長緩坡，給中型機掛重武器的空間
        new Vector2(1500f, 0.65f),   // 臨界：懸崖
        new Vector2(1950f, 0.55f),   // 重量區繼續緩降，之後觸底
    };

    [Header("敏感度 k（實際倍率 = m^k）")]
    public float sprintSpeedK = 1f;
    [Tooltip("比最高速多掉一點，讓重的機體「起步慢」。")]
    public float accelerationK = 1.5f;
    [Tooltip("稍微滑一點就好，太滑會變成操作不準。")]
    public float decelerationK = 0.5f;
    public float dashSpeedK = 1f;
    [Tooltip("刻意很小：陸戰機飛不久，跳躍是它主要的垂直移動手段。")]
    public float jumpHeightK = 0.3f;
    public float flySpeedK = 1f;
    public float flyAccelerationK = 1.5f;
    [Tooltip("能耗類是「除以」m^k：越重越貴。")]
    public float dashEnergyCostK = 0.5f;
    [Tooltip("能耗類是「除以」m^k：越重越貴。")]
    public float flyEnergyCostK = 0.5f;

    /// 總重量 → 機動倍率 m
    public float Evaluate(float totalWeight)
    {
        if (curve == null || curve.Length == 0) return 1f;

        float m;
        if (totalWeight <= curve[0].x)
        {
            m = curve[0].y;
        }
        else if (totalWeight >= curve[curve.Length - 1].x)
        {
            m = curve[curve.Length - 1].y;
        }
        else
        {
            m = curve[curve.Length - 1].y;
            for (int i = 1; i < curve.Length; i++)
            {
                if (totalWeight > curve[i].x) continue;
                Vector2 a = curve[i - 1];
                Vector2 b = curve[i];
                float t = (b.x > a.x) ? (totalWeight - a.x) / (b.x - a.x) : 1f;
                m = Mathf.Lerp(a.y, b.y, t);
                break;
            }
        }

        // m 會被拿來開次方和當除數，不能是 0 或負數
        return Mathf.Max(0.05f, m);
    }
}


public class PlayerStats : MonoBehaviour, IDamageable
{
    public event Action<VisualChange> OnLegVisualChanged;
    public event Action<Vector3> OnThrusterFlameOffsetChanged;

    public event Action<Thruster> OnThrusterVisualChanged;
    public Thruster CurrentThruster { get; private set; }
    public Vector3 CurrentThrusterFlameOffset { get; private set; } = Vector3.zero;
    public event Action OnHandWeaponDataChanged;

    /// <summary>受到傷害（扣完防禦、實際扣血之後）。參數 = 實際扣掉的血量。受擊動畫等表現層訂閱這個。</summary>
    public event Action<float> OnDamaged;


    public static PlayerStats Instance { get; private set; }

    [Header("Feedback")]
    [Tooltip("受到傷害時播放。把 Player 底下的 HitFeedback 拖進來。\n" +
             "刻意不自動搜尋 —— Player 底下有一整排 Feedback 物件（攻擊、換彈、跑步…），\n" +
             "GetComponentInChildren 很容易抓到錯的那一個。")]
    [SerializeField] private MMF_Player hitFeedback;

    //Buff之前全身基礎屬性 
    [Header("Base Stats (Foldout)")]
    public BaseStats baseStats = new BaseStats();
    // === 全身基礎屬性（只加「非手部裝甲」 + 武器的通用加成） ===
    [Header("Defense")]
    public float physicalDefense;
    public float explosionDefense;
    public float energyDefense;
    public float coldDefense;
    [Header("MeleeWeapon")]
    public float meleeOutput;
    public float meleeSpeed;
    public float meleeReloadTime;
    [Header("RangeWeapon")]
    public float reloadTime;
    public float bulletPerShot;
    public float roundPerPull;
    public float timeBetweenShooting;
    public float timeBetweenShots;
    public float spread;
    public float magazineSize;
    public float bulletSpeed;
    public int firingMode; //0:Single, 1:Auto, 2:Charge
    [Header("Critical")]
    public float criticalChance;
    public float criticalMultiplier;
    [Header("Energy")]
    public float maxEnergy;
    public float energyRegen;
    public float currentEnergy;
    public float dashEnergyCost;
    public float flyEnergyCost;
    [Header("Movement")]
    public float sprintSpeed;
    public float accelerationSpeed;
    public float decelerationSpeed;
    public float dashSpeed;
    [Header("Jump / Fly")]
    public float jumpHeight;
    public float flySpeed;
    public float flyAcceleration;
    [Header("Health")]
    public float maxHealth;
    public float currentHealth;
    [Header("Aiming")]
    public float lockOnRange;
    public float aimingDistance;
    [UnityEngine.Serialization.FormerlySerializedAs("autoAimSpeed")]
    public float aimTurnRate;
    [Header("Weight")]
    public WeightSettings weightSettings = new WeightSettings();
    [Tooltip("目前總重量（kg，含本體）。只在執行時計算，改了沒用。")]
    public float totalWeight;
    [Tooltip("目前的機動倍率 m（1 = 輕型基準）。只在執行時計算，改了沒用。")]
    public float mobilityFactor = 1f;
    [Header("手部武器狀態（只在執行時使用）")]
    public WeaponStats leftHand = new WeaponStats();
    public WeaponStats rightHand = new WeaponStats();
    [Header("肩膀武器狀態（只在執行時使用）")]
    public ShoulderWeaponStats leftShoulder = new ShoulderWeaponStats();
    public ShoulderWeaponStats rightShoulder = new ShoulderWeaponStats();


    [Header("equipmentSlots 中左右手武器槽的 index")]
    // 請在 Inspector 裡對應到 EquipmentManager.equipmentSlots 的順序
    public int leftWeaponSlotIndex = -1;
    public int rightWeaponSlotIndex = -1;
    [Header("equipmentSlots 中左右肩膀武器槽的 index")]
    public int leftShoulderWeaponSlotIndex = -1;
    public int rightShoulderWeaponSlotIndex = -1;

    // =============================
    // Equipment Stat Block (UI) API
    // =============================
    [Serializable]
    public struct EnergyEfficiencyInfo
    {
        public bool flyInfinite;
        public float flySustainSeconds;
        public int dashCountFromFull;
        public float sustainableDashPerSecond;
    }
    public int GetDisplayHealth() => Mathf.RoundToInt(maxHealth);

    public int GetDisplayDefenceAverage()
    {
        float avg = (physicalDefense + explosionDefense + energyDefense + coldDefense) / 4f;
        return Mathf.RoundToInt(avg);
    }

    public float GetDisplayJumpHeight() => jumpHeight;
    public int GetDisplayMaxEnergy() => Mathf.RoundToInt(maxEnergy);
    public float GetDisplayEnergyRegen() => energyRegen;
    public float GetDisplayFlySpeed() => flySpeed;
    public float GetDisplayDashSpeed() => dashSpeed;
    public float GetDisplaySprintSpeed() => sprintSpeed;

    /// <summary>
    /// 主人定義的期望 DPS（不含 Spread/命中率）。
    /// critical = global(base+全身裝備) + weapon-side(武器/零件/手甲) 合併。
    /// </summary>
    public float GetHandExpectedDps(bool isLeftHand)
    {
        WeaponStats hand = isLeftHand ? leftHand : rightHand;
        if (hand == null || hand.rangeweapon == null) return 0f;

        float D =
            hand.GetAttribute(Attributes.PhysicalDamage) +
            hand.GetAttribute(Attributes.ExplosionDamage) +
            hand.GetAttribute(Attributes.EnergyDamage) +
            hand.GetAttribute(Attributes.ColdDamage);

        // 顆數 / 次數：至少 1（語意上不能是 0）
        int bulletPerShotVal = Mathf.Max(1, Mathf.RoundToInt(hand.GetAttribute(Attributes.BulletPerShot)));
        int roundPerPullVal = Mathf.Max(1, Mathf.RoundToInt(hand.GetAttribute(Attributes.RoundPerPull)));

        float tbs = hand.GetAttribute(Attributes.TimeBetweenShooting);
        if (tbs <= 0.00001f) return 0f;
        float shotsPerSecond = 1f / tbs;

        float cc = GetCriticalChanceFor(hand.buffs);
        float cm = GetCriticalMultiplierFor(hand.buffs);

        float expectedShot = (1f - cc) * D + cc * (cm * D);
        return expectedShot * bulletPerShotVal * roundPerPullVal * shotsPerSecond;
    }

    public int GetDisplayLhAttack() => Mathf.RoundToInt(GetHandExpectedDps(true));
    public int GetDisplayRhAttack() => Mathf.RoundToInt(GetHandExpectedDps(false));

    public float GetLockedOnRange() => lockOnRange / 2;

    public float GetAimingDistance() => aimingDistance;

    public float GetAimTurnRate() => aimTurnRate;

    public float GetTotalWeight() => totalWeight;
    public float GetMobilityFactor() => mobilityFactor;
    /// <summary>
    /// 主人先前定義的 Energy Efficiency（保留 API，以後若要用可直接接 UI）。
    /// FlyCost: 每秒；DashCost: 每次。
    /// </summary>
    public EnergyEfficiencyInfo GetEnergyEfficiencyInfo()
    {
        EnergyEfficiencyInfo info = new EnergyEfficiencyInfo();

        // Fly：每秒成本
        float netFlyDrain = flyEnergyCost - energyRegen;
        if (netFlyDrain <= 0f)
        {
            info.flyInfinite = true;
            info.flySustainSeconds = float.PositiveInfinity;
        }
        else
        {
            info.flyInfinite = false;
            info.flySustainSeconds = maxEnergy / netFlyDrain;
        }

        // Dash：每次成本
        if (dashEnergyCost <= 0f)
        {
            info.dashCountFromFull = int.MaxValue;
            info.sustainableDashPerSecond = float.PositiveInfinity;
        }
        else
        {
            info.dashCountFromFull = Mathf.FloorToInt(maxEnergy / dashEnergyCost);
            info.sustainableDashPerSecond = energyRegen / dashEnergyCost;
        }

        return info;
    }

    // 一把武器的最終暴擊率：這把武器吃到的武器側 buff（先加後乘）。
    // 手持武器傳 WeaponStats.buffs，肩武器傳 ShoulderWeaponStats.buffs。
    // 子彈 / 近戰的實際暴擊和 DPS 顯示都走這裡，兩者才會一致。
    //
    // ★ 槽位裝了武器，就以「武器本體的暴擊」作為基礎，覆蓋玩家的基礎暴擊（criticalChance / criticalMultiplier）。
    //   所以每把武器本體要自己帶 CriticalChance / CriticalMultiplier 的 Add；沒有的話暴擊率是 0、暴擊倍率是 1。
    //   零件 / 手甲 / 頭盔的 Add 加在武器的基礎上，Multiplier 再乘上去。
    public float GetCriticalChanceFor(List<EquipmentBuff> weaponBuffs)
    {
        float baseCc = 0f;
        float add = 0f;
        float mul = 1f;
        if (weaponBuffs != null)
        {
            foreach (var b in weaponBuffs)
            {
                if (b.attribute != Attributes.CriticalChance) continue;
                if (b.mode == BuffApplyMode.Add) add += b.value;
                else if (b.mode == BuffApplyMode.Multiplier) mul *= (1f + b.value);
            }
        }
        float cc = (baseCc + add) * mul;
        return Mathf.Clamp01(cc);
    }

    public float GetCriticalMultiplierFor(List<EquipmentBuff> weaponBuffs)
    {
        float baseCm = 0f;   // 同上：由武器本體提供；最後至少為 1（暴擊不會讓傷害變少）
        float add = 0f;
        float mul = 1f;
        if (weaponBuffs != null)
        {
            foreach (var b in weaponBuffs)
            {
                if (b.attribute != Attributes.CriticalMultiplier) continue;
                if (b.mode == BuffApplyMode.Add) add += b.value;
                else if (b.mode == BuffApplyMode.Multiplier) mul *= (1f + b.value);
            }
        }
        float cm = (baseCm + add) * mul;
        if (cm < 1f) cm = 1f;
        return cm;
    }

    public void TakeDamage(DamageInfo dmg, GameObject attacker)
    {
        float amount = DefenseFormula.Apply(dmg, physicalDefense, explosionDefense, energyDefense, coldDefense);

        if (amount <= 0f) return;

        currentHealth -= amount;
        OnDamaged?.Invoke(amount);

        // 受傷回饋。
        //
        // ★ 用不帶參數的版本。帶 intensity 的多載（PlayFeedbacks(position, amount)）
        //   實測完全不會震 —— MMF 的 intensity 預期是 1 附近的係數，
        //   直接餵原始傷害值（幾十到上百）會讓 Cinemachine Impulse 失效。
        //   要做「重擊震得更兇」的話，intensity 要先正規化到 0~1 再傳。
        //
        // 連續中彈時靠 MMF_Player 自己的 Cooldown Duration 節流，不在這裡擋。
        if (hitFeedback != null)
            hitFeedback.PlayFeedbacks();

        if (currentHealth <= 0f)
        {
            currentHealth = 0f;
            // TODO: 玩家死亡處理（OnPlayerDeath?.Invoke(); 重生 / GameOver 等）
        }
    }

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        ResetState();
        currentEnergy = maxEnergy; // 開場補滿能量
        currentHealth = maxHealth;   // 開場補滿血量  
        // ✅ 開場保證清空左右手和肩膀狀態（避免殘留引用）
        leftHand.Reset();
        rightHand.Reset();
        leftShoulder.Reset();
        rightShoulder.Reset();

        // (可選) 讓監聽者立刻刷新一次
        OnHandWeaponDataChanged?.Invoke();

        PlayerAiming.Instance?.SetAimAreaSize(lockOnRange);
        PlayerAiming.Instance?.SetLockOnDistance(aimingDistance);
        UIManager.Instance?.SetAmmoBarSize(lockOnRange);
    }

    /// <summary>
    /// 依照目前所有 equipmentSlots 重新計算玩家數值
    /// </summary>
    public void ResetState()
    {

        physicalDefense = baseStats.physicalDefense;
        explosionDefense = baseStats.explosionDefense;
        energyDefense = baseStats.energyDefense;
        coldDefense = baseStats.coldDefense;

        criticalChance = baseStats.criticalChance;
        criticalMultiplier = baseStats.criticalMultiplier;

        maxEnergy = baseStats.maxEnergy;
        energyRegen = baseStats.energyRegen;
        dashEnergyCost = baseStats.dashEnergyCost;
        flyEnergyCost = baseStats.flyEnergyCost;

        sprintSpeed = baseStats.sprintSpeed;
        accelerationSpeed = baseStats.accelerationSpeed;
        decelerationSpeed = baseStats.decelerationSpeed;
        dashSpeed = baseStats.dashSpeed;
        meleeReloadTime = baseStats.meleeReloadTime;
        meleeOutput = baseStats.meleeOutput;
        meleeSpeed = baseStats.meleeSpeed;

        jumpHeight = baseStats.jumpHeight;
        flySpeed = baseStats.flySpeed;
        flyAcceleration = baseStats.flyAcceleration;
        maxHealth = baseStats.maxHealth;
        lockOnRange = baseStats.lockOnRange;
        aimTurnRate = baseStats.aimTurnRate;
        aimingDistance = baseStats.aimingDistance;

        totalWeight = weightSettings.bodyWeight;
        mobilityFactor = 1f;
    }
    public void RecalculateFromEquipment()
    {
        ResetState();
        leftHand.Reset();
        rightHand.Reset();
        leftShoulder.Reset();
        rightShoulder.Reset();

        CurrentLegVisual.heightOffset = 0f;
        CurrentLegVisual.animationType = AnimationType.Bipedal;

        if (EquipmentManager.Instance == null ||
            EquipmentManager.Instance.equipmentSlots == null)
            return;

        // Buffs 分流：
        // - globalBuffs：影響全身（左欄）
        // - sharedWeaponBuffs：由「非手甲裝備」提供，影響左右手武器與左右肩武器的最終屬性（如 HeadArmor 的 spread/crit 等）
        // - left/rightHandArmorWeaponBuffs：由手甲提供，影響該手武器最終屬性（單持時兩手疊加到唯一武器）
        List<EquipmentBuff> globalBuffs = new List<EquipmentBuff>();
        List<EquipmentBuff> sharedWeaponBuffs = new List<EquipmentBuff>();
        List<EquipmentBuff> leftHandArmorWeaponBuffs = new List<EquipmentBuff>();
        List<EquipmentBuff> rightHandArmorWeaponBuffs = new List<EquipmentBuff>();
        List<EquipmentBuff> leftShoulderWeaponBuffs = new List<EquipmentBuff>();
        List<EquipmentBuff> rightShoulderWeaponBuffs = new List<EquipmentBuff>();

        Vector3 thrusterOffset = Vector3.zero;
        bool hasThruster = false;

        List<EquipmentSlot> slots = EquipmentManager.Instance.equipmentSlots;
        Thruster foundThruster = null;
        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (slot == null || slot.item == null)
                continue;

            var inst = slot.item;

            if (inst is ArmorInstance armorInst)
            {
                if (armorInst.item is Thruster thr)
                {
                    foundThruster = thr;                 // ★記住這顆 Thruster（prefab 也在裡面）
                    thrusterOffset = thr.thrusterFlameOffset;
                    hasThruster = true;
                }

                if (slot.equipmentType == ItemType.LegsArmor && armorInst.item is LegArmor leg && leg.visualChange != null)
                {
                    CurrentLegVisual.heightOffset = leg.visualChange.heightOffset;
                    CurrentLegVisual.animationType = leg.visualChange.animationType;
                }

                // 依裝備種類分流 buffs
                if (armorInst.buffs != null)
                {
                    if (slot.equipmentType == ItemType.LeftHandArmor)
                    {
                        leftHand.handArmor = armorInst;
                        SplitArmorBuffsToGlobalAndWeapon(armorInst.buffs, globalBuffs, leftHandArmorWeaponBuffs);
                    }
                    else if (slot.equipmentType == ItemType.RightHandArmor)
                    {
                        rightHand.handArmor = armorInst;
                        SplitArmorBuffsToGlobalAndWeapon(armorInst.buffs, globalBuffs, rightHandArmorWeaponBuffs);
                    }
                    else
                    {
                        // 非手甲裝備：global 既影響左欄，也要參與左右手武器最終屬性
                        // 我們以「武器側屬性」分流到 sharedWeaponBuffs，其餘進 globalBuffs
                        SplitArmorBuffsToGlobalAndWeapon(armorInst.buffs, globalBuffs, sharedWeaponBuffs);
                    }
                }
            }
            else if (i == leftWeaponSlotIndex)
            {
                if (inst is RangeWeaponInstance rangeWeaponInst)
                {
                    leftHand.weaponKind = HandWeaponKind.Range;
                    leftHand.rangeweapon = rangeWeaponInst;
                    leftHand.meleeWeapon = null;
                    AddWeaponAndAttachmentBuffs(leftHand, rangeWeaponInst);
                }
                else if (inst is MeleeWeaponInstance meleeWeaponInst)
                {
                    leftHand.weaponKind = HandWeaponKind.Melee;
                    leftHand.meleeWeapon = meleeWeaponInst;
                    leftHand.rangeweapon = null; // 保持與 AttackManager 相容：近戰時讓 ranged weapon 為 null
                    AddWeaponAndAttachmentBuffs(leftHand, meleeWeaponInst);
                }
            }
            else if (i == rightWeaponSlotIndex)
            {
                if (inst is RangeWeaponInstance rangeWeaponInst2)
                {
                    rightHand.weaponKind = HandWeaponKind.Range;
                    rightHand.rangeweapon = rangeWeaponInst2;
                    rightHand.meleeWeapon = null;
                    AddWeaponAndAttachmentBuffs(rightHand, rangeWeaponInst2);
                }
                else if (inst is MeleeWeaponInstance meleeWeaponInst2)
                {
                    rightHand.weaponKind = HandWeaponKind.Melee;
                    rightHand.meleeWeapon = meleeWeaponInst2;
                    rightHand.rangeweapon = null; // 保持與 AttackManager 相容：近戰時讓 ranged weapon 為 null
                    AddWeaponAndAttachmentBuffs(rightHand, meleeWeaponInst2);
                }
            }
            else if (i == leftShoulderWeaponSlotIndex)
            {
                if (inst is ShoulderWeaponInstance shoulderWeaponInst)
                {
                    leftShoulder.weaponKind = ShoulderWeaponKind.Range;
                    leftShoulder.shoulderweapon = shoulderWeaponInst;
                    AddShoulderWeaponAndAttachmentBuffs(leftShoulder, shoulderWeaponInst);
                }
            }
            else if (i == rightShoulderWeaponSlotIndex)
            {
                if (inst is ShoulderWeaponInstance shoulderWeaponInst)
                {
                    rightShoulder.weaponKind = ShoulderWeaponKind.Range;
                    rightShoulder.shoulderweapon = shoulderWeaponInst;
                    AddShoulderWeaponAndAttachmentBuffs(rightShoulder, shoulderWeaponInst);
                }
            }
        }

        // ★最後統一套用（先加後乘）— 全身屬性
        ApplyBuffListToGlobal(globalBuffs);

        // ★重量：一定要在全身 buff 套完之後 —— 懲罰是乘在「最終數值」上，
        //   裝甲自己給的移動加成也一樣會被重量打折。
        CalculateTotalWeight(globalBuffs);
        ApplyWeightToMobility();

        // ===== 武器側 buffs 組裝（先把武器/零件的 buffs 留在 leftHand/rightHand 內） =====
        // 1) sharedWeaponBuffs（如 HeadArmor 的 spread/crit）永遠套用到左右手武器與肩武器
        AddBuffListToWeapon(leftHand, sharedWeaponBuffs);
        AddBuffListToWeapon(rightHand, sharedWeaponBuffs);

        //    肩武器也吃 sharedWeaponBuffs（頭盔 / 胸甲等），但不吃手甲 —— 手甲只影響手持武器。
        //    肩武器沒有後座力，所以這裡面的 RecoilControl 對它沒有作用。
        if (leftShoulder.HasWeapon) AddBuffListToWeapon(leftShoulder, sharedWeaponBuffs);
        if (rightShoulder.HasWeapon) AddBuffListToWeapon(rightShoulder, sharedWeaponBuffs);

        // 2) 手甲武器側 buffs：各手各吃一份
        AddBuffListToWeapon(leftHand, leftHandArmorWeaponBuffs);
        AddBuffListToWeapon(rightHand, rightHandArmorWeaponBuffs);

        // 3) 單持規則：若只有一把武器，另一手手甲的武器側 buffs 也要疊加到唯一武器
        bool hasLeftWeapon = leftHand.HasWeapon;
        bool hasRightWeapon = rightHand.HasWeapon;
        if (hasLeftWeapon && !hasRightWeapon)
        {
            AddBuffListToWeapon(leftHand, rightHandArmorWeaponBuffs);
        }
        else if (!hasLeftWeapon && hasRightWeapon)
        {
            AddBuffListToWeapon(rightHand, leftHandArmorWeaponBuffs);
        }

        CurrentThrusterFlameOffset = hasThruster ? thrusterOffset : Vector3.zero;
        OnThrusterFlameOffsetChanged?.Invoke(CurrentThrusterFlameOffset);
        if (CurrentThruster != foundThruster)
        {
            CurrentThruster = foundThruster; // null 代表沒裝（卸下）
            OnThrusterVisualChanged?.Invoke(CurrentThruster);
        }

        OnLegVisualChanged?.Invoke(CurrentLegVisual);
        OnHandWeaponDataChanged?.Invoke();
        PlayerAiming.Instance?.SetAimAreaSize(lockOnRange);
        PlayerAiming.Instance?.SetLockOnDistance(aimingDistance);
        UIManager.Instance?.SetAmmoBarSize(lockOnRange);
    }

    // ======= helpers: buff classification / splitting =======
    private static void SplitArmorBuffsToGlobalAndWeapon(List<EquipmentBuff> src, List<EquipmentBuff> toGlobal, List<EquipmentBuff> toWeapon)
    {
        if (src == null) return;
        foreach (var b in src)
        {
            if (IsWeaponSideAttribute(b.attribute))
                toWeapon.Add(b);
            else
                toGlobal.Add(b);
        }
    }

    private static bool IsWeaponSideAttribute(Attributes attr)
    {
        // 這些屬性屬於「武器最終屬性」：由武器/零件/手甲/頭盔等影響，並且不應污染左欄全身 stats。
        switch (attr)
        {
            case Attributes.PhysicalDamage:
            case Attributes.ExplosionDamage:
            case Attributes.EnergyDamage:
            case Attributes.ColdDamage:
            case Attributes.MeleeOutput:
            case Attributes.MeleeSpeed:
            case Attributes.ReloadTime:
            case Attributes.BulletPerShot:
            case Attributes.RoundPerPull:
            case Attributes.TimeBetweenShooting:
            case Attributes.TimeBetweenShots:
            case Attributes.Spread:
            case Attributes.RecoilPerShooting:
            // RecoilControl 概念上是角色能力，但走武器側管線 ——
            // 左右手要獨立計算（手甲各加各的），頭盔則兩手都吃。
            case Attributes.RecoilControl:
            case Attributes.MagazineSize:
            case Attributes.BulletSpeed:
            case Attributes.FiringMode:
            case Attributes.piercing:
            case Attributes.EffectiveRange:
            case Attributes.CriticalChance:
            case Attributes.CriticalMultiplier:
            case Attributes.MeleeReloadTime:
                return true;
            default:
                return false;
        }
    }
    void AddBuffListToWeapon(WeaponStats target, List<EquipmentBuff> buffs)
    {
        if (buffs == null) return;
        target.buffs.AddRange(buffs);
    }
    void AddBuffListToWeapon(ShoulderWeaponStats target, List<EquipmentBuff> buffs)
    {
        if (buffs == null) return;
        target.buffs.AddRange(buffs);
    }


    // ======= local function: 全身屬性累積 =======
    void ApplyBuffListToGlobal(List<EquipmentBuff> buffs)
    {
        if (buffs == null) return;

        // Pass 1: Add
        foreach (var buff in buffs)
        {
            if (buff.mode == BuffApplyMode.Add)
                ApplyAddToGlobal(buff);
        }

        // Pass 2: Multiplier
        foreach (var buff in buffs)
        {
            if (buff.mode == BuffApplyMode.Multiplier)
                ApplyMultiplierToGlobal(buff);
        }
    }

    // ======= helpers: apply buffs to a single scalar (keep buff math inside PlayerStats) =======
    private static float ApplyBuffListToValue(List<EquipmentBuff> buffs, Attributes attr, float baseValue)
    {
        if (buffs == null) return baseValue;

        float v = baseValue;

        // Pass 1: Add
        for (int i = 0; i < buffs.Count; i++)
        {
            var b = buffs[i];
            if (b.attribute != attr) continue;
            if (b.mode == BuffApplyMode.Add) v += b.value;
        }

        // Pass 2: Multiplier
        for (int i = 0; i < buffs.Count; i++)
        {
            var b = buffs[i];
            if (b.attribute != attr) continue;
            if (b.mode == BuffApplyMode.Multiplier) v *= (1f + b.value);
        }

        return v;
    }

    // ======= 重量 =======
    // Weight 不是武器側屬性，所以：
    //   · 防具的 Weight 在 globalBuffs 裡（ApplyAddToGlobal 沒有它的 case，不會影響其他數值）
    //   · 武器本體和零件的 Weight 留在各手 / 各肩的 buffs 裡
    // 頭盔 / 手甲分給武器的 buffs 只有武器側屬性，不會帶 Weight，不會重複計算。
    //
    // 裝備的 Weight 全部合起來算（先加後乘），再加上本體重量。
    // 所以 Weight 的 Multiplier 是「整台機體的裝備重量」打折，例如之後的輕量化詞條。
    void CalculateTotalWeight(List<EquipmentBuff> globalBuffs)
    {
        List<EquipmentBuff> weightBuffs = new List<EquipmentBuff>();
        CollectWeightBuffs(globalBuffs, weightBuffs);
        CollectWeightBuffs(leftHand.buffs, weightBuffs);
        CollectWeightBuffs(rightHand.buffs, weightBuffs);
        CollectWeightBuffs(leftShoulder.buffs, weightBuffs);
        CollectWeightBuffs(rightShoulder.buffs, weightBuffs);

        float equipmentWeight = Mathf.Max(0f, ApplyBuffListToValue(weightBuffs, Attributes.Weight, 0f));
        totalWeight = weightSettings.bodyWeight + equipmentWeight;
        mobilityFactor = weightSettings.Evaluate(totalWeight);
    }

    static void CollectWeightBuffs(List<EquipmentBuff> src, List<EquipmentBuff> dst)
    {
        if (src == null) return;
        for (int i = 0; i < src.Count; i++)
        {
            if (src[i].attribute == Attributes.Weight)
                dst.Add(src[i]);
        }
    }

    void ApplyWeightToMobility()
    {
        float m = mobilityFactor;
        if (Mathf.Approximately(m, 1f)) return;

        var w = weightSettings;

        // 速度類：× m^k
        sprintSpeed       *= Mathf.Pow(m, w.sprintSpeedK);
        accelerationSpeed *= Mathf.Pow(m, w.accelerationK);
        decelerationSpeed *= Mathf.Pow(m, w.decelerationK);
        dashSpeed         *= Mathf.Pow(m, w.dashSpeedK);
        jumpHeight        *= Mathf.Pow(m, w.jumpHeightK);
        flySpeed          *= Mathf.Pow(m, w.flySpeedK);
        flyAcceleration   *= Mathf.Pow(m, w.flyAccelerationK);

        // 能耗類：÷ m^k
        dashEnergyCost    /= Mathf.Pow(m, w.dashEnergyCostK);
        flyEnergyCost     /= Mathf.Pow(m, w.flyEnergyCostK);
    }

#if UNITY_EDITOR
    // 執行中在 Inspector 調重量曲線 / k，馬上重算，不用重新換裝。
    void OnValidate()
    {
        if (Application.isPlaying && Instance == this && EquipmentManager.Instance != null)
            RecalculateFromEquipment();
    }
#endif

    // 這隻手的最終後座力控制：baseStats.recoilControl 再吃該手的武器側 buff。
    //
    // 必須走 ApplyBuffListToValue 而不是 GetAttribute —— 後者在沒有任何 buff 時
    // 回傳 0（add=0 × mul=1），玩家裸裝就會失去基礎控制力。
    public float GetRecoilControlForHand(bool isLeftHand)
    {
        var hand = isLeftHand ? leftHand : rightHand;
        return Mathf.Max(1f, ApplyBuffListToValue(hand.buffs, Attributes.RecoilControl, baseStats.recoilControl));
    }

    // 這隻手的最終近戰輸出倍率：baseStats.meleeOutput 再吃該手的武器側 buff
    public float GetMeleeOutputForHand(bool isLeftHand)
    {
        var hand = isLeftHand ? leftHand : rightHand;
        return ApplyBuffListToValue(hand.buffs, Attributes.MeleeOutput, baseStats.meleeOutput);
    }

    // 這隻手的最終近戰速度倍率（會拿去乘 Animator 的 melee layer speed）
    public float GetMeleeSpeedForHand(bool isLeftHand)
    {
        var hand = isLeftHand ? leftHand : rightHand;
        return ApplyBuffListToValue(hand.buffs, Attributes.MeleeSpeed, baseStats.meleeSpeed);
    }

    // The effective melee reload/cooldown time for the specified hand:
    public float GetMeleeReloadTimeForHand(bool isLeftHand)
    {
        var hand = isLeftHand ? leftHand : rightHand;
        return ApplyBuffListToValue(hand.buffs, Attributes.MeleeReloadTime, baseStats.meleeReloadTime);
    }

    void ApplyAddToGlobal(EquipmentBuff buff)
    {
        switch (buff.attribute)
        {
            case Attributes.PhysicalDefense: physicalDefense += buff.value; break;
            case Attributes.ExplosionDefense: explosionDefense += buff.value; break;
            case Attributes.EnergyDefense: energyDefense += buff.value; break;
            case Attributes.ColdDefense: coldDefense += buff.value; break;

            case Attributes.MaxEnergy: maxEnergy += buff.value; break;
            case Attributes.EnergyRegen: energyRegen += buff.value; break;
            case Attributes.DashEnergyCost: dashEnergyCost += buff.value; break;
            case Attributes.FlyEnergyCost: flyEnergyCost += buff.value; break;

            case Attributes.CriticalChance: criticalChance += buff.value; break;
            case Attributes.CriticalMultiplier: criticalMultiplier += buff.value; break;

            case Attributes.SprintSpeed: sprintSpeed += buff.value; break;
            case Attributes.AccelerationSpeed: accelerationSpeed += buff.value; break;
            case Attributes.DecelerationSpeed: decelerationSpeed += buff.value; break;
            case Attributes.DashSpeed: dashSpeed += buff.value; break;

            case Attributes.JumpHeight: jumpHeight += buff.value; break;
            case Attributes.FlySpeed: flySpeed += buff.value; break;
            case Attributes.FlyAcceleration: flyAcceleration += buff.value; break;

            case Attributes.MaxHealth: maxHealth += buff.value; break;
            case Attributes.AimingDistance: aimingDistance += buff.value; break;
            case Attributes.LockOnRange: lockOnRange += buff.value; break;
            case Attributes.AimTurnRate: aimTurnRate += buff.value; break;
        }
    }
    void ApplyMultiplierToGlobal(EquipmentBuff buff)
    {
        float m = 1f + buff.value; // 0.2 => 1.2倍；-0.1 => 0.9倍
        switch (buff.attribute)
        {
            case Attributes.PhysicalDefense: physicalDefense *= m; break;
            case Attributes.ExplosionDefense: explosionDefense *= m; break;
            case Attributes.EnergyDefense: energyDefense *= m; break;
            case Attributes.ColdDefense: coldDefense *= m; break;

            case Attributes.MaxEnergy: maxEnergy *= m; break;
            case Attributes.EnergyRegen: energyRegen *= m; break;
            case Attributes.DashEnergyCost: dashEnergyCost *= m; break;
            case Attributes.FlyEnergyCost: flyEnergyCost *= m; break;

            case Attributes.CriticalChance: criticalChance *= m; break;
            case Attributes.CriticalMultiplier: criticalMultiplier *= m; break;

            case Attributes.SprintSpeed: sprintSpeed *= m; break;
            case Attributes.AccelerationSpeed: accelerationSpeed *= m; break;
            case Attributes.DecelerationSpeed: decelerationSpeed *= m; break;
            case Attributes.DashSpeed: dashSpeed *= m; break;

            case Attributes.JumpHeight: jumpHeight *= m; break;
            case Attributes.FlySpeed: flySpeed *= m; break;
            case Attributes.FlyAcceleration: flyAcceleration *= m; break;

            case Attributes.MaxHealth: maxHealth *= m; break;
            case Attributes.AimingDistance: aimingDistance *= m; break;
            case Attributes.LockOnRange: lockOnRange *= m; break;
            case Attributes.AimTurnRate: aimTurnRate *= m; break;
        }
    }

    public VisualChange CurrentLegVisual { get; private set; } = new VisualChange()
    {
        heightOffset = 0f,
        animationType = default // 依你 AnimationType 的預設值
    };

    private void AddWeaponAndAttachmentBuffs(WeaponStats target, MeleeWeaponInstance weaponInst)
    {
        if (weaponInst == null) return;

        // 武器本體 buffs
        AddBuffListToWeapon(target, weaponInst.buffs);

        // 零件 buffs（attachment 裡每個 PartInstance）
        if (weaponInst.attachment == null) return;

        foreach (var part in weaponInst.attachment)
        {
            if (part == null || part.item == null) continue;
            AddBuffListToWeapon(target, part.buffs);
        }
    }

    void AddWeaponAndAttachmentBuffs(WeaponStats target, RangeWeaponInstance weaponInst)
    {
        if (weaponInst == null) return;

        // 武器本體 buffs
        AddBuffListToWeapon(target, weaponInst.buffs);

        // 零件 buffs（attachment 裡每個 PartInstance）
        if (weaponInst.attachment == null) return;

        foreach (var part in weaponInst.attachment)
        {
            // 通常 item == null 代表該槽沒裝零件；也避免殘留 buffs 造成幽靈加成
            if (part == null || part.item == null) continue;

            AddBuffListToWeapon(target, part.buffs);
        }
    }

    void AddShoulderWeaponAndAttachmentBuffs(ShoulderWeaponStats target, ShoulderWeaponInstance weaponInst)
    {
        if (weaponInst == null) return;

        // 武器本體 buffs
        AddBuffListToWeapon(target, weaponInst.buffs);

        // 零件 buffs（attachment 裡每個 PartInstance）
        if (weaponInst.attachment == null) return;

        foreach (var part in weaponInst.attachment)
        {
            // 通常 item == null 代表該槽沒裝零件；也避免殘留 buffs 造成幽靈加成
            if (part == null || part.item == null) continue;

            AddBuffListToWeapon(target, part.buffs);
        }
    }

}