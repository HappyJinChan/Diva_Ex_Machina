using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// PSO2 風格的圓形小地圖：以 Bradamant 為中心，跟著鏡頭旋轉（鏡頭前方永遠朝上），顯示半徑 Range 公尺。
///
/// 顯示：
///   · 地形：Minimap Baker 烘好的俯視圖（MinimapData）。
///   · Bradamant：中央的箭頭，指向她面對的方向。
///   · 貝努鳥號：船的實際輪廓（Ship Outline，照真實大小和朝向，由 Ship Minimap Baker 烘焙）。
///     輪廓整個在圓外時，改用船的圖示貼在圓框邊緣，指出方向。沒設輪廓 = 一律用圖示。
///     ＋ 支援範圍圈（以支援砲塔為中心）。
///     Bradamant 在範圍內 / 外，圈用不同顏色。
///   · 炮擊範圍：炮擊中（或砲彈還在空中）時的圓圈。
///   · 敵人：Range 以內的敵人點（每 Scan Interval 秒用 Enemy Layer 掃一次）。
///
/// 縮放：Input Actions 的 Player/Zoom In 拉近、Player/Zoom Out 拉遠，在 Zoom Levels 之間切換（平滑過渡）。
///   找不到這兩個動作時，改用 Numpad + / − 直接讀鍵盤。
///   開場用最接近 Range 的那一級。執行中 Range 欄位顯示的是目前的顯示半徑。
///
/// 階層（建議）：
///   Minimap（這個元件；右上角那個圓）
///   └─ Mask（圓形 Image + Mask，四角撐滿）
///       ├─ Map（RawImage，四角撐滿）               → Map Image
///       ├─ Ship Outline（RawImage，Anchor 置中）    → Ship Outline（敵人點要畫在它上面，所以放在 Icons 前面）
///       ├─ Support Ring（MinimapRing，四角撐滿）    → Support Ring
///       ├─ Barrage Ring（MinimapRing，四角撐滿）    → Barrage Ring
///       ├─ Icons（空的 RectTransform，四角撐滿）    → Icon Layer（敵人點生成在這裡）
///       ├─ Ship（Image，Anchor 置中）               → Ship Icon
///       └─ Player（Image 箭頭，Anchor 置中）        → Player Arrow
///   （外框、方位刻度另外放在 Mask 外面）
///
/// 地圖 RawImage 是正方形、跟 Mask 一樣大：圓形 Mask 是正方形的內切圓，正方形怎麼轉都蓋得住，不會露出角落。
/// </summary>
public class Minimap : MonoBehaviour
{
    [Header("Data")]
    [Tooltip("Minimap Baker 產生的資料。留空就只顯示圖示（沒有地形）。")]
    [SerializeField] private MinimapData data;

    [Tooltip("Ship Minimap Baker 產生的船的輪廓資料。留空 = 船只顯示成 Ship Icon。")]
    [SerializeField] private ShipMinimapIcon shipOutlineData;

    [Header("UI")]
    [Tooltip("小地圖的圓（用它的寬度當直徑）。留空 = 自己。")]
    [SerializeField] private RectTransform area;
    [SerializeField] private RawImage mapImage;
    [SerializeField] private RectTransform iconLayer;
    [SerializeField] private RectTransform playerArrow;
    [SerializeField] private RectTransform shipIcon;
    [Tooltip("船的實際輪廓（RawImage，放在 Mask 底下）。大小、位置、角度每幀由程式設定。")]
    [SerializeField] private RawImage shipOutline;
    [SerializeField] private MinimapRing supportRing;
    [SerializeField] private MinimapRing barrageRing;
    [Tooltip("敵人點的 prefab（一個小 Image）。")]
    [SerializeField] private RectTransform enemyDotPrefab;

    [Header("View")]
    [Tooltip("顯示半徑（公尺）：圓的邊緣 = 離 Bradamant 這麼遠。")]
    [SerializeField, Min(10f)] private float range = 500f;

    [Tooltip("縮放級距（顯示半徑，公尺）。Zoom In 往小的切（拉近），Zoom Out 往大的切（拉遠）。")]
    [SerializeField] private float[] zoomLevels = { 800f, 1500f, 3000f, 5000f };

    [Tooltip("切換級距時的過渡時間（秒，不受 Time Scale 影響）。0 = 立刻切換。")]
    [SerializeField, Min(0f)] private float zoomSmoothTime = 0.2f;

    [Tooltip("Input Actions 裡 Player 底下的動作名稱。")]
    [SerializeField] private string zoomInActionName = "Zoom In";
    [SerializeField] private string zoomOutActionName = "Zoom Out";

    [Tooltip("跟著鏡頭旋轉（鏡頭前方朝上）。關掉 = 北方（+Z）朝上。")]
    [SerializeField] private bool rotateWithCamera = true;

