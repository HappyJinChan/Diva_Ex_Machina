using UnityEngine;
using UnityEngine.AI;

public class PathFinder : MonoBehaviour, IPooled
{
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
    void Start()
    {
        path = new NavMeshPath();
    }

    // 物件池重新上場：清掉上一輩子的船上狀態與路徑。
    public void OnSpawned()
    {
        isOnShip = false;
        isTargetOnShip = false;
        lastPathOnShip = false;
        if (path != null) path.ClearCorners();
    }

    public void OnDespawned() { }

    public Vector3[] FindPath(Vector3 targetLocation)
    {
        Vector3[] result;
        lastPathOnShip = false;   // 預設地面；下面命中 ghost 分支才設 true

        if (isOnShip && isTargetOnShip)// if both side on ship, convert gobal coordinates to ship local coordinates
        {
            result = ComputeGhostPath(transform.position, targetLocation);
            lastPathOnShip = true;

            if (result.Length == 0)
                result = BuildFallbackPath(targetLocation, true);
        }
        else if (isOnShip && !isTargetOnShip)// if entity on ship but target not on ship
        {
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
        else if (!isOnShip && isTargetOnShip) // if player not on ship but target on ship, find path to ship docking point
        {
            Vector3 dockPos = GetClosestDockingLocation();
            result = ComputeGroundPath(transform.position, dockPos);

            if (result.Length == 0)
                result = BuildFallbackPath(dockPos, false);
        }
        else
        {
            result = ComputeGroundPath(transform.position, targetLocation);

            if (result.Length == 0)
                result = BuildFallbackPath(targetLocation, false);
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

        NavMesh.CalculatePath(s.position, e.position, NavMesh.AllAreas, path);

        // corners 是 property，每次存取都配一個新陣列 —— 原本這裡存取了兩次，取一次就好
        Vector3[] corners = path.corners;
        if (path.status == NavMeshPathStatus.PathInvalid || corners.Length == 0)
            return System.Array.Empty<Vector3>();

        return corners;
    }

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