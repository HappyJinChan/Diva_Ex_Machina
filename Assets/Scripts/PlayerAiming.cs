using System.Collections.Generic;
using TMPro;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.UI;

public class PlayerAiming : MonoBehaviour
{
    public static PlayerAiming Instance { get; private set; }

    [Header("Crosshair")]
    // 鎖定圈。用 Graphic 而不是 Image：Image 或 LockRingGraphic（程式畫的圓環，粗細不隨大小改變）都能拖進來。
    // 這裡只用到它的 RectTransform（大小 = 鎖定範圍）。
    [SerializeField] private Graphic AimAreaImage;
    [SerializeField] private Image crosshairImage;

    [Header("Camera")]
    [SerializeField] private Camera mainCam;
    /// <summary>瞄準用的鏡頭（畫面中央 = 這台鏡頭的正前方）。</summary>
    public Camera MainCamera => mainCam != null ? mainCam : Camera.main;

    // 手動轉鏡頭的轉速上限（突擊推進期間由 PlayerMovement 開啟）。
    // 開啟時，滑鼠每幀轉動的角度不超過 AimTurnRate × deltaTime —— 跟自動瞄準共用同一個數值，
    // 所以完全手動瞄準的玩家，這個數值一樣有意義。
    private bool _manualTurnRateLimited;
    public void SetManualTurnRateLimit(bool limited) => _manualTurnRateLimited = limited;
    [SerializeField] private GameObject playerOrientation;
    public Vector2 turn;
    public float TopClamp = 70f;
    public float BottomClamp = -30f;

    [Header("Aiming Settings")]
    private Vector2 screenCenter;
    [SerializeField] public bool lockOn = false;
    [SerializeField] private Ray ray;
    [SerializeField] private float targetDistance;
    [SerializeField] private Vector3 targetDirection = Vector3.zero;

    [Header("Lock Settings")]
    [SerializeField] private float lockOnDistance = 50f;
    [SerializeField] private float freeAimMaxDistance = 999f;

    [Header("Assist Aim (非鎖定)")]
    [Tooltip("換目標的容差，以鎖定圈半徑的比例表示。\n" +
             "目前的目標還合格時，新目標必須比它更靠近畫面中心「這個比例 × 圈半徑」才會換過去，\n" +
             "避免兩隻敵人差不多靠近中心時準星來回跳。0 = 永遠選最近的。")]
    [SerializeField, Range(0f, 0.5f)] private float switchMarginRatio = 0.1f;

    [Tooltip("開啟時，從鏡頭看過去被擋住的敵人不會被輔助瞄準選中。")]
    [SerializeField] private bool requireLineOfSight = true;

    [Tooltip("會擋住視線的層。敵人層不要勾，否則敵人會互相擋住。\n" +
             "留空（Nothing）會在執行時自動用 Default、Ground、Mobile Platform、Defence Fortifications、Obstacle。")]
    [SerializeField] private LayerMask lineOfSightBlockers;

    [Header("UI Speeds")]
    [SerializeField] private float centerLerpSpeed = 10f;
    [SerializeField] private float crosshairLerpSpeed = 18f;
    [SerializeField] private float crosshairTiltLerp = 12f;
    [SerializeField] private float resetTiltLerp = 8f;

    [Header("Aiming Point (Optional)")]
    [SerializeField] public Transform aimingPoint;

    // Some scripts in your project may reference this (note spelling)
    [Header("Auto Find / References")]
    [SerializeField] public Transform meshTransform;

    private Rigidbody currentTargetRb;

    // =========================
    // Constrained Lock state
    // =========================
    private Transform _lockedTarget;              // 當前鎖定目標（持續鎖）
    private Rigidbody _lockedTargetRb;
    private Renderer _lockedTargetRenderer;
    private bool _lockedInsideCircle;             // 這一幀目標是否在圈內（只影響 UI + 圈外速度 cap）

    [Header("Lock Follow (Exponential)")]
    [Tooltip("Exponential follow strength. Larger = snappier. (Used for AutoAim + locked follow)")]
    [SerializeField] private float lockRotateSpeed = 12f;

    [Header("Melee Focus")]
    [Tooltip("近戰期間的鎖定跟隨係數。\n\n" +
             "平時用 lockRotateSpeed 的滯後是刻意的 —— 目標比較容易跑出鎖定圈，\n" +
             "手動瞄準才有意義。但近戰需要目標穩定在畫面中央，所以攻擊期間\n" +
             "大幅提高這個係數，讓穩定誤差趨近 0。\n\n" +
             "只影響指數跟隨的收斂速度；最大轉速仍然由 AimTurnRate 決定。")]
    [SerializeField] private float meleeLockRotateSpeed = 45f;

    [Tooltip("進入 / 離開近戰對焦的過渡時間（秒）。\n" +
             "直接切換會讓鏡頭一頓，0.1~0.2 之間比較順。")]
    [SerializeField] private float meleeFocusBlendTime = 0.15f;

    private bool _meleeFocus;
    private float _meleeFocusWeight;

    // =========================
    // Auto Aim (Middle Mouse Toggle)
    // =========================
    [Header("Auto Aim")]
    [SerializeField] private Vector3 autoAimOffset = new Vector3(0f, 0.2f, 0f);
    [SerializeField] private bool autoAimUseBoundsCenter = true;

    [Tooltip("Smooth aim point to reduce jitter. If you prefer old hard lock feel, set 0.")]
    [SerializeField] private float autoAimPointSmoothTime = 0f;

    [Header("Auto Aim Auto Exit")]
    [SerializeField] private float autoAimAutoExitSeconds_LockArea = 3f;
    [SerializeField] private float autoAimAutoExitSeconds_Distance = 1f;

    private float _autoAimOutDistanceTimer = 0f;
    private float _autoAimOutAreaTimer = 0f;

    private bool _autoAimActive;
    private bool _forcedAutoAim;   // 由程式指定的鎖定（WeaponBalanceBot），不會自動退出、也不會自己換目標
    private Transform _autoAimTarget;
    private Renderer _autoAimTargetRenderer;

    private Vector3 _autoAimPointVel;
    private Vector3 _autoAimPointSmoothed;

