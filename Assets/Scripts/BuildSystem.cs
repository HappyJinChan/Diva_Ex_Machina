using Unity.AI.Navigation;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.SocialPlatforms;
using UnityEngine.UI;
public class BuildSystem : MonoBehaviour
{

    [SerializeField] private Toggle[] buildToggles;

    [Header("Raycast")]
    public Camera cam;
    public LayerMask buildableLayers;
    public float maxDistance = 500f;

    [Header("Multi-Layer Deck")]
    [Tooltip("命中點高度與 GridData.gridHeight 的最大容許誤差（世界單位）。\n\n" +
             "作用有兩個：\n" +
             "1. 排除「打到船殼側面 / 甲板之間的斜面」這類不屬於任何一層的命中點\n" +
             "2. 避免在船外的地面上誤判成某一層\n\n" +
             "實際選層是取「高度最接近」的候選，不是取第一個符合容差的，\n" +
             "所以這個值可以放寬一點。目前各層高度是 50 / 62.5 / 75 / 100，\n" +
             "最小間距 12.5，設 6 以下最安全。")]
    public float layerHeightTolerance = 6f;

    [Tooltip("只接受朝上的表面。\n\n" +
             "射線打到艦島側壁或船殼外側時，命中點的高度會落在兩層之間，\n" +
             "硬選最近的一層會讓建築蓋到牆上。開啟這個可以直接拒絕那類命中。\n\n" +
             "預設關閉 —— 如果你的甲板 collider 法線不夠標準（例如用了合併 mesh\n" +
             "或凸包近似），開啟可能會讓某些正常格子點不到。先確認行為再開。")]
    public bool requireUpwardSurface = false;

    [Range(0f, 89f)]
    [Tooltip("表面法線與船 up 軸的最大夾角。只在 Require Upward Surface 開啟時生效。")]
    public float maxSurfaceAngle = 45f;

    [Tooltip("在 Console 印出選層的完整過程：射線打到什麼、命中高度多少、\n" +
             "每一層是因為水平範圍不符還是高度容差被排除、最後選了哪一層、\n" +
             "以及那一格是不是已經被佔用。\n\n" +
             "結果沒有變化時不會重複印，不會洗版。查完記得關掉。")]
    public bool debugLayerPick = false;

    private readonly System.Text.StringBuilder _dbg = new System.Text.StringBuilder();
    private string _lastLayerReport;

    [Header("TopDown RawImage Input")]
    public bool useRawImageInput = true;
    public RawImage gridRawImage;
    public Camera topDownCamera;
    public Canvas uiCanvas;
    [Header("Blueprint")]
    public int currentFootprintIndex = 0;
    public BuildBlueprint[] footprints;

    [Header("Preview")]
    public bool enablePreview = true;
    public bool previewAlignToGridRotation = true;
    public bool previewParentToGrid = true;
    public bool previewDisableColliders = true;
    public int previewLayer = 2;

    [Header("Placement")]
    public bool placeOnLeftClick = true;
    public bool placeAlignToGridRotation = true;
    public bool placeParentToGrid = true;

    [Header("Rotation")]
    public bool enableRotation = true;
    public float scrollSensitivity = 1f;
    public int rotationStep = 0; // 0..3 , each = 90 degrees clockwise

    [Header("Gizmo Highlight")]
    public bool drawHitCellGizmo = true;
    public bool drawFootprintPreview = true;
    public float gizmoYOffset = 0.01f;

    private BuildingGrid _hitGrid;
    private int _hitGridIndex = -1;
    private int _hitCellX = -1;
    private int _hitCellY = -1;
    private bool _hasHitCell = false;

    private GameObject _previewGO;
    private BuildBlueprint _previewBP;
    private BuildingGrid _previewGrid;
    private int _previewGridIndex;
    private int _previewCellX;
    private int _previewCellY;
    private bool _previewCanPlace;

    public bool buildModeOn = false;

    [Header("Zoom")]
    public CinemachineCamera buildingCamera;
    public CinemachinePositionComposer buildingCameraPositionComposer;
    public float maxZoomDistance = 550;
    public float minZoomDistance = 50;
    public float zoomSensitivity = 25f;
    public float panSensitivity = 0.08f;

    private Vector3 _panStartTargetOffset;
    private Vector2 _panStartMousePos;
    private bool _isPanning;
    [Header("Ghost Duplication")]
    public bool duplicateToGhost = true;
    public bool duplicateDisableRenderers = true;

    [SerializeField] NavMeshSurface ghostSurface;
    AsyncOperation _asyncBuild;

