using System;
using NaughtyAttributes;
using UnityEngine;

public enum EntityMeleeMoveMode
{
    Dash,     // 用 dashSpeed 衝向目標（類似玩家的近戰突進）
    Normal,   // 以減速後的速度繼續朝目標前進
}

/// <summary>一招近戰攻擊的資料。時間點由攻擊動畫的 Animation Event 決定。</summary>
[Serializable]
public class EntityMeleeAttack
{
    public string name = "Attack";
    public bool enabled = true;

    [Header("觸發條件")]
    [Tooltip("與目標的距離範圍（公尺，從實體原點到目標的瞄準點）。")]
    [MinMaxSlider(0f, 60f)]
    public Vector2 distanceRange = new Vector2(0f, 10f);

    [Tooltip("目標偏離身體正前方的最大角度（水平，度）。出招後的預備階段會轉向目標，所以不用設太嚴。")]
    [Range(0f, 180f)] public float maxAngle = 90f;

    [Tooltip("觸發機率（%），同時也是權重。\n" +
             "· 所有符合條件的招式加總 ≤ 100：各自依自己的 % 觸發，剩下的機率什麼都不做\n" +
             "· 加總 > 100：當作權重，一定會出其中一招\n" +
             "每 Evaluation Interval 秒擲一次。")]
    [Min(0f)] public float chance = 30f;

    [Tooltip("這招用過之後多久才能再用（秒）。")]
    [Min(0f)] public float cooldown = 3f;

    [Header("傷害")]
    [Min(0f)] public float physicalDamage = 20f;
    [Min(0f)] public float explosionDamage;
    [Min(0f)] public float energyDamage;
    [Min(0f)] public float coldDamage;

    [Header("命中")]
    [Tooltip("這招使用的判定（掛在爪子、頭等骨頭底下的 EntityMeleeHitbox）。\n" +
             "由動畫事件 MeleeHitOpen / MeleeHitClose 開關。")]
    public EntityMeleeHitbox hitbox;

    [Header("移動")]
    public EntityMeleeMoveMode moveMode = EntityMeleeMoveMode.Dash;

    [Tooltip("出招前（預備階段）把速度降到原本移動速度（sprintSpeed）的多少 %。\nNormal 模式的攻擊段與收招也用這個速度。")]
    [Range(0f, 100f)] public float slowdownPercent = 20f;

    [Tooltip("Dash 的速度倍率（× 實體的 dashSpeed）。")]
    [Min(0f)] public float dashSpeedMultiplier = 1f;

    [Tooltip("Dash：衝刺方向在 MeleeDashStart 那一刻鎖定，之後每秒最多修正幾度。\n" +
             "0 = 完全直線（最容易閃）；30～60 = 會稍微追一下，但側閃、起跳仍然躲得掉。\n" +
             "預備階段不受影響，照樣會一直轉向目標。")]
    [Range(0f, 360f)] public float dashTurnRate = 0f;

    [Tooltip("Normal 模式（以及預備階段）：離目標小於這個水平距離就不再往前推進。\n" +
             "Dash 的衝刺段不檢查這個距離，會照鎖定的方向衝過頭，直到 MeleeDashEnd。")]
    [Min(0f)] public float stopDistance = 3f;

    [Tooltip("攻擊預判：朝目標「到達時會在的位置」移動，而不是目標現在的位置。")]
    public bool usePrediction = true;

    [Header("其他")]
    [Tooltip("出招時播放攻擊預警（ModularEntityEffectManager.PlayAttackWarning）。\n" +
             "想在動畫的某一幀才亮，就關掉這個，改在攻擊動畫加 Animation Event「MeleeWarning」。")]
    public bool showWarning = true;

    [Tooltip("這招進行中，砲塔停止開火（仍然會瞄準）。")]
    public bool stopTurretFire = true;

    [Tooltip("出招時觸發的 Animator Trigger 名稱。")]
    public string animatorTrigger = "Melee";

    [Tooltip("保險：超過這個秒數還沒收到 MeleeEnd 事件就強制結束（動畫被打斷、事件漏掉時用）。\n" +
             "設成比攻擊動畫稍長一點。")]
    [Min(0.1f)] public float maxDuration = 3f;
}

