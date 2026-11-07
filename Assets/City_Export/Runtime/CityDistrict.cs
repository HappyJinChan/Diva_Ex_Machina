// CityDistrict.cs — 放在地圖上的城市街區。在 Inspector 按 Generate 生成（生成邏輯在 Editor/CityGenerator.cs）。
// 原點（左下角）會對齊 100 m 格子；格子 X = Unity +X，格子 Y = Unity +Z。
using System.Collections.Generic;
using UnityEngine;

namespace DivaExMachina.City
{
    public class CityDistrict : MonoBehaviour
    {
        public CityModuleCatalog catalog;

        [Header("範圍與種子")]
        public int seed = 7;
        [Min(3)] public int sizeX = 10;          // 格數（1 格 = 100 m）
        [Min(3)] public int sizeZ = 10;
        [Tooltip("地面高度（世界座標 Y）。平台與塔的底部貼齊這個高度")]
        public float groundY = 6f;

        [Header("街道與街區")]
        [Min(2)] public int minBlock = 3;        // 街區最小邊長（格）
        [Min(2)] public int maxBlock = 5;
        [Min(1)] public int streetWidth = 1;     // 街道寬度（格）
        [Range(0, 1)] public float mergeChance = 0.3f;    // 相鄰街區合併（產生 T 字路口）的機率

        [Header("建築")]
        [Range(0, 1)] public float podiumChance = 0.75f;  // 街區做成平台的機率（否則是地面塔群）
        [Tooltip("平台主體高度候選（層數，必須是偶數）")]
        public int[] podiumHeights = { 2, 4, 6 };
        [Range(0, 1)] public float tunnelChance = 0.7f;   // 每座平台每個方向挖隧道的機率
        [Range(0, 1)] public float towerDensity = 0.35f;
        [Range(0, 1)] public float bridgeChance = 0.5f;
        [Tooltip("允許橋連接兩個不同街區的塔。跨街區的橋放在 Bridges 群組，移動街區時不會跟著動")]
        public bool crossBlockBridges = true;
        public Vector2Int towerBodyOnPodium = new Vector2Int(4, 10);   // 樓身層數範圍
        public Vector2Int towerBodyOnGround = new Vector2Int(5, 14);

        [Header("地形檢查")]
        [Tooltip("格子內地形高度與 groundY 相差超過這個值就警告")]
        public float terrainTolerance = 1f;

        [Header("夜間燈火")]
        [Tooltip("定稿時把每棟建築的牆面換成夜間發光版本（隨機三種）。需要先執行 Tools > City > 4. Create Night Wall Materials")]
        public bool nightWindows = true;

        [HideInInspector] public List<Vector2Int> unevenCells = new List<Vector2Int>();
        [HideInInspector] public List<Vector2Int> builtCells = new List<Vector2Int>();

        public float CellSize => catalog != null ? catalog.cellSize : 100f;

        void OnDrawGizmosSelected()
        {
            float c = CellSize;
            Vector3 o = transform.position;
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.9f);
            Vector3 a = o, b = o + new Vector3(sizeX * c, 0, 0), d = o + new Vector3(0, 0, sizeZ * c), e = o + new Vector3(sizeX * c, 0, sizeZ * c);
            Gizmos.DrawLine(a, b); Gizmos.DrawLine(b, e); Gizmos.DrawLine(e, d); Gizmos.DrawLine(d, a);
            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.25f);
            for (int i = 1; i < sizeX; i++) Gizmos.DrawLine(o + new Vector3(i * c, 0, 0), o + new Vector3(i * c, 0, sizeZ * c));
            for (int j = 1; j < sizeZ; j++) Gizmos.DrawLine(o + new Vector3(0, 0, j * c), o + new Vector3(sizeX * c, 0, j * c));

            var built = new HashSet<Vector2Int>(builtCells);
            foreach (var cell in unevenCells)
            {
                Gizmos.color = built.Contains(cell) ? new Color(1f, 0.1f, 0.1f, 0.6f) : new Color(1f, 0.7f, 0.1f, 0.4f);
                Gizmos.DrawCube(o + new Vector3((cell.x + 0.5f) * c, 1f, (cell.y + 0.5f) * c), new Vector3(c * 0.9f, 2f, c * 0.9f));
            }
        }
    }
}
