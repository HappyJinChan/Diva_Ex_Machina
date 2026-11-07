using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.AI;

public class PathFinder : MonoBehaviour, IPooled
{
    // ── Profiler 量測（只量測，不改行為）────────────────────────────
    // 在 Profiler 的 CPU Usage → Hierarchy 搜尋「PathFinder.」即可看到各分支耗時與呼叫次數。
    // 「GroundToDock.NearDock」= 敵人離登船口 dockUpdateDistance 以內、用高頻更新時的那一部分。
    static readonly ProfilerMarker s_FindPath        = new ProfilerMarker("PathFinder.FindPath");
    static readonly ProfilerMarker s_ShipToShip      = new ProfilerMarker("PathFinder.ShipToShip");
    static readonly ProfilerMarker s_ShipToGround    = new ProfilerMarker("PathFinder.ShipToGround");
    static readonly ProfilerMarker s_GroundToDock    = new ProfilerMarker("PathFinder.GroundToDock");
    static readonly ProfilerMarker s_GroundToDockNear = new ProfilerMarker("PathFinder.GroundToDock.NearDock");
    static readonly ProfilerMarker s_GroundToGround  = new ProfilerMarker("PathFinder.GroundToGround");
    static readonly ProfilerMarker s_CalculatePath   = new ProfilerMarker("PathFinder.CalculatePath");
    static readonly ProfilerMarker s_LaneSpread      = new ProfilerMarker("PathFinder.LaneSpread");
    static readonly ProfilerMarker s_DockSpreadGoal  = new ProfilerMarker("PathFinder.DockSpreadGoal");

    [Header("Profiling")]
    [Tooltip("只用來分類量測：離最近登船口小於這個水平距離時，GroundToDock 另外記到 NearDock。\n" +
             "建議跟 Brain 的 Dock Update Distance 一致。不影響任何行為。")]
    [SerializeField] private float profileNearDockDistance = 30f;

    public Vector3 targetLocation;
    public bool isTargetOnShip = false;
    public bool isOnShip = false;
    private NavMeshPath path;

    // ── 船的 root：唯一來源是 LandshipNavigation ─────────────────────────
    //
    // 原本這裡是兩個 [SerializeField] Transform，靠 Inspector 逐一指定。
    // Prefab 存不了場景物件的引用，所以任何在執行期 Instantiate 出來的敵人
    // （EnemySpawner 生的、物件池生的）那兩個欄位必然是 None。
    // 一旦 SyncShipFlags 把 isOnShip / isTargetOnShip 設成 true，
    // ShipNavProjector 就會拿 null 去做投影 → NRE → FixedUpdate 中斷 →
    // 那隻敵人連移動都停掉。症狀是「平常正常，一靠近船整個 AI 死掉」。
    //
    // 現在一律讀 LandshipNavigation.Instance，場景裡指一次就好，
    // 生成端（EnemySpawner）一行都不用改。
    //
    // ⚠ realShip 與 ghostShip 必須是同一個物件的「本尊 / 分身」關係。
    //   指錯不會報錯，只會讓船上的路徑投影到錯誤的世界座標。


    private Transform RealShipRoot
    {
        get
        {
            LandshipNavigation nav = LandshipNavigation.Instance;
            return (nav != null) ? nav.realShip : null;
        }
    }

    private Transform GhostShipRoot
    {
        get
        {
            LandshipNavigation nav = LandshipNavigation.Instance;
            return (nav != null) ? nav.ghostShip : null;
        }
    }

    /// <summary>兩個 root 都拿得到才能做投影。</summary>
    private bool HasShipRoots => RealShipRoot != null && GhostShipRoot != null;
    float navSampleMaxDistance = 30f; // Maximum distance for NavMesh.SamplePosition

    [Header("Elevated Target Probe")]
    [Tooltip("目標 pivot 懸空時（例如 core 架在甲板上方 20m），先往下找到它正下方的\n" +
             "可走表面，再交給 SamplePosition。\n\n" +
             "不做這件事的話：SamplePosition 會用 navSampleMaxDistance（30）的半徑\n" +
             "去搜最近的 navmesh，而「最近」不保證是甲板 —— 可能吸到船底、地形、\n" +
             "或甲板上某塊不相連的碎片。吸歪不會報錯，只會讓 CalculatePath 拿一個\n" +
             "替身點去算路，結果是永遠的 PathPartial 加一個固定的終點缺口。\n\n" +
             "這條 ray 打在 ghost 空間 —— ghost 建築只關 Renderer、保留 Collider，\n" +
             "所以打得到。groundMask 必須包含 ghost 甲板所在的 layer。")]
    [SerializeField] private float verticalProbeDistance = 40f;
    [SerializeField] private LayerMask groundMask = ~0;

    [Header("Combat Destination Sampling")]
    [Tooltip("戰鬥狀態一次產生幾個候選點。每個候選點各打 1 條向下 ray + 1 條視線 ray。")]
    [SerializeField, Min(1)] private int combatCandidateCount = 4;
    [Tooltip("候選點從「目標高度 + 這個值」往下找地面。要大於地形起伏，否則站在斜坡上方的點會打不到地面。")]
    [SerializeField] private float candidateProbeUp = 10f;
    [Tooltip("候選點往下找地面的最大距離（從 目標高度 + candidateProbeUp 起算）。")]
    [SerializeField] private float candidateProbeDown = 60f;
    [Tooltip("視線檢查的起點高度（候選地面點往上幾公尺，約等於敵人砲口 / 眼睛高度）。")]
    [SerializeField] private float losEyeHeight = 1.5f;
    [Tooltip("視線檢查會被哪些 layer 擋住。目標自己的 layer 也要包含在內，否則永遠「打不到」目標。")]
    [SerializeField] private LayerMask losMask = ~0;

