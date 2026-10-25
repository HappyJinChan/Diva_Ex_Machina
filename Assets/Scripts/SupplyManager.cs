using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 貝努鳥號的支援指令中心。掛在 Landship 上。玩家對戰艦下的支援指令都經過這裡，
/// 由它決定「哪些設施、什麼時候、打哪裡」，再交給各設施的 Brain 執行。
///
/// 目前有：迫擊砲炮擊（Mortar，預設 G）。換裝射出之後也會搬進來（現在還在 SupportTurretBrain）。
///
/// ── 迫擊砲鎖定模式 ──
///   按 G：進入鎖定模式（冷卻中也能進，只是放開時不會開火）。再按一次 G：退出，已鎖定的目標全部取消。
///   模式中玩家照常移動、Dash、突擊推進；左右手、肩膀武器、近戰都不能用（PlayerController 擋掉）。
///
///   按住左鍵（多目標鎖定）：馬上鎖一個，之後每 Lock Interval 秒再鎖一個，
///     從鎖定圈內、玩家的 Lock On Distance 以內（PlayerAiming）、而且離船 Mortar Range 以內、
///     還沒鎖過的敵人裡挑「離畫面中央最近」的；最多鎖到迫擊砲數量。
///     鎖定中的目標死掉、或跑出鎖定圈 → 從清單移除，空出的名額可以再鎖。
///     每個鎖定的目標周圍會畫一個圈（MortarLockMarkers）。
///     放開：一個都沒鎖到 → 什麼都不發生（留在模式裡）；有鎖到 → 每座迫擊砲各分配一個目標開火，模式結束。
///     鎖定目標的炮擊不散布（砲彈直接瞄目標）。
///     迫擊砲比目標多：依序輪流分配（砲 0 → 目標 0、砲 1 → 目標 1…），盡量平均。
///   按住右鍵（範圍炮擊）：準星打到的地面 / 建築顯示圓圈，跟著準星移動，
///     半徑從 Area Start Radius 在 Area Grow Time 秒內擴大到 Area Max Radius。
///     放開：所有迫擊砲往圈內炮擊（砲彈的散布半徑改成圈的半徑），模式結束。
///   左右鍵同時：先按的那個生效，另一個放開前都不理會。
///
/// ── 炮擊 ──
///   順序：所有迫擊砲輪流各射一發，間隔 = Round Duration ÷ 迫擊砲數量，共 Rounds 輪（每座 3 發）。
///         每一輪開始時重新清點（被摧毀的跳過、新建的加入）。
///   目標死掉 → 分配到它的迫擊砲，剩下的砲彈打它最後的位置（在甲板上就跟著船走）。
///   冷卻：從開火開始算 Cooldown 秒。炮擊中不接受新的炮擊。
///   預瞄：沒在炮擊時，迫擊砲一直跟著「現在放開會打的地方」轉（每 Pre Aim Interval 秒更新）。
/// </summary>
[DisallowMultipleComponent]
public class SupplyManager : MonoBehaviour
{
    public static SupplyManager Instance { get; private set; }

    public enum MortarMode { Off, Ready, LockingTargets, AimingArea }

    [Header("Mortar Barrage")]
    [Tooltip("Input Actions 裡 Player 底下的動作名稱。")]
    [SerializeField] private string mortarActionName = "Mortar";

    [Tooltip("幾輪（每座迫擊砲打幾發）。")]
    [SerializeField, Min(1)] private int rounds = 3;

    [Tooltip("每一輪的秒數。發射間隔 = 這個 ÷ 迫擊砲數量。")]
    [SerializeField, Min(0.1f)] private float roundDuration = 6f;

    [Tooltip("冷卻（秒），從開火開始算。要比整段炮擊（Rounds × Round Duration）長。")]
    [SerializeField, Min(0f)] private float cooldown = 30f;

    [Tooltip("預瞄的更新間隔（秒）。")]
    [SerializeField, Min(0.02f)] private float preAimInterval = 0.1f;

    [Tooltip("準星射線最遠打多遠（公尺）。")]
    [SerializeField, Min(10f)] private float maxTargetDistance = 5000f;

    [Tooltip("準星射線會打到的層。留空 = 用 PlayerAiming 的 Ignore Layer 以外的所有層。")]
    [SerializeField] private LayerMask targetRayMask;

    [Tooltip("只用這艘船（子物件）上的迫擊砲。關掉 = 場上所有迫擊砲。")]
    [SerializeField] private bool onlyMortarsOnThisShip = true;