/// <summary>
/// 實體的近戰攻擊。
///
/// 分工：
///   · Brain：在 Combat 狀態呼叫 TryStartAttack()；目標資料一律由 Brain 提供（CombatTarget）
///   · 這裡：出招判斷（條件、擲骰、冷卻、攻擊名額）、移動指令、傷害結算
///   · 動畫：用 Animation Event 決定每個階段什麼時候開始（經由 EntityMeleeAnimationRelay 轉過來）
///       MeleeDashStart → 開始衝刺 / 前進
///       MeleeDashEnd   → 停止推進，進入收招
///       MeleeHitOpen   → 開啟判定
///       MeleeHitClose  → 關閉判定
///       MeleeEnd       → 攻擊結束，交還控制權
///     出招（觸發 Trigger）到 MeleeDashStart 之間是預備階段：減速並轉向目標。
///   · Hitbox：形狀用 Collider 定義，判定時用該形狀做物理查詢（不依賴層碰撞矩陣）
///
/// 沒在攻擊時這個元件是停用的（不跑 FixedUpdate），沒有每幀成本。
/// </summary>
public class ModularEntityMeleeManager : MonoBehaviour
{
    private enum Phase { Windup, Active, Recovery }

    [SerializeField] private EntityMeleeAttack[] attacks = new EntityMeleeAttack[0];

    [Header("判斷頻率")]
    [Tooltip("多久擲一次骰子決定要不要出招（秒）。")]
    [Min(0.05f)] [SerializeField] private float evaluationInterval = 0.5f;
    [Tooltip("任何一招結束後，至少隔多久才能再出下一招（秒）。")]
    [Min(0f)] [SerializeField] private float globalCooldown = 1f;

    [Header("命中")]
    [Tooltip("判定會掃描的層（例如 Player Hurtbox）。")]
    [SerializeField] private LayerMask hitLayers = ~0;
    [Tooltip("只傷害 Brain 的交戰目標。關掉的話，判定碰到的任何可受傷物件都會受傷（自己與其他敵人除外）。")]
    [SerializeField] private bool onlyHitCombatTarget = true;

    [Header("攻擊預判")]
    [Tooltip("目標速度 ÷ 自身速度超過這個比例就不預判（方程式會退化）。")]
    [SerializeField, Range(0.1f, 0.99f)] private float predictionMaxSpeedRatio = 0.9f;
    [Tooltip("預判點最多可以比目標現在的位置遠幾倍。")]
    [SerializeField, Min(1f)] private float predictionMaxLeadMultiplier = 2f;
    [Tooltip("預估抵達時間超過這個秒數就不預判。")]
    [SerializeField, Min(0.05f)] private float predictionMaxTime = 1.5f;

    [Header("除錯")]
    [SerializeField] private bool logEvents = false;

    // ── 參考 ──
    private ModularEntityBrain _brain;
    private ModularEntityMovement _movement;
    private ModularEntityStats _stats;
    private ModularEntityEffectManager _effects;
    private Animator _animator;
    private bool _resolved;

    // ── 執行期狀態 ──
    private float[] _readyTime;
    private float _globalReadyTime;
    private float _nextEvaluateTime;

    private EntityMeleeAttack _current;
    private Phase _phase;
    private float _elapsed;
    private bool _hitOpen;
    private bool _hasHit;
    // Dash 衝刺段的方向（MeleeDashStart 時鎖定）。
    // 在船上時存成「船的區域座標」—— 船轉彎時，衝刺方向跟著船一起轉，跟狼的移動（相對於船）一致。
    private Transform _dashPlatform;
    private Vector3 _dashDirLocal;

    private static readonly Collider[] s_Hits = new Collider[32];

    /// <summary>是否正在攻擊。</summary>
    public bool IsAttacking => _current != null;

    /// <summary>目前這招是否要求砲塔停火。</summary>
    public bool StopsTurretFire => _current != null && _current.stopTurretFire;

    // ═══════════════════════ 初始化 ═══════════════════════

    private void Awake()
    {
        Resolve();
        enabled = false;   // 只有攻擊中才需要 FixedUpdate
    }

