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
/// ── 迫擊砲炮擊 ──
///   目標：鎖定圈裡有敵人 → 追那個敵人（每一發都打他當下的位置，死掉後打他最後的位置）；
///         沒有 → 準星射線打到的地面 / 建築，固定在按下那一刻的位置。
///         打到的是會動的東西（例如貝努鳥號的甲板）→ 黏在它上面，跟著它走（AimSpot）。
///   預瞄：沒在炮擊時，船上所有迫擊砲一直跟著「現在按下會打的地方」轉（每 Pre Aim Interval 秒更新）。
///   順序：所有迫擊砲輪流各射一發，間隔 = Round Duration ÷ 迫擊砲數量，共 Rounds 輪。
///         預設 3 輪 × 6 秒 = 18 秒，不管有幾座。每一輪開始時重新清點（被摧毀的跳過、新建的加入）。
///   冷卻：從按下開始算 Cooldown 秒（要比整段炮擊長）。炮擊中不接受新的指令。
///   船上的迫擊砲：MortarBrain.All 裡、是這艘船子物件的那些（Only Mortars On This Ship）。
/// </summary>
[DisallowMultipleComponent]
public class SupplyManager : MonoBehaviour
{
    public static SupplyManager Instance { get; private set; }

    [Header("Mortar Barrage")]
    [Tooltip("Input Actions 裡 Player 底下的動作名稱。")]
    [SerializeField] private string mortarActionName = "Mortar";

    [Tooltip("幾輪。")]
    [SerializeField, Min(1)] private int rounds = 3;

    [Tooltip("每一輪的秒數。發射間隔 = 這個 ÷ 迫擊砲數量。")]
    [SerializeField, Min(0.1f)] private float roundDuration = 6f;

    [Tooltip("冷卻（秒），從按下開始算。要比整段炮擊（Rounds × Round Duration）長。")]
    [SerializeField, Min(0f)] private float cooldown = 30f;

    [Tooltip("預瞄的更新間隔（秒）。")]
    [SerializeField, Min(0.02f)] private float preAimInterval = 0.1f;

    [Tooltip("沒有鎖定時，準星射線最遠打多遠（公尺）。")]
    [SerializeField, Min(10f)] private float maxTargetDistance = 5000f;

    [Tooltip("準星射線會打到的層。留空 = 用 PlayerAiming 的 Ignore Layer 以外的所有層。")]
    [SerializeField] private LayerMask targetRayMask;

    [Tooltip("只用這艘船（子物件）上的迫擊砲。關掉 = 場上所有迫擊砲。")]
    [SerializeField] private bool onlyMortarsOnThisShip = true;

    /// <summary>炮擊開始。參數：目標（可能是 null）、目標點、這次的迫擊砲數量。</summary>
    public event Action<Transform, Vector3, int> OnBarrageStarted;

    /// <summary>炮擊的所有發射指令都下完了（最後一發可能還在空中）。</summary>
    public event Action OnBarrageFinished;

    public bool IsBarrageActive => _barrage != null;

    /// <summary>最近一次炮擊的目標點（目標還活著就跟著目標；死掉 / 甲板上的點跟著船走）。還沒打過就回傳 false。</summary>
    public bool TryGetBarrageCenter(out Vector3 center)
    {
        if (_centerTarget != null && _centerTarget.gameObject.activeInHierarchy)
        {
            _centerLast = TurretController.GetAimPoint(_centerTarget);
        }
        else
        {
            if (!ReferenceEquals(_centerTarget, null))   // 目標剛被打死：在甲板上就黏在甲板上
            {
                _centerSpot = AimSpot.AnchoredBelow(_centerLast);
                _centerTarget = null;
            }
            if (_centerSpot != null) _centerLast = _centerSpot.Position;
        }

        center = _centerLast;
        return _hasCenter;
    }
    public float CooldownRemaining => Mathf.Max(0f, _cooldownUntil - Time.time);

    private PlayerControllers _controls;
    private InputAction _mortarAction;
    private Coroutine _barrage;
    private float _cooldownUntil;
    private float _nextPreAimTime;
    private readonly List<MortarBrain> _mortars = new List<MortarBrain>();