    [Header("Targeting Mode — 左鍵：多目標鎖定")]
    [Tooltip("按住左鍵時，每隔幾秒多鎖一個。")]
    [SerializeField, Min(0.05f)] private float lockInterval = 0.5f;

    [Tooltip("迫擊砲射程（公尺，從這艘船算）。超出的敵人不會被鎖定。\n" +
             "跟玩家的鎖定距離（PlayerAiming 的 Lock On Distance）兩個條件都要符合。")]
    [SerializeField, Min(10f)] private float mortarRange = 3000f;

    [Header("Targeting Mode — 右鍵：範圍炮擊")]
    [SerializeField, Min(0.5f)] private float areaStartRadius = 5f;
    [SerializeField, Min(0.5f)] private float areaMaxRadius = 30f;
    [Tooltip("從 Start 擴大到 Max 需要幾秒。")]
    [SerializeField, Min(0.01f)] private float areaGrowTime = 1.5f;

    /// <summary>炮擊開始。參數：第一個目標（可能是 null）、第一個目標點、這次的迫擊砲數量。</summary>
    public event Action<Transform, Vector3, int> OnBarrageStarted;

    /// <summary>炮擊的所有發射指令都下完了（最後一發可能還在空中）。</summary>
    public event Action OnBarrageFinished;

    /// <summary>鎖定模式開 / 關（給之後的 UI）。</summary>
    public event Action<bool> OnTargetingModeChanged;

    /// <summary>多鎖了一個目標（給之後的 UI）。</summary>
    public event Action<Transform> OnTargetLocked;

    public bool IsBarrageActive => _barrage != null;
    public float CooldownRemaining => Mathf.Max(0f, _cooldownUntil - Time.time);

    /// <summary>目前的鎖定模式狀態。</summary>
    public MortarMode Mode { get; private set; } = MortarMode.Off;

    /// <summary>是否在迫擊砲鎖定模式中（PlayerController 用來擋掉武器）。</summary>
    public bool IsMortarTargeting => Mode != MortarMode.Off;

    /// <summary>目前鎖定的目標（左鍵）。</summary>
    public IReadOnlyList<Transform> LockedTargets => _locks;

    /// <summary>
    /// 要畫鎖定圈的目標（MortarLockMarkers 用）：鎖定中 → 已鎖定的目標；
    /// includeBarrage 時，鎖定目標的炮擊還沒結束（或砲彈還在空中）→ 這次炮擊還活著的目標。回傳數量。
    /// </summary>
    public int GetMarkedTargets(List<Transform> result, bool includeBarrage)
    {
        result.Clear();
        if (Mode == MortarMode.LockingTargets)
        {
            foreach (Transform t in _locks)
                if (t != null && t.gameObject.activeInHierarchy) result.Add(t);
        }
        else if (includeBarrage && IsTargetLockBarrage && (IsBarrageActive || MortarShell.LiveCount > 0))
        {
            foreach (BarrageAim a in _barrageAims)
            {
                Transform t = a.target;
                if (t != null && t.gameObject.activeInHierarchy && !result.Contains(t)) result.Add(t);
            }
        }
        return result.Count;
    }

    public enum MarkState { Locking, Fired, Lost }

    /// <summary>對敵鎖定的標記（MortarLockArrows 用）。</summary>
    public struct MarkedTarget
    {
        public int id;               // 目標的 InstanceID（從鎖定到開火都一樣，UI 用來對應同一個標記）
        public Transform target;     // 死掉後是 null
        public Vector3 position;     // 目標的瞄準點；死掉後是最後的位置
        public MarkState state;      // 鎖定中 / 已開火 / 目標已死（砲彈還在飛）
    }

    /// <summary>
    /// 對敵鎖定的標記：鎖定中 → 已鎖定的目標（Locking）；
    /// 對敵鎖定的炮擊還沒結束、或砲彈還在空中 → 這次炮擊的目標（活著 = Fired，死掉 = Lost，在最後位置）。回傳數量。
    /// </summary>
    public int GetMarkedTargets(List<MarkedTarget> result)
    {
        result.Clear();
        if (Mode == MortarMode.LockingTargets)
        {
            foreach (Transform t in _locks)
            {
                if (t == null || !t.gameObject.activeInHierarchy) continue;
                result.Add(new MarkedTarget
                {
                    id = t.GetInstanceID(),
                    target = t,
                    position = TurretController.GetAimPoint(t),
                    state = MarkState.Locking,
                });
            }
        }
        else if (IsTargetLockBarrage && (IsBarrageActive || MortarShell.LiveCount > 0))
        {
            foreach (BarrageAim a in _barrageAims)
            {
                Vector3 p = a.MarkPosition(out bool alive);
                result.Add(new MarkedTarget
                {
                    id = a.id,
                    target = alive ? a.target : null,
                    position = p,
                    state = alive ? MarkState.Fired : MarkState.Lost,
                });
            }
        }
        return result.Count;
    }