    private void Resolve()
    {
        if (_resolved) return;
        _resolved = true;

        _brain = GetComponentInParent<ModularEntityBrain>();
        Transform root = _brain != null ? _brain.transform : transform;
        _movement = root.GetComponent<ModularEntityMovement>();
        _stats = root.GetComponent<ModularEntityStats>();
        _animator = root.GetComponentInChildren<Animator>();
        _effects = (_stats != null && _stats.modularEntityEffectManager != null)
            ? _stats.modularEntityEffectManager
            : root.GetComponentInChildren<ModularEntityEffectManager>();

        _readyTime = new float[attacks != null ? attacks.Length : 0];

        if (_animator != null && _animator.GetComponent<EntityMeleeAnimationRelay>() == null)
            Debug.LogWarning($"[MeleeManager] {root.name}：Animator 物件（{_animator.name}）上沒有 EntityMeleeAnimationRelay，" +
                             "動畫事件傳不過來，攻擊只會靠 Max Duration 逾時結束。", this);
    }

    private void OnDisable()
    {
        // 物件被停用（死亡、回收到物件池）時攻擊中斷
        if (_current != null) EndAttack(cancelled: true);
    }

    // ═══════════════════════ 由 Brain 呼叫 ═══════════════════════

    /// <summary>
    /// Brain 在 Combat 狀態、有交戰目標時每次思考呼叫。
    /// 每 evaluationInterval 秒才真的擲骰；出招成功回傳 true。
    /// </summary>
    public bool TryStartAttack()
    {
        Resolve();
        if (_current != null) return true;
        if (_brain == null || _movement == null || _stats == null) return false;
        if (attacks == null || attacks.Length == 0) return false;

        float now = Time.time;
        if (now < _nextEvaluateTime || now < _globalReadyTime) return false;
        _nextEvaluateTime = now + evaluationInterval;

        Transform target = _brain.CombatTarget;
        if (!IsTargetValid(target)) return false;
        if (_movement.IsKnockedBack) return false;

        if (_readyTime.Length != attacks.Length) Array.Resize(ref _readyTime, attacks.Length);

        // ── 篩出符合條件的招式 ──
        Vector3 self = _brain.transform.position;
        Vector3 aim = _brain.GetTargetAimPoint(target);
        float dist = Vector3.Distance(self, aim);
        Vector3 flat = aim - self;
        flat.y = 0f;
        float angle = flat.sqrMagnitude > 0.0001f ? Vector3.Angle(_movement.MeshForward, flat) : 0f;

        float total = 0f;
        for (int i = 0; i < attacks.Length; i++)
            if (IsCandidate(i, now, dist, angle)) total += attacks[i].chance;
        if (total <= 0f) return false;

        // ── 擲骰：加總 ≤ 100 → 依百分比（可能什麼都不出）；> 100 → 當作權重 ──
        float roll = UnityEngine.Random.Range(0f, Mathf.Max(100f, total));
        int pick = -1;
        float acc = 0f;
        for (int i = 0; i < attacks.Length; i++)
        {
            if (!IsCandidate(i, now, dist, angle)) continue;
            acc += attacks[i].chance;
            if (roll < acc) { pick = i; break; }
        }
        if (pick < 0) return false;

        // 攻擊名額：拿不到就不出招（也不消耗冷卻）
        if (!_brain.TryAcquireMeleeSlot()) return false;

        BeginAttack(pick, now);
        return true;
    }

    private bool IsCandidate(int i, float now, float dist, float angle)
    {
        EntityMeleeAttack a = attacks[i];
        if (a == null || !a.enabled || a.chance <= 0f) return false;
        if (now < _readyTime[i]) return false;
        if (dist < a.distanceRange.x || dist > a.distanceRange.y) return false;
        if (angle > a.maxAngle) return false;
        return true;
    }

    // ═══════════════════════ 動畫事件（由 EntityMeleeAnimationRelay 轉過來） ═══════════════════════

    public void OnMeleeDashStart()
    {
        if (_current == null) return;
        Log("DashStart");
        _phase = Phase.Active;
        SetDashDir(ComputeChaseDirection(DashSpeed(_current)));   // 依當下的預判鎖定衝刺方向
    }
    public void OnMeleeDashEnd()   { if (_current == null) return; Log("DashEnd");   _phase = Phase.Recovery; }
    public void OnMeleeHitOpen()   { if (_current == null) return; Log("HitOpen");   _hitOpen = true; }
    public void OnMeleeHitClose()  { if (_current == null) return; Log("HitClose");  _hitOpen = false; }
    public void OnMeleeEnd()       { if (_current == null) return; Log("End");       EndAttack(cancelled: false); }
    public void OnMeleeWarning()   { if (_current == null) return; Log("Warning");   if (_effects != null) _effects.PlayAttackWarning(); }

