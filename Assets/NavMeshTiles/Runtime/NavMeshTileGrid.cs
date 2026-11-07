// NavMeshTileGrid.cs — 把大地圖的 NavMesh 切成格子分批烘焙。
// 這個元件只存設定與畫格線；切格、烘焙、產生接縫 Link 的按鈕在 Editor/NavMeshTileGridEditor.cs。
//
// 物件原點 = 格子 (0, 0) 的左下角（世界 X/Z 最小的那一角），Y 建議放在 0。
// 每格是一個子物件 Tile_x_z，掛一個 Collect Objects = Volume 的 NavMeshSurface。
// 不同 NavMeshSurface 烘出來的 NavMesh 不會自動相連，所以接縫處要放 NavMeshLink（放在子物件 Seam_Links 底下）。
using UnityEngine;

namespace DivaExMachina.Navigation
{
    public class NavMeshTileGrid : MonoBehaviour
    {
        [Header("格子")]
        [Tooltip("每格邊長（公尺）。建議和地形的 1 km 分塊一致")]
        [Min(100)] public float tileSize = 1000f;
        [Min(1)] public int countX = 1;
        [Min(1)] public int countZ = 1;
        [Tooltip("相鄰格子互相重疊的寬度（公尺），每格往外各伸出一半，避免接縫缺一條")]
        [Min(0)] public float overlap = 20f;
        [Tooltip("烘焙範圍的最低與最高高度（相對這個物件）。要包住所有可以走的地方")]
        public float minY = -10f;
        public float maxY = 300f;

        [Header("烘焙設定（套用到每一格的 NavMeshSurface）")]
        [HideInInspector] public int agentTypeID;     // 在 Inspector 用下拉選單選
        [Tooltip("只收集這些 layer 的碰撞體（地形、城市）。不要勾角色、敵人、子彈、特效")]
        public LayerMask layerMask = ~0;
        [Tooltip("體素越大越省記憶體、烘焙越快，但窄路會消失。建議約 agent 半徑的一半")]
        public bool overrideVoxelSize = true;
        [Min(0.05f)] public float voxelSize = 0.8f;
        public bool overrideTileSize = false;
        [Min(16)] public int navTileSize = 256;
        [Min(0)] public float minRegionArea = 2f;

        [Header("接縫 Link")]
        [Tooltip("沿著接縫每隔多少公尺試放一個 Link")]
        [Min(5)] public float linkSpacing = 100f;
        [Tooltip("每個 Link 的寬度（公尺），不會超過間距")]
        [Min(1)] public float linkWidth = 60f;
        [Tooltip("Link 兩端高度差超過這個值就不放")]
        [Min(0.1f)] public float linkMaxHeightDiff = 2f;
        [Tooltip("找 NavMesh 表面時的搜尋半徑（公尺）")]
        [Min(0.5f)] public float linkSampleRadius = 3f;

        public Vector3 TileCenterLocal(int x, int z) => new Vector3((x + 0.5f) * tileSize, 0f, (z + 0.5f) * tileSize);

        public static string TileName(int x, int z) => $"Tile_{x:00}_{z:00}";

        void OnDrawGizmosSelected()
        {
            var o = transform.position;
            float w = countX * tileSize, d = countZ * tileSize;
            Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
            for (int i = 0; i <= countX; i++)
                Gizmos.DrawLine(o + new Vector3(i * tileSize, 0, 0), o + new Vector3(i * tileSize, 0, d));
            for (int j = 0; j <= countZ; j++)
                Gizmos.DrawLine(o + new Vector3(0, 0, j * tileSize), o + new Vector3(w, 0, j * tileSize));
        }
    }
}