    /// <summary>這次 / 最近一次炮擊是從「左鍵多目標鎖定」發起的。</summary>
    public bool IsTargetLockBarrage { get; private set; }

    /// <summary>範圍炮擊的半徑；這次炮擊不是範圍炮擊時為 0。</summary>
    public float BarrageAreaRadius { get; private set; }

    /// <summary>右鍵瞄準中的範圍（跟著準星、慢慢變大）。不在瞄準、或準星沒打到東西時回傳 false。</summary>
    public bool TryGetAreaAim(out Vector3 center, out float radius)
    {
        center = default;
        radius = 0f;
        if (Mode != MortarMode.AimingArea || _areaSpot == null) return false;
        center = _areaSpot.Position;
        radius = CurrentAreaRadius;
        return true;
    }

    /// <summary>最近一次炮擊的目標點（第一個目標；死掉 / 甲板上的點跟著船走）。還沒打過就回傳 false。</summary>
    public bool TryGetBarrageCenter(out Vector3 center)
    {
        center = default;
        if (_barrageAims.Count == 0) return false;
        BarrageAim a = _barrageAims[0];
        a.Refresh();
        center = a.target != null ? TurretController.GetAimPoint(a.target) : a.spot.Position;
        return true;
    }

    // ── 內部狀態 ─────────────────────────────────────────────

    /// <summary>一個炮擊目標：追一個敵人（死掉後打最後的位置），或一個點（可選：散布半徑）。</summary>
    private sealed class BarrageAim
    {
        public Transform target;
        public AimSpot spot;
        public float scatter = -1f;   // < 0 = 用砲彈自己的散布半徑
        public int id;                // 目標的 InstanceID（標記 UI 用來對應同一個目標）；點目標 = 0
        public Vector3 lastSeen;      // 目標最後一次被看到的瞄準點

        public static BarrageAim ForTarget(Transform t, float scatter = -1f)
        {
            Vector3 p = TurretController.GetAimPoint(t);
            return new BarrageAim { target = t, spot = AimSpot.Static(p), scatter = scatter, id = t.GetInstanceID(), lastSeen = p };
        }

        public static BarrageAim ForSpot(AimSpot s, float scatter = -1f) =>
            new BarrageAim { spot = s, scatter = scatter };

        /// <summary>目標還活著 → 更新最後位置；剛死掉 → 最後位置在甲板上就黏在甲板上。</summary>
        public void Refresh()
        {
            if (target != null && target.gameObject.activeInHierarchy)
            {
                lastSeen = TurretController.GetAimPoint(target);
                spot = AimSpot.Static(lastSeen);
            }
            else if (!ReferenceEquals(target, null))   // 被 Destroy 的物件 == null 也是 true，要看參照本身
            {
                spot = AimSpot.AnchoredBelow(lastSeen);
                target = null;
            }
        }

        /// <summary>標記 UI 用：目標活著 → 它現在的瞄準點；死掉 → 最後的位置（甲板上跟著船走）。活著時不配置記憶體。</summary>
        public Vector3 MarkPosition(out bool alive)
        {
            if (target != null && target.gameObject.activeInHierarchy)
            {
                alive = true;
                lastSeen = TurretController.GetAimPoint(target);
                return lastSeen;
            }
            Refresh();   // 剛死掉：換成最後位置
            alive = false;
            return spot.Position;
        }

        public void ApplyTo(MortarBrain m, bool withScatter)
        {
            Refresh();
            m.SetAim(target, spot);
            if (withScatter && scatter >= 0f) m.SetScatterOverride(scatter);
        }
    }

    private PlayerControllers _controls;
    private InputAction _mortarAction;
    private InputAction _leftAction;
    private InputAction _rightAction;

    private Coroutine _barrage;
    private float _cooldownUntil;
    private float _nextPreAimTime;
    private readonly List<MortarBrain> _mortars = new List<MortarBrain>();
    private readonly List<BarrageAim> _barrageAims = new List<BarrageAim>();   // 這次 / 最近一次炮擊
    private readonly List<BarrageAim> _planAims = new List<BarrageAim>();      // 預瞄用（暫存）