    [Header("Shoulder Offset")]
    [SerializeField] private CinemachineCamera virtualCamera;  // ✅ 改這裡
    [SerializeField] private PlayerMovement playerMovement;
    [SerializeField] private float shoulderOffsetXLeft = -2f;
    [SerializeField] private float shoulderOffsetXRight = 2f;
    [SerializeField] private float shoulderOffsetLerpSpeed = 5f;
    [SerializeField] private float shoulderOffsetResetSpeed = 3f; // 回中速度（可比移動速度慢）
    [SerializeField] private float resistancePow = 0.5f;
    [SerializeField] private float shoulderOffsetZForward = 0f;
    [SerializeField] private float shoulderOffsetZBackward = -1.2f;
    [SerializeField] private float shoulderOffsetZDefault = -1f;
    [SerializeField] private float shoulderOffsetZLerpSpeed = 5f;
    [SerializeField] private float shoulderOffsetYDefault = 1f;
    [SerializeField] private float shoulderOffsetYJump = 0.65f;
    [SerializeField] private float shoulderOffsetYFall = 1.5f;
    [SerializeField] private float shoulderOffsetYJumpLerpSpeed = 10f;  // 跳躍：快
    [SerializeField] private float shoulderOffsetYFlyLerpSpeed = 2f;    // 飛行/下落：慢
    [SerializeField] private float shoulderOffsetYGroundLerpSpeed = 5f; // 落地回預設：正常
    [Header("Assault Boost Shoulder Offset")]
    [SerializeField] private float assaultBoostShoulderOffsetZForward = 0.5f;
    [SerializeField] private float assaultBoostShoulderOffsetX = 1f;

    private CinemachineThirdPersonFollow _thirdPersonFollow;

    public LayerMask ignoreLayer;

    // Debug hook (optional)
    private int _autoAimLastWriteFrame = -1;
    public bool IsAutoAimActiveDebug() => _autoAimActive;
    /// AutoAim 是否進行中（即使目標暫時跑出鎖圈、lockOn 被設為 false 也算）。
    /// PlayerMovement.RotateCharacter 用它讓角色在圈外仍持續面向準星。
    public bool IsAutoAimActive => _autoAimActive;
    public bool DidAutoAimWriteThisFrameDebug(int frame) => _autoAimLastWriteFrame == frame;

    // --- Platform (Landship) yaw follow ---
    private Transform _platform;         // 目前站的移動平台（null = 沒站）
    private float _platformManualYaw;    // 相對平台的手動 yaw（度）
    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (meshTransform == null) meshTransform = transform;
        if (mainCam == null) mainCam = Camera.main;

        if (lineOfSightBlockers.value == 0)
            lineOfSightBlockers = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Defence Fortifications", "Obstacle");

