using System;
using NaughtyAttributes;
using UnityEngine;

public enum EntityMeleeMoveMode
{
    Dash,     // 用 dashSpeed 衝向目標（類似玩家的近戰突進）
    Normal,   // 以減速後的速度繼續朝目標前進
}

/// <summary>一招近戰攻擊的資料。</summary>
[Serializable]
public class EntityMeleeAttack
{
    public string name = "Attack";
    public bool enabled = true;

    [Header("觸發條件")]
    [Tooltip("與目標的距離範圍（公尺，從實體原點到目標的瞄準點）。")]
    [MinMaxSlider(0f, 60f)]
    public Vector2 distanceRange = new Vector2(0f, 10f);

    [Tooltip("目標偏離身體正前方的最大角度（水平，度）。")]
    [Range(0f, 180f)] public float maxAngle = 45f;

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

    [Header("移動")]
    public EntityMeleeMoveMode moveMode = EntityMeleeMoveMode.Dash;

    [Tooltip("出招前把速度降到原本移動速度（sprintSpeed）的多少 %。Normal 模式的攻擊段也用這個速度。")]
    [Range(0f, 100f)] public float slowdownPercent = 10f;

    [Tooltip("Dash 的速度倍率（× 實體的 dashSpeed）。")]
    [Min(0f)] public float dashSpeedMultiplier = 1f;

    [Tooltip("離目標小於這個距離就不再往前推進（避免穿過目標衝過頭）。")]
    [Min(0f)] public float stopDistance = 2f;

    [Tooltip("攻擊預判：朝目標「到達時會在的位置」移動，而不是目標現在的位置。")]
    public bool usePrediction = true;

    [Header("時間軸（秒）")]
    [Tooltip("預備：減速並轉向目標。")]
    [Min(0f)] public float windup = 0.3f;
    [Tooltip("攻擊段：Dash 衝刺 / Normal 前進的時間。")]
    [Min(0f)] public float activeDuration = 0.4f;
    [Tooltip("收招：維持減速後的速度，不再追蹤目標。")]
    [Min(0f)] public float recovery = 0.4f;
    [Tooltip("命中判定的開始 / 結束時間（從出招開始算，秒）。")]
    [MinMaxSlider(0f, 3f)]
    public Vector2 hitWindow = new Vector2(0.35f, 0.65f);

    [Header("命中範圍（相對於身體：X 右、Y 上、Z 前）")]
    public Vector3 hitOffset = new Vector3(0f, 1f, 2f);
    [Min(0.05f)] public float hitRadius = 1.5f;

    [Header("其他")]
    [Tooltip("這招進行中，砲塔停止開火（仍然會瞄準）。")]
    public bool stopTurretFire = true;

    [Tooltip("出招時觸發的 Animator Trigger 名稱。留空就不觸發。")]
    public string animatorTrigger = "Melee";

    public float TotalDuration => windup + activeDuration + recovery;
}

/// <summary>
/// 實體的近戰攻擊。由 ModularEntityBrain 驅動：
///   · Brain 在 Combat 狀態每次思考時呼叫 TryStartAttack()，這裡決定要不要出招、出哪一招
///   · 出招後，這裡每個物理步負責攻擊的時間軸、移動指令、命中判定與傷害
///   · 目標資料一律向 Brain 讀取（CombatTarget），這裡不保存目標
///   · 移動只透過 ModularEntityMovement 的覆寫指令，不直接改 Rigidbody
///
/// 沒在攻擊時這個元件是停用的（不跑 FixedUpdate），沒有每幀成本。
/// </summary>
public class ModularEntityMeleeManager : MonoBehaviour
{
    [SerializeField] private EntityMeleeAttack[] attacks = new EntityMeleeAttack[0];

    [Header("判斷頻率")]
    [Tooltip("多久擲一次骰子決定要不要出招（秒）。")]
    [Min(0.05f)] [SerializeField] private float evaluationInterval = 0.5f;
    [Tooltip("任何一招結束後，至少隔多久才能再出下一招（秒）。")]
    [Min(0f)] [SerializeField] private float globalCooldown = 1f;

    [Header("命中")]
    [Tooltip("命中判定會掃描的層。只有 Brain 的交戰目標會受到傷害。")]
    [SerializeField] private LayerMask hitLayers = ~0;
    [Tooltip("命中範圍的基準（身體）。留空 = 實體底下第一個 Animator 所在的物件。")]
    [SerializeField] private Transform hitReference;

