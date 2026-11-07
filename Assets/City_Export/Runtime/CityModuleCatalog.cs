// CityModuleCatalog.cs — 城市模組目錄
// 由選單 Tools > City > 3. Build Module Catalog 依 manifest.json 與匯入的 FBX 自動建立，不需要手動編輯。
using System;
using System.Collections.Generic;
using UnityEngine;

namespace DivaExMachina.City
{
    public class CityModuleCatalog : ScriptableObject
    {
        [Serializable]
        public class Entry
        {
            public string name;                 // BLD_xxx
            public GameObject prefab;
            public int heightUnits;             // 幾層（1 層 = unitHeight）
            public string profile;              // "W" / "N" / ""（非塔樓模組）
            public string category;             // tower_base / tower_floor / tower_top / podium / bridge
            public string[] gateOpenBlender;    // 門洞開口方向（Blender 座標，旋轉 0 時），例如 "+X"
            public Vector3 boundsMin;           // Unity 中的局部範圍
            public Vector3 boundsMax;
        }

        public float cellSize = 100f;
        public float unitHeight = 24.5f;

        [Header("Blender 水平方向 → Unity 格子方向（格子 X = Unity X，格子 Y = Unity Z）")]
        [Tooltip("由 Build Module Catalog 依模組範圍自動校正")]
        public Vector2Int blenderXToGrid = new Vector2Int(-1, 0);
        public Vector2Int blenderYToGrid = new Vector2Int(0, -1);
        [Tooltip("校正後所有模組範圍的平均誤差（公尺），應接近 0")]
        public float calibrationError;

        public List<Entry> entries = new List<Entry>();

        Dictionary<string, Entry> _map;

        void OnEnable() { _map = null; }

        public Entry Get(string name)
        {
            if (_map == null || _map.Count != entries.Count)
            {
                _map = new Dictionary<string, Entry>();
                foreach (var e in entries)
                    if (e != null && !string.IsNullOrEmpty(e.name)) _map[e.name] = e;
            }
            _map.TryGetValue(name, out var found);
            return found;
        }

        /// <summary>Blender 中的水平方向 (bx, by) 轉成 Unity 格子方向。</summary>
        public Vector2Int BlenderDirToGrid(int bx, int by)
        {
            return blenderXToGrid * bx + blenderYToGrid * by;
        }
    }
}