        if (virtualCamera != null)
            _thirdPersonFollow = virtualCamera.GetCinemachineComponent(CinemachineCore.Stage.Body)
                         as CinemachineThirdPersonFollow;
    }

    private void Update()
    {
        if (UIManager.Instance != null && UIManager.Instance.currentCameraSet != 0)
            return;

        if (_autoAimActive)
        {
            CrosshairDetect_ConstrainedLock();
            UpdateShoulderOffset(); // ✅ 加在這裡
            return;
        }

        float mouseX = Input.GetAxis("Mouse X");
        float mouseY = Input.GetAxis("Mouse Y");

        // 突擊推進中：手動轉鏡頭受 AimTurnRate 限制（上下左右合起來算，維持滑鼠移動的方向）
        if (_manualTurnRateLimited && PlayerStats.Instance != null)
        {
            float maxStep = Mathf.Max(0f, PlayerStats.Instance.GetAimTurnRate()) * Time.deltaTime;
            float step = Mathf.Sqrt(mouseX * mouseX + mouseY * mouseY);
            if (step > maxStep && step > 0.0001f)
            {
                float scale = maxStep / step;
                mouseX *= scale;
                mouseY *= scale;
            }
        }
        // pitch（上下）照舊
        if (Mathf.Abs(mouseY) > 0.001f)
        {
            turn.y -= mouseY;
            turn.y = ClampAngle(turn.y, BottomClamp, TopClamp);
        }

        if (_platform != null)
        {
            // 站在平台上：手動 yaw 存「相對平台」的量，
            // 世界 yaw = 手動 yaw + 平台當前朝向。每幀重建 => 不會累積漂移。
            if (Mathf.Abs(mouseX) > 0.001f)
                _platformManualYaw += mouseX;

            turn.x = _platformManualYaw + HeadingOf(_platform.rotation);

            if (playerOrientation != null)
                playerOrientation.transform.rotation = Quaternion.Euler(turn.y, turn.x, 0f);
        }
        else
        {
            // 沒站平台：維持原本的世界 yaw 累加
            if (Mathf.Abs(mouseX) > 0.001f)
            {
                turn.x += mouseX;
                turn.x = ClampAngle(turn.x, float.MinValue, float.MaxValue);
            }

            if ((Mathf.Abs(mouseX) > 0.001f || Mathf.Abs(mouseY) > 0.001f) && playerOrientation != null)
                playerOrientation.transform.rotation = Quaternion.Euler(turn.y, turn.x, 0f);
        }

        CrosshairDetect_ConstrainedLock();
        UpdateShoulderOffset();
    }

    private void LateUpdate()
    {
        // AutoAim：用指數式跟隨
        if (_autoAimActive)
            LateUpdateAutoAimRotation_ExponentialWithCap();
    }

    // =========================
    // Input API
    // =========================
    public void ToggleAutoAim()
    {
        if (_autoAimActive)
        {
            EndAutoAim();
            return;
        }

        // 只允許：當下有鎖定目標時開啟
        if (_lockedTarget == null) return;

        _autoAimTarget = _lockedTarget;
        _autoAimTargetRenderer = _lockedTargetRenderer;
        _autoAimActive = true;

        _autoAimPointSmoothed = GetAutoAimPointRaw();
        _autoAimPointVel = Vector3.zero;
    }

    /// <summary>
    /// 由程式強制鎖定某個目標，鏡頭跟按中鍵鎖定時一樣轉向它（同一套指數跟隨、同樣的轉速上限）。
    ///
    /// 跟玩家的鎖定差在：
    ///   · 不會因為超出 lockOnDistance 或跑出鎖定圈而自動退出（射擊場 80m 靶也能鎖）
    ///   · 目標死亡時不會自己找下一個目標，直接退出 —— 由呼叫端決定下一個
    /// 給 WeaponBalanceBot 用；只影響鏡頭與準星 UI，不影響彈道。
    /// </summary>
    public void ForceAutoAim(Transform target)
    {
        if (target == null) { ReleaseForcedAutoAim(); return; }

        _autoAimTarget = target;
        _autoAimTargetRenderer = target.GetComponentInChildren<Renderer>();
        _lockedTarget = target;
        _lockedTargetRb = target.GetComponentInParent<Rigidbody>();
        _lockedTargetRenderer = _autoAimTargetRenderer;

        _autoAimActive = true;
        _forcedAutoAim = true;

        _autoAimPointSmoothed = GetAutoAimPointRaw();
        _autoAimPointVel = Vector3.zero;
        _autoAimOutDistanceTimer = 0f;
        _autoAimOutAreaTimer = 0f;
    }

    /// <summary>解除 ForceAutoAim。沒有強制鎖定時什麼都不做（不會影響玩家自己的鎖定）。</summary>
    public void ReleaseForcedAutoAim()
    {
        if (!_forcedAutoAim) return;
        EndAutoAim();
        ClearLock();
    }

    private void EndAutoAim()
    {
        _autoAimActive = false;
        _forcedAutoAim = false;
        _autoAimTarget = null;
        _autoAimTargetRenderer = null;
        _autoAimPointVel = Vector3.zero;

        _autoAimOutDistanceTimer = 0f;
        _autoAimOutAreaTimer = 0f;
        // 若站在平台上，退出 AutoAim 後重新校準，避免鏡頭瞬跳
        if (_platform != null)
            _platformManualYaw = Mathf.DeltaAngle(HeadingOf(_platform.rotation), turn.x);
    }

    // =========================
    // AutoAim：目標丟失/被擊殺時重新尋找目標
    // =========================

    // AutoAim 進行中，當前目標丟失/被擊殺時呼叫。
    // 成功取得新目標回傳 true（AutoAim 繼續）；找不到回傳 false（已退出 AutoAim）。
    private bool HandleAutoAimTargetLost()
    {
        // 強制鎖定：目標沒了就退出，不自己換目標
        if (_forcedAutoAim)
        {
            EndAutoAim();
            return false;
        }

        if (TryAcquireNextAutoAimTarget())
            return true;

        // 找不到任何目標 -> 退出 AutoAim
        EndAutoAim();
        return false;
    }

    // 搜尋下一個目標：必須在相機前方、在 lockOnDistance（3D 距離）內、
    // 且落在螢幕鎖定圈（lockOnRange）內；在符合者中選離螢幕中心最近的。
    // 找到則設為新目標並回傳 true，否則回傳 false。
    private bool TryAcquireNextAutoAimTarget()
    {
        if (mainCam == null || playerOrientation == null || GameManager.Instance == null)
            return false;

        List<GameObject> enemies = GameManager.Instance.GetEnemies();
        if (enemies == null || enemies.Count == 0)
            return false;

        Vector2 sc = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float radius = GetLockAreaPixelRadius(); // 螢幕鎖定圈半徑（由 lockOnRange 決定）

        GameObject best = null;
        float bestProximity = Mathf.Infinity;

        for (int i = 0; i < enemies.Count; i++)
        {
            var enemy = enemies[i];
            if (!enemy) continue;
            if (!enemy.activeInHierarchy) continue;

            // 跳過舊的（已丟失/被擊殺的）目標，避免重新選到同一個
            if (_autoAimTarget != null && enemy.transform == _autoAimTarget) continue;

            // 條件 1：在 lockOnDistance 內（3D 世界距離）
            float dist = Vector3.Distance(playerOrientation.transform.position, enemy.transform.position);
            if (dist > lockOnDistance) continue;

            // 必須在相機前方
            Vector3 aimPos = GetTargetAimPoint(enemy.transform);
            Vector3 sp = mainCam.WorldToScreenPoint(aimPos);
            if (sp.z <= 0f) continue;

            Vector2 pt = new Vector2(sp.x, sp.y);
            float prox = Vector2.Distance(pt, sc);

            // 條件 2：必須在螢幕鎖定圈（lockOnRange）內
            if (prox > radius + 0.01f) continue;

            // 條件 3：在符合上述條件的目標中，選離螢幕中心最近的
            if (prox < bestProximity)
            {
                // 條件 4：不能在牆後（跟第一次鎖定 TryPickAssistTarget 用同一個視線檢查）。
                // 放在這裡：只對「會成為新答案」的敵人打射線，省掉多餘的 Raycast。
                if (requireLineOfSight && !HasLineOfSight(enemy.transform, aimPos)) continue;

                bestProximity = prox;
                best = enemy;
            }
        }

        if (best == null)
            return false;

        // 設定為新目標
        _autoAimTarget = best.transform;
        _autoAimTargetRenderer = best.GetComponentInChildren<Renderer>();

        _lockedTarget = _autoAimTarget;
        _lockedTargetRb = best.GetComponentInParent<Rigidbody>();
        _lockedTargetRenderer = _autoAimTargetRenderer;

        lockOn = true;
        currentTargetRb = _lockedTargetRb;
        _lockedInsideCircle = true;

        // 重置平滑與自動退出計時器，避免換目標瞬間誤觸退出
        _autoAimPointSmoothed = GetAutoAimPointRaw();
        _autoAimPointVel = Vector3.zero;
        _autoAimOutDistanceTimer = 0f;
        _autoAimOutAreaTimer = 0f;

        return true;
    }

    // =========================
    // Constrained Lock (核心)
    // =========================
    //
    // 兩種狀態：
    //   · 鎖定（AutoAim，按中鍵之後）：黏住同一個目標，只有目標死亡 / 丟失才換。
    //   · 輔助瞄準（按中鍵之前）：每一幀重新挑「鎖定圈內、離畫面中心最近」的敵人，
    //     有更靠近中心的敵人就換過去（帶一點容差，避免兩隻差不多近時來回跳）。
    private void CrosshairDetect_ConstrainedLock()
    {
        screenCenter = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        float radius = GetLockAreaPixelRadius();

        if (_autoAimActive)
        {
            UpdateAutoAimLock(radius);
            return;
        }

        if (TryPickAssistTarget(radius, out Vector2 targetScreen))
        {
            _lockedInsideCircle = true;
            DriveCrosshairTo(targetScreen, true);
            ray = mainCam.ScreenPointToRay(targetScreen);
            ApplyLockedTargetAim();
            return;
        }

        ClearLock();
        FreeAim();
    }

    /// <summary>
    /// 鎖定（AutoAim）期間：目標固定為 _autoAimTarget。
    /// 目標在圈外時準星夾在圈邊、改打準星方向；背後時把螢幕座標鏡像翻回來再夾。
    /// </summary>
    private void UpdateAutoAimLock(float radius)
    {
        if (_autoAimTarget == null || !_autoAimTarget.gameObject.activeInHierarchy)
        {
            // 目標丟失 / 被擊殺：搜尋下一個目標，找不到才退出 AutoAim
            if (!HandleAutoAimTargetLost())
            {
                ClearLock();
                return;
            }
        }

        if (_lockedTarget != _autoAimTarget)
        {
            _lockedTarget = _autoAimTarget;
            _lockedTargetRb = _autoAimTarget.GetComponentInParent<Rigidbody>();
            _lockedTargetRenderer = _autoAimTarget.GetComponentInChildren<Renderer>();
        }
        if (_lockedTargetRenderer == null)
            _lockedTargetRenderer = _lockedTarget.GetComponentInChildren<Renderer>();

        // 超出 lockOnDistance 不在這裡斷鎖，交給 LateUpdate 的 _autoAimOutDistanceTimer（超距 N 秒才退出）
        targetDistance = Vector3.Distance(playerOrientation.transform.position, _lockedTarget.position);

        Vector3 sp = mainCam.WorldToScreenPoint(GetTargetAimPoint(_lockedTarget));
        bool behindCamera = sp.z <= 0f;

        Vector2 targetScreen = new Vector2(sp.x, sp.y);
        Vector2 delta = targetScreen - screenCenter;
        if (behindCamera)
            delta = -delta; // 背後時 WorldToScreenPoint 會鏡像，翻回來

        _lockedInsideCircle = !behindCamera && delta.magnitude <= radius + 0.01f;

        // 圈外：準星夾在圈邊（等鏡頭追上來）
        Vector2 uiPoint = targetScreen;
        if (!_lockedInsideCircle)
        {
            Vector2 dirOnScreen = (delta.sqrMagnitude > 0.0001f) ? delta.normalized : Vector2.up;
            uiPoint = screenCenter + dirOnScreen * radius;
        }

        // 圈外看起來要跟一般準星一樣（灰色、不傾斜）
        DriveCrosshairTo(uiPoint, _lockedInsideCircle);

        // ray 永遠跟著準星（圈外開火需要）
        ray = mainCam.ScreenPointToRay(uiPoint);

        if (_lockedInsideCircle)
        {
            ApplyLockedTargetAim();   // 圈內：真正鎖定，打目標
            return;
        }

        // 圈外（夾在圈邊）：不算鎖定，打準星方向
        lockOn = false;
        currentTargetRb = null;
        targetDirection = Vector3.zero;

        if (Physics.Raycast(ray, out RaycastHit hit, freeAimMaxDistance, ~0, QueryTriggerInteraction.Ignore))
        {
            if (aimingPoint) aimingPoint.position = hit.point;
            SetDistanceText(hit.distance, locked: false);
        }
        else
        {
            if (aimingPoint) aimingPoint.position = ray.origin + ray.direction * freeAimMaxDistance;
            SetDistanceText(0f, locked: false);
        }
    }

    /// <summary>
    /// 輔助瞄準：挑出鎖定圈內、離畫面中心最近的敵人，設為 _lockedTarget。
    ///
    /// 候選條件：在 lockOnDistance 內（3D 距離）、在鏡頭前方、螢幕位置在鎖定圈內、
    /// Renderer 可見、而且（requireLineOfSight 開啟時）從鏡頭看過去沒有被擋住。
    ///
    /// 容差：目前鎖著的目標仍然合格時，新目標要比它「更靠近中心 switchMarginRatio × 圈半徑」才換。
    /// </summary>
    private bool TryPickAssistTarget(float radius, out Vector2 bestScreen)
    {
        bestScreen = Vector2.zero;
        if (mainCam == null || playerOrientation == null || GameManager.Instance == null)
            return false;

        List<GameObject> enemies = GameManager.Instance.GetEnemies();
        if (enemies == null || enemies.Count == 0)
            return false;

        Vector3 origin = playerOrientation.transform.position;
        float maxDistSqr = lockOnDistance * lockOnDistance;
        float circleSqr = (radius + 0.01f) * (radius + 0.01f);

        Transform current = _lockedTarget;

        Transform best = null;
        Renderer bestRenderer = null;
        float bestSqr = float.PositiveInfinity;

        bool currentValid = false;
        Renderer currentRenderer = null;
        Vector2 currentScreen = Vector2.zero;
        float currentSqr = float.PositiveInfinity;

        for (int i = 0; i < enemies.Count; i++)
        {
            GameObject enemy = enemies[i];
            if (!enemy || !enemy.activeInHierarchy) continue;

            Transform t = enemy.transform;
            Vector3 pos = t.position;

            // 便宜的檢查先做
            if ((pos - origin).sqrMagnitude > maxDistSqr) continue;

            // 螢幕位置與視線檢查用瞄準點（有 AimPoint 就用它，沒有就是 transform 原點）
            Vector3 aimPos = GetTargetAimPoint(t);
            Vector3 sp = mainCam.WorldToScreenPoint(aimPos);
            if (sp.z <= 0f) continue;

            Vector2 pt = new Vector2(sp.x, sp.y);
            float proxSqr = (pt - screenCenter).sqrMagnitude;
            if (proxSqr > circleSqr) continue;

            // 貴的檢查（Renderer、射線）只做在「可能成為答案」的敵人上：
            // 比目前最佳更靠近中心，或它就是目前鎖著的目標（容差判斷要用）
            bool isCurrent = t == current;
            if (!isCurrent && proxSqr >= bestSqr) continue;

            Renderer r = enemy.GetComponentInChildren<Renderer>();
            if (r != null && !r.isVisible) continue;
            if (requireLineOfSight && !HasLineOfSight(t, aimPos)) continue;

            if (isCurrent)
            {
                currentValid = true;
                currentRenderer = r;
                currentScreen = pt;
                currentSqr = proxSqr;
            }

            if (proxSqr < bestSqr)
            {
                bestSqr = proxSqr;
                best = t;
                bestRenderer = r;
                bestScreen = pt;
            }
        }

        // 容差：新目標沒有明顯更靠近中心，就留在目前的目標上
        if (currentValid && best != current)
        {
            float margin = radius * switchMarginRatio;
            if (Mathf.Sqrt(bestSqr) > Mathf.Sqrt(currentSqr) - margin)
            {
                best = current;
                bestRenderer = currentRenderer;
                bestScreen = currentScreen;
            }
        }

        if (best == null)
            return false;

        if (best != _lockedTarget)
        {
            _lockedTarget = best;
            _lockedTargetRb = best.GetComponentInParent<Rigidbody>();
            _lockedTargetRenderer = bestRenderer;
        }

        targetDistance = Vector3.Distance(origin, best.position);
        return true;
    }

    /// <summary>
    /// 從鏡頭看向目標，中間有沒有被擋住。
    /// 只有 lineOfSightBlockers 裡的層會擋視線（敵人彼此不互擋）；
    /// 打到的碰撞體屬於目標自己時也算看得到。
    /// </summary>
    private bool HasLineOfSight(Transform target, Vector3 targetPoint)
    {
        Vector3 from = mainCam.transform.position;
        Vector3 dir = targetPoint - from;
        float dist = dir.magnitude;
        if (dist < 0.01f) return true;

        if (!Physics.Raycast(from, dir / dist, out RaycastHit hit, dist, lineOfSightBlockers, QueryTriggerInteraction.Ignore))
            return true;

        return hit.collider.transform.IsChildOf(target);
    }

    /// <summary>準星壓在 _lockedTarget 上（圈內）時的鎖定狀態與 UI。</summary>
    private void ApplyLockedTargetAim()
    {
        lockOn = true;
        currentTargetRb = _lockedTargetRb;
        Vector3 aimPos = GetTargetAimPoint(_lockedTarget);
        targetDirection = (aimPos - transform.position).normalized;

        if (aimingPoint) aimingPoint.position = aimPos;
        SetDistanceText(targetDistance, locked: true);
    }

    /// <summary>沒有目標：準星回中心，打畫面中心的射線。</summary>
    private void FreeAim()
    {
        ray = mainCam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
        SmoothResetCrosshairToCenter();

        int layerMask = ~ignoreLayer;
        if (playerOrientation && Physics.Raycast(ray, out RaycastHit hit, freeAimMaxDistance, layerMask, QueryTriggerInteraction.Ignore))
        {
            if (aimingPoint) aimingPoint.position = hit.point;
            SetDistanceText(hit.distance, locked: false);
        }
        else if (playerOrientation)
        {
            if (aimingPoint) aimingPoint.position = playerOrientation.transform.position + (playerOrientation.transform.forward * freeAimMaxDistance);
            SetDistanceText(0f, locked: false);
        }
    }

    private void SetDistanceText(float distance, bool locked)
    {
        if (UIManager.Instance == null) return;

        UIManager.Instance.distanceText.text = distance.ToString("F2");
        UIManager.Instance.distanceText.color = locked ? UIManager.Instance.lockonColor : UIManager.Instance.normalColor;
        UIManager.Instance.distanceText.fontStyle = locked ? FontStyles.Bold : FontStyles.Normal;
    }

    private void ClearLock()
    {
        _lockedTarget = null;
        _lockedTargetRb = null;
        _lockedTargetRenderer = null;
        _lockedInsideCircle = false;

        lockOn = false;
        currentTargetRb = null;
    }

    // =========================
    // AutoAim rotation (Exponential + cap when out of circle)
    // =========================
    private void LateUpdateAutoAimRotation_ExponentialWithCap()
    {
        if (_autoAimTarget == null || playerOrientation == null || mainCam == null)
        {
            // playerOrientation / mainCam 缺失屬於異常情況，直接退出
            if (playerOrientation == null || mainCam == null)
            {
                EndAutoAim();
                return;
            }

            // 目標丟失 / 被擊殺：嘗試搜尋下一個目標，找不到才退出
            if (!HandleAutoAimTargetLost())
                return;
        }

        // 如果目標被 destroy / inactive：嘗試搜尋下一個目標，找不到才退出
        if (!_autoAimTarget.gameObject.activeInHierarchy)
        {
            if (!HandleAutoAimTargetLost())
                return;
        }

        float dt = Time.deltaTime;

        // ======================================================
        // Auto exit conditions (>= autoAimAutoExitSeconds)
        // ======================================================

        // 1) Out of lockOnDistance for > N seconds
        float distToTarget = Vector3.Distance(playerOrientation.transform.position, _autoAimTarget.position);
        if (distToTarget > lockOnDistance)
            _autoAimOutDistanceTimer += dt;
        else
            _autoAimOutDistanceTimer = 0f;

        // 2) Out of LockArea (crosshair clamped) for > N seconds
        // _lockedInsideCircle is updated by CrosshairDetect_ConstrainedLock() while AutoAim is active
        if (!_lockedInsideCircle)
            _autoAimOutAreaTimer += dt;
        else
            _autoAimOutAreaTimer = 0f;

        if (!_forcedAutoAim &&
            (_autoAimOutDistanceTimer >= autoAimAutoExitSeconds_Distance || _autoAimOutAreaTimer >= autoAimAutoExitSeconds_LockArea))
        {
            EndAutoAim();
            return;
        }

        // ======================================================
        // Aim point
        // ======================================================
        Vector3 raw = GetAutoAimPointRaw();

        if (autoAimPointSmoothTime > 0f)
            _autoAimPointSmoothed = Vector3.SmoothDamp(_autoAimPointSmoothed, raw, ref _autoAimPointVel, autoAimPointSmoothTime);
        else
            _autoAimPointSmoothed = raw;

        // desired rotation from camera position -> aim point
        Vector3 camPos = mainCam.transform.position;
        Vector3 dir = _autoAimPointSmoothed - camPos;
        if (dir.sqrMagnitude < 0.0001f)
            dir = mainCam.transform.forward;

        Quaternion look = Quaternion.LookRotation(dir.normalized, Vector3.up);
        Vector3 e = look.eulerAngles;

        float desiredYaw = e.y;
        float desiredPitch = ClampAngle(NormalizePitch(e.x), BottomClamp, TopClamp);
        Quaternion targetRot = Quaternion.Euler(desiredPitch, desiredYaw, 0f);

        Quaternion current = playerOrientation.transform.rotation;
        float angle = Quaternion.Angle(current, targetRot);
        if (angle < 0.0001f)
            return;

        // 近戰對焦的權重過渡。放在這裡而不是 Update，是因為它只影響這段運算。
        float focusTarget = _meleeFocus ? 1f : 0f;
        _meleeFocusWeight = Mathf.MoveTowards(
            _meleeFocusWeight, focusTarget, dt / Mathf.Max(0.01f, meleeFocusBlendTime));

        // 指數式跟隨：stepWanted = angle * (1 - exp(-k * dt))
        //
        // 這個 k 決定「穩定誤差」有多大 —— 每幀只走剩餘角度的固定比例，
        // 所以目標移動時鏡頭會停在一個平衡點而非完全對準。
        // 近戰期間把 k 拉高，平衡點就會逼近畫面中央。
        float k = Mathf.Max(0f, Mathf.Lerp(lockRotateSpeed, meleeLockRotateSpeed, _meleeFocusWeight));
        float expT = 1f - Mathf.Exp(-k * dt);
        float stepWanted = angle * expT;

        // 永遠用 AimTurnRate 當 max deg/s（圈內圈外都 cap）
        float maxDegPerSec = 120f;
        if (PlayerStats.Instance != null)
            maxDegPerSec = Mathf.Max(0f, PlayerStats.Instance.GetAimTurnRate());

        float maxStep = maxDegPerSec * dt;
        stepWanted = Mathf.Min(stepWanted, maxStep);

        float t = Mathf.Clamp01(stepWanted / angle);
        Quaternion newRot = Quaternion.Slerp(current, targetRot, t);

        playerOrientation.transform.rotation = newRot;
        _autoAimLastWriteFrame = Time.frameCount;

        // sync turn (prevents oscillation between systems)
        Vector3 applied = newRot.eulerAngles;
        turn.x = applied.y;
        turn.y = ClampAngle(NormalizePitch(applied.x), BottomClamp, TopClamp);
    }


    private Vector3 GetAutoAimPointRaw()
    {
        if (_autoAimTarget == null) return Vector3.zero;

        // 有 AimPoint：鏡頭直接對準它（不加 autoAimOffset，位置由你在 prefab 裡擺放決定）
        AimPoint ap = FindAimPoint(_autoAimTarget);
        if (ap != null) return ap.transform.position;

        if (autoAimUseBoundsCenter)
        {
            if (_autoAimTargetRenderer == null)
                _autoAimTargetRenderer = _autoAimTarget.GetComponentInChildren<Renderer>();

            if (_autoAimTargetRenderer != null)
                return _autoAimTargetRenderer.bounds.center + autoAimOffset;
        }

        return _autoAimTarget.position + autoAimOffset;
    }

    // =========================
    // AimPoint（瞄準點）
    // =========================
    //
    // 敵人底下放一個掛了 AimPoint 的空物件，準星、子彈、鏡頭就會瞄準它；
    // 沒有放的敵人維持原本的行為（瞄準 transform 原點）。
    // 每個敵人只在第一次被查詢時搜尋一次，結果（包括「沒有」）會快取起來。
    private readonly Dictionary<Transform, AimPoint> _aimPointCache = new Dictionary<Transform, AimPoint>();
    private const int AimPointCacheLimit = 1024;

    /// <summary>目標的瞄準位置：有 AimPoint 用它，沒有就用 transform.position。</summary>
    public Vector3 GetTargetAimPoint(Transform target)
    {
        if (target == null) return Vector3.zero;
        AimPoint ap = FindAimPoint(target);
        return ap != null ? ap.transform.position : target.position;
    }

    private AimPoint FindAimPoint(Transform target)
    {
        if (_aimPointCache.TryGetValue(target, out AimPoint ap))
            return ap;

        // 敵人如果是 Destroy 而不是回收到物件池，快取會越長越大；超過上限就清掉重建
        if (_aimPointCache.Count >= AimPointCacheLimit)
            _aimPointCache.Clear();

        ap = target.GetComponentInChildren<AimPoint>();
        _aimPointCache[target] = ap;   // 找不到也記下 null，避免每幀重新搜尋
        return ap;
    }

    // =========================
    // UI helpers
    // =========================
    private void DriveCrosshairTo(Vector2 screenPoint, bool tilt)
    {
        if (!crosshairImage) return;

        RectTransform t = crosshairImage.rectTransform;
        t.position = Vector2.Lerp(t.position, screenPoint, Time.deltaTime * crosshairLerpSpeed);

        Vector3 targetTilt = tilt ? new Vector3(0f, 0f, 45f) : Vector3.zero;
        t.rotation = Quaternion.Euler(Vector3.Lerp(t.rotation.eulerAngles, targetTilt, Time.deltaTime * crosshairTiltLerp));

        crosshairImage.color = tilt ? new Color32(24, 180, 0, 200) : new Color32(53, 53, 53, 152);
    }

    private void SmoothResetCrosshairToCenter()
    {
        if (!crosshairImage) return;

        RectTransform t = crosshairImage.rectTransform;
        t.rotation = Quaternion.Euler(Vector3.Lerp(t.rotation.eulerAngles, Vector3.zero, Time.deltaTime * resetTiltLerp));
        t.position = Vector2.Lerp(t.position, screenCenter, Time.deltaTime * centerLerpSpeed);
        crosshairImage.color = new Color32(53, 53, 53, 152);
    }

    private float GetLockAreaPixelRadius()
    {
        if (!AimAreaImage) return 0f;
        RectTransform rt = AimAreaImage.rectTransform;
        Vector3[] corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        return Vector3.Distance(corners[0], corners[3]) * 0.5f;
    }

    // =========================
    // Public helpers
    // =========================
    public Rigidbody GetTargetRigidbody() => currentTargetRb;

    // AutoAim 目標的 Rigidbody 快取（目標換了才重新找）
    private Transform _autoAimRbOwner;
    private Rigidbody _autoAimRb;

    /// <summary>
    /// AutoAim（中鍵鎖定）中的目標。跟 GetTargetRigidbody 不同：目標暫時跑出鎖定圈（鏡頭還在追）時也拿得到。
    /// 沒有在 AutoAim、或目標已失效時回傳 false。
    /// aimPoint = 瞄準位置（有 AimPoint 用它，沒有就是 transform 原點）；rb 可能是 null（目標沒有 Rigidbody）。
    /// </summary>
    public bool TryGetAutoAimTarget(out Transform target, out Vector3 aimPoint, out Rigidbody rb)
    {
        target = null;
        aimPoint = default;
        rb = null;

        if (!_autoAimActive || _autoAimTarget == null || !_autoAimTarget.gameObject.activeInHierarchy)
            return false;

        if (_autoAimRbOwner != _autoAimTarget)
        {
            _autoAimRbOwner = _autoAimTarget;
            _autoAimRb = _autoAimTarget.GetComponentInParent<Rigidbody>();
        }

        target = _autoAimTarget;
        aimPoint = GetTargetAimPoint(_autoAimTarget);
        rb = _autoAimRb;
        return true;
    }
    public Ray GetRay() => ray;

    /// <summary>
    /// 目前鎖定圈裡的目標（lockOn 中）。沒有鎖定、或目標已失效時回傳 false。
    /// 給要追著目標打的系統用（例如 SupplyManager 的迫擊砲炮擊）。
    /// </summary>
    public bool TryGetLockedTarget(out Transform target)
    {
        target = null;
        if (!lockOn || _lockedTarget == null || !_lockedTarget.gameObject.activeInHierarchy)
            return false;
        target = _lockedTarget;
        return true;
    }

    /// <summary>
    /// 給迫擊砲多目標鎖定用：跟武器一樣在 Lock On Distance（玩家到敵人）以內、鎖定圈內，
    /// 另外還要在 rangeOrigin 的 rangeMax 以內（迫擊砲射程，從船算）、不在 exclude 裡；
    /// 符合的敵人中離畫面中央最近的那一個。其他條件跟輔助瞄準相同（在鏡頭前方、Renderer 可見、沒被擋住）。
    /// 找不到回傳 null。不會改變武器的鎖定狀態。
    /// </summary>
    public Transform FindLockCandidate(Vector3 rangeOrigin, float rangeMax, ICollection<Transform> exclude)
    {
        Camera cam = mainCam;   // HasLineOfSight 也用 mainCam
        if (cam == null || playerOrientation == null || GameManager.Instance == null)
            return null;

        List<GameObject> enemies = GameManager.Instance.GetEnemies();
        if (enemies == null || enemies.Count == 0)
            return null;

        Vector3 origin = playerOrientation.transform.position;
        float maxDistSqr = lockOnDistance * lockOnDistance;
        float rangeSqr = rangeMax * rangeMax;
        float radius = GetLockAreaPixelRadius();
        float circleSqr = (radius + 0.01f) * (radius + 0.01f);
        Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

        Transform best = null;
        float bestSqr = float.PositiveInfinity;

        for (int i = 0; i < enemies.Count; i++)
        {
            GameObject enemy = enemies[i];
            if (!enemy || !enemy.activeInHierarchy) continue;

            Transform t = enemy.transform;
            if (exclude != null && exclude.Contains(t)) continue;
            if ((t.position - origin).sqrMagnitude > maxDistSqr) continue;        // 玩家的鎖定距離
            if ((t.position - rangeOrigin).sqrMagnitude > rangeSqr) continue;    // 迫擊砲射程

            Vector3 aimPos = GetTargetAimPoint(t);
            Vector3 sp = cam.WorldToScreenPoint(aimPos);
            if (sp.z <= 0f) continue;

            float proxSqr = (new Vector2(sp.x, sp.y) - center).sqrMagnitude;
            if (proxSqr > circleSqr || proxSqr >= bestSqr) continue;

            // 貴的檢查只做在「可能成為答案」的敵人上
            Renderer r = enemy.GetComponentInChildren<Renderer>();
            if (r != null && !r.isVisible) continue;
            if (requireLineOfSight && !HasLineOfSight(t, aimPos)) continue;

            bestSqr = proxSqr;
            best = t;
        }
        return best;
    }

    /// <summary>目標（的瞄準點）現在在不在螢幕的鎖定圈裡（而且在鏡頭前方）。</summary>
    public bool IsInLockCircle(Transform target)
    {
        if (target == null || mainCam == null) return false;
        Vector3 sp = mainCam.WorldToScreenPoint(GetTargetAimPoint(target));
        if (sp.z <= 0f) return false;
        float radius = GetLockAreaPixelRadius() + 0.01f;
        Vector2 center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        return (new Vector2(sp.x, sp.y) - center).sqrMagnitude <= radius * radius;
    }

    /// <summary>
    /// 目前鎖定目標的瞄準位置（有 AimPoint 用它，沒有就是 transform 原點）。
    /// 沒有鎖定（或目標已失效）時回傳 false。給開火時計算落點用。
    /// </summary>
    public bool TryGetLockedAimPoint(out Vector3 point)
    {
        point = default;
        if (!lockOn || _lockedTarget == null || !_lockedTarget.gameObject.activeInHierarchy)
            return false;
        point = GetTargetAimPoint(_lockedTarget);
        return true;
    }

    /// <summary>
    /// 沿著準星射線找出實際瞄到的點（和 FreeAim 用同一個 layer mask）；
    /// 什麼都沒打到時取 freeAimMaxDistance 處。
    /// 子彈要射向這個點，而不是射線上固定距離的點，否則槍口和鏡頭的位置差會造成視差。
    /// </summary>
    public Vector3 GetAimPointAlongRay(Ray r)
    {
        if (Physics.Raycast(r, out RaycastHit hit, freeAimMaxDistance, ~ignoreLayer, QueryTriggerInteraction.Ignore))
            return hit.point;
        return r.GetPoint(freeAimMaxDistance);
    }

    public void SetLockOnDistance(float newLockOnDistance) => lockOnDistance = newLockOnDistance;

    /// <summary>
    /// 近戰對焦。開啟時大幅降低鎖定滯後，讓目標穩定在畫面中央。
    /// 由 MeleeAttackController 在連段開始 / 結束時呼叫。
    ///
    /// 只改變收斂速度，最大轉速仍受 AimTurnRate 限制 ——
    /// 所以高速目標還是有可能甩開鏡頭，手動瞄準的價值不會完全消失。
    /// </summary>
    public void SetMeleeFocus(bool active) => _meleeFocus = active;

    // pitch 0..360 -> -180..180
    private static float NormalizePitch(float xDeg)
    {
        if (xDeg > 180f) xDeg -= 360f;
        return xDeg;
    }

    private static float ClampAngle(float angle, float min, float max)
    {
        if (angle < -360f) angle += 360f;
        if (angle > 360f) angle -= 360f;
        return Mathf.Clamp(angle, min, max);
    }
    public void SetAimAreaSize(float newSize)
    {
        if (AimAreaImage == null) return;

        AimAreaImage.rectTransform.sizeDelta = new Vector2(newSize, newSize);

        // 如果你 UIManager 入面有呢兩個，保持兼容（冇就會 null-safe）
        if (UIManager.Instance != null)
        {
            UIManager.Instance.speedInfo.anchoredPosition = new Vector2((newSize / 2) + 55, 0);
            UIManager.Instance.distanceInfo.anchoredPosition = new Vector2(-((newSize / 2) + 55), 0);
        }
    }
    private void UpdateShoulderOffset()
    {
        if (_thirdPersonFollow == null) return;

        float inputX = PlayerController.Instance != null
            ? PlayerController.Instance.LastMoveInput.x
            : 0f;

        float currentX = _thirdPersonFollow.ShoulderOffset.x;
        float newX;

        if (playerMovement != null && playerMovement.IsAssaultBoosting)
        {
            // 突擊推進：鏡頭固定偏到一側，角色被推到畫面另一邊（正值 = 鏡頭往右、角色在左）。
            // 不看左右輸入 —— 推進中的左右鍵是側移，鏡頭跟著甩會晃。
            newX = Mathf.Lerp(currentX, assaultBoostShoulderOffsetX, Time.deltaTime * shoulderOffsetLerpSpeed);
        }
        else if (inputX < -0.1f || inputX > 0.1f)
        {
            // 有左右輸入：移向目標側，帶阻力
            float targetX = inputX < -0.1f ? shoulderOffsetXLeft : shoulderOffsetXRight;

            float range = Mathf.Abs(targetX);
            float distRatio = range > 0f ? Mathf.Clamp01(Mathf.Abs(targetX - currentX) / (range * 2f)) : 0f;
            float resistanceCurve = Mathf.Pow(distRatio, resistancePow);
            float dynamicSpeed = shoulderOffsetLerpSpeed * resistanceCurve;

            newX = Mathf.Lerp(currentX, targetX, Time.deltaTime * dynamicSpeed);
        }
        else
        {
            // 無輸入：平滑回中
            newX = Mathf.Lerp(currentX, 0f, Time.deltaTime * shoulderOffsetResetSpeed);
        }

        // Z 軸：根據前後輸入調整
        float inputY = PlayerController.Instance != null
            ? PlayerController.Instance.LastMoveInput.y
            : 0f;

        float currentZ = _thirdPersonFollow.ShoulderOffset.z;
        float targetZ;

        if (playerMovement != null && playerMovement.IsAssaultBoosting)
        {
            targetZ = assaultBoostShoulderOffsetZForward;   // 突擊推進：拉近鏡頭（推進中前後鍵沒有作用，不看輸入）
        }
        else if (inputY > 0.1f)
            targetZ = shoulderOffsetZForward;       // 向前：拉近鏡頭
        else if (inputY < -0.1f)
            targetZ = shoulderOffsetZBackward;      // 向後：推遠鏡頭
        else
            targetZ = shoulderOffsetZDefault;       // 無輸入：回預設值

        float newZ = Mathf.Lerp(currentZ, targetZ, Time.deltaTime * shoulderOffsetZLerpSpeed);

        // Y 軸：根據跳躍 / 飛行 / 下落狀態調整
        float currentY = _thirdPersonFollow.ShoulderOffset.y;
        float targetY;
        float yLerpSpeed;

        if (playerMovement != null)
        {
            float vertVel = playerMovement.VerticalVelocity;
            bool isFlying = playerMovement.IsFlyingActive;
            bool isGrounded = playerMovement.IsGrounded;

            if (isGrounded)
            {
                // 落地：回預設值，正常速度
                targetY = shoulderOffsetYDefault;
                yLerpSpeed = shoulderOffsetYGroundLerpSpeed;
            }
            else if (isFlying)
            {
                // 飛行中：固定用慢速，目標 Y 隨 vertVel 在 Jump~Fall 之間插值
                // vertVel >= 0 → Jump(0.65)，vertVel 越負 → 越接近 Fall(1.5)
                float velT = Mathf.Clamp01(-vertVel / 10f); // 10f = 下落速度參考值，可 Inspector 調
                targetY = Mathf.Lerp(shoulderOffsetYJump, shoulderOffsetYFall, velT);
                yLerpSpeed = shoulderOffsetYFlyLerpSpeed;
            }
            else
            {
                // 空中（跳躍）：目標 Y 一樣用 vertVel 插值，但 LerpSpeed 隨 vertVel 平滑過渡
                // vertVel 大（剛起跳）→ 速度快；vertVel 趨近 0（頂點）→ 速度慢
                float velT = Mathf.Clamp01(-vertVel / 10f);
                targetY = Mathf.Lerp(shoulderOffsetYJump, shoulderOffsetYFall, velT);
                // 速度：上升時用 jumpLerpSpeed，下落時漸漸過渡到 flyLerpSpeed
                float speedT = Mathf.Clamp01(-vertVel / 5f); // 過渡區間，可調
                yLerpSpeed = Mathf.Lerp(shoulderOffsetYJumpLerpSpeed, shoulderOffsetYFlyLerpSpeed, speedT);
            }
        }
        else
        {
            targetY = shoulderOffsetYDefault;
            yLerpSpeed = shoulderOffsetYGroundLerpSpeed;
        }

        float newY = Mathf.Lerp(currentY, targetY, Time.deltaTime * yLerpSpeed);

        _thirdPersonFollow.ShoulderOffset = new Vector3(
            newX,
            newY,
            newZ
        );
    }

    // 由 PlayerMovement 的 OnTriggerStay / OnTriggerExit 呼叫
    public void SetPlatform(Transform platform)
    {
        if (platform == _platform) return;

        if (platform != null)
        {
            // 進平台：記住當前手動偏移，避免鏡頭瞬間跳
            // turn.x = manual + heading  =>  manual = turn.x - heading
            _platformManualYaw = Mathf.DeltaAngle(HeadingOf(platform.rotation), turn.x);
        }

        _platform = platform;
    }

    // 把 rotation 的水平朝向（forward 投影到 XZ）換成角度（度）。對 pitch / roll 免疫。
    private static float HeadingOf(Quaternion rot)
    {
        Vector3 f = rot * Vector3.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 1e-6f)   // forward 幾乎垂直，改用 right 當參考
        {
            f = rot * Vector3.right;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return 0f;
        }
        return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
    }
}