    [Header("攻擊預判")]
    [Tooltip("目標速度 ÷ 自身速度超過這個比例就不預判（方程式會退化）。")]
    [SerializeField, Range(0.1f, 0.99f)] private float predictionMaxSpeedRatio = 0.9f;
    [Tooltip("預判點最多可以比目標現在的位置遠幾倍。")]
    [SerializeField, Min(1f)] private float predictionMaxLeadMultiplier = 2f;
    [Tooltip("預估抵達時間超過這個秒數就不預判。")]
    [SerializeField, Min(0.05f)] private float predictionMaxTime = 1.5f;

    [Header("除錯")]
    [SerializeField] private bool drawGizmos = true;

    // ── 參考 ──
    private ModularEntityBrain _brain;
    private ModularEntityMovement _movement;
    private ModularEntityStats _stats;
    private Animator _animator;
    private bool _resolved;

    // ── 執行期狀態 ──
    private float[] _readyTime;          // 每招下一次可用的時間
    private float _globalReadyTime;
    private float _nextEvaluateTime;

    private EntityMeleeAttack _current;
    private float _elapsed;
    private bool _hasHit;
    private IDamageable _targetDamageable;
    private Transform _targetDamageableOwner;   // _targetDamageable 是針對哪個目標找的

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
        if (hitReference == null) hitReference = _animator != null ? _animator.transform : root;

        _readyTime = new float[attacks != null ? attacks.Length : 0];
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
        if (pick < 0) return false;   // 落在「什麼都不做」的區間

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

    // ═══════════════════════ 攻擊流程 ═══════════════════════

    private void BeginAttack(int index, float now)
    {
        _current = attacks[index];
        _readyTime[index] = now + _current.cooldown;
        _elapsed = 0f;
        _hasHit = false;

        if (_animator != null && !string.IsNullOrEmpty(_current.animatorTrigger) && HasTrigger(_current.animatorTrigger))
            _animator.SetTrigger(_current.animatorTrigger);

        enabled = true;
        StepMovement(0f);   // 這一步就開始減速、轉向，不用等下一個物理步
    }

    private void FixedUpdate()
    {
        if (_current == null) { enabled = false; return; }

        float dt = Time.fixedDeltaTime;
        _elapsed += dt;

        // ── 中斷條件 ──
        if (_brain == null || _brain.currentState != EntityState.Combat ||
            !IsTargetValid(_brain.CombatTarget) ||
            _movement == null || _movement.IsKnockedBack ||
            (_stats != null && _stats.IsDead))
        {
            EndAttack(cancelled: true);
            return;
        }

        if (_elapsed >= _current.TotalDuration)
        {
            EndAttack(cancelled: false);
            return;
        }

        StepMovement(_elapsed);
        StepHit(_elapsed);
    }

    private void StepMovement(float t)
    {
        EntityMeleeAttack a = _current;
        Transform target = _brain.CombatTarget;
        Vector3 self = _brain.transform.position;
        Vector3 aim = _brain.GetTargetAimPoint(target);

        float slowSpeed = _stats.sprintSpeed * a.slowdownPercent / 100f;
        bool inRecovery = t >= a.windup + a.activeDuration;
        bool inActive = !inRecovery && t >= a.windup;

        float speed;
        float accel;
        if (inActive && a.moveMode == EntityMeleeMoveMode.Dash)
        {
            speed = _stats.dashSpeed * a.dashSpeedMultiplier;
            accel = 0f;                                     // 衝刺：立刻到達
        }
        else if (inActive)
        {
            speed = slowSpeed;
            accel = _stats.accelerationSpeed;
        }
        else
        {
            speed = slowSpeed;                              // 預備 / 收招：降到減速後的速度
            accel = Mathf.Max(_stats.decelerationSpeed, _stats.accelerationSpeed);
        }

        // 收招：沿目前朝向滑行，不再追蹤
        if (inRecovery)
        {
            _movement.SetVelocityOverride(_movement.MeshForward, speed, accel);
            return;
        }

        // ── 攻擊預判：朝「到達時目標會在的位置」──
        Vector3 goal = aim;
        if (a.usePrediction && speed > 0.01f)
            goal = PredictIntercept(self, aim, _brain.GetCombatTargetVelocity(), speed);

        Vector3 toGoal = goal - self;
        toGoal.y = 0f;
        Vector3 toTarget = aim - self;
        toTarget.y = 0f;

        // 已經貼到目標：不再往前推進（避免穿過去），只維持減速後的速度
        if (toTarget.magnitude <= a.stopDistance) speed = Mathf.Min(speed, slowSpeed);

        Vector3 dir = toGoal.sqrMagnitude > 0.0001f ? toGoal : _movement.MeshForward;
        _movement.SetVelocityOverride(dir, speed, accel);
        _movement.SetFacingTarget(toTarget.sqrMagnitude > 0.0001f ? toTarget : dir, 1f);
    }