    private static readonly RaycastHit[] losHits = new RaycastHit[16];

    [Header("Debug Gizmo")]
    [SerializeField] private bool drawPathGizmo = true;
    [SerializeField] private Color pathColor = Color.green;
    [SerializeField] private float waypointGizmoRadius = 0.3f;

    // ── Path cache ──────────────────────────────────────────────
    // lastPath      : 計算當下的 real world 快照（地面路徑用；船上路徑會 stale，僅供參考）
    // lastPathGhost : ghost 空間的原始 corner（船上路徑用，不會 stale），每幀再投影
    // lastPathOnShip: 這條路徑是不是船上算的 → 決定要不要每幀即時投影
    private Vector3[] lastPath = System.Array.Empty<Vector3>();
    private Vector3[] lastPathGhost = System.Array.Empty<Vector3>();
    private bool lastPathOnShip = false;

    [SerializeField] private float disembarkDistance = 50f;

    // ── 路徑分散（只套用在地面路徑）─────────────────────────────
    //
    // 問題：同一區域出發的敵人，起點、終點（同一個 docking point）、
    //       NavMesh 轉角（全部貼著障礙物邊緣）都一樣 → 路徑完全重合，150 隻看起來像一條線。
    //
    // 做法：每隻敵人生成時抽一個固定的車道值 laneValue（-1 ~ 1），一輩子不變，
    //       所以每次重算路徑都偏在同一側，不會左右亂跳。
    //   1. 中間的轉角點沿路徑垂直方向偏移 laneValue × laneWidth，
    //      越接近終點偏移越小（laneFadeDistance），最後收回原路徑。
    //   2. 太長的直線段先在中點插一個點，讓空曠地形上也能散開成弧形。
    //   3. 地面 → 船：終點從 docking point 改成它旁邊的一個橫向位置（dockSpreadRadius），
    //      離 docking point dockSpreadInnerDistance 以內收回 0，保證最後真的走進登船口。
    //   每個偏移點都用 NavMesh.Raycast 檢查，被擋住就先減半再試，還是不行就用原本的點，不會被推進牆裡。
    //   成本只在重算路徑時發生（每隻每 1~3 秒一次，幾條 NavMesh 射線）。
    [Header("Path Spread (ground paths only)")]
    [Tooltip("關掉 = 完全照舊走最短路徑。飛行單位（Falcon）可以關掉。")]
    [SerializeField] private bool enableLaneSpread = true;
    [Tooltip("車道最大橫向偏移（公尺）。實際偏移 = laneValue(-1~1) × 這個值。")]
    [SerializeField, Min(0f)] private float laneWidth = 6f;
    [Tooltip("離終點剩下多少路程內，偏移從全額線性收回 0。")]
    [SerializeField, Min(0.01f)] private float laneFadeDistance = 25f;
    [Tooltip("直線段長於這個值就在中點插一個點，讓它也能偏移。0 = 不插點。")]
    [SerializeField, Min(0f)] private float splitSegmentLength = 40f;
    [Tooltip("登船口前的最大橫向分散距離（公尺）。")]
    [SerializeField, Min(0f)] private float dockSpreadRadius = 8f;
    [Tooltip("離 docking point 這個水平距離以內，終點不再分散，直接走進登船口。建議跟 Brain 的 Dock Update Distance 一致。")]
    [SerializeField, Min(0f)] private float dockSpreadInnerDistance = 30f;
    [Tooltip("超過 Inner Distance 之後，再過這麼遠才達到全額分散。")]
    [SerializeField, Min(0.01f)] private float dockSpreadRampDistance = 30f;

    private float laneValue;
    private bool laneAssigned;

    // ── 船身繞行（ShipHullOutline）──────────────────────────────
    //
    // 取代船上 17 個會一直重挖 NavMesh 的 NavMeshObstacle（Carving 每幀 3–4 ms）。
    // 場景裡有有效的 ShipHullOutline 時：
    //   · 地面 → 登船口：終點改成登船口外的接近點，最後再接登船口本身；
    //     接近點與登船口存成船本地座標，每次讀取時跟著船投影 → 不會撲到船幾秒前的位置。
    //   · 任何地面路徑穿過船身輪廓時，從穿越處改成沿輪廓頂點繞過去（頂點也跟著船走）。
    // 沒有 ShipHullOutline 時行為完全照舊。
    //
    // 混合路徑：每個點各自記錄是世界座標（地面 NavMesh 轉角）還是船本地座標（輪廓、接近點、登船口）。
    private bool lastPathHybrid;
    private Vector3[] hybridPoints = System.Array.Empty<Vector3>();
    private bool[] hybridLocal = System.Array.Empty<bool>();
    private readonly List<Vector3> _hybridPts = new List<Vector3>();
    private readonly List<bool> _hybridLoc = new List<bool>();
    private static readonly List<int> s_hullRoute = new List<int>();
    private static Vector2[] s_hullPoly = new Vector2[0];
    static readonly ProfilerMarker s_HullDetour = new ProfilerMarker("PathFinder.HullDetour");

