using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 武器平衡自動測試機器人。產生 ML 訓練資料用。
///
/// 流程（不停重複，直到按 F9 或達到 maxVariants）：
///   1. 選一把基礎武器（baseWeapons 輪流），把它的數值隨機縮放成一個「變體」，
///      寫進玩家目前手上的遠程武器 Instance，再讓 PlayerStats 重算 ——
///      所以接下來的每一發都走遊戲真正的開火、散佈、後座力、子彈、防禦程式。
///   2. 對每個血量 × 每個生成點：清場 → 只生成那一個靶子 → 補滿彈匣、歸零後座偏移 →
///      雙手全速開火直到靶子死亡或逾時 → 寫一列 CSV。
///      一次只放一個靶子，流彈不會打到下一個靶子。
///   3. 結束時把武器數值還原。
///
/// 瞄準是理想化的（永遠瞄靶子中心），量的是「武器本身的能力」，不含玩家手感。
///
/// 輸出：{專案根目錄}/ShootingRangeData/bot_{時間}.csv，一列 = 一次交戰，每列寫完就存檔。
///
/// 使用前：
///   · 雙手裝備遠程武器（單手也可以），站在綠色踏板上。
///   · ShootingRangeTrigger 的 Slots 設好要測的距離（Enabled 的才會測）。
///   · 測試期間不要操作鍵盤滑鼠。
/// </summary>
public class WeaponBalanceBot : MonoBehaviour
{
    [Serializable]
    public class AttributeRange
    {
        public Attributes attribute;
        [Tooltip("數值會乘上 [min, max] 之間的隨機倍率（對數均勻分佈：×0.5 和 ×2 機率相同）。")]
        public float minFactor = 0.5f;
        public float maxFactor = 2f;
    }

    [Header("References（留空自動找）")]
    [SerializeField] private ShootingRangeTrigger trigger;
    [SerializeField] private AttackManager attackManager;
    [SerializeField] private RangeAttackController rangeController;
    [SerializeField] private PlayerMovement playerMovement;
    [SerializeField] private ShootingRangeScoreboard scoreboard;

    [Header("Weapons")]
    [Tooltip("要測試的基礎武器（Gun 1 / Gun 2 / Gun 3 的 ScriptableObject）。每個變體輪流取一把。\n" +
             "留空 = 只用玩家目前手上的武器當基礎。\n" +
             "注意：只會換「數值」，模型、槍口、子彈 prefab 仍是手上那把的。")]
    [SerializeField] private List<RangeWeapon> baseWeapons = new List<RangeWeapon>();

    [Tooltip("每把基礎武器的第一個變體不做任何縮放（原始數值），當作對照組。")]
    [SerializeField] private bool includeUnmodifiedBaseline = true;

    [Tooltip("要隨機縮放的屬性。只縮放 Add 類型的 buff（武器本體的基礎值）；零件的 Multiplier 不動。")]
    [SerializeField] private List<AttributeRange> ranges = new List<AttributeRange>
    {
        new AttributeRange { attribute = Attributes.PhysicalDamage,      minFactor = 0.5f, maxFactor = 2f },
        new AttributeRange { attribute = Attributes.TimeBetweenShooting, minFactor = 0.5f, maxFactor = 2f },
        new AttributeRange { attribute = Attributes.Spread,              minFactor = 0.3f, maxFactor = 3f },
        new AttributeRange { attribute = Attributes.RecoilPerShooting,   minFactor = 0.3f, maxFactor = 3f },
        new AttributeRange { attribute = Attributes.MagazineSize,        minFactor = 0.5f, maxFactor = 2f },
        new AttributeRange { attribute = Attributes.ReloadTime,          minFactor = 0.5f, maxFactor = 2f },
        new AttributeRange { attribute = Attributes.BulletSpeed,         minFactor = 0.6f, maxFactor = 1.6f },
        new AttributeRange { attribute = Attributes.CriticalChance,      minFactor = 0.5f, maxFactor = 2f },
    };

    [Tooltip("後座力控制的隨機加減（每隻手）。以武器側 RecoilControl buff 的形式加上去，\n" +
             "跟護甲提供的控制值疊加。(0, 0) = 不變。")]
    [SerializeField] private Vector2 recoilControlOffset = new Vector2(-500f, 500f);