    private void StepHit(float t)
    {
        EntityMeleeAttack a = _current;
        if (_hasHit || t < a.hitWindow.x || t > a.hitWindow.y) return;

        IDamageable victim = GetTargetDamageable(_brain.CombatTarget);
        if (victim == null) return;

        Vector3 center = hitReference.TransformPoint(a.hitOffset);
        int n = Physics.OverlapSphereNonAlloc(center, a.hitRadius, s_Hits, hitLayers, QueryTriggerInteraction.Collide);
        for (int i = 0; i < n; i++)
        {
            Collider c = s_Hits[i];
            if (c == null) continue;
            IDamageable d = c.GetComponentInParent<IDamageable>();
            if (d == null || !ReferenceEquals(d, victim)) continue;   // 只打 Brain 的交戰目標

            var dmg = new DamageInfo(a.physicalDamage, a.explosionDamage, a.energyDamage, a.coldDamage);
            victim.TakeDamage(dmg, _brain.gameObject);
            _hasHit = true;
            break;
        }
    }

    private void EndAttack(bool cancelled)
    {
        if (_current == null) return;

        if (cancelled && _animator != null && !string.IsNullOrEmpty(_current.animatorTrigger) && HasTrigger(_current.animatorTrigger))
            _animator.ResetTrigger(_current.animatorTrigger);

        _current = null;
        _globalReadyTime = Time.time + globalCooldown;
        if (_movement != null) _movement.ClearVelocityOverride();
        enabled = false;
    }

    // ═══════════════════════ 工具 ═══════════════════════

    private static bool IsTargetValid(Transform t)
    {
        return t != null && t.gameObject.activeInHierarchy;
    }

    /// <summary>目標身上負責受傷的 IDamageable（換目標才重新找）。</summary>
    private IDamageable GetTargetDamageable(Transform target)
    {
        if (target != _targetDamageableOwner)
        {
            _targetDamageableOwner = target;
            _targetDamageable = target != null ? target.GetComponentInParent<IDamageable>() : null;
        }
        return _targetDamageable;
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

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        if (!drawGizmos || attacks == null) return;

        Transform reference = hitReference;
        if (reference == null)
        {
            var anim = GetComponentInParent<ModularEntityBrain>() != null
                ? GetComponentInParent<ModularEntityBrain>().GetComponentInChildren<Animator>()
                : GetComponentInChildren<Animator>();
            reference = anim != null ? anim.transform : transform;
        }
        Transform root = GetComponentInParent<ModularEntityBrain>() != null ? GetComponentInParent<ModularEntityBrain>().transform : transform;

        for (int i = 0; i < attacks.Length; i++)
        {
            EntityMeleeAttack a = attacks[i];
            if (a == null || !a.enabled) continue;

            // 命中範圍（紅）
            Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.8f);
            Gizmos.DrawWireSphere(reference.TransformPoint(a.hitOffset), a.hitRadius);

            // 觸發距離（黃：最大、橘：最小）
            UnityEditor.Handles.color = new Color(1f, 0.9f, 0.2f, 0.6f);
            UnityEditor.Handles.DrawWireDisc(root.position, Vector3.up, a.distanceRange.y);
            if (a.distanceRange.x > 0f)
            {
                UnityEditor.Handles.color = new Color(1f, 0.55f, 0.1f, 0.6f);
                UnityEditor.Handles.DrawWireDisc(root.position, Vector3.up, a.distanceRange.x);
            }

            // 觸發角度
            Vector3 fwd = reference.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.0001f && a.maxAngle < 180f)
            {
                fwd.Normalize();
                UnityEditor.Handles.color = new Color(1f, 0.9f, 0.2f, 0.12f);
                Vector3 from = Quaternion.AngleAxis(-a.maxAngle, Vector3.up) * fwd;
                UnityEditor.Handles.DrawSolidArc(root.position, Vector3.up, from, a.maxAngle * 2f, a.distanceRange.y);
            }
        }
    }
#endif
}
