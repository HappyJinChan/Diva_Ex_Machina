using UnityEngine;

/// <summary>
/// 讓 TerrainStreamer 追蹤這個物件：啟用時自動登記，停用 / 銷毀時自動取消。
/// 掛在母艦、Boss 這類「玩家不在旁邊時也需要腳下有地形」的物件上（可以直接放進 prefab）。
///
/// 一般敵人不要掛：每個目標都會讓 streamer 多算一輪，而且會讓整片地形一直開著，失去串流的意義。
/// </summary>
[DisallowMultipleComponent]
public class TerrainStreamTarget : MonoBehaviour
{
    [Tooltip("這個目標自己的載入半徑（公尺）。0 = 沿用 TerrainStreamer 的 Load Radius。\n" +
             "Boss 通常只需要腳下附近有地形，可以設小一點（例如 1000）。")]
    [SerializeField, Min(0f)] private float loadRadiusOverride = 0f;

    public float LoadRadiusOverride => loadRadiusOverride;

    private void OnEnable()  => TerrainStreamer.Register(this);
    private void OnDisable() => TerrainStreamer.Unregister(this);
}