    [Header("Test Conditions")]
    [Tooltip("每個變體要測的靶子血量。")]
    [SerializeField] private float[] hpLevels = { 50f, 150f, 400f };

    [Tooltip("靶子左右移動速度（m/s）。靶子 prefab 要有 TestTargetMovement + TestTargetBrain。")]
    [SerializeField] private float[] speedLevels = { 0f, 10f, 15f, 35f };

    [Tooltip("每個變體隨機抽幾個條件（距離 × 血量 × 速度）來測。\n" +
             "抽樣可以在同樣時間內測更多種武器 —— 模型要學的是「屬性的影響」，武器種類越多越好。\n" +
             "原始數值的對照組永遠跑完整的格子。0 = 每個變體都跑完整格子。")]
    [SerializeField] private int conditionsPerVariant = 12;

    [Tooltip("靶子在動時算提前量（跟遊戲的鎖定瞄準同一套攔截算法）。\n" +
             "取消 = 永遠瞄靶子當下的位置，模擬不會提前量的玩家。")]
    [SerializeField] private bool leadMovingTargets = true;

    [Tooltip("鏡頭像按中鍵鎖定一樣轉向目前的靶子（同一套跟隨與轉速上限）。\n" +
             "只影響畫面，不影響彈道 —— 彈道永遠由 aimOverride 決定。錄影展示時建議打開。")]
    [SerializeField] private bool cameraLockOnTarget = true;

    [Tooltip("單一靶子最多打幾秒。超過就記成「沒打死」（killed = 0），time_to_kill 記成這個值。")]
    [SerializeField] private float engagementTimeout = 30f;

    [Tooltip("生成靶子後等多久才開火（秒，遊戲時間）。讓上一輪的流彈飛遠。")]
    [SerializeField] private float settleDelay = 0.75f;

    [Tooltip("0 = 不限，一直跑到按停止。")]
    [SerializeField] private int maxVariants = 0;

    [Tooltip("固定亂數種子可以重現同一組變體。0 = 每次不同。")]
    [SerializeField] private int randomSeed = 0;

    [Header("Speed")]
    [Tooltip("遊戲速度倍率。2～3 可以加快收資料；時間都用遊戲時間計，所以結果不受影響。\n" +
             "太高會讓物理變不準（子彈穿透），建議不超過 4。")]
    [SerializeField, Range(1f, 4f)] private float timeScale = 1f;

    [Header("Control")]
    [SerializeField] private bool autoStartOnPlay = false;
    [SerializeField] private KeyCode toggleKey = KeyCode.F9;

    // ───── 執行狀態 ─────
    private bool _running;
    private bool _stopRequested;
    private Coroutine _routine;
    private readonly Dictionary<RangeWeaponInstance, List<EquipmentBuff>> _originalBuffs =
        new Dictionary<RangeWeaponInstance, List<EquipmentBuff>>();
    private StreamWriter _writer;
    private string _sessionId;
    private System.Random _rng;
    private int _rows;
    private float _realStartTime;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public bool IsRunning => _running;

    // ═══════════════ 控制 ═══════════════