    // 最近一次炮擊的目標（給 TryGetBarrageCenter）
    private bool _hasCenter;
    private Transform _centerTarget;
    private AimSpot _centerSpot;
    private Vector3 _centerLast;

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
    }

    private void OnEnable() => _controls?.Player.Enable();
    private void OnDisable() => _controls?.Player.Disable();

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        _controls?.Dispose();
    }

    private void Update()
    {
        if (_mortarAction != null && _mortarAction.WasPressedThisFrame() && !IsGameplayInputBlocked())
            RequestMortarBarrage();

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

    // ── 迫擊砲炮擊 ───────────────────────────────────────────

    /// <summary>要求一次迫擊砲炮擊（目標 = 玩家現在鎖定的敵人 / 準星的落點）。回傳 false = 這次不打。</summary>
    public bool RequestMortarBarrage()
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

        if (!TryGetPlayerTarget(out Transform target, out AimSpot spot))
        {
            Debug.Log("SupplyManager: 沒有鎖定敵人，準星也沒有打到任何東西", this);
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
        foreach (var m in _mortars)
        {
            m.SetAim(target, spot);
            if (m.CanHit) canHit++;
        }
        if (canHit == 0)
        {
            Debug.Log("SupplyManager: 目標在所有迫擊砲的射程外（太遠或太近）", this);
            return false;
        }

        _cooldownUntil = Time.time + cooldown;
        _hasCenter = true;
        _centerTarget = target;
        _centerSpot = target != null ? null : spot;
        _centerLast = target != null ? TurretController.GetAimPoint(target) : spot.Position;

        _barrage = StartCoroutine(Barrage(target, spot));
        OnBarrageStarted?.Invoke(target, spot.Position, _mortars.Count);

        string what = target != null ? $"'{target.name}'"
                    : spot.IsMoving ? $"{spot.Position}（跟著移動的物體）"
                    : $"{spot.Position}";
        Debug.Log($"SupplyManager: 迫擊砲炮擊 → {what}，{_mortars.Count} 座（{canHit} 座在射程內），" +
                  $"{rounds} 輪 × {roundDuration:0.#} 秒", this);
        return true;
    }

    private IEnumerator Barrage(Transform target, AimSpot spot)
    {
        var mortars = new List<MortarBrain>();
        AimSpot lastKnown = target != null ? AimSpot.Static(TurretController.GetAimPoint(target)) : spot;

        for (int round = 0; round < rounds; round++)
        {
            // 每一輪重新清點：被摧毀的跳過、新建的加入
            CollectMortars(mortars);
            if (mortars.Count == 0) break;

            // 目標死掉 → 之後都打最後的位置（在甲板上就跟著船走）
            if (target != null && target.gameObject.activeInHierarchy)
                lastKnown = AimSpot.Static(TurretController.GetAimPoint(target));
            else if (!ReferenceEquals(target, null))
            {
                lastKnown = AimSpot.AnchoredBelow(lastKnown.Position);
                target = null;
            }

            float interval = roundDuration / mortars.Count;
            for (int i = 0; i < mortars.Count; i++)
            {
                MortarBrain m = mortars[i];
                if (m != null && m.isActiveAndEnabled)
                {
                    if (target != null && target.gameObject.activeInHierarchy)
                        lastKnown = AimSpot.Static(TurretController.GetAimPoint(target));
                    else if (!ReferenceEquals(target, null))
                    {
                        lastKnown = AimSpot.AnchoredBelow(lastKnown.Position);
                        target = null;
                    }

                    m.SetAim(target, lastKnown);
                    m.RequestFire();
                }
                yield return new WaitForSeconds(interval);
            }
        }

        _barrage = null;
        OnBarrageFinished?.Invoke();
    }

    private void PreAim()
    {
        CollectMortars(_mortars);
        if (_mortars.Count == 0) return;

        bool has = TryGetPlayerTarget(out Transform target, out AimSpot spot);
        foreach (var m in _mortars)
        {
            if (m.HasPendingFire) continue;   // 上一次炮擊最後一發還在等對準，不要打斷
            if (has) m.SetAim(target, spot);
            else m.ClearAim();
        }
    }

    /// <summary>
    /// 玩家現在「按下會打的地方」：鎖定的敵人，或準星射線的落點。
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