    // 鎖定模式
    private readonly List<Transform> _locks = new List<Transform>();
    private float _nextLockTime;
    private AimSpot _areaSpot;
    private float _areaStartTime;

    private float CurrentAreaRadius =>
        Mathf.Lerp(areaStartRadius, Mathf.Max(areaStartRadius, areaMaxRadius),
                   Mathf.Clamp01((Time.time - _areaStartTime) / areaGrowTime));

    // ── 生命週期 ─────────────────────────────────────────────

    private void Awake()
    {
        if (Instance != null && Instance != this)
            Debug.LogWarning("SupplyManager: 場上有兩個 SupplyManager，後來的這個會取代前一個", this);
        Instance = this;

        _controls = new PlayerControllers();
        // 用名稱找，不直接用 _controls.Player.Mortar：Input Actions 還沒存檔重新產生程式碼時也能編譯
        _mortarAction = _controls.asset.FindAction($"Player/{mortarActionName}", false);
        if (_mortarAction == null)
            Debug.LogWarning($"SupplyManager: Input Actions 裡找不到 Player/{mortarActionName}（記得在 Input Actions 視窗按 Save Asset）", this);

        _leftAction = _controls.Player.LeftHandAttack;
        _rightAction = _controls.Player.RightHandAttack;
    }

    private void OnEnable() => _controls?.Player.Enable();

    private void OnDisable()
    {
        _controls?.Player.Disable();
        if (Mode != MortarMode.Off) ExitTargeting();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _controls?.Dispose();
    }

    private void Update()
    {
        if (IsGameplayInputBlocked())
        {
            // 裝備 / 合成介面打開：直接退出鎖定模式
            if (Mode != MortarMode.Off) ExitTargeting();
        }
        else
        {
            if (_mortarAction != null && _mortarAction.WasPressedThisFrame())
            {
                if (Mode == MortarMode.Off) EnterTargeting();
                else ExitTargeting();
            }
            else if (Mode != MortarMode.Off)
            {
                UpdateTargeting();
            }
        }

        // 預瞄（炮擊中由炮擊自己指定目標）
        if (_barrage == null && Time.time >= _nextPreAimTime)
        {
            _nextPreAimTime = Time.time + preAimInterval;
            PreAim();
        }
    }

    // 跟 PlayerController 一樣：裝備 / 合成介面開著時不接受操作
    private static bool IsGameplayInputBlocked() =>
        UIManager.Instance != null && UIManager.Instance.currentCameraSet != 0;

    // ── 鎖定模式 ─────────────────────────────────────────────

    private void EnterTargeting()
    {
        Mode = MortarMode.Ready;
        _locks.Clear();
        _areaSpot = null;
        OnTargetingModeChanged?.Invoke(true);
    }

    /// <summary>退出鎖定模式，取消所有鎖定（已經開始的炮擊不受影響）。</summary>
    public void ExitTargeting()
    {
        if (Mode == MortarMode.Off) return;
        Mode = MortarMode.Off;
        _locks.Clear();
        _areaSpot = null;
        OnTargetingModeChanged?.Invoke(false);
    }

    private void UpdateTargeting()
    {
        switch (Mode)
        {
            case MortarMode.Ready:
                // 要「新按下」才開始，進模式時剛好按著的不算
                if (_leftAction.WasPressedThisFrame())
                {
                    Mode = MortarMode.LockingTargets;
                    _nextLockTime = 0f;   // 按下馬上鎖第一個
                    UpdateLocking();
                }
                else if (_rightAction.WasPressedThisFrame())
                {
                    Mode = MortarMode.AimingArea;
                    _areaStartTime = Time.time;
                    UpdateAreaAim();
                }
                break;

            case MortarMode.LockingTargets:
                if (_leftAction.IsPressed()) UpdateLocking();
                else ReleaseLocking();
                break;

            case MortarMode.AimingArea:
                if (_rightAction.IsPressed()) UpdateAreaAim();
                else ReleaseAreaAim();
                break;
        }
    }

    private void UpdateLocking()
    {
        PruneLocks();

        if (Time.time < _nextLockTime) return;

        CollectMortars(_mortars);
        if (_locks.Count >= _mortars.Count) return;

        var pa = PlayerAiming.Instance;
        if (pa == null) return;

        Transform t = pa.FindLockCandidate(transform.position, mortarRange, _locks);
        if (t == null) return;   // 圈內沒有新目標：下一幀再找（不用等滿間隔）

        _locks.Add(t);
        _nextLockTime = Time.time + lockInterval;
        OnTargetLocked?.Invoke(t);
    }

