using UnityEngine;

/// <summary>
/// 小地圖的烘焙結果：一張俯視圖，以及它對應的世界範圍。
/// 由 Tools > Diva Ex Machina > Minimap Baker 建立 / 更新，Minimap 元件讀它。
///
/// 換算：u = (世界 X − worldMin.x) / worldSize.x，v = (世界 Z − worldMin.y) / worldSize.y。
/// </summary>
[CreateAssetMenu(menuName = "Diva Ex Machina/Minimap Data", fileName = "MinimapData")]
public class MinimapData : ScriptableObject
{
    [Tooltip("烘好的俯視圖（平面色塊 + 等高線）。")]
    public Texture2D texture;

    [Tooltip("貼圖左下角的世界座標（X, Z）。")]
    public Vector2 worldMin;

    [Tooltip("貼圖涵蓋的世界大小（公尺，X, Z）。")]
    public Vector2 worldSize;

    [Tooltip("每個像素幾公尺（烘焙時的設定，僅供參考）。")]
    public float metersPerPixel;

    /// <summary>世界座標（X, Z）→ 貼圖 UV。</summary>
    public Vector2 WorldToUV(Vector3 world)
    {
        return new Vector2(
            worldSize.x > 0f ? (world.x - worldMin.x) / worldSize.x : 0f,
            worldSize.y > 0f ? (world.z - worldMin.y) / worldSize.y : 0f);
    }
}