    // ----------  初始設施  ----------
    /// <summary>開場時直接完工的一座建築。</summary>
    [System.Serializable]
    public class StartingBuilding
    {
        public BuildBlueprint blueprint;
        [Tooltip("要放在哪個網格（拖船上的 BuildingGrid）。")]
        public BuildingGrid grid;
        [Tooltip("那個 BuildingGrid 的第幾個 GridData（grids 陣列的索引）。")]
        [Min(0)] public int gridIndex;
        [Tooltip("錨點格子 (x, y)。跟玩家建造時滑鼠指到的那一格同一個意思。")]
        public Vector2Int cell;
        [Tooltip("旋轉，每一步順時針 90 度。")]
        [Range(0, 3)] public int rotationStep;
    }

    [Header("Starting Buildings（開場直接完工，不扣資源）")]
    [Tooltip("開場時依序放置。跟玩家建造走同一條路（佔格、ghost、BuildingRegistry），之後可以照常拆除、被打爆。\n" +
             "放不下的項目（超出網格、跟前面的重疊）會跳過並在 Console 警告。")]
    public StartingBuilding[] startingBuildings;
    [Tooltip("在 Scene 視圖畫出初始設施的佔地（只在非播放模式）。綠 = 沒問題，紅 = 超出網格或彼此重疊。")]
    public bool drawStartingBuildingsGizmo = true;
    private void Awake()
    {
        if (!cam) cam = Camera.main;
    }

    private void Start()
    {
        PlaceStartingBuildings();
    }

    private void OnEnable()
    {
        HookBuildToggles(true);
        EvaluateBuildTogglesNow();
    }

    private void OnDisable()
    {
        HookBuildToggles(false);
        DestroyPreview();
    }
    private void HookBuildToggles(bool hook)
    {
        if (buildToggles == null) return;

        for (int i = 0; i < buildToggles.Length; i++)
        {
            var t = buildToggles[i];
            if (!t) continue;

            if (hook) t.onValueChanged.AddListener(OnAnyBuildToggleChanged);
            else t.onValueChanged.RemoveListener(OnAnyBuildToggleChanged);
        }
    }

    private void OnAnyBuildToggleChanged(bool _)
    {
        EvaluateBuildTogglesNow();
    }

    private void EvaluateBuildTogglesNow()
    {
        if (buildToggles == null || buildToggles.Length == 0) return;

        bool allOff = true;

        for (int i = 0; i < buildToggles.Length; i++)
        {
            var t = buildToggles[i];
            if (!t) continue;

            if (t.isOn) allOff = false;
        }

        // 通常更像是「全部都 off 才清掉」
        if (allOff) ClearBuildingFootprints();
    }


    private void Update()
    {
        HandleZoomInput();
        HandlePanInput();
        if (buildModeOn)
        {
            UpdateHitCell();
            HandleRotateInput();

            if (enablePreview) UpdatePreview();
            else DestroyPreview();

            if (placeOnLeftClick && Input.GetMouseButtonDown(0))
                TryPlaceCurrentBlueprint();
        }
    }

