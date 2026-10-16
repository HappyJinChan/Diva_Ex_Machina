using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 用程式畫的鎖定圈 / X 字準星。取代原本被拉伸的 Image：
/// 圈的大小跟著 RectTransform 走（PlayerAiming.SetAimAreaSize 改 sizeDelta 就會跟著變），
/// 但線條粗細、圈與圈的間距、缺口寬度都固定不變。
///
/// 兩種圖形，可以只開其中一種：
///   · Rings：同心圓環。每一層的位置用「距離外框幾像素」指定，所以圈放大時層與層的間距不變。
///            四個方向各有一道固定寬度的缺口（跟原圖一樣是筆直的切口）。
///   · Cross：四條 45 度斜線（X 字）。位置用「佔半徑的比例」指定，跟原圖一樣會跟著這個物件的大小縮放，
///            只有粗細固定。原本的 X 字是固定大小的另一張圖，就另外放一個這個元件、只開 Cross。
///
/// 所有像素數值都是「原圖 512×512」上的尺寸，再乘上 Reference Size / 512：
///   把 Reference Size 設成「看起來跟原圖一樣」的那個大小（例如預設的鎖定範圍），
///   在那個大小時畫出來跟原圖完全一致；大小改變時，粗細維持那時的樣子。
///
/// 放法：Image 跟這個元件不能放在同一個物件上（都是 Graphic）。
///   在原本的鎖定圈 Image 底下建一個子物件，RectTransform 四角撐滿（Anchor 0～1、Offset 0），
///   掛這個元件，再把原本的 Image 元件關掉（PlayerAiming 只讀它的 RectTransform 大小，關掉不影響鎖定判斷）。
/// </summary>
[RequireComponent(typeof(CanvasRenderer))]
[ExecuteAlways]
public class LockRingGraphic : MaskableGraphic
{
    [System.Serializable]
    public class Ring
    {
        [Tooltip("圓環外緣距離 RectTransform 外框幾像素（原圖尺寸）。0 = 貼著外框；負數 = 超出外框。")]
        public float inset = 0f;

        [Tooltip("圓環粗細（像素，原圖尺寸）。")]
        [Min(0.1f)] public float thickness = 2f;

        [Tooltip("上下左右四道缺口的半寬（像素，原圖尺寸）。缺口是筆直的帶狀切口。0 = 完整的圓。")]
        [Min(0f)] public float gapHalfWidth = 35f;

        [Tooltip("這一層的透明度（乘在 Graphic 的顏色上）。")]
        [Range(0f, 1f)] public float alpha = 1f;
    }

    [Header("Scale")]
    [Tooltip("在這個大小（RectTransform 寬度）時，畫出來跟 512×512 的原圖完全一樣。\n" +
             "所有像素數值都會乘上 Reference Size / 512，而且不會隨實際大小改變。")]
    [Min(1f)] [SerializeField] private float referenceSize = 512f;

    [Tooltip("邊緣的柔化寬度（螢幕像素，不受 Reference Size 影響）。1 左右可以消除鋸齒。")]
    [Min(0f)] [SerializeField] private float feather = 1f;

    [Tooltip("每段圓弧最長多少像素切一段。越小越圓滑。")]
    [Min(1f)] [SerializeField] private float segmentLength = 4f;

    [Header("Rings")]
    [SerializeField] private bool drawRings = true;

    // 預設值量自原本的鎖定圈圖（512×512）：外圈粗 11.2、外緣超出外框 2.9（原圖在四個缺口旁被圖片邊緣切掉一點）；
    // 內圈粗 2.8、外緣離外框 41.7；缺口寬 70（上下左右都一樣）。
    [SerializeField] private List<Ring> rings = new List<Ring>
    {
        new Ring { inset = -2.9f, thickness = 11.2f, gapHalfWidth = 35f, alpha = 1f },
        new Ring { inset = 41.7f, thickness = 2.8f, gapHalfWidth = 35f, alpha = 1f },
    };

    [Header("Cross (X)")]
    [SerializeField] private bool drawCross = false;

    // 預設值量自原本的 X 字圖（512×512）：粗 7；內端離中心 78（= 半徑的 0.305）；外端到圖的角落（= 半徑的 1.397）。
    [Tooltip("X 字線的粗細（像素，原圖尺寸）。")]
    [Min(0.1f)] [SerializeField] private float crossThickness = 7f;

    [Tooltip("內端離中心的距離，佔半寬的比例（原圖 0.305）。")]
    [Min(0f)] [SerializeField] private float crossInner = 0.305f;

    [Tooltip("外端離中心的距離，佔半寬的比例（原圖 1.397 = 正好到正方形的角落）。")]
    [Min(0f)] [SerializeField] private float crossOuter = 1.397f;

    [Tooltip("X 字這幾條線的透明度（乘在 Graphic 的顏色上）。")]
    [Range(0f, 1f)] [SerializeField] private float crossAlpha = 1f;

    private const float ArtSize = 512f;

    protected override void Awake()
    {
        base.Awake();
        raycastTarget = false;   // 準星不該擋住 UI 點擊
    }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        vh.Clear();

        Rect r = GetPixelAdjustedRect();
        Vector2 center = r.center;
        float half = Mathf.Min(r.width, r.height) * 0.5f;
        if (half <= 0.01f) return;

        float px = referenceSize / ArtSize;   // 原圖像素 → UI 像素（固定，不隨大小改變）

        if (drawRings && rings != null)
        {
            foreach (Ring ring in rings)
            {
                if (ring == null) continue;
                float outer = half - ring.inset * px;
                float inner = outer - ring.thickness * px;
                if (outer <= 0f) continue;
                inner = Mathf.Max(0f, inner);
                AddRing(vh, center, inner, outer, ring.gapHalfWidth * px, MulAlpha(color, ring.alpha));
            }
        }