    [Tooltip("超出範圍的圖示（船）貼在圓框邊緣時，往內縮幾像素。")]
    [SerializeField, Min(0f)] private float edgePadding = 8f;

    [Header("Targets（留空自動找）")]
    [SerializeField] private Transform player;
    [Tooltip("Bradamant 面對的方向用哪個 Transform（模型會轉、根物件不一定會轉）。留空 = PlayerAiming 的 Mesh Transform，再沒有就是 Player。")]
    [SerializeField] private Transform playerFacing;
    [SerializeField] private Camera viewCamera;
    [SerializeField] private Transform ship;
    [SerializeField] private SupportTurretBrain supportBrain;

    [Header("Support Range")]
    [SerializeField] private Color insideRangeColor = new Color(0.3f, 0.9f, 1f, 0.9f);
    [SerializeField] private Color outsideRangeColor = new Color(1f, 0.35f, 0.25f, 0.9f);

    [Header("Barrage")]
    [Tooltip("炮擊範圍圈半徑（公尺）。0 = 自動：砲彈的散布半徑 + 爆炸半徑。")]
    [SerializeField, Min(0f)] private float barrageRadiusOverride = 0f;

    [Header("Enemies")]
    [SerializeField] private LayerMask enemyLayer;
    [SerializeField, Min(0.05f)] private float scanInterval = 0.2f;
    [SerializeField, Min(1)] private int maxEnemyDots = 128;

    private float _nextScanTime;
    private readonly List<Transform> _enemies = new List<Transform>();
    private readonly HashSet<int> _seen = new HashSet<int>();
    private readonly List<RectTransform> _dots = new List<RectTransform>();
    private static readonly Collider[] _scanBuffer = new Collider[512];

    private float _barrageRadiusAuto = -1f;

    private int _zoomIndex = -1;      // 目前在 zoomLevels 的哪一級；-1 = 沒有設定級距
    private float _zoomFrom;          // 這次過渡的起點半徑
    private float _zoomT = 1f;        // 過渡進度 0～1
    private PlayerControllers _controls;
    private InputAction _zoomInAction;
    private InputAction _zoomOutAction;

    private void Awake()
    {
        if (area == null) area = transform as RectTransform;
        InitZoom();
        if (mapImage != null && data != null && data.texture != null) mapImage.texture = data.texture;
        if (mapImage != null && (data == null || data.texture == null)) mapImage.enabled = false;

        if (shipOutline != null)
        {
            RectTransform rt = shipOutline.rectTransform;
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            shipOutline.raycastTarget = false;
            if (shipOutlineData != null && shipOutlineData.texture != null) shipOutline.texture = shipOutlineData.texture;
            else shipOutline.enabled = false;
        }
    }

    private void ResolveTargets()
    {
        if (player == null)
        {
            var go = GameObject.FindGameObjectWithTag("Player");
            if (go != null) player = go.transform;
        }
        if (playerFacing == null)
            playerFacing = PlayerAiming.Instance != null && PlayerAiming.Instance.meshTransform != null
                ? PlayerAiming.Instance.meshTransform : player;
        if (viewCamera == null) viewCamera = Camera.main;
        if (ship == null && SupplyManager.Instance != null) ship = SupplyManager.Instance.transform;
        if (supportBrain == null) supportBrain = FindFirstObjectByType<SupportTurretBrain>();
    }

    private void OnEnable()
    {
        _zoomInAction?.Enable();
        _zoomOutAction?.Enable();
    }

    private void OnDisable()
    {
        _zoomInAction?.Disable();
        _zoomOutAction?.Disable();
    }

    private void OnDestroy() => _controls?.Dispose();