    [Header("Ship Hull Approach")]
    [Tooltip("走過接近點之後，離「接近點 → 登船口」這條線多遠以內，就直接走向登船口、不再回頭找接近點（公尺）。\n" +
             "要比 Dock Spread Radius 大，否則分散到旁邊的敵人會被判定成還沒走過。")]
    [SerializeField, Min(0f)] private float approachPassWidth = 15f;

    private void AssignLane()
    {
        laneValue = Random.Range(-1f, 1f);
        laneAssigned = true;
    }

    void Start()
    {
        path = new NavMeshPath();
        if (!laneAssigned) AssignLane();   // 場景裡直接擺的敵人不會收到 OnSpawned
    }

    // 物件池重新上場：清掉上一輩子的船上狀態與路徑。
    public void OnSpawned()
    {
        isOnShip = false;
        isTargetOnShip = false;
        lastPathOnShip = false;
        lastPathHybrid = false;
        if (path != null) path.ClearCorners();
        AssignLane();   // 每一輩子重抽車道
    }

    public void OnDespawned() { }

    public Vector3[] FindPath(Vector3 targetLocation)
    {
        using (s_FindPath.Auto())
            return FindPathImpl(targetLocation);
    }

    private Vector3[] FindPathImpl(Vector3 targetLocation)
    {
        Vector3[] result;
        lastPathOnShip = false;   // 預設地面；下面命中 ghost 分支才設 true
        lastPathHybrid = false;   // 預設不是混合路徑；下面繞船 / 登船口分支才設 true

        if (isOnShip && isTargetOnShip)// if both side on ship, convert gobal coordinates to ship local coordinates
        {
            using var _m = s_ShipToShip.Auto();
            result = ComputeGhostPath(transform.position, targetLocation);
            lastPathOnShip = true;

            if (result.Length == 0)
                result = BuildFallbackPath(targetLocation, true);
        }
        else if (isOnShip && !isTargetOnShip)// if entity on ship but target not on ship
        {
            using var _m = s_ShipToGround.Auto();
            // 先算「走到 dock」的船上路徑
            Vector3 dockPos = GetTargetClosestDockingLocation();
            result = ComputeGhostPath(transform.position, dockPos);
            lastPathOnShip = true;

            // ★ 在路徑尾端追加一個「離船點」：dock 往船外方向延伸幾公尺。
            //   實體為了走到它，必然跨出船的 trigger → isOnShip 翻 false
            //   → 下次 PathUpdate 自然改用地面路徑直奔目標。
            if (result.Length > 0)
            {
                Vector3 exitPoint = GetDisembarkPoint(dockPos);
                var extended = new Vector3[result.Length + 1];
                result.CopyTo(extended, 0);
                extended[extended.Length - 1] = exitPoint;
                result = extended;

                // ghost 快取也要同步追加，否則 GetCurrentWorldPath 投影出來會少最後一點
                var extendedGhost = new Vector3[lastPathGhost.Length + 1];
                lastPathGhost.CopyTo(extendedGhost, 0);
                extendedGhost[extendedGhost.Length - 1] =
                    ShipNavProjector.RealToGhostPoint(RealShipRoot, GhostShipRoot, exitPoint);
                lastPathGhost = extendedGhost;
            }
            else
            {
                result = BuildFallbackPath(dockPos, true);
            }
        }
        else if (!isOnShip && isTargetOnShip && HullAvailable(out ShipHullOutline hull))
        {
            using var _m = s_GroundToDock.Auto();
            result = FindDockPathAroundHull(hull);
        }
        else if (!isOnShip && isTargetOnShip) // if player not on ship but target on ship, find path to ship docking point
        {
            Vector3 dockPos = GetClosestDockingLocation();
            Vector3 toDock = dockPos - transform.position; toDock.y = 0f;
            bool nearDock = toDock.sqrMagnitude < profileNearDockDistance * profileNearDockDistance;
            using var _m = s_GroundToDock.Auto();
            if (nearDock) s_GroundToDockNear.Begin();   // 這個分支中間沒有 return，結尾一定會 End
            Vector3 dockGoal = enableLaneSpread ? GetDockSpreadGoal(dockPos) : dockPos;
            result = ComputeGroundPath(transform.position, dockGoal);
            if (result.Length == 0 && dockGoal != dockPos)
                result = ComputeGroundPath(transform.position, dockPos);   // 分散點算不出路 → 退回原本的 docking point

            if (result.Length == 0)
                result = BuildFallbackPath(dockPos, false);
            else if (enableLaneSpread)
                result = ApplyLaneSpread(result);
            if (nearDock) s_GroundToDockNear.End();
        }
        else
        {
            using var _m = s_GroundToGround.Auto();
            result = ComputeGroundPath(transform.position, targetLocation);

            if (result.Length == 0)
                result = BuildFallbackPath(targetLocation, false);
            else
            {
                if (enableLaneSpread)
                    result = ApplyLaneSpread(result);

                // 路徑穿過船身才改成繞船；沒穿過就完全照舊
                if (HullAvailable(out ShipHullOutline groundHull))
                {
                    Transform ship = RealShipRoot;
                    if (BuildHullPath(groundHull, ship, result, result.Length - 1, result[result.Length - 1], false))
                        result = CommitHybrid(ship);
                }
            }
        }

        lastPath = result;
        return result;
    }

