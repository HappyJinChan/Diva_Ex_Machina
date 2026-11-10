using UnityEngine;

/// <summary>
/// 子彈的「殘留光束」：子彈消失（命中、飛完）之後，在它飛過的路徑上留下一條直線，
/// 在 Duration 秒內一邊變寬、一邊淡出 —— 像電磁砲在空氣裡留下的電離痕跡。
///
/// 為什麼需要它：BulletTracer 每一幀只畫「發射點 → 子彈目前位置」，子彈一消失線就跟著消失。
/// 高速子彈（例如恐狼的蓄力彈，350 m/s）只在畫面上停留十幾格，玩家來不及看清楚。
/// 這裡把那條線在子彈消失的瞬間「留下來」，玩家就算錯過子彈本體，也看得到它打過來的軌跡。
///
/// 用法：掛在子彈 prefab 上（跟 Bullet 同一個物件）。
/// 預設沿用 TrailRenderer 的材質、寬度與顏色（取漸層最靠子彈頭那一端的顏色），
/// 材質要用支援頂點色 alpha 的 shader（例如粒子用的 Additive），淡出才會有效。
///
/// 只適合低頻率的子彈（蓄力彈、狙擊）：每一發都會 new 一個 GameObject，沒有接物件池。
/// 光束留在世界座標，不跟船移動 —— 敵人從地面開火沒有問題；不建議掛在玩家在船上射擊的子彈上。
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Bullet))]
public class BulletResidualBeam : MonoBehaviour
{
    [Header("Timing")]
    [Tooltip("殘留光束從出現到完全消失的秒數。")]
    [Min(0.01f)] public float duration = 0.5f;

    [Header("Width")]
    [Tooltip("光束一開始的寬度（公尺）。0 = 沿用 TrailRenderer 的 Width Multiplier。")]
    [Min(0f)] public float width = 0f;
    [Tooltip("消失時寬度變成幾倍。> 1 = 邊消失邊擴散。")]
    [Min(0f)] public float endWidthScale = 3f;

    [Header("Look")]
    [Tooltip("材質。留空 = 沿用 TrailRenderer 的材質。")]
    public Material material;
    [Tooltip("勾選 = 用 TrailRenderer 漸層最靠子彈頭那一端的顏色；不勾 = 用下面的 Color。")]
    public bool useTrailColor = true;
    [ColorUsage(true, true)]
    public Color color = Color.white;

    [Header("Filter")]
    [Tooltip("路徑短於這個長度（公尺）就不留光束（例如貼臉直接命中）。")]
    [Min(0f)] public float minLength = 2f;

    private Bullet _bullet;
    private TrailRenderer _trail;
    private bool _tracking;
    private Vector3 _start;

    private static bool s_quitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        s_quitting = false;
        Application.quitting -= OnQuitting;
        Application.quitting += OnQuitting;
    }

    private static void OnQuitting() => s_quitting = true;

    private void Awake()
    {
        _bullet = GetComponent<Bullet>();
        _trail = GetComponent<TrailRenderer>();
        if (_trail == null) _trail = GetComponentInChildren<TrailRenderer>(true);
    }

    private void OnEnable()
    {
        // 物件池重新上場：不要沿用上一世的起點
        _tracking = false;
    }

    private void LateUpdate()
    {
        if (_bullet == null || !_bullet.IsLive) return;

        // 生成那一幀的 LateUpdate 還沒跑過 physics step，位置就是槍口
        if (!_tracking)
        {
            _tracking = true;
            _start = transform.position;
        }
    }

    private void OnDisable()
    {
        if (!_tracking) return;
        _tracking = false;

        // 關閉遊戲、卸載場景時不要生成新物件
        if (s_quitting || !gameObject.scene.isLoaded) return;

        Vector3 end = transform.position;   // 命中時 Bullet 會把位置移到命中點
        if ((end - _start).sqrMagnitude < minLength * minLength) return;

        SpawnBeam(_start, end);
    }

    private void SpawnBeam(Vector3 start, Vector3 end)
    {
        var go = new GameObject("ResidualBeam");
        go.layer = gameObject.layer;

        var line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = 2;
        line.SetPosition(0, end);
        line.SetPosition(1, start);
        line.widthCurve = AnimationCurve.Constant(0f, 1f, 1f);
        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        line.receiveShadows = false;

        Material mat = material != null ? material : (_trail != null ? _trail.sharedMaterial : null);
        if (mat != null) line.sharedMaterial = mat;

        if (_trail != null)
        {
            line.textureMode = _trail.textureMode;
            line.alignment = _trail.alignment;
            line.numCapVertices = _trail.numCapVertices;
            line.sortingLayerID = _trail.sortingLayerID;
            line.sortingOrder = _trail.sortingOrder;
        }

        float w = width > 0f ? width : (_trail != null ? _trail.widthMultiplier : 0.5f);
        Color c = (useTrailColor && _trail != null) ? _trail.colorGradient.Evaluate(0f) : color;

        go.AddComponent<ResidualBeamFade>().Play(line, c, w, w * endWidthScale, duration);
    }
}