    private void LateUpdate()
    {
        UpdateZoom();
        ResolveTargets();
        if (player == null || area == null) return;

        Vector3 center = player.position;
        float yaw = rotateWithCamera && viewCamera != null ? viewCamera.transform.eulerAngles.y : 0f;
        float radiusPx = area.rect.width * 0.5f;
        float pxPerMeter = radiusPx / range;

        UpdateMap(center, yaw);

        // Bradamant：箭頭指向她面對的方向（鏡頭前方 = 上）
        if (playerArrow != null)
        {
            float facing = playerFacing != null ? playerFacing.eulerAngles.y : 0f;
            playerArrow.anchoredPosition = Vector2.zero;
            playerArrow.localEulerAngles = new Vector3(0f, 0f, yaw - facing);
        }

        // 貝努鳥號的實際輪廓：照公尺換算大小，跟船一起轉
        bool outlineVisible = false;
        if (shipOutline != null)
        {
            if (ship != null && shipOutlineData != null && shipOutlineData.texture != null)
            {
                Vector2 op = ToMap(shipOutlineData.WorldCenter(ship), center, yaw, pxPerMeter);
                Vector2 osz = shipOutlineData.size * pxPerMeter;
                float reach = radiusPx + osz.magnitude * 0.5f;   // 輪廓的任何一角還可能碰到圓 → 畫
                outlineVisible = op.sqrMagnitude < reach * reach;
                shipOutline.enabled = outlineVisible;
                if (outlineVisible)
                {
                    RectTransform rt = shipOutline.rectTransform;
                    rt.anchoredPosition = op;
                    rt.sizeDelta = osz;
                    rt.localEulerAngles = new Vector3(0f, 0f, yaw - ship.eulerAngles.y);
                }
            }
            else shipOutline.enabled = false;
        }

        // 貝努鳥號的圖示：輪廓看得到時不顯示；看不到（或沒設輪廓）就用圖示，超出範圍貼邊
        if (shipIcon != null)
        {
            if (ship != null && !outlineVisible)
            {
                shipIcon.gameObject.SetActive(true);
                Vector2 p = ToMap(ship.position, center, yaw, pxPerMeter);
                float max = radiusPx - edgePadding;
                if (p.sqrMagnitude > max * max) p = p.normalized * max;
                shipIcon.anchoredPosition = p;
                shipIcon.localEulerAngles = new Vector3(0f, 0f, yaw - ship.eulerAngles.y);
            }
            else shipIcon.gameObject.SetActive(false);
        }

        // 支援範圍圈（以支援砲塔為中心）：Bradamant 在範圍內 / 外換顏色
        if (supportRing != null)
        {
            if (supportBrain != null)
            {
                supportRing.enabled = true;
                Vector3 c = supportBrain.transform.position;
                supportRing.Set(ToMap(c, center, yaw, pxPerMeter), supportBrain.SupportRange * pxPerMeter);
                supportRing.color = supportBrain.IsInSupportRange(center) ? insideRangeColor : outsideRangeColor;
            }
            else supportRing.enabled = false;
        }

        // 炮擊範圍圈
        if (barrageRing != null)
        {
            var sm = SupplyManager.Instance;
            bool active = sm != null && (sm.IsBarrageActive || MortarShell.LiveCount > 0);
            if (active && sm.TryGetBarrageCenter(out Vector3 bc) && BarrageRadius > 0f)
            {
                barrageRing.enabled = true;
                barrageRing.Set(ToMap(bc, center, yaw, pxPerMeter), BarrageRadius * pxPerMeter);
            }
            else barrageRing.enabled = false;
        }

        UpdateEnemies(center, yaw, pxPerMeter, radiusPx);
    }

    // ── 縮放：Zoom In / Zoom Out 切換級距，range 在對數空間平滑過渡（每一級的視覺變化一樣大） ──
    private void InitZoom()
    {
        if (zoomLevels == null || zoomLevels.Length == 0) return;

        System.Array.Sort(zoomLevels);
        for (int i = 0; i < zoomLevels.Length; i++) zoomLevels[i] = Mathf.Max(10f, zoomLevels[i]);

        // 用名稱找（跟 SupplyManager 一樣）：Input Actions 還沒存檔重新產生程式碼時也能編譯
        _controls = new PlayerControllers();
        _zoomInAction = _controls.asset.FindAction($"Player/{zoomInActionName}", false);
        _zoomOutAction = _controls.asset.FindAction($"Player/{zoomOutActionName}", false);
        if (_zoomInAction == null || _zoomOutAction == null)
            Debug.LogWarning($"Minimap: Input Actions 裡找不到 Player/{zoomInActionName} 或 Player/{zoomOutActionName}，" +
                             "改用 Numpad + / −（記得在 Input Actions 視窗按 Save Asset）", this);
        _zoomInAction?.Enable();
        _zoomOutAction?.Enable();

        // 開場：最接近 Inspector 上 Range 的那一級
        _zoomIndex = 0;
        for (int i = 1; i < zoomLevels.Length; i++)
            if (Mathf.Abs(zoomLevels[i] - range) < Mathf.Abs(zoomLevels[_zoomIndex] - range)) _zoomIndex = i;
        range = zoomLevels[_zoomIndex];
        _zoomFrom = range;
        _zoomT = 1f;
    }

