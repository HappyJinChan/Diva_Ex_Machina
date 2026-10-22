using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 迫擊砲的 Brain：決定「瞄哪裡、什麼時候把這一發打出去」。
/// 實際的瞄準與射擊交給 BallisticTurretController（Fixed Speed High Arc 模式）。
/// 「誰在什麼時候打」由 SupplyManager 決定 —— 這裡只接兩個指令：
///
///   · SetAim(目標, 點)：預瞄。有目標就瞄目標「現在」的位置（每次思考更新），目標死掉就瞄它最後的位置；沒有目標就瞄點。
///     不對目標本身做攔截（不算它自己的移動提前量，砲彈在空中也不追它）——命中靠散布和飽和炮擊。
///     但目標站在會動的甲板上時，那個位置會黏在甲板上（AimSpot），船的移動照樣算進去，砲彈也會跟著船追。
///   · RequestFire()：射一發。炮口已經對準就立刻射；還沒對準就等它對準再射（最多等 Fire Timeout 秒）。
///
/// 所有啟用中的迫擊砲會登記在 MortarBrain.All，SupplyManager 從這裡找出船上的迫擊砲
/// （建在船上 = 船的子物件；被摧毀 / 停用就自動移除）。
///
/// 砲塔設定建議（BallisticTurretController）：Fixed Speed High Arc 打勾、Gravity Multiplier 5、
/// Bullet Speed 384（3 km 射程）、Min Elevation 45、Max Elevation 89、Yaw Limit 180、
/// Magazine Size 夠大（例如 99）、Time Between Shooting 比 SupplyManager 的發射間隔小（例如 0.5）。
/// </summary>
[DisallowMultipleComponent]
public class MortarBrain : MonoBehaviour
{
    private static readonly List<MortarBrain> _all = new List<MortarBrain>();

    /// <summary>所有啟用中的迫擊砲。</summary>
    public static IReadOnlyList<MortarBrain> All => _all;

    [SerializeField] private BallisticTurretController turret;

    [Tooltip("砲彈的攻擊者（傷害結算 / 擊殺統計）。留空 = 往上找到的 Rigidbody（船）所在物件，再沒有就是自己。")]
    [SerializeField] private GameObject attacker;

    [Tooltip("多久思考一次（秒）。要比砲塔的追蹤逾時（0.5 秒）小。")]
    [SerializeField, Min(0.02f)] private float thinkInterval = 0.1f;

    [Tooltip("輪到這座射擊時，最多等炮口對準幾秒；超過就放棄這一發。")]
    [SerializeField, Min(0.1f)] private float fireTimeout = 3f;

    private bool _hasAim;
    private Transform _aimTarget;
    private AimSpot _aimSpot;         // 目標最後的位置 / 指定的點（可能黏在會動的甲板上）
    private bool _pendingFire;
    private float _pendingSince;
    private float _nextThinkTime;
    private float _scatterOverride = -1f;   // < 0 = 用砲彈自己的散布半徑

    public BallisticTurretController Turret => turret;

    /// <summary>有瞄準目標（SetAim 過、還沒 ClearAim）。</summary>
    public bool HasAim => _hasAim;

    /// <summary>目前的目標在射程內（有可行的拋物線）。</summary>
    public bool CanHit => turret != null && turret.HasBallisticSolution;

    /// <summary>炮口已經對準。</summary>
    public bool IsAimed => turret != null && turret.IsAimed;

    /// <summary>有一發在等炮口對準。</summary>
    public bool HasPendingFire => _pendingFire;

    private void Awake()
    {
        if (turret == null) turret = GetComponentInChildren<BallisticTurretController>();
        if (turret == null)
        {
            Debug.LogWarning("MortarBrain: 找不到 BallisticTurretController", this);
            return;
        }
        turret.OnProjectileSpawned += HandleProjectileSpawned;

        if (attacker == null)
        {
            var rb = GetComponentInParent<Rigidbody>();
            attacker = rb != null ? rb.gameObject : gameObject;
        }
    }

    private void OnEnable()
    {
        if (!_all.Contains(this)) _all.Add(this);
    }

    private void OnDisable()
    {
        _all.Remove(this);
        _pendingFire = false;
    }

    private void OnDestroy()
    {
        if (turret != null) turret.OnProjectileSpawned -= HandleProjectileSpawned;
    }

    private void Start()
    {
        if (turret != null && !_hasAim) turret.AimAtRest();
    }

    // ── 指令 ─────────────────────────────────────────────────

