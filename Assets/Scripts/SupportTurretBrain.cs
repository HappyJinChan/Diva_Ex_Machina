using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 支援炮塔的 Brain：處理玩家給的指令，決定「打誰、什麼時候打」。
/// 實際的瞄準與射擊交給 BallisticTurretController —— 跟 ModularEntityBrain 和 TurretController 的關係一樣。
///
/// 換裝射出：
///   玩家按 Set1 / Set2 / Set3（PlayerControllers 的 Player 動作）→ 對 Bradamant 射出那一套。
///   - 已經穿著那一套 → 什麼都不做。
///   - 那一套是空的 → 不射（射一個空艙沒有意義）。
///   - 砲塔正在瞄準、或已經有一個艙在空中 → 不接受新的請求（一次一個艙）。
///   支援艙命中 Bradamant 的那一刻 → SupplyBurst.Burst()（換裝爆炸）→ SupplyBurst.GearChange()（換裝硬直）
///   → EquipmentManager.SwitchSet(那一套) → SupplyBurst.PlayHitFlash()（全身閃光，經 PlayerEffectManager 播 MMF）。
///
/// 流程：
///   RequestLaunch(目標, 套裝) → 檢查範圍 → 瞄準中（每次思考 TrackTarget 續約）
///   → 炮口對準（IsAimed）→ Shoot() → 砲彈生成時把「命中後換第幾套」掛到艙上
///   → 回到待命（炮管放回中立朝向）；艙命中或消失後才接受下一個請求。
///
/// 測試：Test Key（預設 L）或 ⋮ → Request Launch At Test Target，對 Test Target 射一個不換裝的艙。
///
/// 炮塔設定建議（BallisticTurretController）：Magazine Size 1、Rounds Per Fire 1、Bullet Per Round 1、
/// Bullet Spread 0、Yaw Limit 180（不限制）。
/// </summary>
public class SupportTurretBrain : MonoBehaviour
{
    public enum State { Idle, Aiming }

    [SerializeField] private BallisticTurretController turret;

    [Tooltip("Bradamant。留空就找 Tag 為 \"Player\" 的物件。")]
    [SerializeField] private Transform player;

    [Tooltip("換裝爆炸。留空就從 Player 往上、再往下找。")]
    [SerializeField] private SupplyBurst supplyBurst;

    [Tooltip("支援範圍（公尺，水平距離，以砲塔為中心）。補給範圍 = 炮擊範圍。")]
    [SerializeField] private float supportRange = 3000f;

    [Tooltip("多久思考一次（秒）。砲塔的追蹤逾時是 0.5 秒，這個值要比它小。")]
    [SerializeField, Min(0.02f)] private float thinkInterval = 0.1f;

    [Tooltip("瞄準超過這個秒數還沒射出就放棄（例如目標一直在頭頂盲區）。")]
    [SerializeField, Min(1f)] private float aimTimeout = 15f;

    [Header("Test")]
    [SerializeField] private Transform testTarget;
    [SerializeField] private Key testKey = Key.L;

    /// <summary>射出時觸發。參數：目標、預測飛行時間（秒）。</summary>
    public event Action<Transform, float> OnLaunched;

    public State CurrentState => _state;
    public float SupportRange => supportRange;

    /// <summary>空中有艙（還沒命中 / 消失）。</summary>
    public bool HasPodInFlight => _inFlightPod != null;

    private State _state = State.Idle;
    private Transform _target;
    private int _pendingSet = -1;        // 這次請求要送第幾套（-1 = 不換裝，測試用）
    private float _aimStartTime;
    private float _nextThinkTime;

    private SupplyPod _inFlightPod;
    private PlayerControllers _controls;

    private void Awake()
    {
        if (turret == null) turret = GetComponentInChildren<BallisticTurretController>();
        if (turret == null)
            Debug.LogWarning("SupportTurretBrain: 找不到 BallisticTurretController", this);
        else
            turret.OnProjectileSpawned += HandleProjectileSpawned;

        _controls = new PlayerControllers();
    }

    private void OnEnable() => _controls?.Player.Enable();
    private void OnDisable() => _controls?.Player.Disable();

    private void OnDestroy()
    {
        if (turret != null) turret.OnProjectileSpawned -= HandleProjectileSpawned;
        _controls?.Dispose();
    }

    private void Start()
    {
        if (turret != null) turret.AimAtRest();
        if (player == null)
        {
            var go = GameObject.FindGameObjectWithTag("Player");
            if (go != null) player = go.transform;
        }
        ResolveSupplyBurst();
    }

    /// <summary>支援範圍內嗎（水平距離）。之後小地圖也可以用。</summary>
    public bool IsInSupportRange(Vector3 worldPos)
    {
        Vector3 d = worldPos - transform.position;
        d.y = 0f;
        return d.sqrMagnitude <= supportRange * supportRange;
    }

    // ── 玩家指令：換裝射出 ──────────────────────────────────

    /// <summary>玩家要求送第 setIndex 套（0 起算）。回傳 false = 這次不射。</summary>
    public bool RequestEquipmentSet(int setIndex)
    {
        var em = EquipmentManager.Instance;
        if (em == null || player == null) return false;

        if (setIndex == em.ActiveSetIndex) return false;   // 已經穿著 → 什麼都不做

        if (em.IsSetEmpty(setIndex))
        {
            Debug.Log($"SupportTurretBrain: 第 {setIndex + 1} 套是空的，不射出", this);
            return false;
        }

        return RequestLaunch(player, setIndex);
    }