    // ═══════════════════════ 攻擊流程 ═══════════════════════

    private void BeginAttack(int index, float now)
    {
        _current = attacks[index];
        _readyTime[index] = now + _current.cooldown;
        _phase = Phase.Windup;
        _elapsed = 0f;
        _hitOpen = false;
        _hasHit = false;
        _dashPlatform = null;
        _dashDirLocal = Vector3.zero;

        if (_animator != null && !string.IsNullOrEmpty(_current.animatorTrigger))
        {
            if (HasTrigger(_current.animatorTrigger))
                _animator.SetTrigger(_current.animatorTrigger);
            else
                Debug.LogWarning($"[MeleeManager] Animator 沒有名為 '{_current.animatorTrigger}' 的 Trigger。", this);
        }

        if (_current.showWarning && _effects != null)
            _effects.PlayAttackWarning();

        Log($"Begin '{_current.name}'");
        enabled = true;
        StepMovement(0f);   // 這一步就開始減速、轉向
    }

    private void FixedUpdate()
    {
        if (_current == null) { enabled = false; return; }

        _elapsed += Time.fixedDeltaTime;

        // ── 中斷條件 ──
        if (_brain == null || _brain.currentState != EntityState.Combat ||
            !IsTargetValid(_brain.CombatTarget) ||
            _movement == null || _movement.IsKnockedBack ||
            (_stats != null && _stats.IsDead))
        {
            EndAttack(cancelled: true);
            return;
        }

        // ── 保險逾時 ──
        // 舊資料升級上來的招式 maxDuration 會是 0 → 用 3 秒當預設，避免一出招就逾時
        float limit = _current.maxDuration > 0.05f ? _current.maxDuration : 3f;
        if (_elapsed >= limit)
        {
            Log("Timeout");
            EndAttack(cancelled: false);
            return;
        }

        StepMovement(Time.fixedDeltaTime);
        if (_hitOpen && !_hasHit) StepHit();
    }

    private void StepMovement(float dt)
    {
        EntityMeleeAttack a = _current;
        float slowSpeed = _stats.sprintSpeed * a.slowdownPercent / 100f;
        float fullAccel = Mathf.Max(_stats.decelerationSpeed, _stats.accelerationSpeed);

        // ── 收招：沿目前朝向滑行，不再追蹤 ──
        if (_phase == Phase.Recovery)
        {
            _movement.SetVelocityOverride(_movement.MeshForward, slowSpeed, fullAccel);
            return;
        }

        // ── Dash 衝刺段：沿鎖定的方向直線衝刺，只允許有限修正；不檢查停止距離 ──
        if (_phase == Phase.Active && a.moveMode == EntityMeleeMoveMode.Dash)
        {
            float dashSpeed = DashSpeed(a);
            Vector3 dashDir = GetDashDir();
            if (dashDir.sqrMagnitude < 0.0001f) dashDir = _movement.MeshForward;

            if (a.dashTurnRate > 0f && dt > 0f)
            {
                Vector3 want = ComputeChaseDirection(dashSpeed);
                dashDir = Vector3.RotateTowards(dashDir, want, a.dashTurnRate * Mathf.Deg2Rad * dt, 0f);
            }
            SetDashDir(dashDir);   // 重新存一次：上下船時換成新的基準
            dashDir = GetDashDir();

            _movement.SetVelocityOverride(dashDir, dashSpeed, 0f);   // 衝刺：立刻到達（相對於船）
            _movement.SetFacingTarget(dashDir, 1f);
            return;
        }

        // ── 預備階段、Normal 攻擊段：持續轉向目標（含預判），靠近到停止距離就不再推進 ──
        float speed = slowSpeed;
        float accel = (_phase == Phase.Active) ? _stats.accelerationSpeed : fullAccel;

        Vector3 self = _brain.transform.position;
        Vector3 toTarget = _brain.GetTargetAimPoint(_brain.CombatTarget) - self;
        toTarget.y = 0f;
        if (toTarget.magnitude <= a.stopDistance) speed = 0f;

        Vector3 dir = ComputeChaseDirection(Mathf.Max(slowSpeed, 0.01f));
        _movement.SetVelocityOverride(dir, speed, accel);
        _movement.SetFacingTarget(toTarget.sqrMagnitude > 0.0001f ? toTarget : dir, 1f);
    }

