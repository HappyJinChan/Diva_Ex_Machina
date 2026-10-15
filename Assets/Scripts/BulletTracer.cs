using UnityEngine;

/// <summary>
/// 子彈的曳光：每一幀從子彈目前的位置往後畫一條直線。
///
/// 取代 TrailRenderer 的原因：TrailRenderer 把經過的位置留在世界座標裡，
/// 玩家站在行進中的船上開火時，鏡頭跟著船走，留在原地的頂點在畫面上會往後滑，
/// 看起來子彈不是從槍口射出來的；想把那些頂點跟著船搬動，又會被渲染順序與
/// Rigidbody 插值干擾，變成鋸齒或彎鉤。
///
/// 這裡不留任何歷史，每一幀重算：
///   頭 = 子彈目前的位置
///   方向 = 子彈速度 − 發射平台的速度（相對於船的飛行方向；不在船上時就是子彈速度）
///   長度 = 相對速度 × lengthTime，而且不超過「頭到發射點」的距離（發射點跟著船移動）
/// 所以永遠是一條從槍口方向延伸過來的直線，幀數高低、船怎麼開都一樣。
///
/// 用法：掛在子彈 prefab 上（跟 Bullet 同一個物件）。TrailRenderer 可以在同一個物件或子物件上。
/// 外觀（材質、寬度曲線、顏色漸層）直接沿用同一個物件上的 TrailRenderer 設定，
/// TrailRenderer 的 Time 就是這裡的長度秒數；啟動後 TrailRenderer 本身會被關掉。
/// 想調外觀，照舊調 TrailRenderer 的欄位即可。
/// </summary>
[DisallowMultipleComponent]
public class BulletTracer : MonoBehaviour
{
    [Tooltip("曳光長度 = 相對速度 × 這個秒數。\n有 TrailRenderer 時會用它的 Time 覆蓋這個值。")]
    [Min(0f)] public float lengthTime = 0.04f;

    [Tooltip("曳光長度上限（公尺）。0 = 不限制。高速武器（例如狙擊槍）的曳光太長時用這個壓住。")]
    [Min(0f)] public float maxLength = 0f;

    [Tooltip("沿用 TrailRenderer 的外觀設定（材質、寬度、顏色），並關掉 TrailRenderer。")]
    public bool copyFromTrailRenderer = true;

    private Bullet _bullet;
    private Rigidbody _rb;
    private LineRenderer _line;
    private readonly Vector3[] _points = new Vector3[2];

    private void Awake()
    {
        _bullet = GetComponent<Bullet>();
        _rb = GetComponent<Rigidbody>();

        // LineRenderer 放在子物件上：避免跟同一個物件上的其他 Renderer（TrailRenderer、MeshRenderer）衝突。
        // 用世界座標，所以子物件本身的位置不重要。
        var go = new GameObject("Tracer");
        go.layer = gameObject.layer;
        go.transform.SetParent(transform, false);

        _line = go.AddComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.positionCount = 2;
        _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _line.receiveShadows = false;
        _line.enabled = false;

        // 先找自己身上的；沒有的話找子物件（例如子彈裡套了特效 prefab，TrailRenderer 在特效裡面）
        var trail = GetComponent<TrailRenderer>();
        if (trail == null) trail = GetComponentInChildren<TrailRenderer>(true);
        if (copyFromTrailRenderer && trail != null)
        {
            lengthTime = trail.time;
            _line.sharedMaterials = trail.sharedMaterials;
            _line.widthMultiplier = trail.widthMultiplier;
            _line.widthCurve = trail.widthCurve;       // 曲線 0 = 頭（子彈這一端），跟 TrailRenderer 相同
            _line.colorGradient = trail.colorGradient;
            _line.textureMode = trail.textureMode;
            _line.alignment = trail.alignment;
            _line.numCapVertices = trail.numCapVertices;
            _line.numCornerVertices = trail.numCornerVertices;
            _line.sortingLayerID = trail.sortingLayerID;
            _line.sortingOrder = trail.sortingOrder;

            trail.enabled = false;
        }
    }

    private void OnEnable()
    {
        // 物件池重新上場時，第一幀還沒算好之前不要畫出上一世的線
        if (_line != null) _line.enabled = false;
    }

    private void LateUpdate()
    {
        if (_line == null) return;

        // 子彈還沒顯示（生成後第一個 physics step 之前）時，曳光也先不畫 —— 理由見 Bullet.IsRevealed
        if (_bullet != null && (!_bullet.IsLive || !_bullet.IsRevealed))
        {
            _line.enabled = false;
            return;
        }

        Vector3 head = transform.position;

        Vector3 velocity = (_rb != null) ? _rb.linearVelocity : Vector3.zero;
        if (_bullet != null) velocity -= _bullet.PlatformVelocity;

        float speed = velocity.magnitude;
        if (speed < 0.01f)
        {
            _line.enabled = false;
            return;
        }

        float length = speed * lengthTime;
        if (maxLength > 0f) length = Mathf.Min(length, maxLength);

        // 尾端不超過發射點：剛射出時曳光從槍口長出來，而不是一出現就是完整長度、尾巴伸到身後
        if (_bullet != null)
            length = Mathf.Min(length, Vector3.Distance(head, _bullet.SpawnPoint));

        if (length < 0.01f)
        {
            _line.enabled = false;
            return;
        }

        _points[0] = head;
        _points[1] = head - velocity / speed * length;
        _line.SetPositions(_points);
        _line.enabled = true;
    }
}