    /// <summary>對 target 發射。setIndex ≥ 0：命中後換成那一套；-1：不換裝（測試用）。回傳 false = 拒絕。</summary>
    public bool RequestLaunch(Transform target, int setIndex = -1)
    {
        if (turret == null || target == null) return false;

        if (_state != State.Idle || HasPodInFlight)
        {
            Debug.Log("SupportTurretBrain: 砲塔正在瞄準或已有支援艙在空中，這次不接受", this);
            return false;
        }
        if (!IsInSupportRange(target.position))
        {
            Debug.Log("SupportTurretBrain: 目標在支援範圍外", this);
            return false;
        }

        _target = target;
        _pendingSet = setIndex;
        _state = State.Aiming;
        _aimStartTime = Time.time;
        _nextThinkTime = 0f;   // 下一幀立刻思考
        turret.TrackTarget(_target);
        return true;
    }

    /// <summary>取消目前的請求，炮管放回中立朝向。（已經在空中的艙不受影響。）</summary>
    public void CancelRequest()
    {
        _state = State.Idle;
        _target = null;
        _pendingSet = -1;
        if (turret != null) turret.AimAtRest();
    }

    // ── 每幀 ───────────────────────────────────────────────

    private void Update()
    {
        ReadInput();

        if (_state == State.Idle || turret == null) return;
        if (Time.time < _nextThinkTime) return;
        _nextThinkTime = Time.time + thinkInterval;

        Think();
    }

    private void ReadInput()
    {
        if (_controls != null)
        {
            var p = _controls.Player;
            if (p.Set1.WasPressedThisFrame()) RequestEquipmentSet(0);
            else if (p.Set2.WasPressedThisFrame()) RequestEquipmentSet(1);
            else if (p.Set3.WasPressedThisFrame()) RequestEquipmentSet(2);
        }

        if (Keyboard.current != null && Keyboard.current[testKey].wasPressedThisFrame)
            RequestLaunchAtTestTarget();
    }

    private void Think()
    {
        if (_target == null)
        {
            CancelRequest();
            return;
        }

        if (!IsInSupportRange(_target.position))
        {
            Debug.Log("SupportTurretBrain: 目標離開支援範圍，取消", this);
            CancelRequest();
            return;
        }

        if (Time.time - _aimStartTime > aimTimeout)
        {
            Debug.Log("SupportTurretBrain: 瞄準逾時（目標可能在頭頂盲區），取消", this);
            CancelRequest();
            return;
        }

        turret.TrackTarget(_target);   // 續約（砲塔每幀自己重算拋物線）

        if (!turret.IsAimed) return;

        // Shoot() 有自己的閘門（冷卻、換彈中會安靜跳過）→ 用彈匣數確認真的射出了。
        // 射出時 HandleProjectileSpawned 會在 Shoot() 裡同步被呼叫，_pendingSet 那時還在。
        int ammoBefore = turret.AmmoInMagazine;
        float flightTime = turret.ExpectedFlightTime;
        turret.Shoot();
        if (turret.AmmoInMagazine < ammoBefore)
        {
            OnLaunched?.Invoke(_target, flightTime);
            CancelRequest();
        }
    }

    // ── 砲彈生成：把「命中後換第幾套」掛上去 ──────────────────

    private void HandleProjectileSpawned(GameObject go)
    {
        if (go == null || !go.TryGetComponent(out SupplyPod pod)) return;

        _inFlightPod = pod;
        pod.OnDespawn += HandlePodDespawn;

        int setIndex = _pendingSet;
        if (setIndex >= 0)
        {
            pod.OnHitTarget += (p, target) =>
            {
                var em = EquipmentManager.Instance;
                if (em == null) return;
                Debug.Log($"SupportTurretBrain: 支援艙命中，換上第 {setIndex + 1} 套", this);
                // 先爆炸、再換裝：閃光蓋住換模型的那一幀
                if (ResolveSupplyBurst() != null)
                {
                    supplyBurst.Burst();
                    supplyBurst.GearChange();   // 速度歸零、暫停移動、播 Gear Change
                }
                else Debug.LogWarning("SupportTurretBrain: 找不到 SupplyBurst（Player 和它的父 / 子物件都沒有），這次不爆炸", this);
                em.SwitchSet(setIndex);   // 已經穿著那一套的話，SwitchSet 什麼都不做

                // 閃光放在換裝之後：新裝甲才會一起閃
                if (supplyBurst != null) supplyBurst.PlayHitFlash();
            };
        }
    }

    // 命中當下再找一次：SupplyBurst 比 Brain 的 Start 晚加上去（例如 Play 中才加）也找得到
    private SupplyBurst ResolveSupplyBurst()
    {
        if (supplyBurst == null && player != null)
        {
            supplyBurst = player.GetComponentInParent<SupplyBurst>();
            if (supplyBurst == null) supplyBurst = player.GetComponentInChildren<SupplyBurst>();
        }
        return supplyBurst;
    }

    private void HandlePodDespawn(SupplyPod pod)
    {
        if (_inFlightPod == pod) _inFlightPod = null;
    }

    // ── 測試 ───────────────────────────────────────────────

    [ContextMenu("Request Launch At Test Target")]
    private void RequestLaunchAtTestTarget()
    {
        Transform t = testTarget != null ? testTarget : player;
        if (t == null)
        {
            Debug.LogWarning("SupportTurretBrain: 沒有 Test Target，也找不到 Player", this);
            return;
        }
        RequestLaunch(t);
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.8f);
        Vector3 c = transform.position;
        const int seg = 128;
        Vector3 prev = c + new Vector3(supportRange, 0f, 0f);
        for (int i = 1; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2f / seg;
            Vector3 p = c + new Vector3(Mathf.Cos(a) * supportRange, 0f, Mathf.Sin(a) * supportRange);
            Gizmos.DrawLine(prev, p);
            prev = p;
        }
    }
#endif
}