    /// <summary>死掉的、跑出鎖定圈的目標取消鎖定（交還名額）。</summary>
    private void PruneLocks()
    {
        var pa = PlayerAiming.Instance;
        for (int i = _locks.Count - 1; i >= 0; i--)
        {
            Transform t = _locks[i];
            if (t == null || !t.gameObject.activeInHierarchy || (pa != null && !pa.IsInLockCircle(t)))
                _locks.RemoveAt(i);
        }
    }

    private void ReleaseLocking()
    {
        PruneLocks();
        if (_locks.Count == 0)
        {
            Mode = MortarMode.Ready;   // 一個都沒鎖到：什麼都不發生
            return;
        }

        var aims = new List<BarrageAim>(_locks.Count);
        foreach (Transform t in _locks) aims.Add(BarrageAim.ForTarget(t, 0f));   // 鎖定目標：不散布

        if (StartBarrage(aims, 0f, fromLock: true)) ExitTargeting();
        else
        {
            _locks.Clear();            // 冷卻中 / 射程外：不開火，留在模式裡
            Mode = MortarMode.Ready;
        }
    }

    private void UpdateAreaAim()
    {
        var pa = PlayerAiming.Instance;
        if (pa == null) { _areaSpot = null; return; }

        int mask = targetRayMask.value != 0 ? targetRayMask.value : ~pa.ignoreLayer.value;
        _areaSpot = Physics.Raycast(pa.GetRay(), out RaycastHit hit, maxTargetDistance, mask, QueryTriggerInteraction.Ignore)
            ? AimSpot.FromHit(hit)
            : null;
    }

    private void ReleaseAreaAim()
    {
        AimSpot spot = _areaSpot;
        float radius = CurrentAreaRadius;
        _areaSpot = null;

        if (spot == null)
        {
            Mode = MortarMode.Ready;   // 準星沒打到東西
            return;
        }

        var aims = new List<BarrageAim>(1) { BarrageAim.ForSpot(spot, radius) };
        if (StartBarrage(aims, radius, fromLock: false)) ExitTargeting();
        else Mode = MortarMode.Ready;
    }

    // ── 炮擊 ─────────────────────────────────────────────────

    /// <summary>
    /// 立刻對「現在的鎖定目標 / 準星落點」炮擊（不經過鎖定模式）。給測試和其他系統用。
    /// 回傳 false = 這次不打。
    /// </summary>
    public bool RequestMortarBarrage()
    {
        if (!TryGetPlayerTarget(out Transform target, out AimSpot spot))
        {
            Debug.Log("SupplyManager: 沒有鎖定敵人，準星也沒有打到任何東西", this);
            return false;
        }
        var aims = new List<BarrageAim>(1) { target != null ? BarrageAim.ForTarget(target) : BarrageAim.ForSpot(spot) };
        return StartBarrage(aims, 0f, fromLock: false);
    }

    /// <summary>開始炮擊。迫擊砲 i 打 aims[i % aims.Count]。</summary>
    private bool StartBarrage(List<BarrageAim> aims, float areaRadius, bool fromLock)
    {
        if (_barrage != null)
        {
            Debug.Log("SupplyManager: 炮擊進行中", this);
            return false;
        }
        if (Time.time < _cooldownUntil)
        {
            Debug.Log($"SupplyManager: 迫擊砲冷卻中，還有 {CooldownRemaining:0.0} 秒", this);
            return false;
        }

        CollectMortars(_mortars);
        if (_mortars.Count == 0)
        {
            Debug.Log("SupplyManager: 船上沒有迫擊砲", this);
            return false;
        }

        // 先全部瞄過去（同步解算），確認至少一座打得到
        int canHit = 0;
        for (int i = 0; i < _mortars.Count; i++)
        {
            aims[i % aims.Count].ApplyTo(_mortars[i], withScatter: false);
            if (_mortars[i].CanHit) canHit++;
        }
        if (canHit == 0)
        {
            Debug.Log("SupplyManager: 目標在所有迫擊砲的射程外（太遠或太近）", this);
            return false;
        }

        _cooldownUntil = Time.time + cooldown;
        _barrageAims.Clear();
        _barrageAims.AddRange(aims);
        BarrageAreaRadius = areaRadius;
        IsTargetLockBarrage = fromLock;

        _barrage = StartCoroutine(Barrage());

        BarrageAim first = aims[0];
        OnBarrageStarted?.Invoke(first.target, first.spot.Position, _mortars.Count);

        string what = areaRadius > 0f ? $"範圍 {first.spot.Position}（半徑 {areaRadius:0.#} m）"
                    : aims.Count > 1 ? $"{aims.Count} 個目標"
                    : first.target != null ? $"'{first.target.name}'"
                    : $"{first.spot.Position}";
        Debug.Log($"SupplyManager: 迫擊砲炮擊 → {what}，{_mortars.Count} 座（{canHit} 座在射程內），" +
                  $"{rounds} 輪 × {roundDuration:0.#} 秒", this);
        return true;
    }

