// CityGenerator.cs — 城市生成規則 v2.1（由 Blender 版 DistrictGenerator.py 移植）
// 只做純運算：輸入 CityDistrict 的參數，輸出每個模組要放在哪一格、第幾層、轉幾次 90°。
// 規則細節見專案文件「城市模組_組合規則_v2.md」。
//
// 座標：格子 (x, y)，x = Unity +X，y = Unity +Z。旋轉 quarterTurns = Unity 繞 Y 軸 +90° 的次數。
// Unity 繞 Y +90° 會把 +Z 轉到 +X、把 +X 轉到 -Z，也就是格子方向 (x, y) → (y, -x)。
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DivaExMachina.City.EditorTools
{
    public struct CityPlacement
    {
        public string module;      // BLD_xxx
        public Vector2Int cell;
        public int unit;           // 第幾層（底部高度 = unit × unitHeight）
        public int quarterTurns;
        public string group;       // 街區：平台 P#（含其上的塔）、地面塔群 L#；跨街區的橋放在 Bridges
    }

    public class CityGenerator
    {
        static readonly Vector2Int[] Dirs =
            { new Vector2Int(1, 0), new Vector2Int(0, 1), new Vector2Int(-1, 0), new Vector2Int(0, -1) };

        readonly CityDistrict d;
        readonly CityModuleCatalog cat;
        readonly System.Random rng;
        public readonly List<string> log = new List<string>();

        // 旋轉 0 時的標準朝向（格子座標），由 Blender 標準朝向經目錄校正換算
        readonly Vector2Int cornerDiag, edgeOut, rampUp, sideFeature, bridgeExtend;
        readonly Dictionary<string, Vector2Int[]> gateCanon = new Dictionary<string, Vector2Int[]>();

        // 狀態
        readonly Dictionary<Vector2Int, int> height = new Dictionary<Vector2Int, int>();
        readonly Dictionary<Vector2Int, Vector2Int> ramp = new Dictionary<Vector2Int, Vector2Int>();
        readonly List<Podium> podiums = new List<Podium>();
        readonly List<RectInt> lots = new List<RectInt>();
        readonly List<CityPlacement> output = new List<CityPlacement>();

        class Podium
        {
            public string tag;
            public RectInt rect;
            public int H;
            public bool round;
            public readonly HashSet<Vector2Int> cells = new HashSet<Vector2Int>();
            public readonly HashSet<Vector2Int> core = new HashSet<Vector2Int>();
            public readonly HashSet<Vector2Int> annex = new HashSet<Vector2Int>();
            public readonly Dictionary<Vector2Int, HashSet<Vector2Int>> gates = new Dictionary<Vector2Int, HashSet<Vector2Int>>();
        }

        class Tower
        {
            public string tag;
            public string block;   // 所屬街區（P# 或 L#），決定放在哪個群組
            public Vector2Int cell;
            public int z0;
            public string baseKey;
            public int baseTurns;
            public readonly HashSet<int> forced = new HashSet<int>();
            public readonly HashSet<Vector2Int> usedDirs = new HashSet<Vector2Int>();
            public int bridgeCount;
            public List<(string key, int u)> seq;
        }

        class Bridge
        {
            public Tower a, b;
            public Vector2Int dir;
            public int band;
            public List<Vector2Int> gap;
        }

        struct Kind
        {
            public bool edge;
            public Vector2Int dir;   // 邊：外側方向；角：外側對角
        }

        public CityGenerator(CityDistrict district)
        {
            d = district;
            cat = district.catalog;
            rng = new System.Random(district.seed);
            cornerDiag = G(1, -1);
            edgeOut = G(0, -1);
            rampUp = G(0, -1);
            sideFeature = G(1, 0);
            bridgeExtend = G(0, 1);
            foreach (var key in new[] { "POD_Gate_I", "POD_Gate_L", "POD_Gate_T", "POD_Gate_X",
                                        "BASE_W_GateI", "BASE_W_GateL", "BASE_N_GateX" })
            {
                var e = cat.Get("BLD_" + key);
                if (e == null || e.gateOpenBlender == null || e.gateOpenBlender.Length == 0)
                {
                    log.Add($"目錄缺少 {key} 的門洞資料，該門洞不會使用");
                    continue;
                }
                gateCanon[key] = e.gateOpenBlender.Select(ParseBlenderDir).ToArray();
            }
        }

        // ======================= 工具 =======================
        Vector2Int G(int bx, int by) => cat.BlenderDirToGrid(bx, by);

        Vector2Int ParseBlenderDir(string s)
        {
            switch (s)
            {
                case "+X": return G(1, 0);
                case "-X": return G(-1, 0);
                case "+Y": return G(0, 1);
                case "-Y": return G(0, -1);
                default: throw new ArgumentException("未知方向 " + s);
            }
        }

        static Vector2Int RotQ(Vector2Int v, int q)
        {
            q = ((q % 4) + 4) % 4;
            for (int i = 0; i < q; i++) v = new Vector2Int(v.y, -v.x);
            return v;
        }

        static int TurnsFor(Vector2Int canonical, Vector2Int target)
        {
            for (int q = 0; q < 4; q++)
                if (RotQ(canonical, q) == target) return q;
            throw new ArgumentException($"無法把 {canonical} 轉到 {target}");
        }

        static int TurnsForSet(Vector2Int[] canonical, HashSet<Vector2Int> target)
        {
            for (int q = 0; q < 4; q++)
            {
                var s = new HashSet<Vector2Int>(canonical.Select(c => RotQ(c, q)));
                if (s.SetEquals(target)) return q;
            }
            return -1;
        }

        static Vector2Int Neg(Vector2Int v) => new Vector2Int(-v.x, -v.y);

        static int Cheb(Vector2Int a, Vector2Int b) => Math.Max(Math.Abs(a.x - b.x), Math.Abs(a.y - b.y));

        int HeightOf(string key)
        {
            var e = cat.Get("BLD_" + key);
            return e != null ? e.heightUnits : 1;
        }

        int Hgt(Vector2Int c) => height.TryGetValue(c, out var h) ? h : 0;
        int Eff(Vector2Int c) => Hgt(c) + (ramp.ContainsKey(c) ? 1 : 0);

        void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = rng.Next(i + 1);
                (list[i], list[j]) = (list[j], list[i]);
            }
        }

        void Place(string key, Vector2Int cell, int unit, int q, string group)
        {
            output.Add(new CityPlacement { module = "BLD_" + key, cell = cell, unit = unit, quarterTurns = q, group = group });
        }

        // ======================= 主流程 =======================
        public List<CityPlacement> Generate()
        {
            var blocks = MakeBlocks();
            int pi = 0;
            foreach (var b in blocks)
            {
                if (b.width >= 3 && b.height >= 3 && rng.NextDouble() < d.podiumChance)
                {
                    var p = MakePodium(b, "P" + (pi + 1));
                    if (p != null) { podiums.Add(p); pi++; continue; }
                }
                lots.Add(b);
            }
            foreach (var p in podiums) PlanTunnels(p);
            foreach (var p in podiums) BuildPodiumPieces(p);

            var towers = PlaceTowers();
            var bridges = PlanBridges(towers);
            BuildTowersAndBridges(towers, bridges);

            log.Insert(0, $"街區 {blocks.Count} 個：平台 {podiums.Count}、地面塔群 {lots.Count}；塔 {towers.Count} 座、橋 {bridges.Count} 座；模組 {output.Count} 件");
            return output;
        }

        // ======================= 街道與街區 =======================
        List<(int start, int len)> Split(int n)
        {
            var res = new List<(int, int)>();
            int p = 0;
            int lo = Math.Max(2, d.minBlock), hi = Math.Max(lo, d.maxBlock);
            while (p < n)
            {
                int len = rng.Next(lo, hi + 1);
                if (p + len > n) len = n - p;
                if (len < 2) break;                      // 剩下的邊角當空地
                res.Add((p, len));
                p += len + Math.Max(1, d.streetWidth);
            }
            return res;
        }

        List<RectInt> MakeBlocks()
        {
            var cols = Split(d.sizeX);
            var rows = Split(d.sizeZ);
            var blocks = new List<RectInt>();
            foreach (var row in rows)
            {
                RectInt? cur = null;
                foreach (var col in cols)
                {
                    var r = new RectInt(col.start, row.start, col.len, row.len);
                    if (cur.HasValue && rng.NextDouble() < d.mergeChance)
                        cur = new RectInt(cur.Value.x, cur.Value.y, r.xMax - cur.Value.x, cur.Value.height);   // 合併，產生 T 字路口
                    else
                    {
                        if (cur.HasValue) blocks.Add(cur.Value);
                        cur = r;
                    }
                }
                if (cur.HasValue) blocks.Add(cur.Value);
            }
            return blocks;
        }

        // ======================= 平台：高度圖與坡道路線（R1–R3） =======================
        Podium MakePodium(RectInt r, string tag)
        {
            bool tr = rng.NextDouble() < 0.5, fx = rng.NextDouble() < 0.5, fy = rng.NextDouble() < 0.5;
            int LW = tr ? r.height : r.width;
            int LD = tr ? r.width : r.height;
            Vector2Int Map(int u, int v)
            {
                int x = tr ? v : u, y = tr ? u : v;
                if (fx) x = r.width - 1 - x;
                if (fy) y = r.height - 1 - y;
                return new Vector2Int(r.x + x, r.y + y);
            }

            // 外圍路線：一條邊 + 相鄰的另一條邊（L 形），起點背後是街道
            var path = new List<Vector2Int>();
            for (int u = 0; u < LW; u++) path.Add(Map(u, 0));
            for (int v = 1; v < LD; v++) path.Add(Map(LW - 1, v));
            var corner = Map(LW - 1, 0);
            var straight = new List<int>();
            for (int k = 0; k < path.Count - 1; k++)
                if (path[k] != corner) straight.Add(k);

            var heights = (d.podiumHeights != null && d.podiumHeights.Length > 0) ? d.podiumHeights : new[] { 2, 4, 6 };
            int H = heights[rng.Next(heights.Length)];
            H -= H % 2;                                                 // P3：主體高度用偶數
            int maxH = straight.Count - straight.Count % 2;
            H = Math.Min(H, maxH);
            if (H < 2) return null;

            var order = straight.ToList(); Shuffle(order);
            var ramps = new HashSet<int>(order.Take(H));

            var p = new Podium { tag = tag, rect = r, H = H, round = rng.NextDouble() < 0.5 };
            for (int x = r.x; x < r.xMax; x++)
                for (int y = r.y; y < r.yMax; y++) p.cells.Add(new Vector2Int(x, y));
            foreach (var c in path) p.annex.Add(c);
            foreach (var c in p.cells) if (!p.annex.Contains(c)) { p.core.Add(c); height[c] = H; }

            int cur = 0;
            for (int k = 0; k < path.Count; k++)
            {
                var c = path[k];
                height[c] = cur;
                if (ramps.Contains(k)) { ramp[c] = path[k + 1] - c; cur++; }
            }
            return p;
        }

        // ======================= 地面門洞（G1–G2） =======================
        void PlanTunnels(Podium p)
        {
            var rows = new List<(List<Vector2Int> cells, Vector2Int dir)>();
            var cols = new List<(List<Vector2Int> cells, Vector2Int dir)>();
            for (int y = p.rect.y; y < p.rect.yMax; y++)
                rows.Add((Enumerable.Range(p.rect.x, p.rect.width).Select(x => new Vector2Int(x, y)).ToList(), new Vector2Int(1, 0)));
            for (int x = p.rect.x; x < p.rect.xMax; x++)
                cols.Add((Enumerable.Range(p.rect.y, p.rect.height).Select(y => new Vector2Int(x, y)).ToList(), new Vector2Int(0, 1)));

            bool Valid((List<Vector2Int> cells, Vector2Int dir) line)
            {
                if (!line.cells.All(c => Hgt(c) >= 1)) return false;           // 坡道直接放在地面的格子不能有門洞
                return Eff(line.cells[0] - line.dir) == 0 && Eff(line.cells[line.cells.Count - 1] + line.dir) == 0;
            }

            foreach (var group in new[] { rows, cols })
            {
                if (rng.NextDouble() >= d.tunnelChance) continue;
                var ok = group.Where(Valid).ToList();
                if (ok.Count == 0) continue;
                var line = ok[rng.Next(ok.Count)];
                foreach (var c in line.cells)
                {
                    if (!p.gates.TryGetValue(c, out var set)) p.gates[c] = set = new HashSet<Vector2Int>();
                    set.Add(line.dir); set.Add(Neg(line.dir));
                }
            }
        }

        (string key, int q) GatePiece(HashSet<Vector2Int> open)
        {
            foreach (var key in new[] { "POD_Gate_I", "POD_Gate_L", "POD_Gate_T", "POD_Gate_X" })
            {
                if (!gateCanon.TryGetValue(key, out var canon)) continue;
                int q = TurnsForSet(canon, open);
                if (q >= 0) return (key, q);
            }
            return (null, 0);
        }

        // ======================= 平台板塊（P1–P5） =======================
        HashSet<Vector2Int> Exposed(Vector2Int c, int z)
        {
            var s = new HashSet<Vector2Int>();
            foreach (var dd in Dirs) if (Eff(c + dd) <= z) s.Add(dd);
            return s;
        }

        Kind? Classify(Vector2Int c, HashSet<Vector2Int> E)
        {
            if (E.Count == 0) return null;
            if (E.Count == 1) return new Kind { edge = true, dir = E.First() };
            var list = E.ToList();
            int best = int.MaxValue;
            Vector2Int ba = default, bb = default;
            bool found = false;
            for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    var a = list[i]; var b = list[j];
                    if (a + b == Vector2Int.zero) continue;                       // 兩面相對不算角
                    int s = Eff(c + a) + Eff(c + b);
                    if (s < best) { best = s; ba = a; bb = b; found = true; }
                }
            if (!found)                                                           // 只有兩面相對：面向較低一側
                return new Kind { edge = true, dir = list.OrderBy(x => Eff(c + x)).First() };
            return new Kind { edge = false, dir = ba + bb };
        }

        float RampSurface(Vector2Int nc, Vector2Int s, Kind k)
        {
            int b = Hgt(nc);
            var r = ramp[nc];
            bool parallel = r.x * s.x + r.y * s.y == 0;
            if (k.edge)
            {
                if (parallel) return b + 0.5f;
                return r == s ? b : b + 1;
            }
            if (parallel)
            {
                var end = s.x == 0 ? new Vector2Int(k.dir.x, 0) : new Vector2Int(0, k.dir.y);
                return r == end ? b + 1 : b;
            }
            return r == s ? b : b + 1;
        }

        List<Vector2Int> BadTrims(Vector2Int c, Kind k, int z)
        {
            var sides = k.edge
                ? new List<Vector2Int> { k.dir }
                : new List<Vector2Int> { new Vector2Int(k.dir.x, 0), new Vector2Int(0, k.dir.y) };
            return sides.Where(s => ramp.ContainsKey(c + s) && RampSurface(c + s, s, k) > z).ToList();
        }

        void BuildPodiumPieces(Podium p)
        {
            foreach (var c in p.cells)
            {
                int h = Hgt(c);
                var col = new List<(string key, int z, int q)>();
                int z = 0;
                if (p.gates.TryGetValue(c, out var open))
                {
                    var (gk, gq) = GatePiece(open);
                    if (gk != null) { col.Add((gk, 0, gq)); z = 1; }
                    else log.Add($"{p.tag} {c}：找不到符合開口 {string.Join(",", open)} 的門洞板塊");
                }
                while (z < h)
                {
                    bool two = z % 2 == 0 && z + 1 < h;                          // P2：2 層板塊從偶數層開始
                    var E = Exposed(c, z);
                    if (two) E.UnionWith(Exposed(c, z + 1));                     // P1：上下兩層的聯集
                    var kind = Classify(c, E);
                    while (kind.HasValue)                                         // P5：不穿出坡道
                    {
                        var bad = BadTrims(c, kind.Value, z);
                        if (bad.Count == 0) break;
                        E.ExceptWith(bad);
                        kind = Classify(c, E);
                    }
                    if (!kind.HasValue)
                    {
                        col.Add(("POD_Fill", z, 0));
                        if (two) col.Add(("POD_Fill", z + 1, 0));
                    }
                    else
                    {
                        bool cap = two && z + 2 == h && p.core.Contains(c);       // P3：主體頂部用 Cap
                        var k = kind.Value;
                        if (k.edge)
                            col.Add((two ? (cap ? "POD_CapEdge" : "POD_Edge") : "POD_EdgeHalf", z, TurnsFor(edgeOut, k.dir)));
                        else
                            col.Add((two ? (cap ? "POD_CapCorner" : "POD_Corner") : "POD_CornerHalf", z, TurnsFor(cornerDiag, k.dir)));
                    }
                    z += two ? 2 : 1;
                }
                // P4：整根角柱都是 2 層板塊時換成圓角
                if (p.round)
                {
                    var corners = col.Where(e => e.key.Contains("Corner")).ToList();
                    if (corners.Count > 0 && corners.All(e => HeightOf(e.key) == 2))
                        for (int i = 0; i < col.Count; i++)
                        {
                            if (col[i].key == "POD_CapCorner") col[i] = ("POD_CapCornerRound", col[i].z, col[i].q);
                            else if (col[i].key == "POD_Corner") col[i] = ("POD_CornerRound", col[i].z, col[i].q);
                        }
                }
                foreach (var e in col) Place(e.key, c, e.z, e.q, p.tag);
                if (ramp.TryGetValue(c, out var up)) Place("POD_Ramp", c, h, TurnsFor(rampUp, up), p.tag);
            }
        }

        // ======================= 塔（T1–T5） =======================
        List<Tower> PlaceTowers()
        {
            var cand = new List<(Vector2Int cell, int z0, string block)>();
            foreach (var p in podiums) foreach (var c in p.core) cand.Add((c, p.H, p.tag));
            for (int li = 0; li < lots.Count; li++)
            {
                var lot = lots[li];
                for (int x = lot.x; x < lot.xMax; x++)
                    for (int y = lot.y; y < lot.yMax; y++) cand.Add((new Vector2Int(x, y), 0, "L" + (li + 1)));
            }
            Shuffle(cand);

            var podiumCells = new HashSet<Vector2Int>(podiums.SelectMany(p => p.cells));
            var towers = new List<Tower>();
            foreach (var (cell, z0, block) in cand)
            {
                if (rng.NextDouble() >= d.towerDensity) continue;
                if (towers.Any(t => Cheb(t.cell, cell) < 2)) continue;           // T5：塔與塔至少隔一格
                if (z0 == 0 && podiumCells.Any(pc => Cheb(pc, cell) < 2)) continue; // 地面塔與平台至少隔一格
                towers.Add(new Tower { tag = "T" + (towers.Count + 1), block = block, cell = cell, z0 = z0 });
            }
            return towers;
        }

        List<Bridge> PlanBridges(List<Tower> towers)
        {
            var towerCells = new HashSet<Vector2Int>(towers.Select(t => t.cell));
            var pairs = new List<(Tower a, Tower b)>();
            for (int i = 0; i < towers.Count; i++)
                for (int j = i + 1; j < towers.Count; j++)
                {
                    var ca = towers[i].cell; var cb = towers[j].cell;
                    if (ca.x != cb.x && ca.y != cb.y) continue;
                    int dist = Math.Abs(ca.x - cb.x) + Math.Abs(ca.y - cb.y);
                    if (dist < 2 || dist > 5) continue;
                    pairs.Add((towers[i], towers[j]));
                }
            Shuffle(pairs);

            var bridgeCells = new HashSet<Vector2Int>();
            var bridges = new List<Bridge>();
            foreach (var (a, b) in pairs)
            {
                if (rng.NextDouble() >= d.bridgeChance) continue;
                if (!d.crossBlockBridges && a.block != b.block) continue;        // 不允許跨街區的橋
                var delta = b.cell - a.cell;
                var dir = new Vector2Int(Math.Sign(delta.x), Math.Sign(delta.y));
                int n = Math.Abs(delta.x) + Math.Abs(delta.y) - 1;
                var gap = Enumerable.Range(1, n).Select(k => a.cell + dir * k).ToList();
                if (gap.Any(c => towerCells.Contains(c) || bridgeCells.Contains(c))) continue;   // B2
                if (a.usedDirs.Contains(dir) || b.usedDirs.Contains(Neg(dir))) continue;
                if (a.bridgeCount >= 2 || b.bridgeCount >= 2) continue;
                int lo = Math.Max(Math.Max(a.z0, b.z0), gap.Max(c => Eff(c))) + 3;
                var br = new Bridge { a = a, b = b, dir = dir, band = rng.Next(lo, lo + 5), gap = gap };
                bridges.Add(br);
                foreach (var c in gap) bridgeCells.Add(c);
                a.usedDirs.Add(dir); b.usedDirs.Add(Neg(dir));
                a.bridgeCount++; b.bridgeCount++;
            }
            return bridges;
        }

        (string key, int q) ChooseTowerBase(Tower t)
        {
            var opts = new List<(string, int)> { ("BASE_W", 0) };
            foreach (var (key, w) in new[] { ("BASE_W_GateI", 2), ("BASE_W_GateL", 1), ("BASE_N_GateX", 2) })
            {
                if (!gateCanon.TryGetValue(key, out var canon)) continue;
                var qs = new List<int> { 0, 1, 2, 3 }; Shuffle(qs);
                foreach (int q in qs)
                {
                    if (canon.All(c => Eff(t.cell + RotQ(c, q)) == t.z0))           // 開口面向同高度的地面/平台頂
                    {
                        for (int i = 0; i < w; i++) opts.Add((key, q));
                        break;
                    }
                }
            }
            return opts[rng.Next(opts.Count)];
        }

        List<(string key, int u)> SampleTower(string baseKey, int lo, int hi, HashSet<int> forced)
        {
            int bh = HeightOf(baseKey);
            int fmax = forced.Count > 0 ? forced.Max() : -1;
            for (int attempt = 0; attempt < 30000; attempt++)
            {
                int B = rng.Next(lo, hi + 1);
                if (fmax >= bh + B) continue;
                string prof = baseKey == "BASE_N_GateX" ? "N" : "W";
                var seq = new List<(string, int)> { (baseKey, 0) };
                int u = bh, end = bh + B, wRun = 0, nRun = 0;
                bool first = true;
                while (u < end)
                {
                    int rem = end - u;
                    var opts = new List<(string name, int w)> { ("plain", 3) };
                    if (prof == "W") { if (rem >= 3 && (wRun >= 1 || first)) opts.Add(("toN", 2)); }
                    else
                    {
                        if (rem >= 3) opts.Add(("silo", 2));
                        if (rem >= 2 && nRun >= 1) opts.Add(("fromN", 1));
                    }
                    first = false;
                    int total = opts.Sum(o => o.w), r = rng.Next(total);
                    string pick = opts[0].name;
                    foreach (var o in opts) { if (r < o.w) { pick = o.name; break; } r -= o.w; }

                    if (prof == "W")
                    {
                        if (pick == "plain") { seq.Add(("FLR_W_Plain", u++)); wRun++; }
                        else { seq.Add(("FLR_W_ToN", u++)); prof = "N"; nRun = 0; }           // T2
                    }
                    else
                    {
                        if (pick == "plain") { seq.Add(("FLR_N_Plain", u++)); nRun++; }
                        else if (pick == "silo")                                              // T3
                        {
                            int n = rng.Next(1, Math.Min(3, rem - 2) + 1);
                            seq.Add(("FLR_N_Silo_Start", u++));
                            for (int k = 0; k < n; k++) seq.Add(("FLR_N_Silo_Mid", u++));
                            seq.Add(("FLR_N_Silo_End", u++));
                            nRun++;
                        }
                        else { seq.Add(("FLR_W_FromN", u++)); prof = "W"; wRun = 0; }
                    }
                }
                seq.Add((prof == "N" ? "TOP_N_Tower" : "TOP_W_Cap", u));                     // T4
                var at = new Dictionary<int, string>();
                foreach (var (k, uu) in seq) at[uu] = k;
                if (forced.All(f => at.TryGetValue(f, out var k2) && k2 == "FLR_N_Plain")) return seq;
            }
            return null;
        }

        void BuildTowersAndBridges(List<Tower> towers, List<Bridge> bridges)
        {
            // 抽樣失敗（找不到符合橋高度的樓層組合）時，拿掉那座塔的橋再重來
            for (int round = 0; round < 8; round++)
            {
                foreach (var t in towers) t.forced.Clear();
                foreach (var br in bridges)
                {
                    br.a.forced.Add(br.band - br.a.z0); br.a.forced.Add(br.band + 1 - br.a.z0);
                    br.b.forced.Add(br.band - br.b.z0); br.b.forced.Add(br.band + 1 - br.b.z0);
                }
                Tower failed = null;
                foreach (var t in towers)
                {
                    (t.baseKey, t.baseTurns) = ChooseTowerBase(t);
                    var range = t.z0 > 0 ? d.towerBodyOnPodium : d.towerBodyOnGround;
                    t.seq = SampleTower(t.baseKey, Math.Max(1, range.x), Math.Max(range.x, range.y), t.forced);
                    if (t.seq == null) { failed = t; break; }
                }
                if (failed == null) break;
                int removed = bridges.RemoveAll(b => b.a == failed || b.b == failed);
                log.Add($"{failed.tag} 找不到符合橋高度的樓層組合，拿掉 {removed} 座橋");
            }

            // 重新計算各塔被橋佔用的方向
            foreach (var t in towers) t.usedDirs.Clear();
            foreach (var br in bridges) { br.a.usedDirs.Add(br.dir); br.b.usedDirs.Add(Neg(br.dir)); }
            var towerCells = new HashSet<Vector2Int>(towers.Select(t => t.cell));

            foreach (var t in towers)
            {
                if (t.seq == null) continue;
                // T5'：圓筒與塔頂側翼朝向沒有橋、也不朝向隔一格另一座塔的方向
                var free = Dirs.Where(dd => !t.usedDirs.Contains(dd) && !towerCells.Contains(t.cell + dd * 2)).ToList();
                if (free.Count == 0) free = Dirs.Where(dd => !t.usedDirs.Contains(dd)).ToList();
                if (free.Count == 0) free = Dirs.ToList();
                var side = free[rng.Next(free.Count)];
                int qs = TurnsFor(sideFeature, side);
                foreach (var (key, u) in t.seq)
                {
                    int q = key.StartsWith("BASE_") ? t.baseTurns
                          : (key.Contains("Silo") || key == "TOP_N_Tower") ? qs : 0;
                    Place(key, t.cell, t.z0 + u, q, t.block);
                }
            }
            int cross = 0;
            foreach (var br in bridges)                                                      // B1
            {
                if (br.a.seq == null || br.b.seq == null) continue;
                int q = TurnsFor(bridgeExtend, br.dir);
                string group = br.a.block == br.b.block ? br.a.block : "Bridges";           // 跨街區的橋不屬於任何一邊
                if (group == "Bridges") cross++;
                for (int k = 0; k < br.gap.Count; k++)
                    Place(k == 0 ? "BRG_End" : "BRG_Span", br.gap[k], br.band, q, group);
            }
            if (cross > 0) log.Add($"{cross} 座橋跨越兩個街區，放在 Bridges 群組（移動街區時不會跟著動）");
        }
    }
}