    /// <summary>
    /// 算不出路徑時的保底：回傳「只有目的地一個點」的路徑，而不是空陣列。
    ///
    /// ★ 這是這次修的 bug 的一半。
    ///   舊版只要 SamplePosition 或 CalculatePath 失敗一次就回傳空陣列，
    ///   呼叫端（Brain）拿到空路徑就整段 return → 實體停在原地不動。
    ///   而「原地」正是剛剛 sample 失敗的那個位置，所以下一次 FindPath 的
    ///   起點還是同一個點 → 再失敗 → 永遠失敗。一次失誤就變成永久死鎖。
    ///
    ///   給一個直線保底點，實體至少會離開那個壞位置，
    ///   移動之後 NavMesh.SamplePosition 才有機會重新成功。
    /// </summary>
    private Vector3[] BuildFallbackPath(Vector3 endWorld, bool ghostSpace)
    {

        if (ghostSpace && HasShipRoots)
        {
            // 船上的保底點也要存 ghost 空間，GetCurrentWorldPath 才會每幀跟著船投影
            lastPathOnShip = true;
            lastPathGhost = new Vector3[]
            {
                ShipNavProjector.RealToGhostPoint(RealShipRoot, GhostShipRoot, endWorld)
            };
        }
        else
        {
            lastPathOnShip = false;
            lastPathGhost = System.Array.Empty<Vector3>();
        }

        return new Vector3[] { endWorld };
    }

    // 船上路徑即時投影用的緩衝區。原本每次呼叫都 new 一個陣列 ——
    // 船上每隻敵人每次思考都配置一次，是 Brain 產生 GC 的主要來源之一。
    private Vector3[] _worldPathBuffer = System.Array.Empty<Vector3>();

    // ★ 每次思考呼叫：船上路徑用「當下船姿態」即時投影 → 不 stale；地面路徑直接回傳
    //
    // ⚠ 船上路徑回傳的是重複使用的緩衝區，下一次呼叫會被覆寫。
    //   拿來當下讀取就好，不要存起來。
    public Vector3[] GetCurrentWorldPath()
    {
        if (lastPathHybrid)
        {
            Transform ship = RealShipRoot;
            if (ship == null) return lastPath;
            if (_worldPathBuffer.Length != hybridPoints.Length)
                _worldPathBuffer = new Vector3[hybridPoints.Length];
            for (int i = 0; i < hybridPoints.Length; i++)
                _worldPathBuffer[i] = hybridLocal[i] ? ship.TransformPoint(hybridPoints[i]) : hybridPoints[i];
            return _worldPathBuffer;
        }

        if (!lastPathOnShip)
            return lastPath;   // 地面路徑不會動，世界座標本來就不 stale

        if (lastPathGhost == null || lastPathGhost.Length == 0 || !HasShipRoots)
            return System.Array.Empty<Vector3>();

        if (_worldPathBuffer.Length != lastPathGhost.Length)
            _worldPathBuffer = new Vector3[lastPathGhost.Length];   // 只有路徑長度改變時才配置

        Transform real = RealShipRoot;
        Transform ghost = GhostShipRoot;
        for (int i = 0; i < lastPathGhost.Length; i++)
            _worldPathBuffer[i] = ShipNavProjector.GhostToRealPoint(real, ghost, lastPathGhost[i]);
        return _worldPathBuffer;
    }

    /// <summary>
    /// 戰鬥用目的地：在目標周圍產生 combatCandidateCount 個候選點，
    /// 只保留「站在那裡看得到目標」的點；多個可用時選高度最接近目標的。
    ///
    /// 解決的問題：目標站在懸崖邊時，隨機點落在崖外，SamplePosition 會把它吸到崖底。
    /// 崖底的點視線會被崖壁擋住 → 被淘汰；就算崖底剛好看得到，高度差也會輸給崖頂的點。
    ///
    /// 全部候選都看不到目標時，退而選高度最接近的點（仍比舊版的純隨機好）；
    /// 連地面都打不到，就回傳目標位置本身。
    /// </summary>
    public Vector3 PickCombatDestination(Transform target, float radius)
    {
        Vector3 targetPos = target.position;
        Vector3 aimPoint = TurretController.GetAimPoint(target);

        bool haveVisible = false, haveAny = false;
        Vector3 bestVisible = targetPos, bestAny = targetPos;
        float bestVisibleDy = float.MaxValue, bestAnyDy = float.MaxValue;

        for (int i = 0; i < combatCandidateCount; i++)
        {
            Vector2 r = Random.insideUnitCircle * radius;
            Vector3 probeOrigin = new Vector3(targetPos.x + r.x, targetPos.y + candidateProbeUp, targetPos.z + r.y);

            // 用向下 ray 找「這個水平位置」的地面，而不是 SamplePosition 的「最近 navmesh」——
            // 後者在崖邊會橫向吸附，前者至少保證 XZ 不變，高度才有比較的意義。
            if (!Physics.Raycast(probeOrigin, Vector3.down, out RaycastHit ground,
                    candidateProbeUp + candidateProbeDown, groundMask, QueryTriggerInteraction.Ignore))
                continue;

            // 候選點落在目標自己身上（例如大型建築的屋頂）→ 不算地面
            if (IsPartOfTarget(ground.collider, target))
                continue;

            Vector3 candidate = ground.point;
            float dy = Mathf.Abs(candidate.y - targetPos.y);

            if (dy < bestAnyDy) { bestAnyDy = dy; bestAny = candidate; haveAny = true; }

            if (dy < bestVisibleDy && HasLineOfSight(candidate + Vector3.up * losEyeHeight, aimPoint, target))
            {
                bestVisibleDy = dy;
                bestVisible = candidate;
                haveVisible = true;
            }
        }

        if (haveVisible) return bestVisible;
        if (haveAny) return bestAny;
        return targetPos;
    }