    private Vector3 GetDashDir()
    {
        Vector3 d = _dashPlatform != null ? _dashPlatform.TransformDirection(_dashDirLocal) : _dashDirLocal;
        d.y = 0f;
        return d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.zero;
    }

    private void SetDashDir(Vector3 world)
    {
        world.y = 0f;
        if (world.sqrMagnitude < 0.0001f) return;
        world.Normalize();
        _dashPlatform = _movement.PlatformTransform;
        _dashDirLocal = _dashPlatform != null ? _dashPlatform.InverseTransformDirection(world) : world;
    }

    private float DashSpeed(EntityMeleeAttack a)
    {
        return a.moveMode == EntityMeleeMoveMode.Dash
            ? _stats.dashSpeed * a.dashSpeedMultiplier
            : _stats.sprintSpeed * a.slowdownPercent / 100f;
    }

    /// <summary>朝目標的水平方向；開啟預判時，朝「以 speed 前進、到達時目標會在的位置」。</summary>
    private Vector3 ComputeChaseDirection(float speed)
    {
        Vector3 self = _brain.transform.position;
        Vector3 aim = _brain.GetTargetAimPoint(_brain.CombatTarget);
        Vector3 goal = aim;
        if (_current != null && _current.usePrediction && speed > 0.01f)
        {
            // ★ 用「相對於自己腳下平台」的目標速度預判。
            //   Movement 的速度是相對於船的（最後才加上船速），如果拿目標的世界速度來解，
            //   兩隻都在船上時會把船速多算一次，預判點整個偏到船前進的方向 —— 撲空。
            Vector3 relVel = _brain.GetCombatTargetVelocity() - _movement.PlatformVelocity;
            goal = PredictIntercept(self, aim, relVel, speed);
        }

        Vector3 dir = goal - self;
        dir.y = 0f;
        if (dir.sqrMagnitude < 0.0001f)
        {
            dir = aim - self;
            dir.y = 0f;
        }
        return dir.sqrMagnitude > 0.0001f ? dir.normalized : _movement.MeshForward;
    }

    private void StepHit()
    {
        EntityMeleeAttack a = _current;
        if (a.hitbox == null) return;

        int n = a.hitbox.Overlap(s_Hits, hitLayers);
        if (n == 0) return;

        Transform target = _brain.CombatTarget;
        IDamageable targetDamageable = target != null ? target.GetComponentInParent<IDamageable>() : null;
        Rigidbody targetBody = target != null ? target.GetComponentInParent<Rigidbody>() : null;

        for (int i = 0; i < n; i++)
        {
            Collider c = s_Hits[i];
            if (c == null) continue;

            IDamageable d = c.GetComponentInParent<IDamageable>();
            if (d == null) continue;

            // 不打自己、不打其他敵人
            if (d is Component comp)
            {
                if (comp.transform.IsChildOf(_brain.transform)) continue;
                if (comp.GetComponentInParent<ModularEntityStats>() != null) continue;
            }

            if (onlyHitCombatTarget)
            {
                // 同一個 IDamageable，或屬於同一個 Rigidbody（玩家的 hurtbox 與被偵測到的碰撞體可能掛在不同物件上）
                bool sameDamageable = targetDamageable != null && ReferenceEquals(d, targetDamageable);
                bool sameBody = targetBody != null && c.attachedRigidbody == targetBody;
                if (!sameDamageable && !sameBody) continue;
            }

            var dmg = new DamageInfo(a.physicalDamage, a.explosionDamage, a.energyDamage, a.coldDamage);
            d.TakeDamage(dmg, _brain.gameObject);
            _hasHit = true;   // 每招只打中一次
            Log($"Hit '{c.name}'");
            break;
        }
    }