    /// <summary>預瞄。target 不是 null 就追它（死掉後打它最後的位置）；是 null 就瞄 spot。</summary>
    public void SetAim(Transform target, AimSpot spot)
    {
        _hasAim = true;
        _aimTarget = target;
        _scatterOverride = -1f;   // 每次重新指定目標都恢復預設；範圍炮擊會在 SetAim 之後再設
        _aimSpot = target != null ? AimSpot.Static(TurretController.GetAimPoint(target))
                 : spot ?? AimSpot.Static(transform.position);
        ApplyAim();
    }

    /// <summary>
    /// 接下來射出的砲彈改用這個散布半徑（公尺），例如範圍炮擊的圓圈半徑。小於 0 = 用砲彈自己的。
    /// 要在 SetAim 之後呼叫（SetAim 會把它重設）。
    /// </summary>
    public void SetScatterOverride(float radius) => _scatterOverride = radius;

    /// <summary>預瞄一個固定的世界座標點。</summary>
    public void SetAim(Transform target, Vector3 point) => SetAim(target, AimSpot.Static(point));

    /// <summary>沒有可瞄的東西：炮管放回中立朝向，取消等待中的那一發。</summary>
    public void ClearAim()
    {
        if (!_hasAim && !_pendingFire) return;

        _hasAim = false;
        _aimTarget = null;
        _pendingFire = false;
        if (turret != null)
        {
            turret.ClearPointTarget();
            turret.AimAtRest();
        }
    }

    /// <summary>射一發。回傳 false = 沒有瞄準目標，不接受。</summary>
    public bool RequestFire()
    {
        if (turret == null || !_hasAim) return false;

        _pendingFire = true;
        _pendingSince = Time.time;
        TryFire();
        return true;
    }

    // ── 每幀 ─────────────────────────────────────────────────

    private void Update()
    {
        if (turret == null || !_hasAim) return;

        if (Time.time >= _nextThinkTime)
        {
            _nextThinkTime = Time.time + thinkInterval;
            ApplyAim();
        }

        if (_pendingFire)
        {
            if (Time.time - _pendingSince > fireTimeout)
            {
                _pendingFire = false;
                Debug.Log($"MortarBrain: '{name}' 等了 {fireTimeout:0.#} 秒還沒對準（或射程外），放棄這一發", this);
            }
            else
            {
                TryFire();
            }
        }
    }

    private void ApplyAim()
    {
        // 目標還在 → 瞄它「現在」的位置（不攔截：不算它自己的移動）。
        //   腳下是甲板就黏在甲板上：船的移動照樣補償。死掉 / 停用 → 瞄最後的位置。
        if (_aimTarget != null && _aimTarget.gameObject.activeInHierarchy)
        {
            _aimSpot = AimSpot.AnchoredBelow(TurretController.GetAimPoint(_aimTarget));
            turret.TrackPoint(_aimSpot.Position, _aimSpot.Velocity);
        }
        else
        {
            // 目標剛被打死：最後的位置如果在甲板上，就黏在甲板上跟著船走
            // （ReferenceEquals：被 Destroy 的物件 == null 也會是 true，要看參照本身還在不在）
            if (!ReferenceEquals(_aimTarget, null))
                _aimSpot = AimSpot.AnchoredBelow(_aimSpot.Position);

            _aimTarget = null;
            turret.TrackPoint(_aimSpot.Position, _aimSpot.Velocity);
        }
    }

    private void TryFire()
    {
        if (!turret.IsAimed) return;

        // Shoot() 有自己的閘門（冷卻、換彈中會安靜跳過）→ 用彈匣數確認真的射出了
        int ammoBefore = turret.AmmoInMagazine;
        turret.Shoot();
        if (turret.AmmoInMagazine < ammoBefore)
            _pendingFire = false;
    }

    private void HandleProjectileSpawned(GameObject go)
    {
        if (go == null || !go.TryGetComponent(out MortarShell shell)) return;

        shell.attacker = attacker;
        if (_scatterOverride >= 0f) shell.SetScatterRadius(_scatterOverride);
        // 甲板上的點：砲彈也跟著船追（目標本身不追 —— 砲塔一律用點模式，砲彈拿不到目標）
        if (_aimSpot != null && _aimSpot.IsMoving)
            shell.FollowSpot(_aimSpot);
    }

    // ── 測試 ─────────────────────────────────────────────────

    [ContextMenu("Test Fire At Player Crosshair")]
    private void TestFire()
    {
        var pa = PlayerAiming.Instance;
        if (pa == null) { Debug.LogWarning("MortarBrain: 找不到 PlayerAiming", this); return; }

        if (Physics.Raycast(pa.GetRay(), out RaycastHit hit, 5000f, ~pa.ignoreLayer, QueryTriggerInteraction.Ignore))
        {
            SetAim(null, AimSpot.FromHit(hit));
            RequestFire();
        }
        else
        {
            Debug.LogWarning("MortarBrain: 準星沒有打到任何東西", this);
        }
    }
}