    /// <summary>
    /// 從 from 往 to 打一條線，忽略自己的 collider；
    /// 第一個擋到的東西是目標 → 看得到；途中沒有任何東西 → 也算看得到。
    /// </summary>
    private bool HasLineOfSight(Vector3 from, Vector3 to, Transform target)
    {
        Vector3 dir = to - from;
        float dist = dir.magnitude;
        if (dist < 0.01f) return true;
        dir /= dist;

        // 多打 0.5m，確保 bounds 中心在 collider 內部時仍能命中表面
        int n = Physics.RaycastNonAlloc(from, dir, losHits, dist + 0.5f, losMask, QueryTriggerInteraction.Ignore);

        float nearest = float.MaxValue;
        Collider nearestCol = null;
        for (int i = 0; i < n; i++)
        {
            Collider col = losHits[i].collider;
            if (col.transform.IsChildOf(transform)) continue;   // 自己的身體不算遮蔽
            if (losHits[i].distance < nearest)
            {
                nearest = losHits[i].distance;
                nearestCol = col;
            }
        }

        if (nearestCol == null) return true;
        return IsPartOfTarget(nearestCol, target);
    }

    /// <summary>
    /// collider 是否屬於目標。
    /// 不用 target.root 比對：建築 / Core 可能掛在船底下，root 是整艘船，會把船身誤判成目標。
    /// </summary>
    private bool IsPartOfTarget(Collider col, Transform target)
    {
        if (col.transform == target || col.transform.IsChildOf(target)) return true;

        // EnemyDetection 傳進來的可能是子物件 collider，本體在同一個 Rigidbody 下
        Rigidbody rb = col.attachedRigidbody;
        if (rb == null || rb.transform == RealShipRoot) return false;
        return target.IsChildOf(rb.transform);
    }

    public Vector3 GetClosestDockingLocation()
    {
        float closestDistance = Mathf.Infinity;
        Vector3 closestDockingPoint = Vector3.zero;
        for (int i = 0; i < LandshipNavigation.Instance.dockingPoints.Length; i++)
        {
            float distance = Vector3.Distance(transform.position, LandshipNavigation.Instance.dockingPoints[i].position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestDockingPoint = LandshipNavigation.Instance.dockingPoints[i].position;
            }
        }
        return closestDockingPoint;
    }

    private int GetClosestDockIndex()
    {
        LandshipNavigation nav = LandshipNavigation.Instance;
        if (nav == null || nav.dockingPoints == null) return -1;
        float best = Mathf.Infinity;
        int index = -1;
        for (int i = 0; i < nav.dockingPoints.Length; i++)
        {
            if (nav.dockingPoints[i] == null) continue;
            float d = (nav.dockingPoints[i].position - transform.position).sqrMagnitude;
            if (d < best) { best = d; index = i; }
        }
        return index;
    }

    // ======================= 船身繞行 =======================

    private bool HullAvailable(out ShipHullOutline hull)
    {
        hull = ShipHullOutline.Instance;
        return hull != null && hull.isActiveAndEnabled && hull.IsValid && RealShipRoot != null;
    }

    /// <summary>
    /// 地面 → 登船口（有 ShipHullOutline 時）。
    /// 路徑 = 地面 NavMesh 轉角（世界座標）→〔必要時沿輪廓繞船〕→ 接近點 → 登船口（後三者船本地座標）。
    /// </summary>
    private Vector3[] FindDockPathAroundHull(ShipHullOutline hull)
    {
        Transform ship = RealShipRoot;
        int di = GetClosestDockIndex();
        if (di < 0) return BuildFallbackPath(transform.position, false);

        Transform dockT = LandshipNavigation.Instance.dockingPoints[di];
        Transform appT = hull.GetApproach(di);
        Vector3 appPos = appT != null ? appT.position : dockT.position;

        // 已經走過接近點、正在「接近點 → 登船口」這一段上 → 直接走進登船口，不要再回頭找接近點。
        // 少了這一步，登船口附近每 0.1–0.3 秒重算一次路徑時，每次都會把終點設回接近點，
        // 敵人一走過接近點就被拉回去，全部卡在接近點來回打轉。
        if (appT != null && IsPastApproach(appPos, dockT.position))
        {
            _hybridPts.Clear();
            _hybridLoc.Clear();
            _hybridPts.Add(transform.position);
            _hybridLoc.Add(false);
            _hybridPts.Add(ship.InverseTransformPoint(dockT.position));
            _hybridLoc.Add(true);
            return CommitHybrid(ship);
        }

        Vector3 goal = enableLaneSpread ? GetDockSpreadGoal(appPos) : appPos;
        Vector3[] corners = ComputeGroundPath(transform.position, goal);
        if (corners.Length == 0 && goal != appPos)
        {
            goal = appPos;                                                    // 分散點算不出路 → 退回接近點
            corners = ComputeGroundPath(transform.position, appPos);
        }
        if (corners.Length > 0 && enableLaneSpread)
            corners = ApplyLaneSpread(corners);

        // 地面路徑的最後一點就是 goal（sample 過的版本）→ 拿掉，改用跟著船走的版本。
        // 算不出地面路徑時，只剩「目前位置 → 接近點」，一樣會做繞船檢查。
        Vector3[] fixedPts = corners.Length > 0 ? corners : new[] { transform.position };
        int fixedCount = corners.Length > 0 ? corners.Length - 1 : 1;
        if (fixedCount < 1) { fixedPts = new[] { transform.position }; fixedCount = 1; }

        BuildHullPath(hull, ship, fixedPts, fixedCount, ship.InverseTransformPoint(goal), true);
        _hybridPts.Add(ship.InverseTransformPoint(dockT.position));          // 最後走進登船口
        _hybridLoc.Add(true);
        return CommitHybrid(ship);
    }