    private void HandleRotateInput()
    {
        if (!enableRotation) return;

        if (!Input.GetKeyDown(KeyCode.R)) return;

        int dir = 1;
        if (Input.GetKeyDown(KeyCode.R) && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            dir = -1;

        rotationStep = Mod(rotationStep + dir, 4);
    }
    private int Mod(int a, int m)
    {
        int r = a % m;
        return r < 0 ? r + m : r;
    }

    private void UpdateHitCell()
    {
        _hasHitCell = false;
        _hitGrid = null;
        _hitGridIndex = -1;
        _hitCellX = -1;
        _hitCellY = -1;

        Ray ray;

        if (useRawImageInput && TryGetRayFromRawImage(out ray))
        {
            // ok
        }
        else
        {
            if (!cam) return;
            ray = cam.ScreenPointToRay(Input.mousePosition);
        }

        if (!Physics.Raycast(ray, out RaycastHit hit, maxDistance, buildableLayers))
        {
            ReportLayerPick("射線沒有打到任何東西（檢查 buildableLayers 有沒有包含甲板的 layer）");
            return;
        }

        var grid = hit.collider.GetComponentInParent<BuildingGrid>();
        if (!grid)
        {
            ReportLayerPick($"打到 '{hit.collider.name}'（layer {LayerMask.LayerToName(hit.collider.gameObject.layer)}）" +
                            "，但它和它的父物件上都沒有 BuildingGrid");
            return;
        }
        if (grid.grids == null || grid.grids.Length == 0) return;

        if (!TryGetCellFromHit(grid, hit.point, hit.normal, out int gridIndex, out int cellX, out int cellY))
            return;

        if (debugLayerPick)
        {
            _dbg.Append("  → 選中 grid[").Append(gridIndex).Append("] cell (")
                .Append(cellX).Append(", ").Append(cellY).Append(")   occupied = ")
                .Append(grid.IsOccupied(gridIndex, cellX, cellY));
            ReportLayerPick(null);
        }

        _hasHitCell = true;
        _hitGrid = grid;
        _hitGridIndex = gridIndex;
        _hitCellX = cellX;
        _hitCellY = cellY;
    }

    private bool TryGetRayFromRawImage(out Ray ray)
    {
        ray = default;

        if (!gridRawImage || !topDownCamera) return false;

        RectTransform rt = gridRawImage.rectTransform;

        Camera eventCam = null;
        if (uiCanvas && uiCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            eventCam = uiCanvas.worldCamera;

        Vector2 screenPos = Input.mousePosition;

        if (!RectTransformUtility.RectangleContainsScreenPoint(rt, screenPos, eventCam))
            return false;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(rt, screenPos, eventCam, out Vector2 local))
            return false;

        Rect rect = rt.rect;

        float nx = (local.x - rect.xMin) / rect.width;
        float ny = (local.y - rect.yMin) / rect.height;

        if (nx < 0f || nx > 1f || ny < 0f || ny > 1f)
            return false;

        Rect uvRect = gridRawImage.uvRect;
        float u = uvRect.x + nx * uvRect.width;
        float v = uvRect.y + ny * uvRect.height;

        ray = topDownCamera.ViewportPointToRay(new Vector3(u, v, 0f));
        return true;
    }

    private BuildBlueprint GetCurrentBlueprint()
    {
        if (footprints == null || footprints.Length == 0) return null;
        if (currentFootprintIndex < 0 || currentFootprintIndex >= footprints.Length) return null;
        return footprints[currentFootprintIndex];
    }

    private BoolMatrix GetCurrentFootprint()
    {
        var bp = GetCurrentBlueprint();
        if (bp == null) return null;

        var fp = bp.footprint;
        if (fp == null || fp.cells == null || fp.width <= 0 || fp.height <= 0) return null;
        return fp;
    }

    private void TryPlaceCurrentBlueprint()
    {
        if (!_hasHitCell || !_hitGrid) return;

        var bp = GetCurrentBlueprint();
        if (bp == null || !bp.buildingPrefab) return;

        var fp = GetCurrentFootprint();
        if (fp == null) return;

        bool outOfBounds;
        bool canPlace = EvaluatePlacement(_hitGrid, _hitGridIndex, _hitCellX, _hitCellY, fp, rotationStep, out outOfBounds);
        if (!canPlace) return;

        if (!InventoryManager.Instance.DeductItem(bp.costs)) return;

        PlaceBuilding(bp, _hitGrid, _hitGridIndex, _hitCellX, _hitCellY, rotationStep);
    }

    /// <summary>
    /// 真正放置一座建築：生成、佔格、上材質、複製 ghost、登記 BuildingRegistry。
    /// 不檢查合法性、不扣資源 —— 呼叫前自己用 EvaluatePlacement 確認過。
    /// 玩家建造和開場初始設施共用這裡，兩者的結果完全一樣。
    /// </summary>
    private GameObject PlaceBuilding(BuildBlueprint bp, BuildingGrid grid, int gridIndex, int cellX, int cellY, int rotStep)
    {
        var fp = bp.footprint;

        Vector3 pos = GetFootprintCenterWorld(grid, gridIndex, cellX, cellY, fp, rotStep);

        Quaternion baseRot = placeAlignToGridRotation ? grid.transform.rotation : Quaternion.identity;
        Quaternion rot = baseRot * Quaternion.Euler(0f, rotStep * 90f, 0f);

        Transform parent = placeParentToGrid ? grid.transform : null;

        GameObject placed = Instantiate(bp.buildingPrefab, pos, rot, parent);

        // 佔用要在 Instantiate 之後：grid 記的是「哪幾格屬於這棟建築」，
        // 需要先有建築本人才能登記。合法性由呼叫端的 EvaluatePlacement 確認。
        ApplyFootprintOccupancyRotated(grid, gridIndex, cellX, cellY, fp, rotStep, placed);

        if (bp.buildingMaterial != null)
            ApplyMaterialToAllRenderers(placed, bp.buildingMaterial);


        // ----------  DUPLICATE TO GHOST SHIP  ----------
        Transform ghostTransform = null;

        if (duplicateToGhost && TryGetShipRoots(out var realRoot, out var ghostRoot))
        {
            Vector3 gp = placed.transform.localPosition;
            var ghost = Instantiate(bp.buildingPrefab, ghostRoot);
            ghost.transform.localPosition = placed.transform.localPosition;
            ghost.transform.localRotation = placed.transform.localRotation;
            ghost.name = placed.name + "_GHOST";

            ghostTransform = ghost.transform;

            if (duplicateDisableRenderers)
            {
                foreach (var r in ghost.GetComponentsInChildren<Renderer>())
                    r.enabled = false;   // 只保留 Collider + NavMeshObstacle
            }
        }

        // 敵人 AI 需要一份可列舉的建築清單，用來搜尋「路被封死時該拆哪一棟」。
        // 本尊 + 分身一起登記，之後寫拆除功能時兩邊才不會漏掉一邊。
        BuildingRegistry.Register(placed.transform, ghostTransform);

        return placed;
    }

    /// <summary>開場時放置 startingBuildings。不扣資源；放不下的跳過並警告。</summary>
    private void PlaceStartingBuildings()
    {
        if (startingBuildings == null) return;

        for (int i = 0; i < startingBuildings.Length; i++)
        {
            var sb = startingBuildings[i];
            if (sb == null) continue;

            if (!ValidateStartingBuilding(sb, out string error))
            {
                Debug.LogWarning($"[BuildSystem] 初始設施第 {i} 項跳過：{error}", this);
                continue;
            }

            bool canPlace = EvaluatePlacement(sb.grid, sb.gridIndex, sb.cell.x, sb.cell.y, sb.blueprint.footprint, sb.rotationStep, out bool outOfBounds);
            if (!canPlace)
            {
                string why = outOfBounds ? "超出網格範圍" : "格子已被佔用（跟前面的項目重疊？）";
                Debug.LogWarning($"[BuildSystem] 初始設施第 {i} 項（{sb.blueprint.name}，格子 {sb.cell}，旋轉 {sb.rotationStep}）跳過：{why}", this);
                continue;
            }

            PlaceBuilding(sb.blueprint, sb.grid, sb.gridIndex, sb.cell.x, sb.cell.y, sb.rotationStep);
        }
    }

    /// <summary>檢查欄位有沒有填好（藍圖、prefab、footprint、網格索引）。不檢查佔用。</summary>
    private static bool ValidateStartingBuilding(StartingBuilding sb, out string error)
    {
        error = null;
        if (sb.blueprint == null) { error = "沒有指定 Blueprint"; return false; }
        if (!sb.blueprint.buildingPrefab) { error = $"{sb.blueprint.name} 沒有 buildingPrefab"; return false; }
        var fp = sb.blueprint.footprint;
        if (fp == null || fp.cells == null || fp.width <= 0 || fp.height <= 0) { error = $"{sb.blueprint.name} 的 footprint 是空的"; return false; }
        if (sb.grid == null) { error = "沒有指定 Grid"; return false; }
        if (sb.grid.grids == null || sb.gridIndex < 0 || sb.gridIndex >= sb.grid.grids.Length) { error = $"Grid Index {sb.gridIndex} 超出 {sb.grid.name}.grids 的範圍"; return false; }
        return true;
    }

    private void UpdatePreview()
    {
        if (!_hasHitCell || !_hitGrid)
        {
            DestroyPreview();
            return;
        }

        var bp = GetCurrentBlueprint();
        if (bp == null || !bp.buildingPrefab)
        {
            DestroyPreview();
            return;
        }

        var fp = GetCurrentFootprint();
        if (fp == null)
        {
            DestroyPreview();
            return;
        }

        bool outOfBounds;
        bool canPlace = EvaluatePlacement(_hitGrid, _hitGridIndex, _hitCellX, _hitCellY, fp, rotationStep, out outOfBounds);

        bool needNew =
            _previewGO == null ||
            _previewBP != bp ||
            _previewGrid != _hitGrid ||
            _previewGridIndex != _hitGridIndex;

        if (needNew)
        {
            DestroyPreview();

            _previewGO = Instantiate(bp.pendingPrefab);
            _previewGO.name = bp.pendingPrefab.name + "_PREVIEW";

            if (previewParentToGrid)
                _previewGO.transform.SetParent(_hitGrid.transform, true);

            SetLayerRecursive(_previewGO, previewLayer);

            if (previewDisableColliders)
                DisableAllColliders(_previewGO);

            _previewBP = bp;
            _previewGrid = _hitGrid;
            _previewGridIndex = _hitGridIndex;
        }

        Vector3 pos = GetFootprintCenterWorld(_hitGrid, _hitGridIndex, _hitCellX, _hitCellY, fp, rotationStep);

        Quaternion baseRot = previewAlignToGridRotation ? _hitGrid.transform.rotation : Quaternion.identity;
        Quaternion rot = baseRot * Quaternion.Euler(0f, rotationStep * 90f, 0f);

        _previewGO.transform.SetPositionAndRotation(pos, rot);

        bool changed = (_previewCellX != _hitCellX) || (_previewCellY != _hitCellY) || (_previewCanPlace != canPlace);
        _previewCellX = _hitCellX;
        _previewCellY = _hitCellY;
        _previewCanPlace = canPlace;

        if (changed)
        {
            Material m = canPlace ? bp.pendingMaterial : bp.unavailableMaterial;
            if (m != null)
                ApplyMaterialToAllRenderers(_previewGO, m);
        }
    }

    private void DestroyPreview()
    {
        if (_previewGO != null) Destroy(_previewGO);
        _previewGO = null;
        _previewBP = null;
        _previewGrid = null;
        _previewGridIndex = -1;
        _previewCellX = 0;
        _previewCellY = 0;
        _previewCanPlace = false;
    }

    private bool EvaluatePlacement(BuildingGrid grid, int gridIndex, int anchorX, int anchorY, BoolMatrix fp, int rotStep, out bool outOfBounds)
    {
        outOfBounds = false;
        if (grid == null || fp == null) return false;

        int srcW = fp.width;
        int srcH = fp.height;

        int dstW = (rotStep % 2 == 0) ? srcW : srcH;
        int dstH = (rotStep % 2 == 0) ? srcH : srcW;

        var gd = grid.grids[gridIndex];

        bool any = false;

        for (int y = 0; y < srcH; y++)
        {
            for (int x = 0; x < srcW; x++)
            {
                if (!fp.Get(x, y)) continue;
                any = true;

                RotateCell(x, y, srcW, srcH, rotStep, out int rx, out int ry);

                int gx = anchorX + rx;
                int gy = anchorY + ry;

                if (gx < 0 || gx >= gd.gridSizeX || gy < 0 || gy >= gd.gridSizeY)
                {
                    outOfBounds = true;
                    return false;
                }

                if (grid.IsOccupied(gridIndex, gx, gy))
                    return false;
            }
        }

        return any;
    }

    /// <summary>
    /// 把 footprint 依 rotStep 旋轉後寫進 grid 的佔用表，並把佔用者登記給 grid，
    /// 建築被摧毀時 grid 才知道該還哪幾格。
    /// </summary>
    private void ApplyFootprintOccupancyRotated(BuildingGrid grid, int gridIndex, int anchorX, int anchorY, BoolMatrix fp, int rotStep, GameObject owner)
    {
        int srcW = fp.width;
        int srcH = fp.height;

        for (int y = 0; y < srcH; y++)
        {
            for (int x = 0; x < srcW; x++)
            {
                if (!fp.Get(x, y)) continue;

                RotateCell(x, y, srcW, srcH, rotStep, out int rx, out int ry);

                int gx = anchorX + rx;
                int gy = anchorY + ry;

                grid.OccupyCell(gridIndex, gx, gy, owner);
            }
        }
    }

    private void RotateCell(int x, int y, int w, int h, int rotStep, out int rx, out int ry)
    {
        rotStep = Mod(rotStep, 4);

        if (rotStep == 0)
        {
            rx = x; ry = y; return;
        }

        if (rotStep == 1) // 90 CW
        {
            rx = y;
            ry = (w - 1) - x;
            return;
        }

        if (rotStep == 2) // 180
        {
            rx = (w - 1) - x;
            ry = (h - 1) - y;
            return;
        }

        // 270 CW
        rx = (h - 1) - y;
        ry = x;
    }

    private void OnDrawGizmos()
    {
        if (drawStartingBuildingsGizmo && !Application.isPlaying)
            DrawStartingBuildingsGizmo();

        if (!drawHitCellGizmo) return;
        if (!_hasHitCell || !_hitGrid) return;

        var fp = GetCurrentFootprint();
        if (fp == null) return;

        bool outOfBounds;
        bool canPlace = EvaluatePlacement(_hitGrid, _hitGridIndex, _hitCellX, _hitCellY, fp, rotationStep, out outOfBounds);

        Color c = canPlace ? Color.green : Color.red;
        DrawFootprintOutlineRotated(_hitGrid, _hitGridIndex, _hitCellX, _hitCellY, fp, rotationStep, c);
    }

    /// <summary>
    /// 編輯模式下畫初始設施的佔地。只看清單本身（出界、彼此重疊），
    /// 不讀 grid 的佔用表 —— 那張表在編輯模式下不代表任何東西，讀它還可能順手改到序列化資料。
    /// </summary>
    private void DrawStartingBuildingsGizmo()
    {
        if (startingBuildings == null || startingBuildings.Length == 0) return;

        // (grid, gridIndex, x, y) → 被幾個項目佔用
        var counts = new System.Collections.Generic.Dictionary<(BuildingGrid, int, int, int), int>();
        var cellsPer = new System.Collections.Generic.List<(int gx, int gy, bool inBounds)>[startingBuildings.Length];

        for (int i = 0; i < startingBuildings.Length; i++)
        {
            var sb = startingBuildings[i];
            if (sb == null || !ValidateStartingBuilding(sb, out _)) continue;

            var fp = sb.blueprint.footprint;
            var gd = sb.grid.grids[sb.gridIndex];
            var list = new System.Collections.Generic.List<(int, int, bool)>();

            for (int y = 0; y < fp.height; y++)
            {
                for (int x = 0; x < fp.width; x++)
                {
                    if (!fp.Get(x, y)) continue;
                    RotateCell(x, y, fp.width, fp.height, sb.rotationStep, out int rx, out int ry);
                    int gx = sb.cell.x + rx;
                    int gy = sb.cell.y + ry;
                    bool inBounds = gx >= 0 && gx < gd.gridSizeX && gy >= 0 && gy < gd.gridSizeY;
                    list.Add((gx, gy, inBounds));
                    if (inBounds)
                    {
                        var key = (sb.grid, sb.gridIndex, gx, gy);
                        counts.TryGetValue(key, out int n);
                        counts[key] = n + 1;
                    }
                }
            }
            cellsPer[i] = list;
        }

        for (int i = 0; i < startingBuildings.Length; i++)
        {
            var list = cellsPer[i];
            if (list == null) continue;
            var sb = startingBuildings[i];

            bool bad = false;
            foreach (var c in list)
            {
                if (!c.inBounds || counts[(sb.grid, sb.gridIndex, c.gx, c.gy)] > 1) { bad = true; break; }
            }

            Color col = bad ? Color.red : Color.green;
            foreach (var c in list)
            {
                if (!c.inBounds) continue;   // 出界的格子畫不出來，整座標紅就好
                Color cc = counts[(sb.grid, sb.gridIndex, c.gx, c.gy)] > 1 ? Color.red : col;
                DrawSingleCellOutline(sb.grid, sb.gridIndex, c.gx, c.gy, cc);
            }

#if UNITY_EDITOR
            Vector3 center = GetFootprintCenterWorld(sb.grid, sb.gridIndex, sb.cell.x, sb.cell.y, sb.blueprint.footprint, sb.rotationStep);
            UnityEditor.Handles.Label(center + sb.grid.transform.up * 1f, $"[{i}] {sb.blueprint.name}");
#endif
        }
    }

    private void DrawFootprintOutlineRotated(BuildingGrid grid, int gridIndex, int anchorX, int anchorY, BoolMatrix fp, int rotStep, Color color)
    {
        int srcW = fp.width;
        int srcH = fp.height;
        var gd = grid.grids[gridIndex];

        for (int y = 0; y < srcH; y++)
        {
            for (int x = 0; x < srcW; x++)
            {
                if (!fp.Get(x, y)) continue;

                RotateCell(x, y, srcW, srcH, rotStep, out int rx, out int ry);

                int gx = anchorX + rx;
                int gy = anchorY + ry;

                if (gx < 0 || gx >= gd.gridSizeX || gy < 0 || gy >= gd.gridSizeY)
                    continue;

                Color cc = grid.IsOccupied(gridIndex, gx, gy) ? Color.red : color;
                DrawSingleCellOutline(grid, gridIndex, gx, gy, cc);
            }
        }
    }

    private void DrawSingleCellOutline(BuildingGrid grid, int gridIndex, int cellX, int cellY, Color color)
    {
        var g = grid.grids[gridIndex];

        Transform t = grid.transform;
        Vector3 right = t.right;
        Vector3 forward = t.forward;
        Vector3 up = t.up;

        Vector3 baseCenter =
            t.position + up * g.gridHeight +
            right * (g.centreOffsetX * g.cellSize) +
            forward * (g.centreOffsetY * g.cellSize);

        float width = g.gridSizeX * g.cellSize;
        float depth = g.gridSizeY * g.cellSize;

        Vector3 origin =
            baseCenter - right * (width * 0.5f) - forward * (depth * 0.5f);

        Vector3 cellMin =
            origin + right * (cellX * g.cellSize) + forward * (cellY * g.cellSize);

        Vector3 p0 = cellMin + up * gizmoYOffset;
        Vector3 p1 = cellMin + right * g.cellSize + up * gizmoYOffset;
        Vector3 p2 = cellMin + right * g.cellSize + forward * g.cellSize + up * gizmoYOffset;
        Vector3 p3 = cellMin + forward * g.cellSize + up * gizmoYOffset;

        Gizmos.color = color;
        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p1, p2);
        Gizmos.DrawLine(p2, p3);
        Gizmos.DrawLine(p3, p0);
    }

