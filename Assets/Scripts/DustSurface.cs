using UnityEngine;

/// <summary>
/// 指定這個物件（和它的子物件）被踩到時噴出的塵土顏色，覆寫自動取色。
///
/// 用在自動取色不準的地方，例如：
///   · 船的甲板碰撞體沒有 Renderer，或材質顏色跟看起來的不一樣
///   · 金屬地板想要偏灰白的粉塵
///   · 水面、玻璃之類不該揚起塵土的表面（取消勾選 Emit Dust）
///   · 金屬地面：Kind 設成 Metal，貼地推進時會噴火花
/// </summary>
[DisallowMultipleComponent]
public class DustSurface : MonoBehaviour
{
    [Tooltip("踩到這個表面時要不要噴塵土。")]
    public bool emitDust = true;

    [Tooltip("塵土顏色。會直接使用，不再和預設顏色混合。")]
    public Color dustColor = new Color(0.72f, 0.72f, 0.74f, 1f);

    public enum SurfaceKind { Dust, Metal }

    [Tooltip("表面材質。Metal：高速推進 / Dash 貼地時噴火花，塵土減量（PlayerGroundWake 使用）。")]
    public SurfaceKind kind = SurfaceKind.Dust;
}