    /// <summary>
    /// 自己是否已經在「接近點 → 登船口」這一段上（水平面）：
    /// 投影超過接近點，而且離這條線的橫向距離在 approachPassWidth 以內。
    /// </summary>
    private bool IsPastApproach(Vector3 app, Vector3 dock)
    {
        Vector2 a = new Vector2(app.x, app.z);
        Vector2 v = new Vector2(dock.x, dock.z) - a;
        Vector2 w = new Vector2(transform.position.x, transform.position.z) - a;
        float len2 = v.sqrMagnitude;
        if (len2 < 0.0001f) return true;
        float t = Vector2.Dot(w, v) / len2;
        if (t <= 0f) return false;
        Vector2 lateral = w - v * Mathf.Min(t, 1f);
        return lateral.magnitude < approachPassWidth;
    }

    /// <summary>
    /// 把 fixedPts[0..fixedCount-1]（世界座標）接到 goal，途中穿過船身輪廓就改成沿輪廓頂點繞過去。
    /// 結果寫進 _hybridPts / _hybridLoc（尚未套用，呼叫 CommitHybrid 才生效）。
    /// goalIsLocal = true 代表 goal 是船本地座標（會跟著船走）。回傳是否真的繞了船。
    /// </summary>
    private bool BuildHullPath(ShipHullOutline hull, Transform ship, Vector3[] fixedPts, int fixedCount, Vector3 goal, bool goalIsLocal)
    {
        using var _m = s_HullDetour.Auto();
        _hybridPts.Clear();
        _hybridLoc.Clear();

        int n = hull.FillLocalPolygon(ship, ref s_hullPoly);
        Vector3 goalLocal3 = goalIsLocal ? goal : ship.InverseTransformPoint(goal);
        Vector2 goal2 = new Vector2(goalLocal3.x, goalLocal3.z);

        // 找出第一段穿過輪廓的線段（最後一段是 fixedPts 的最後一點 → goal）
        int cross = -1;
        Vector2 a2 = default;
        for (int i = 0; i < fixedCount; i++)
        {
            Vector3 p = ship.InverseTransformPoint(fixedPts[i]);
            Vector2 p2 = new Vector2(p.x, p.z);
            Vector2 q2;
            if (i + 1 < fixedCount)
            {
                Vector3 q = ship.InverseTransformPoint(fixedPts[i + 1]);
                q2 = new Vector2(q.x, q.z);
            }
            else q2 = goal2;

            if (ShipHullOutline.Blocked(s_hullPoly, n, p2, q2)) { cross = i; a2 = p2; break; }
        }

        bool detoured = false;
        if (cross >= 0 &&
            !ShipHullOutline.PointInPolygon(s_hullPoly, n, a2) &&                // 起點在輪廓內（例如站在登船口凹槽裡）→ 不繞
            !ShipHullOutline.PointInPolygon(s_hullPoly, n, goal2) &&
            ShipHullOutline.ShortestRoute(s_hullPoly, n, a2, goal2, s_hullRoute))
        {
            for (int i = 0; i <= cross; i++) { _hybridPts.Add(fixedPts[i]); _hybridLoc.Add(false); }
            for (int r = 0; r < s_hullRoute.Count; r++) { _hybridPts.Add(hull.CornerLocal(ship, s_hullRoute[r])); _hybridLoc.Add(true); }
            detoured = true;
        }
        else
        {
            for (int i = 0; i < fixedCount; i++) { _hybridPts.Add(fixedPts[i]); _hybridLoc.Add(false); }
        }

        _hybridPts.Add(goal);
        _hybridLoc.Add(goalIsLocal);
        return detoured;
    }

    /// <summary>把 _hybridPts / _hybridLoc 設為目前路徑，回傳當下的世界座標版本（新陣列，可以存起來）。</summary>
    private Vector3[] CommitHybrid(Transform ship)
    {
        hybridPoints = _hybridPts.ToArray();
        hybridLocal = _hybridLoc.ToArray();
        lastPathHybrid = true;
        lastPathOnShip = false;
        lastPathGhost = System.Array.Empty<Vector3>();

        var world = new Vector3[hybridPoints.Length];
        for (int i = 0; i < world.Length; i++)
            world[i] = hybridLocal[i] ? ship.TransformPoint(hybridPoints[i]) : hybridPoints[i];
        return world;
    }