    private void ApplyMaterialToAllRenderers(GameObject root, Material mat)
    {
        if (!root || mat == null) return;

        var renderers = root.GetComponentsInChildren<Renderer>(true);
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (!r) continue;

            var shared = r.sharedMaterials;
            if (shared == null || shared.Length == 0)
            {
                r.sharedMaterial = mat;
                continue;
            }

            for (int s = 0; s < shared.Length; s++)
                shared[s] = mat;

            r.sharedMaterials = shared;
        }
    }

    private void DisableAllColliders(GameObject root)
    {
        var cols = root.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < cols.Length; i++)
            cols[i].enabled = false;
    }

    private void SetLayerRecursive(GameObject root, int layer)
    {
        if (!root) return;

        root.layer = layer;
        for (int i = 0; i < root.transform.childCount; i++)
        {
            var child = root.transform.GetChild(i);
            if (child) SetLayerRecursive(child.gameObject, layer);
        }
    }

    private bool TryGetCellFromHit(BuildingGrid gridComp, Vector3 hitPointWorld, Vector3 hitNormalWorld,
        out int gridIndex, out int cellX, out int cellY)
    {
        gridIndex = -1;
        cellX = -1;
        cellY = -1;

        Transform t = gridComp.transform;
        Vector3 right = t.right;
        Vector3 forward = t.forward;
        Vector3 up = t.up;
        Vector3 basePos = t.position;

        // 命中點沿船 up 軸的高度。基準跟 GridData.gridHeight 完全一致 ——
        // 那個欄位的定義就是「t.position + up * gridHeight」。
        float hitHeight = Vector3.Dot(hitPointWorld - basePos, up);

        if (debugLayerPick)
        {
            _dbg.Clear();
            _dbg.Append("[BuildSystem] 命中高度 h = ").Append(hitHeight.ToString("F2"))
                .Append("   (容差 ").Append(layerHeightTolerance).Append(")\n");
        }

        // 打到側壁 / 斜面時，高度會落在兩層之間，硬選最近的一層會蓋到牆上
        if (requireUpwardSurface && hitNormalWorld.sqrMagnitude > 0.0001f)
        {
            float surfAngle = Vector3.Angle(hitNormalWorld, up);
            if (surfAngle > maxSurfaceAngle)
            {
                if (debugLayerPick)
                {
                    _dbg.Append("  法線與船 up 夾角 ").Append(surfAngle.ToString("F1"))
                        .Append("° > ").Append(maxSurfaceAngle).Append("° → 判定為側壁，拒絕");
                    ReportLayerPick(null);
                }
                return false;
            }
        }

        int bestIndex = -1;
        int bestX = -1;
        int bestY = -1;
        float bestHeightDelta = float.PositiveInfinity;

        for (int i = 0; i < gridComp.grids.Length; i++)
        {
            var g = gridComp.grids[i];
            if (g.cellSize <= 0f || g.gridSizeX <= 0 || g.gridSizeY <= 0) continue;

            Vector3 center =
                basePos + up * g.gridHeight +
                right * (g.centreOffsetX * g.cellSize) +
                forward * (g.centreOffsetY * g.cellSize);

            float width = g.gridSizeX * g.cellSize;
            float depth = g.gridSizeY * g.cellSize;

            Vector3 origin =
                center - right * (width * 0.5f) - forward * (depth * 0.5f);

            Vector3 delta = hitPointWorld - origin;

            // right / forward 都跟 up 垂直，所以格座標的計算本來就不受高度影響 ——
            // 這正是舊版能完全忽略 gridHeight 的原因，也是它會選錯層的原因。
            float xUnits = Vector3.Dot(delta, right) / g.cellSize;
            float yUnits = Vector3.Dot(delta, forward) / g.cellSize;

            int x = Mathf.FloorToInt(xUnits);
            int y = Mathf.FloorToInt(yUnits);

            float heightDelta = Mathf.Abs(hitHeight - g.gridHeight);

            if (debugLayerPick)
                _dbg.Append("  grid[").Append(i).Append("] gridHeight=").Append(g.gridHeight)
                    .Append("  cell=(").Append(x).Append(",").Append(y).Append(")/")
                    .Append(g.gridSizeX).Append("x").Append(g.gridSizeY)
                    .Append("  Δh=").Append(heightDelta.ToString("F2"));

            if (x < 0 || x >= g.gridSizeX || y < 0 || y >= g.gridSizeY)
            {
                if (debugLayerPick) _dbg.Append("  ✗ 水平範圍外\n");
                continue;
            }

            // ★ 修正核心 ────────────────────────────────────────────────
            //   舊版是「第一個水平範圍包含命中點的 grid 就 return true」。
            //   Element 2（19×45 主甲板）在水平上罩住了 Element 4 和 5，
            //   而它的索引比較小，所以點頂層永遠選到主甲板 ——
            //   然後吃到主甲板的 occupied 狀態，顯示成不能蓋。
            //
            //   現在收集所有水平符合的候選，挑高度最接近命中點的那一層。
            //   金字塔形船體保證同一個 (x,y) 上只有一層外露，所以不會有歧義。
            if (heightDelta > layerHeightTolerance)
            {
                if (debugLayerPick) _dbg.Append("  ✗ 超出高度容差\n");
                continue;
            }

            if (debugLayerPick) _dbg.Append("  ✓ 候選\n");

            if (heightDelta < bestHeightDelta)
            {
                bestHeightDelta = heightDelta;
                bestIndex = i;
                bestX = x;
                bestY = y;
            }
        }

        if (bestIndex < 0)
        {
            if (debugLayerPick)
            {
                _dbg.Append("  → 沒有任何候選層。若各層 Δh 都很大，代表 gridHeight 跟實際甲板高度對不上；\n")
                    .Append("     若各層都是「水平範圍外」，代表 centreOffset / gridSize 沒有涵蓋到這個位置。");
                ReportLayerPick(null);
            }
            return false;
        }

        gridIndex = bestIndex;
        cellX = bestX;
        cellY = bestY;
        return true;
    }

    /// <summary>診斷輸出。內容跟上次相同時不重複印，避免每幀洗版。</summary>
    private void ReportLayerPick(string overrideMessage)
    {
        if (!debugLayerPick) return;

        string msg = overrideMessage ?? _dbg.ToString();
        if (msg == _lastLayerReport) return;

        _lastLayerReport = msg;
        Debug.Log(msg);
    }

    public void SetBuildingFootprints(int index)
    {
        buildModeOn = true;
        currentFootprintIndex = index;
    }
    public void ClearBuildingFootprints()
    {
        buildModeOn = false;
        currentFootprintIndex = 0;
    }

    private void HandleZoomInput()
    {
        if (!useRawImageInput) return;
        if (!gridRawImage) return;
        if (!buildingCamera) return;

        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Approximately(scroll, 0f)) return;

        RectTransform rt = gridRawImage.rectTransform;

        Camera eventCam = null;
        if (uiCanvas && uiCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            eventCam = uiCanvas.worldCamera;

        if (!RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, eventCam))
            return;

        var lens = buildingCamera.Lens;
        lens.OrthographicSize = Mathf.Clamp(lens.OrthographicSize - scroll * zoomSensitivity, minZoomDistance, maxZoomDistance);
        buildingCamera.Lens = lens;
    }
    private void HandlePanInput()
    {
        if (!useRawImageInput) return;
        if (!gridRawImage) return;
        if (!buildingCamera) return;

        if (!buildingCameraPositionComposer) return;

        RectTransform rt = gridRawImage.rectTransform;

        Camera eventCam = null;
        if (uiCanvas && uiCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            eventCam = uiCanvas.worldCamera;

        bool over = RectTransformUtility.RectangleContainsScreenPoint(rt, Input.mousePosition, eventCam);

        if (Input.GetMouseButtonDown(1) && over)
        {
            _isPanning = true;
            _panStartMousePos = Input.mousePosition;
            _panStartTargetOffset = buildingCameraPositionComposer.TargetOffset;
        }

        if (Input.GetMouseButtonUp(1))
            _isPanning = false;

        if (!_isPanning) return;

        Vector2 cur = Input.mousePosition;
        Vector2 delta = cur - _panStartMousePos;

        Vector3 off = _panStartTargetOffset;
        off.x += delta.x * panSensitivity;
        off.z += delta.y * panSensitivity;

        buildingCameraPositionComposer.TargetOffset = off;
    }

    private Vector3 GetFootprintCenterWorld(BuildingGrid grid, int gridIndex, int anchorX, int anchorY, BoolMatrix fp, int rotStep)
    {
        int srcW = fp.width;
        int srcH = fp.height;

        int dstW = (rotStep % 2 == 0) ? srcW : srcH;
        int dstH = (rotStep % 2 == 0) ? srcH : srcW;

        float centerOffsetX = (dstW - 1) * 0.5f;
        float centerOffsetY = (dstH - 1) * 0.5f;

        Vector3 anchorWorld = grid.GetCellWorldCenter(gridIndex, anchorX, anchorY);
        float cellSize = grid.grids[gridIndex].cellSize;

        Transform t = grid.transform;
        return anchorWorld + (t.right * (centerOffsetX * cellSize)) + (t.forward * (centerOffsetY * cellSize));
    }
    // -----  GHOST HELPERS  ---------------------------------------------------
    private bool TryGetShipRoots(out Transform realRoot, out Transform ghostRoot)
    {
        realRoot = null; ghostRoot = null;

        var nav = LandshipNavigation.Instance;
        if (nav == null) return false;

        realRoot = nav.core ? nav.core : null;
        ghostRoot = nav.ghostShip ? nav.ghostShip : null;

        return realRoot && ghostRoot;
    }

    private Vector3 ProjectPointToGhost(Transform realRoot, Transform ghostRoot, Vector3 worldPos)
    {
        Vector3 local = realRoot.InverseTransformPoint(worldPos);
        return ghostRoot.TransformPoint(local);
    }

    private Quaternion ProjectRotToGhost(Transform realRoot, Transform ghostRoot, Quaternion worldRot)
    {
        Quaternion localRot = Quaternion.Inverse(realRoot.rotation) * worldRot;
        return ghostRoot.rotation * localRot;
    }
}