    private IEnumerator Barrage()
    {
        var mortars = new List<MortarBrain>();

        for (int round = 0; round < rounds; round++)
        {
            // 每一輪重新清點：被摧毀的跳過、新建的加入
            CollectMortars(mortars);
            if (mortars.Count == 0) break;

            float interval = roundDuration / mortars.Count;
            for (int i = 0; i < mortars.Count; i++)
            {
                MortarBrain m = mortars[i];
                if (m != null && m.isActiveAndEnabled)
                {
                    _barrageAims[i % _barrageAims.Count].ApplyTo(m, withScatter: true);
                    m.RequestFire();
                }
                yield return new WaitForSeconds(interval);
            }
        }

        _barrage = null;
        OnBarrageFinished?.Invoke();
    }

    // ── 預瞄 ─────────────────────────────────────────────────

    private void PreAim()
    {
        CollectMortars(_mortars);
        if (_mortars.Count == 0) return;

        BuildPlan(_planAims);
        for (int i = 0; i < _mortars.Count; i++)
        {
            MortarBrain m = _mortars[i];
            if (m.HasPendingFire) continue;   // 上一次炮擊最後一發還在等對準，不要打斷
            if (_planAims.Count > 0) _planAims[i % _planAims.Count].ApplyTo(m, withScatter: false);
            else m.ClearAim();
        }
    }

    /// <summary>「現在放開會打的地方」：鎖定中的目標 / 範圍圓心 / 準星。</summary>
    private void BuildPlan(List<BarrageAim> plan)
    {
        plan.Clear();

        if (Mode == MortarMode.LockingTargets && _locks.Count > 0)
        {
            foreach (Transform t in _locks)
                if (t != null && t.gameObject.activeInHierarchy) plan.Add(BarrageAim.ForTarget(t));
            if (plan.Count > 0) return;
        }

        if (Mode == MortarMode.AimingArea && _areaSpot != null)
        {
            plan.Add(BarrageAim.ForSpot(_areaSpot));
            return;
        }

        if (TryGetPlayerTarget(out Transform target, out AimSpot spot))
            plan.Add(target != null ? BarrageAim.ForTarget(target) : BarrageAim.ForSpot(spot));
    }

    /// <summary>
    /// 玩家現在指著的地方：武器鎖定中的敵人，或準星射線的落點。
    /// 落點打在會動的東西（甲板）上時，spot 會黏在它上面。
    /// </summary>
    public bool TryGetPlayerTarget(out Transform target, out AimSpot spot)
    {
        target = null;
        spot = null;

        var pa = PlayerAiming.Instance;
        if (pa == null) return false;

        if (pa.TryGetLockedTarget(out target))
        {
            spot = AimSpot.Static(TurretController.GetAimPoint(target));
            return true;
        }

        int mask = targetRayMask.value != 0 ? targetRayMask.value : ~pa.ignoreLayer.value;
        if (Physics.Raycast(pa.GetRay(), out RaycastHit hit, maxTargetDistance, mask, QueryTriggerInteraction.Ignore))
        {
            spot = AimSpot.FromHit(hit);
            return true;
        }
        return false;
    }

    private void CollectMortars(List<MortarBrain> result)
    {
        result.Clear();
        var all = MortarBrain.All;
        for (int i = 0; i < all.Count; i++)
        {
            MortarBrain m = all[i];
            if (m == null || !m.isActiveAndEnabled) continue;
            if (onlyMortarsOnThisShip && !m.transform.IsChildOf(transform)) continue;
            result.Add(m);
        }
    }

    [ContextMenu("Request Mortar Barrage")]
    private void RequestMortarBarrageFromMenu() => RequestMortarBarrage();
}