    private void Start()
    {
        if (autoStartOnPlay) StartBot();
    }

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            if (_running) StopBot();
            else StartBot();
        }
    }

    [ContextMenu("Start Bot")]
    public void StartBot()
    {
        if (_running) return;
        if (!ResolveReferences()) return;

        _routine = StartCoroutine(Run());
    }

    [ContextMenu("Stop Bot")]
    public void StopBot()
    {
        if (!_running) return;
        _stopRequested = true;   // 目前這次交戰打完（或逾時）就停
        Debug.Log("[WeaponBalanceBot] 收到停止指令，這次交戰結束後停止。", this);
    }

    private void OnDisable() => Cleanup();
    private void OnApplicationQuit() => Cleanup();

    private bool ResolveReferences()
    {
        if (trigger == null) trigger = FindFirstObjectByType<ShootingRangeTrigger>();
        if (attackManager == null) attackManager = FindFirstObjectByType<AttackManager>();
        if (rangeController == null && attackManager != null) rangeController = attackManager.GetComponent<RangeAttackController>();
        if (playerMovement == null) playerMovement = FindFirstObjectByType<PlayerMovement>();
        if (scoreboard == null) scoreboard = FindFirstObjectByType<ShootingRangeScoreboard>();

        string missing =
            trigger == null ? "ShootingRangeTrigger" :
            attackManager == null ? "AttackManager" :
            rangeController == null ? "RangeAttackController" :
            PlayerStats.Instance == null ? "PlayerStats" : null;

        if (missing != null)
        {
            Debug.LogError($"[WeaponBalanceBot] 找不到 {missing}，無法開始。", this);
            return false;
        }
        return true;
    }

    // ═══════════════ 主流程 ═══════════════

    private IEnumerator Run()
    {
        var hands = GetRangeHands();
        if (hands.Count == 0)
        {
            Debug.LogError("[WeaponBalanceBot] 手上沒有遠程武器。請先裝備槍再開始。", this);
            yield break;
        }

        var slotIndices = new List<int>();
        for (int i = 0; i < trigger.Slots.Count; i++)
        {
            var s = trigger.Slots[i];
            if (s != null && s.enabled && s.point != null) slotIndices.Add(i);
        }
        if (slotIndices.Count == 0)
        {
            Debug.LogError("[WeaponBalanceBot] ShootingRangeTrigger 沒有啟用的生成點。", this);
            yield break;
        }

        _running = true;
        _stopRequested = false;
        _rows = 0;
        _realStartTime = Time.realtimeSinceStartup;
        _rng = (randomSeed != 0) ? new System.Random(randomSeed) : new System.Random();

        // 記下原始數值，結束時還原
        _originalBuffs.Clear();
        foreach (var h in hands)
            _originalBuffs[h.instance] = new List<EquipmentBuff>(h.instance.buffs);

        if (scoreboard != null) scoreboard.paused = true;
        trigger.DespawnAll();
        OpenWriter();
        Time.timeScale = timeScale;

        float originalHp = trigger.HP;
        float originalSpeed = trigger.MoveSpeed;
        int baseCount = Mathf.Max(1, baseWeapons.Count);

        // 完整的條件格子：血量 × 速度 × 距離
        var grid = new List<(float hp, float speed, int slot)>();
        var speeds = (speedLevels != null && speedLevels.Length > 0) ? speedLevels : new[] { 0f };
        foreach (float hp in hpLevels)
            foreach (float sp in speeds)
                foreach (int slot in slotIndices)
                    grid.Add((hp, sp, slot));

        int sampled = (conditionsPerVariant > 0) ? Mathf.Min(conditionsPerVariant, grid.Count) : grid.Count;
        Debug.Log($"[WeaponBalanceBot] 開始。{hands.Count} 隻手；完整格子 {grid.Count} 個條件" +
                  $"（{slotIndices.Count} 距離 × {hpLevels.Length} 血量 × {speeds.Length} 速度），" +
                  $"一般變體每次抽 {sampled} 個。按 {toggleKey} 停止。", this);

        for (int variant = 0; maxVariants <= 0 || variant < maxVariants; variant++)
        {
            if (_stopRequested) break;

            // ── 1. 產生變體 ──
            int baseIndex = variant % baseCount;
            string baseName;
            List<EquipmentBuff> template;
            if (baseWeapons.Count > 0 && baseWeapons[baseIndex] != null)
            {
                baseName = baseWeapons[baseIndex].itemName;
                template = baseWeapons[baseIndex].buffs;
            }
            else
            {
                baseName = hands[0].instance.item != null ? hands[0].instance.item.itemName : "Equipped";
                template = _originalBuffs[hands[0].instance];
            }

            bool unmodified = includeUnmodifiedBaseline && variant < baseCount;
            var variantBuffs = MakeVariant(template, unmodified);

            foreach (var h in hands)
                h.instance.buffs = new List<EquipmentBuff>(variantBuffs);

            PlayerStats.Instance.RecalculateFromEquipment();
            yield return null;

            // 重算後重新取一次（AttackManager 的 Weapon 物件不變，但保險起見）
            hands = GetRangeHands();
            Weapon w0 = hands[0].weapon;

            // ── 2. 逐一交戰 ──
            // 對照組跑完整格子；一般變體隨機抽樣（洗牌後取前 N 個，同一變體不重複）
            var conditions = new List<(float hp, float speed, int slot)>(grid);
            if (!unmodified && sampled < grid.Count)
            {
                for (int i = conditions.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    (conditions[i], conditions[j]) = (conditions[j], conditions[i]);
                }
                conditions.RemoveRange(sampled, conditions.Count - sampled);
            }

            foreach (var c in conditions)
            {
                if (_stopRequested) break;
                trigger.SetHP(c.hp);
                trigger.SetMoveSpeed(c.speed);
                yield return Engage(variant, baseName, unmodified, hands, w0, c.hp, c.speed, c.slot);
            }

            float elapsedReal = Time.realtimeSinceStartup - _realStartTime;
            Debug.Log($"[WeaponBalanceBot] 變體 #{variant}（{baseName}{(unmodified ? "，原始數值" : "")}）完成。" +
                      $"累計 {_rows} 列，實際經過 {elapsedReal / 60f:F1} 分鐘，" +
                      $"平均每個變體 {elapsedReal / (variant + 1):F0} 秒。", this);
        }

        trigger.SetHP(originalHp);
        trigger.SetMoveSpeed(originalSpeed);
        Debug.Log($"[WeaponBalanceBot] 結束。共 {_rows} 列。武器數值已還原。", this);
        _routine = null;   // 自己就是這個 coroutine，不要在 Cleanup 裡 StopCoroutine 自己
        Cleanup();
    }

    private IEnumerator Engage(int variant, string baseName, bool unmodified, List<Hand> hands, Weapon w0,
                               float hp, float speed, int slotIndex)
    {
        // 清場，只放這一個靶子
        trigger.DespawnAll();
        yield return null;

        GameObject target = trigger.SpawnSingle(slotIndex);
        if (target == null) yield break;

        var stats = target.GetComponentInChildren<ModularEntityStats>();
        var mover = target.GetComponentInChildren<TestTargetMovement>();
        var brain = target.GetComponentInChildren<TestTargetBrain>();
        float halfWidth = (brain != null) ? brain.StrafeHalfWidth : 0f;
        if (speed > 0f && mover == null)
            Debug.LogWarning("[WeaponBalanceBot] 靶子沒有 TestTargetMovement，速度設定無效（這一列會記成靜止）。", this);
        float actualSpeed = (mover != null) ? mover.Speed : 0f;

        float deathTime = -1f;
        Action onDeath = () => deathTime = Time.time;
        if (stats != null) stats.OnDeath += onDeath;

        rangeController.aimOverride = target.transform;
        if (playerMovement != null) playerMovement.SetExternalFacing(target.transform.position);
        if (cameraLockOnTarget && PlayerAiming.Instance != null) PlayerAiming.Instance.ForceAutoAim(target.transform);

        // 等上一次換彈結束，再補滿彈匣、歸零後座偏移 —— 每次交戰從同樣的起點開始
        float waitUntil = Time.time + 10f;
        while (AnyReloading(hands) && Time.time < waitUntil) yield return null;

        yield return new WaitForSeconds(settleDelay);

        foreach (var h in hands)
        {
            h.weapon.rangeRuntime.bulletsLeft = h.weapon.range.magazineSize;
            h.weapon.rangeRuntime.accumulatedDeviation = 0f;
        }

        // 開火：每幀每隻手都嘗試扣扳機，冷卻 / 換彈由遊戲自己處理
        float t0 = Time.time;
        int pulls = 0;
        while (target != null && deathTime < 0f && Time.time - t0 < engagementTimeout)
        {
            // 每幀更新：靶子速度（給提前量）與角色朝向
            rangeController.aimOverrideVelocity = (leadMovingTargets && mover != null) ? mover.CurrentVelocity : Vector3.zero;
            if (playerMovement != null) playerMovement.SetExternalFacing(target.transform.position);

            foreach (var h in hands)
                if (attackManager.TryAttack(h.weapon)) pulls++;
            yield return null;
        }

        bool killed = deathTime >= 0f;
        float ttk = killed ? deathTime - t0 : engagementTimeout;
        float damageFraction = 1f;
        if (!killed && stats != null && stats.maxHealth > 0f)
            damageFraction = Mathf.Clamp01(1f - stats.health / stats.maxHealth);

        if (stats != null) stats.OnDeath -= onDeath;
        rangeController.aimOverride = null;
        rangeController.aimOverrideVelocity = Vector3.zero;
        if (PlayerAiming.Instance != null) PlayerAiming.Instance.ReleaseForcedAutoAim();

        WriteRow(variant, baseName, unmodified, hands.Count, w0, hp, actualSpeed, halfWidth, slotIndex,
                 trigger.Slots[slotIndex].ResolvedName, killed, ttk, pulls, damageFraction);

        trigger.DespawnAll();
    }

    // ═══════════════ 變體 ═══════════════

    private List<EquipmentBuff> MakeVariant(List<EquipmentBuff> template, bool unmodified)
    {
        var result = new List<EquipmentBuff>(template.Count + 1);

        // 每個屬性一個倍率，整個變體共用（同一屬性有多條 Add 時一起縮放）
        var factors = new Dictionary<Attributes, float>();
        if (!unmodified)
        {
            foreach (var r in ranges)
            {
                float lo = Mathf.Max(0.01f, Mathf.Min(r.minFactor, r.maxFactor));
                float hi = Mathf.Max(lo, Mathf.Max(r.minFactor, r.maxFactor));
                factors[r.attribute] = Mathf.Exp(Mathf.Lerp(Mathf.Log(lo), Mathf.Log(hi), (float)_rng.NextDouble()));
            }
        }

        foreach (var b in template)
        {
            var nb = b;
            if (b.mode == BuffApplyMode.Add && factors.TryGetValue(b.attribute, out float f))
            {
                nb.value = b.value * f;
                if (b.attribute == Attributes.MagazineSize)
                    nb.value = Mathf.Max(1f, Mathf.Round(nb.value));   // 彈匣要是整數
            }
            result.Add(nb);
        }

        if (!unmodified && (recoilControlOffset.x != 0f || recoilControlOffset.y != 0f))
        {
            float offset = Mathf.Lerp(recoilControlOffset.x, recoilControlOffset.y, (float)_rng.NextDouble());
            result.Add(new EquipmentBuff { attribute = Attributes.RecoilControl, mode = BuffApplyMode.Add, value = offset });
        }

        return result;
    }

    // ═══════════════ 手 ═══════════════

    private struct Hand
    {
        public RangeWeaponInstance instance;
        public Weapon weapon;
    }

    private List<Hand> GetRangeHands()
    {
        var list = new List<Hand>();
        var ps = PlayerStats.Instance;
        if (ps == null || attackManager == null) return list;

        if (ps.leftHand.weaponKind == HandWeaponKind.Range && ps.leftHand.rangeweapon != null)
            list.Add(new Hand { instance = ps.leftHand.rangeweapon, weapon = attackManager.leftHandWeapon });
        if (ps.rightHand.weaponKind == HandWeaponKind.Range && ps.rightHand.rangeweapon != null)
            list.Add(new Hand { instance = ps.rightHand.rangeweapon, weapon = attackManager.rightHandWeapon });

        return list;
    }

    private static bool AnyReloading(List<Hand> hands)
    {
        foreach (var h in hands)
            if (h.weapon.rangeRuntime.reloading) return true;
        return false;
    }

    // ═══════════════ 還原 ═══════════════

    private void Cleanup()
    {
        if (!_running) return;
        _running = false;

        if (_routine != null) { StopCoroutine(_routine); _routine = null; }

        Time.timeScale = 1f;

        if (rangeController != null)
        {
            rangeController.aimOverride = null;
            rangeController.aimOverrideVelocity = Vector3.zero;
        }
        if (playerMovement != null) playerMovement.ClearExternalFacing();
        if (PlayerAiming.Instance != null) PlayerAiming.Instance.ReleaseForcedAutoAim();
        if (trigger != null) trigger.DespawnAll();
        if (scoreboard != null) scoreboard.paused = false;

        foreach (var kv in _originalBuffs)
            if (kv.Key != null) kv.Key.buffs = new List<EquipmentBuff>(kv.Value);
        _originalBuffs.Clear();
        if (PlayerStats.Instance != null) PlayerStats.Instance.RecalculateFromEquipment();

        if (_writer != null)
        {
            _writer.Flush();
            _writer.Dispose();
            _writer = null;
        }
    }

    // ═══════════════ CSV ═══════════════

    private static readonly string[] Header =
    {
        "session_id", "variant_id", "base_weapon", "is_baseline", "hands",
        "target_hp", "distance", "target_speed", "strafe_half_width", "lead_aim", "slot_name",
        "killed", "time_to_kill", "damage_fraction", "trigger_pulls",
        // 武器的最終數值（含零件、含護甲，跟實戰完全一樣），單隻手
        "physical_damage", "explosion_damage", "energy_damage", "cold_damage",
        "crit_chance", "crit_multiplier",
        "bullets_per_shot", "rounds_per_pull", "time_between_shooting", "time_between_shots",
        "spread", "recoil_per_shooting", "recoil", "recoil_control", "recoil_ratio",
        "max_deviation", "deviation_decay_per_second",
        "magazine_size", "reload_time", "bullet_speed", "firing_mode",
        // 由上面推導（全部手加總）：給基準公式用
        "burst_dps", "sustained_dps", "baseline_ttk",
        "time_scale",
    };

    private void OpenWriter()
    {
        _sessionId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string folder = (scoreboard != null)
            ? scoreboard.ExportFolderFullPath
            : Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ShootingRangeData"));
        Directory.CreateDirectory(folder);

        string path = Path.Combine(folder, $"bot_{_sessionId}.csv");
        _writer = new StreamWriter(path, false, new UTF8Encoding(false));
        _writer.Write(string.Join(",", Header) + "\n");
        _writer.Flush();

        Debug.Log($"[WeaponBalanceBot] 輸出到 {path}", this);
    }

    private void WriteRow(int variant, string baseName, bool unmodified, int handCount, Weapon w, float hp,
                          float speed, float halfWidth, int slotIndex, string slotName, bool killed, float ttk, int pulls, float damageFraction)
    {
        if (_writer == null) return;

        var d = w.damage;
        var r = w.range;

        // 基準公式：每把槍的期望 DPS，再乘上手數
        float perShot = d.physicalDamage + d.explosionDamage + d.energyDamage + d.coldDamage;
        float expected = perShot * (1f + Mathf.Clamp01(d.criticalChance) * (Mathf.Max(1f, d.criticalMultiplier) - 1f));
        int pellets = Mathf.Max(1, r.bulletPerShot);
        int rounds = Mathf.Max(1, r.roundPerTap);
        // 一次扣扳機的完整循環：點射間隔 + 扣扳機冷卻（冷卻在點射打完才開始）
        float cycle = Mathf.Max(0.01f, r.timeBetweenShooting + (rounds - 1) * r.timeBetweenShots);
        float burstDps = expected * pellets * rounds / cycle;

        float pullsPerMag = Mathf.Max(1f, r.magazineSize / (float)rounds);
        float magTime = pullsPerMag * cycle;
        float magDamage = r.magazineSize * pellets * expected;
        float sustainedDps = magDamage / Mathf.Max(0.01f, magTime + r.reloadTime);

        float totalBurst = burstDps * handCount;
        float totalSustained = sustainedDps * handCount;
        float baselineTtk = (totalBurst > 0f) ? hp / totalBurst : -1f;

        string[] cells =
        {
            _sessionId, I(variant), Esc(baseName), unmodified ? "1" : "0", I(handCount),
            F(hp), F(trigger.DistanceTo(slotIndex)), F(speed), F(halfWidth), leadMovingTargets ? "1" : "0", Esc(slotName),
            killed ? "1" : "0", F(ttk), F(damageFraction), I(pulls),
            F(d.physicalDamage), F(d.explosionDamage), F(d.energyDamage), F(d.coldDamage),
            F(d.criticalChance), F(d.criticalMultiplier),
            I(r.bulletPerShot), I(r.roundPerTap), F(r.timeBetweenShooting), F(r.timeBetweenShots),
            F(r.spread), F(r.recoilPerShooting), F(r.recoil), F(r.recoilControl), F(r.recoilRatio),
            F(r.maxDeviation), F(r.deviationDecayPerSecond),
            I(r.magazineSize), F(r.reloadTime), F(r.bulletSpeed), I(r.firingMode),
            F(totalBurst), F(totalSustained), F(baselineTtk),
            F(timeScale),
        };

        _writer.Write(string.Join(",", cells) + "\n");
        _writer.Flush();   // 每列都存，當機也不會丟
        _rows++;
    }

    private static string F(float v) => v.ToString("0.######", Inv);
    private static string I(int v) => v.ToString(Inv);

    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