    private void EndAttack(bool cancelled)
    {
        if (_current == null) return;

        if (cancelled && _animator != null && !string.IsNullOrEmpty(_current.animatorTrigger) && HasTrigger(_current.animatorTrigger))
            _animator.ResetTrigger(_current.animatorTrigger);
        if (cancelled && _effects != null)
            _effects.StopAttackWarning();   // 攻擊被打斷：預警也收掉

        Log(cancelled ? "Cancelled" : "Finished");

        _current = null;
        _hitOpen = false;
        _globalReadyTime = Time.time + globalCooldown;
        if (_movement != null) _movement.ClearVelocityOverride();
        enabled = false;
    }

    // ═══════════════════════ 工具 ═══════════════════════

    private static bool IsTargetValid(Transform t)
    {
        return t != null && t.gameObject.activeInHierarchy;
    }

    /// <summary>
    /// 攔截點（與玩家近戰突進相同的三道防呆）。任一不過就退回目標現在的位置。
    ///   1) 目標速度相對自身速度太高 → 方程式退化，不解
    ///   2) 攔截點比目標遠太多倍 → 拒絕
    ///   3) 預估抵達時間太長 → 拒絕
    /// </summary>
    private Vector3 PredictIntercept(Vector3 self, Vector3 targetPos, Vector3 targetVel, float speed)
    {
        if (targetVel.sqrMagnitude <= 0.01f) return targetPos;
        if (targetVel.magnitude / speed >= predictionMaxSpeedRatio) return targetPos;
        if (!MathToolKit.InterceptionPoint(targetPos, self, targetVel, speed, out Vector3 solved)) return targetPos;

        Vector3 toTarget = targetPos - self;
        Vector3 toSolved = solved - self;
        if (toSolved.sqrMagnitude <= 0.0001f) return targetPos;
        if (Vector3.Dot(toSolved, toTarget) <= 0f) return targetPos;
        if (toSolved.magnitude > toTarget.magnitude * predictionMaxLeadMultiplier) return targetPos;
        if (toSolved.magnitude / speed > predictionMaxTime) return targetPos;
        return solved;
    }

    private bool HasTrigger(string trigger)
    {
        if (_animator == null || _animator.runtimeAnimatorController == null) return false;
        foreach (AnimatorControllerParameter p in _animator.parameters)
            if (p.type == AnimatorControllerParameterType.Trigger && p.name == trigger) return true;
        return false;
    }

    private void Log(string msg)
    {
        if (logEvents) Debug.Log($"[MeleeManager] {(_brain != null ? _brain.name : name)} {msg} (t={_elapsed:F2})", this);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (attacks == null) return;
        Transform root = GetComponentInParent<ModularEntityBrain>() != null ? GetComponentInParent<ModularEntityBrain>().transform : transform;
        Animator anim = root.GetComponentInChildren<Animator>();
        Transform body = anim != null ? anim.transform : root;

        for (int i = 0; i < attacks.Length; i++)
        {
            EntityMeleeAttack a = attacks[i];
            if (a == null || !a.enabled) continue;

            // 觸發距離（黃：最大、橘：最小）
            UnityEditor.Handles.color = new Color(1f, 0.9f, 0.2f, 0.6f);
            UnityEditor.Handles.DrawWireDisc(root.position, Vector3.up, a.distanceRange.y);
            if (a.distanceRange.x > 0f)
            {
                UnityEditor.Handles.color = new Color(1f, 0.55f, 0.1f, 0.6f);
                UnityEditor.Handles.DrawWireDisc(root.position, Vector3.up, a.distanceRange.x);
            }

            // 觸發角度
            Vector3 fwd = body.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.0001f && a.maxAngle < 180f)
            {
                fwd.Normalize();
                UnityEditor.Handles.color = new Color(1f, 0.9f, 0.2f, 0.12f);
                Vector3 from = Quaternion.AngleAxis(-a.maxAngle, Vector3.up) * fwd;
                UnityEditor.Handles.DrawSolidArc(root.position, Vector3.up, from, a.maxAngle * 2f, a.distanceRange.y);
            }

            // 停止推進距離（白）
            UnityEditor.Handles.color = new Color(1f, 1f, 1f, 0.35f);
            UnityEditor.Handles.DrawWireDisc(root.position, Vector3.up, a.stopDistance);
        }
    }
#endif
}
