using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 在地上畫一個圓圈，顯示迫擊砲炮擊的範圍。
///
/// 顯示時機：
///   · 鎖定模式按住右鍵（範圍炮擊）時：跟著準星、半徑慢慢變大（用預覽顏色）。
///   · 範圍炮擊開始 → 最後一顆砲彈落地為止，用圈的半徑。
///     鎖定目標的炮擊不在這裡畫（每個目標的圈由 MortarLockMarkers 畫）。
///     不是從鎖定模式發起的炮擊（RequestMortarBarrage，測試用）照舊畫在目標上。
///   （可選）Show Preview：沒在炮擊時，也在「現在按下會打的地方」顯示（顏色可以另外設定）。
/// 位置：炮擊的目標點 —— 跟著鎖定的敵人；敵人死掉、或打的是甲板上的點時，跟著船走（SupplyManager.TryGetBarrageCenter）。
/// 半徑：散布半徑 + 爆炸半徑（自動讀船上第一座迫擊砲的砲彈 prefab），或手動指定。
/// 貼地：圓上每一點往下打射線，貼著地形 / 甲板畫，不會插進斜坡或懸在空中。
///
/// 設定：建一個空物件（例如 Landship 的子物件 "Barrage Area"），加這個元件（會自動加 LineRenderer）。
///   LineRenderer 的材質、寬度、顏色在 LineRenderer 上設（例如 Unlit / Additive 材質，寬度 0.5～1）。
///   Supply Manager 留空會自動找。
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class BarrageAreaIndicator : MonoBehaviour
{
    [SerializeField] private SupplyManager supplyManager;

    [Header("Radius")]
    [Tooltip("圓圈半徑（公尺）。0 = 自動：砲彈的散布半徑（+ 爆炸半徑）。")]
    [SerializeField, Min(0f)] private float radiusOverride = 0f;

    [Tooltip("自動半徑時加上爆炸半徑（= 可能被炸到的範圍）。關掉 = 只顯示落點散布範圍。")]
    [SerializeField] private bool includeBlastRadius = true;

    [Header("Ground")]
    [Tooltip("圓圈會貼在這些層上（地面、甲板、建築）。")]
    [SerializeField] private LayerMask groundLayers = ~0;

    [Tooltip("離地高度（公尺），避免跟地面 z-fighting。")]
    [SerializeField, Min(0f)] private float heightOffset = 0.3f;

    [Tooltip("往下找地面的最遠距離（公尺）。")]
    [SerializeField, Min(1f)] private float probeDistance = 200f;

    [Tooltip("圓圈的點數。")]
    [SerializeField, Range(16, 256)] private int segments = 96;

    [Header("Preview（沒在炮擊時）")]
    [Tooltip("沒在炮擊時，也在「現在按下會打的地方」顯示圓圈。")]
    [SerializeField] private bool showPreview = false;

    [Tooltip("預覽時的顏色（乘上 LineRenderer 原本的顏色）。")]
    [SerializeField] private Color previewTint = new Color(1f, 1f, 1f, 0.35f);

    [Tooltip("預覽時，冷卻中也顯示。")]
    [SerializeField] private bool previewDuringCooldown = false;

    private LineRenderer _line;
    private Vector3[] _points;
    private Gradient _baseGradient;
    private Gradient _previewGradient;
    private bool _showingPreview;
    private float _autoRadius = -1f;

    private void Reset()
    {
        groundLayers = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Defence Fortifications", "Obstacle");
        if (groundLayers.value == 0) groundLayers = ~0;
    }

    private void Awake()
    {
        _line = GetComponent<LineRenderer>();
        _line.useWorldSpace = true;
        _line.loop = true;
        _line.enabled = false;

        _baseGradient = _line.colorGradient;
        _previewGradient = TintGradient(_baseGradient, previewTint);
    }

    private void LateUpdate()
    {
        if (supplyManager == null) supplyManager = SupplyManager.Instance;
        if (supplyManager == null) { Hide(); return; }

        // 鎖定模式的範圍瞄準（右鍵按住中）
        if (supplyManager.TryGetAreaAim(out Vector3 areaCenter, out float areaRadius))
        {
            Draw(areaCenter, areaRadius, preview: true);
            return;
        }

        // 炮擊中（或還有砲彈在空中）→ 顯示炮擊的目標點
        bool barrage = supplyManager.IsBarrageActive || MortarShell.LiveCount > 0;
        if (barrage && !supplyManager.IsTargetLockBarrage && supplyManager.TryGetBarrageCenter(out Vector3 center))
        {
            float r = supplyManager.BarrageAreaRadius > 0f ? supplyManager.BarrageAreaRadius : Radius;
            Draw(center, r, preview: false);
            return;
        }

        // 預覽：現在按下會打的地方
        if (showPreview && !barrage &&
            (previewDuringCooldown || supplyManager.CooldownRemaining <= 0f) &&
            supplyManager.TryGetPlayerTarget(out Transform t, out AimSpot spot))
        {
            Vector3 p = t != null ? TurretController.GetAimPoint(t) : spot.Position;
            Draw(p, Radius, preview: true);
            return;
        }

        Hide();
    }

    private void Draw(Vector3 center, float r, bool preview)
    {
        if (r <= 0f) { Hide(); return; }

        if (preview != _showingPreview || !_line.enabled)
        {
            _showingPreview = preview;
            _line.colorGradient = preview ? _previewGradient : _baseGradient;
        }

        int n = Mathf.Max(16, segments);
        if (_points == null || _points.Length != n) _points = new Vector3[n];

        // 圓心也貼地：當作沒打到地面時的高度
        float baseY = Ground(center, center.y);

        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            Vector3 p = center + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
            p.y = Ground(p, baseY);
            _points[i] = p;
        }

        _line.positionCount = n;
        _line.SetPositions(_points);
        _line.enabled = true;
    }

    // 從上方往下打射線，回傳地面高度 + 離地高度；沒打到就用 fallbackY
    private float Ground(Vector3 p, float fallbackY)
    {
        Vector3 origin = new Vector3(p.x, p.y + probeDistance * 0.5f, p.z);
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, probeDistance, groundLayers, QueryTriggerInteraction.Ignore))
            return hit.point.y + heightOffset;
        return fallbackY + heightOffset;
    }

    private void Hide()
    {
        if (_line != null && _line.enabled) _line.enabled = false;
    }

    private float Radius
    {
        get
        {
            if (radiusOverride > 0f) return radiusOverride;
            if (_autoRadius < 0f) _autoRadius = FindAutoRadius();
            return _autoRadius;
        }
    }

    // 船上第一座迫擊砲的砲彈 prefab：散布半徑（+ 爆炸半徑）。找不到就先不快取，下次再找。
    private float FindAutoRadius()
    {
        IReadOnlyList<MortarBrain> all = MortarBrain.All;
        for (int i = 0; i < all.Count; i++)
        {
            MortarBrain m = all[i];
            if (m == null || m.Turret == null || m.Turret.bulletPrefab == null) continue;
            if (!m.Turret.bulletPrefab.TryGetComponent(out MortarShell shell)) continue;

            return shell.ScatterRadius + (includeBlastRadius ? shell.Radius : 0f);
        }
        return -1f;
    }

    private static Gradient TintGradient(Gradient g, Color tint)
    {
        var colorKeys = g.colorKeys;
        var alphaKeys = g.alphaKeys;
        for (int i = 0; i < colorKeys.Length; i++)
            colorKeys[i].color *= new Color(tint.r, tint.g, tint.b, 1f);
        for (int i = 0; i < alphaKeys.Length; i++)
            alphaKeys[i].alpha *= tint.a;

        var result = new Gradient { mode = g.mode };
        result.SetKeys(colorKeys, alphaKeys);
        return result;
    }
}