    public Vector3 GetTargetClosestDockingLocation()
    {
        float closestDistance = Mathf.Infinity;
        Vector3 closestDockingPoint = Vector3.zero;
        for (int i = 0; i < LandshipNavigation.Instance.dockingPoints.Length; i++)
        {
            float distance = Vector3.Distance(targetLocation, LandshipNavigation.Instance.dockingPoints[i].position);
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestDockingPoint = LandshipNavigation.Instance.dockingPoints[i].position;
            }
        }
        return closestDockingPoint;
    }

    private Vector3[] ComputeGhostPath(Vector3 startWorld, Vector3 endWorld)
    {
        // 兩個 root 都拿不到（場景沒有 LandshipNavigation、或它沒指好）→
        // 回傳空陣列讓呼叫端走保底直線，而不是把 null 丟進 ShipNavProjector 直接 NRE。
        if (!HasShipRoots)
        {
            lastPathGhost = System.Array.Empty<Vector3>();
            return System.Array.Empty<Vector3>();
        }

        Vector3 navStart = ShipNavProjector.RealToGhostPoint(RealShipRoot, GhostShipRoot, startWorld);
        Vector3 navEnd = ShipNavProjector.RealToGhostPoint(RealShipRoot, GhostShipRoot, endWorld);

        // ★ 懸空目標先往下找地板。core 的 pivot 在甲板上方 20m，直接 sample 會被
        //   30m 的半徑吸到「最近的任意一塊 navmesh」，而那塊不一定跟甲板相連。
        //   打不到東西就維持原值 —— 目標本來就在地面高度時（例如 docking point），
        //   這條 ray 要嘛打到腳下的甲板、要嘛落空，兩種情況行為都跟改動前一樣。
        if (Physics.Raycast(navEnd, Vector3.down, out RaycastHit floor,
                verticalProbeDistance, groundMask, QueryTriggerInteraction.Ignore))
            navEnd = floor.point;

        if (!NavMesh.SamplePosition(navStart, out NavMeshHit s, navSampleMaxDistance, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(navEnd, out NavMeshHit e, navSampleMaxDistance, NavMesh.AllAreas))
        {
            lastPathGhost = System.Array.Empty<Vector3>();
            return System.Array.Empty<Vector3>();
        }

        using (s_CalculatePath.Auto())
            NavMesh.CalculatePath(s.position, e.position, NavMesh.AllAreas, path);

        // corners 是 property，每次存取都配一個新陣列 —— 取一次，後面都用它
        Vector3[] g = path.corners;

        if (path.status == NavMeshPathStatus.PathInvalid || g.Length == 0)
        {
            lastPathGhost = System.Array.Empty<Vector3>();
            return System.Array.Empty<Vector3>();
        }

        // ★ 存 ghost 空間原始 corner（不會隨船移動而 stale），供 GetCurrentWorldPath 每幀投影
        lastPathGhost = (Vector3[])g.Clone();

        // return 當下的 real world 版本（呼叫端 SetPath 立即使用一次）
        var outArr = new Vector3[g.Length];
        for (int i = 0; i < g.Length; i++)
            outArr[i] = ShipNavProjector.GhostToRealPoint(RealShipRoot, GhostShipRoot, g[i]);
        return outArr;
    }

    private Vector3[] ComputeGroundPath(Vector3 startWorld, Vector3 endWorld)
    {
        // 地面路徑：清掉 ghost 快取，避免萬一 lastPathOnShip 判斷有誤時投影到舊資料
        lastPathGhost = System.Array.Empty<Vector3>();

        if (!NavMesh.SamplePosition(startWorld, out NavMeshHit s, navSampleMaxDistance, NavMesh.AllAreas) ||
            !NavMesh.SamplePosition(endWorld, out NavMeshHit e, navSampleMaxDistance, NavMesh.AllAreas))
            return System.Array.Empty<Vector3>();

        using (s_CalculatePath.Auto())
            NavMesh.CalculatePath(s.position, e.position, NavMesh.AllAreas, path);

        // corners 是 property，每次存取都配一個新陣列 —— 原本這裡存取了兩次，取一次就好
        Vector3[] corners = path.corners;
        if (path.status == NavMeshPathStatus.PathInvalid || corners.Length == 0)
            return System.Array.Empty<Vector3>();

        return corners;
    }

    /// <summary>
    /// 登船口前的分散終點：docking point 往「接近方向的垂直方向」偏移。
    /// 離 docking point 越近偏移越小，進入 dockSpreadInnerDistance 就是 docking point 本身。
    /// </summary>
    private Vector3 GetDockSpreadGoal(Vector3 dockPos)
    {
        using var _m = s_DockSpreadGoal.Auto();
        if (dockSpreadRadius <= 0f || Mathf.Abs(laneValue) < 0.001f) return dockPos;

        Vector3 approach = dockPos - transform.position;
        approach.y = 0f;
        float dist = approach.magnitude;
        if (dist < 0.001f) return dockPos;

        float scale = Mathf.Clamp01((dist - dockSpreadInnerDistance) / dockSpreadRampDistance);
        if (scale <= 0f) return dockPos;

        Vector3 side = new Vector3(approach.z, 0f, -approach.x) / dist;   // 接近方向的右手邊
        Vector3 goal = dockPos + side * (laneValue * dockSpreadRadius * scale);

        // 分散點要跟 docking point 在 NavMesh 上直線相通，否則退回 docking point
        if (!NavMesh.SamplePosition(dockPos, out NavMeshHit d, navSampleMaxDistance, NavMesh.AllAreas)) return dockPos;
        if (!NavMesh.SamplePosition(goal, out NavMeshHit g, dockSpreadRadius, NavMesh.AllAreas)) return dockPos;
        if (NavMesh.Raycast(d.position, g.position, out _, NavMesh.AllAreas)) return dockPos;
        return g.position;
    }

    /// <summary>
    /// 把地面路徑的中間轉角點沿垂直方向偏移 laneValue × laneWidth。起點和終點不動。
    /// 長直線段先插中點。每個偏移點都用 NavMesh.Raycast 檢查進出兩段是否通暢。
    /// </summary>
    private Vector3[] ApplyLaneSpread(Vector3[] corners)
    {
        using var _m = s_LaneSpread.Auto();
        if (corners == null || corners.Length < 2) return corners;
        if (laneWidth <= 0f || Mathf.Abs(laneValue) < 0.001f) return corners;

        Vector3[] c = (splitSegmentLength > 0f) ? SplitLongSegments(corners) : corners;
        int n = c.Length;
        if (n < 3) return c;

        // 每個點到終點的剩餘路程（水平），用來決定偏移收回多少
        float[] remain = new float[n];
        for (int i = n - 2; i >= 0; i--)
            remain[i] = remain[i + 1] + FlatDistance(c[i], c[i + 1]);

        Vector3[] outArr = (Vector3[])c.Clone();
        for (int i = 1; i < n - 1; i++)
        {
            float scale = Mathf.Clamp01(remain[i] / laneFadeDistance);
            if (scale <= 0f) continue;

            Vector3 dIn = FlatDir(c[i - 1], c[i]);
            Vector3 dOut = FlatDir(c[i], c[i + 1]);
            Vector3 normal = Perp(dIn) + Perp(dOut);
            if (normal.sqrMagnitude < 0.0001f) normal = Perp(dIn);   // 180° 折返
            if (normal.sqrMagnitude < 0.0001f) continue;
            normal.Normalize();

            Vector3 offset = normal * (laneValue * laneWidth * scale);
            Vector3 prev = outArr[i - 1];
            Vector3 next = c[i + 1];

            // 全額 → 半額 → 放棄（用原本的轉角點）
            for (int attempt = 0; attempt < 2; attempt++)
            {
                Vector3 candidate = c[i] + offset;
                if (!NavMesh.Raycast(prev, candidate, out _, NavMesh.AllAreas) &&
                    !NavMesh.Raycast(candidate, next, out _, NavMesh.AllAreas))
                {
                    outArr[i] = candidate;
                    break;
                }
                offset *= 0.5f;
            }
        }
        return outArr;
    }

    /// <summary>長於 splitSegmentLength 的線段在中點插一個點。沒有要插的就回傳原陣列。</summary>
    private Vector3[] SplitLongSegments(Vector3[] c)
    {
        int extra = 0;
        for (int i = 0; i < c.Length - 1; i++)
            if (FlatDistance(c[i], c[i + 1]) > splitSegmentLength) extra++;
        if (extra == 0) return c;

        var outArr = new Vector3[c.Length + extra];
        int k = 0;
        for (int i = 0; i < c.Length - 1; i++)
        {
            outArr[k++] = c[i];
            if (FlatDistance(c[i], c[i + 1]) > splitSegmentLength)
                outArr[k++] = (c[i] + c[i + 1]) * 0.5f;
        }
        outArr[k] = c[c.Length - 1];
        return outArr;
    }

    private static float FlatDistance(Vector3 a, Vector3 b)
    {
        float dx = b.x - a.x, dz = b.z - a.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static Vector3 FlatDir(Vector3 a, Vector3 b)
    {
        Vector3 d = b - a;
        d.y = 0f;
        float m = d.magnitude;
        return (m > 0.0001f) ? d / m : Vector3.zero;
    }

    /// <summary>水平方向的右手垂直向量。</summary>
    private static Vector3 Perp(Vector3 d) => new Vector3(d.z, 0f, -d.x);

    private void OnDrawGizmos()
    {
        if (!drawPathGizmo) return;
        if (!Application.isPlaying) return;   // 編輯模式沒有即時 path，避免雜訊 / 空引用

        // ★ 用即時投影版：船上路徑會貼著船跑，不再停在舊位置
        Vector3[] draw = GetCurrentWorldPath();
        if (draw == null || draw.Length == 0) return;

        Gizmos.color = pathColor;

        for (int i = 0; i < draw.Length; i++)
            Gizmos.DrawSphere(draw[i], waypointGizmoRadius);

        for (int i = 0; i < draw.Length - 1; i++)
            Gizmos.DrawLine(draw[i], draw[i + 1]);

        Gizmos.DrawLine(transform.position, draw[0]);
    }
    private Vector3 GetDisembarkPoint(Vector3 dockPos)
    {
        Transform ship = RealShipRoot;
        if (ship == null) return dockPos;   // 沒有 root 就沒有「往船外」的方向可言

        Vector3 outward = dockPos - ship.position;
        outward.y = 0f;
        if (outward.sqrMagnitude < 0.0001f) outward = ship.forward;   // dock 在船中心的退路
        outward.Normalize();

        Vector3 candidate = dockPos + outward * disembarkDistance;

        // 貼回地面 navmesh（船外是地面 navmesh 的領域）
        if (UnityEngine.AI.NavMesh.SamplePosition(candidate, out UnityEngine.AI.NavMeshHit hit,
                navSampleMaxDistance, UnityEngine.AI.NavMesh.AllAreas))
            return hit.position;

        return candidate;   // sample 失敗就用原始點，至少方向是對的
    }
}