        if (drawCross)
        {
            float t = crossThickness * px;
            Color32 c = MulAlpha(color, crossAlpha);
            for (int q = 0; q < 4; q++)
            {
                float a = (45f + 90f * q) * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                AddLine(vh, center + dir * (crossInner * half), center + dir * (crossOuter * half), t, c);
            }
        }
    }

    // ───────── 圓環：四個象限各一段圓弧，缺口是固定寬度的筆直帶狀切口 ─────────

    private void AddRing(VertexHelper vh, Vector2 c, float rIn, float rOut, float gapHalf, Color32 col)
    {
        float f = feather;
        Color32 clear = col; clear.a = 0;

        // 由外到內四個半徑：外柔邊、外緣、內緣、內柔邊
        float rOutF = rOut + f * 0.5f, rOutS = rOut - f * 0.5f;
        float rInS = rIn + f * 0.5f, rInF = Mathf.Max(0f, rIn - f * 0.5f);
        if (rOutS < rInS) { float m = (rOut + rIn) * 0.5f; rOutS = rInS = m; }   // 比柔化寬度還細時

        for (int q = 0; q < 4; q++)
        {
            float baseDeg = 90f * q;

            // 每個半徑各自算缺口的角度，切口才會是筆直的（跟原圖的帶狀缺口一樣），不是放射狀
            if (!ArcRange(rOutF, gapHalf, out float aOF0, out float aOF1)) continue;
            ArcRange(rOutS, gapHalf, out float aOS0, out float aOS1);
            ArcRange(rInS, gapHalf, out float aIS0, out float aIS1);
            ArcRange(rInF, gapHalf, out float aIF0, out float aIF1);

            float arcLen = (aOF1 - aOF0) * rOutF;
            int seg = Mathf.Clamp(Mathf.CeilToInt(arcLen / Mathf.Max(1f, segmentLength)), 2, 256);

            int start = vh.currentVertCount;
            for (int i = 0; i <= seg; i++)
            {
                float k = i / (float)seg;
                vh.AddVert(Polar(c, rOutF, baseDeg, Mathf.Lerp(aOF0, aOF1, k)), clear, Vector4.zero);
                vh.AddVert(Polar(c, rOutS, baseDeg, Mathf.Lerp(aOS0, aOS1, k)), col, Vector4.zero);
                vh.AddVert(Polar(c, rInS, baseDeg, Mathf.Lerp(aIS0, aIS1, k)), col, Vector4.zero);
                vh.AddVert(Polar(c, rInF, baseDeg, Mathf.Lerp(aIF0, aIF1, k)), clear, Vector4.zero);
            }
            for (int i = 0; i < seg; i++)
            {
                int a = start + i * 4, b = a + 4;
                for (int s = 0; s < 3; s++)
                {
                    vh.AddTriangle(a + s, b + s, a + s + 1);
                    vh.AddTriangle(a + s + 1, b + s, b + s + 1);
                }
            }
        }
    }

    /// <summary>第一象限內（0～90 度）、扣掉兩道缺口後的角度範圍（弧度）。缺口把這一圈切光時回傳 false。</summary>
    private static bool ArcRange(float radius, float gapHalf, out float a0, out float a1)
    {
        a0 = 0f; a1 = Mathf.PI * 0.5f;
        if (gapHalf <= 0f) return true;
        if (radius <= gapHalf * 1.41421356f) { a0 = a1 = Mathf.PI * 0.25f; return false; }
        float d = Mathf.Asin(Mathf.Clamp01(gapHalf / Mathf.Max(radius, 0.0001f)));
        a0 = d; a1 = Mathf.PI * 0.5f - d;
        return true;
    }

    private static Vector2 Polar(Vector2 c, float r, float baseDeg, float rad)
    {
        float a = baseDeg * Mathf.Deg2Rad + rad;
        return c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
    }

    // ───────── 直線（X 字）：中間實心、兩側各一條柔邊 ─────────

    private void AddLine(VertexHelper vh, Vector2 p0, Vector2 p1, float thickness, Color32 col)
    {
        Vector2 d = p1 - p0;
        if (d.sqrMagnitude < 0.0001f) return;
        Vector2 n = new Vector2(-d.y, d.x).normalized;

        float f = feather;
        float hOut = thickness * 0.5f + f * 0.5f;
        float hIn = Mathf.Max(0f, thickness * 0.5f - f * 0.5f);
        Color32 clear = col; clear.a = 0;

        int i0 = vh.currentVertCount;
        float[] offs = { hOut, hIn, -hIn, -hOut };
        Color32[] cols = { clear, col, col, clear };
        for (int s = 0; s < 4; s++)
        {
            vh.AddVert(p0 + n * offs[s], cols[s], Vector4.zero);
            vh.AddVert(p1 + n * offs[s], cols[s], Vector4.zero);
        }
        for (int s = 0; s < 3; s++)
        {
            int a = i0 + s * 2, b = a + 2;
            vh.AddTriangle(a, a + 1, b);
            vh.AddTriangle(b, a + 1, b + 1);
        }
    }

    private static Color32 MulAlpha(Color c, float a)
    {
        Color32 c32 = c;
        c32.a = (byte)Mathf.Clamp(Mathf.RoundToInt(c.a * a * 255f), 0, 255);
        return c32;
    }

#if UNITY_EDITOR
    protected override void OnValidate()
    {
        base.OnValidate();
        SetVerticesDirty();
    }
#endif
}
