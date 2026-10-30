using UnityEngine;

/// <summary>
/// 貝努鳥號在小地圖上的輪廓圖：一張俯視圖，以及它在「船的座標系」裡對應的範圍。
/// 由 Tools > Diva Ex Machina > Ship Minimap Baker 建立 / 更新，Minimap 元件讀它。
///
/// 船的座標系 = 船的位置 + 船的水平朝向（只取 Y 旋轉），單位是公尺，不含縮放。
///   x = 船的右方，z = 船頭方向。貼圖的上方 = 船頭。
///
/// 換算：貼圖中心在船座標系的 (localMin + size / 2)，
///       世界座標 = ship.position + Quaternion.Euler(0, ship.eulerAngles.y, 0) * (中心.x, 0, 中心.y)。
/// </summary>
[CreateAssetMenu(menuName = "Diva Ex Machina/Ship Minimap Icon", fileName = "ShipMinimapIcon")]
public class ShipMinimapIcon : ScriptableObject
{
    [Tooltip("烘好的俯視輪廓圖（高度分層色塊 + 外框）。")]
    public Texture2D texture;

    [Tooltip("貼圖左下角在船座標系的位置（公尺，X = 船的右方，Y = 船頭方向）。")]
    public Vector2 localMin;

    [Tooltip("貼圖涵蓋的大小（公尺，X = 船寬方向，Y = 船長方向）。")]
    public Vector2 size;

    [Tooltip("每個像素幾公尺（烘焙時的設定，僅供參考）。")]
    public float metersPerPixel;

    /// <summary>貼圖中心在船座標系的位置（X, Z）。</summary>
    public Vector2 LocalCenter => localMin + size * 0.5f;

    /// <summary>貼圖中心的世界座標（Y 跟船一樣）。</summary>
    public Vector3 WorldCenter(Transform ship)
    {
        Vector2 c = LocalCenter;
        return ship.position + Quaternion.Euler(0f, ship.eulerAngles.y, 0f) * new Vector3(c.x, 0f, c.y);
    }
}
