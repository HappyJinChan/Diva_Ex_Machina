using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 小地圖上的圓圈（支援範圍、炮擊範圍…）。用程式畫：圓心、半徑可以任意設定，線條粗細固定。
/// 半徑可以比小地圖本身大很多（例如 3 km 的支援範圍），超出的部分會被 Mask 裁掉。
///
/// 放法：放在小地圖 Mask 底下，RectTransform 四角撐滿（Anchor 0～1、Offset 0、Pivot 0.5）。
/// 圓心座標是相對於這個 RectTransform 中心的像素位置，由 Minimap 每幀設定。
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
public class MinimapRing : MaskableGraphic
{
    [Tooltip("線條粗細（像素）。")]
    [SerializeField, Min(0.1f)] private float thickness = 2f;

    [Tooltip("每段圓弧最長多少像素切一段。越小越圓滑。")]
    [SerializeField, Min(1f)] private float segmentLength = 6f;

    private Vector2 _center;
    private float _radius;

    /// <summary>設定圓心（相對中心的像素）與半徑（像素）。變化很小時不重建網格。</summary>
    public void Set(Vector2 center, float radius)
    {
        if ((center - _center).sqrMagnitude < 0.04f && Mathf.Abs(radius - _radius) < 0.2f) return;
        _center = center;
        _radius = radius;
        SetVerticesDirty();
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();
        if (_radius <= 0f) return;

        float rIn = Mathf.Max(0f, _radius - thickness * 0.5f);
        float rOut = _radius + thickness * 0.5f;
        int n = Mathf.Clamp(Mathf.CeilToInt(2f * Mathf.PI * _radius / segmentLength), 24, 4096);

        Color32 c = color;
        for (int i = 0; i <= n; i++)
        {
            float a = i * Mathf.PI * 2f / n;
            Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            vh.AddVert(_center + dir * rIn, c, Vector4.zero);
            vh.AddVert(_center + dir * rOut, c, Vector4.zero);

            if (i > 0)
            {
                int k = i * 2;
                vh.AddTriangle(k - 2, k - 1, k + 1);
                vh.AddTriangle(k - 2, k + 1, k);
            }
        }
    }
}
