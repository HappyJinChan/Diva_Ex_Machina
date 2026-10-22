using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// 迫擊砲「對敵鎖定」的標記：每個鎖定的目標周圍畫一個圈（預設半徑 5 m），跟著目標移動、貼著地面。
///
/// 顯示時機：鎖定模式按住左鍵、正在鎖定時；（可選）放開後炮擊進行中 / 砲彈還在空中時，也畫在還活著的目標上。
///
/// 設定：建一個空物件（例如 Landship 的子物件 "Lock Markers"），加這個元件（會自動加 LineRenderer）。
///   這個 LineRenderer 是「樣板」，本身不顯示：材質、寬度、顏色在它上面設，每個圈都會複製它的外觀。
///   Supply Manager 留空會自動找。
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class MortarLockMarkers : MonoBehaviour
{
    [SerializeField] private SupplyManager supplyManager;

    [Tooltip("圈的半徑（公尺）。")]
    [SerializeField, Min(0.5f)] private float radius = 5f;

    [Tooltip("放開左鍵、開始炮擊後，繼續在目標上顯示，直到最後一顆砲彈落地。")]
    [SerializeField] private bool showDuringBarrage = true;

    [Header("Ground")]
    [Tooltip("圈會貼在這些層上（地面、甲板、建築）。")]
    [SerializeField] private LayerMask groundLayers = ~0;

    [Tooltip("離地高度（公尺），避免跟地面 z-fighting。")]
    [SerializeField, Min(0f)] private float heightOffset = 0.3f;

    [Tooltip("往下找地面的最遠距離（公尺）。")]
    [SerializeField, Min(1f)] private float probeDistance = 60f;

    [Tooltip("每個圈的點數。")]
    [SerializeField, Range(12, 128)] private int segments = 40;

    private LineRenderer _template;
    private readonly List<LineRenderer> _lines = new List<LineRenderer>();
    private readonly List<Transform> _targets = new List<Transform>();
    private Vector3[] _points;

    private void Reset()
    {
        groundLayers = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Defence Fortifications", "Obstacle");
        if (groundLayers.value == 0) groundLayers = ~0;
    }

    private void Awake()
    {
        _template = GetComponent<LineRenderer>();
        _template.enabled = false;   // 樣板本身不畫
    }

    private void LateUpdate()
    {
        if (supplyManager == null) supplyManager = SupplyManager.Instance;
        int count = supplyManager != null ? supplyManager.GetMarkedTargets(_targets, showDuringBarrage) : 0;

        while (_lines.Count < count) _lines.Add(CreateLine());

        for (int i = 0; i < _lines.Count; i++)
        {
            if (i < count) Draw(_lines[i], _targets[i].position);
            else if (_lines[i].enabled) _lines[i].enabled = false;
        }
    }

    private void Draw(LineRenderer line, Vector3 center)
    {
        int n = Mathf.Max(12, segments);
        if (_points == null || _points.Length != n) _points = new Vector3[n];

        // 圓心也貼地：當作沒打到地面時的高度（目標在空中時，圈畫在它腳下的地面上）
        float baseY = Ground(center, center.y);

        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            Vector3 p = center + new Vector3(Mathf.Cos(a) * radius, 0f, Mathf.Sin(a) * radius);
            p.y = Ground(p, baseY);
            _points[i] = p;
        }

        line.positionCount = n;
        line.SetPositions(_points);
        if (!line.enabled) line.enabled = true;
    }

    // 從上方往下打射線，回傳地面高度 + 離地高度；沒打到就用 fallbackY
    private float Ground(Vector3 p, float fallbackY)
    {
        Vector3 origin = new Vector3(p.x, p.y + probeDistance * 0.5f, p.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, probeDistance, groundLayers, QueryTriggerInteraction.Ignore))
            return hit.point.y + heightOffset;
        return fallbackY + heightOffset;
    }

    // 複製樣板 LineRenderer 的外觀
    private LineRenderer CreateLine()
    {
        var go = new GameObject("Lock Marker");
        go.transform.SetParent(transform, false);
        var line = go.AddComponent<LineRenderer>();

        line.sharedMaterials = _template.sharedMaterials;
        line.widthMultiplier = _template.widthMultiplier;
        line.widthCurve = _template.widthCurve;
        line.colorGradient = _template.colorGradient;
        line.numCornerVertices = _template.numCornerVertices;
        line.numCapVertices = _template.numCapVertices;
        line.alignment = _template.alignment;
        line.textureMode = _template.textureMode;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.receiveShadows = false;
        line.useWorldSpace = true;
        line.loop = true;
        line.enabled = false;
        return line;
    }
}