    private void UpdateZoom()
    {
        if (_zoomIndex < 0) return;

        bool zoomIn, zoomOut;
        if (_zoomInAction != null && _zoomOutAction != null)
        {
            zoomIn = _zoomInAction.WasPressedThisFrame();
            zoomOut = _zoomOutAction.WasPressedThisFrame();
        }
        else
        {
            Keyboard kb = Keyboard.current;
            zoomIn = kb != null && kb.numpadPlusKey.wasPressedThisFrame;
            zoomOut = kb != null && kb.numpadMinusKey.wasPressedThisFrame;
        }

        int next = _zoomIndex;
        if (zoomIn) next--;    // 拉近 = 半徑變小
        if (zoomOut) next++;   // 拉遠 = 半徑變大
        next = Mathf.Clamp(next, 0, zoomLevels.Length - 1);
        if (next != _zoomIndex)
        {
            _zoomIndex = next;
            _zoomFrom = range;   // 從目前的半徑開始（過渡中連按也不會跳）
            _zoomT = 0f;
        }

        float target = zoomLevels[_zoomIndex];
        if (_zoomT >= 1f) { range = target; return; }

        _zoomT = zoomSmoothTime > 0f ? Mathf.Min(1f, _zoomT + Time.unscaledDeltaTime / zoomSmoothTime) : 1f;
        float s = Mathf.SmoothStep(0f, 1f, _zoomT);
        range = Mathf.Exp(Mathf.Lerp(Mathf.Log(_zoomFrom), Mathf.Log(target), s));
    }

    // ── 地形：RawImage 的 uvRect 取以 Bradamant 為中心、邊長 2×Range 的範圍，整張轉 yaw ──
    private void UpdateMap(Vector3 center, float yaw)
    {
        if (mapImage == null || data == null || data.texture == null) return;

        Vector2 uv = data.WorldToUV(center);
        Vector2 size = new Vector2(
            data.worldSize.x > 0f ? 2f * range / data.worldSize.x : 1f,
            data.worldSize.y > 0f ? 2f * range / data.worldSize.y : 1f);
        mapImage.uvRect = new Rect(uv - size * 0.5f, size);
        mapImage.rectTransform.localEulerAngles = new Vector3(0f, 0f, yaw);
    }

    // ── 世界座標 → 小地圖座標（相對中心的像素）。地圖逆時針轉 yaw 度，鏡頭前方就會朝上 ──
    private static Vector2 ToMap(Vector3 world, Vector3 center, float yawDeg, float pxPerMeter)
    {
        float dx = (world.x - center.x) * pxPerMeter;
        float dz = (world.z - center.z) * pxPerMeter;
        float r = yawDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(r), sin = Mathf.Sin(r);
        return new Vector2(dx * cos - dz * sin, dx * sin + dz * cos);
    }

    // ── 敵人：定時掃描，每幀更新位置 ──
    private void UpdateEnemies(Vector3 center, float yaw, float pxPerMeter, float radiusPx)
    {
        if (enemyDotPrefab == null || iconLayer == null || enemyLayer.value == 0) return;

        if (Time.time >= _nextScanTime)
        {
            _nextScanTime = Time.time + scanInterval;
            _enemies.Clear();
            _seen.Clear();

            int count = Physics.OverlapSphereNonAlloc(center, range * 1.1f, _scanBuffer, enemyLayer.value,
                                                      QueryTriggerInteraction.Collide);
            for (int i = 0; i < count && _enemies.Count < maxEnemyDots; i++)
            {
                Collider col = _scanBuffer[i];
                if (col == null) continue;
                Transform root = col.attachedRigidbody != null ? col.attachedRigidbody.transform : col.transform;
                if (_seen.Add(root.GetInstanceID())) _enemies.Add(root);
            }
        }

        int shown = 0;
        float max = radiusPx;
        for (int i = 0; i < _enemies.Count; i++)
        {
            Transform e = _enemies[i];
            if (e == null || !e.gameObject.activeInHierarchy) continue;

            Vector2 p = ToMap(e.position, center, yaw, pxPerMeter);
            if (p.sqrMagnitude > max * max) continue;

            RectTransform dot = GetDot(shown++);
            dot.anchoredPosition = p;
        }
        for (int i = shown; i < _dots.Count; i++)
            if (_dots[i].gameObject.activeSelf) _dots[i].gameObject.SetActive(false);
    }

    private RectTransform GetDot(int index)
    {
        while (_dots.Count <= index)
        {
            RectTransform d = Instantiate(enemyDotPrefab, iconLayer);
            d.anchorMin = d.anchorMax = new Vector2(0.5f, 0.5f);
            _dots.Add(d);
        }
        RectTransform dot = _dots[index];
        if (!dot.gameObject.activeSelf) dot.gameObject.SetActive(true);
        return dot;
    }

    private float BarrageRadius
    {
        get
        {
            if (barrageRadiusOverride > 0f) return barrageRadiusOverride;
            if (_barrageRadiusAuto > 0f) return _barrageRadiusAuto;

            var all = MortarBrain.All;
            for (int i = 0; i < all.Count; i++)
            {
                MortarBrain m = all[i];
                if (m == null || m.Turret == null || m.Turret.bulletPrefab == null) continue;
                if (!m.Turret.bulletPrefab.TryGetComponent(out MortarShell shell)) continue;
                _barrageRadiusAuto = shell.ScatterRadius + shell.Radius;
                break;
            }
            return _barrageRadiusAuto;
        }
    }
}
