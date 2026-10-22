using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 防止角色靠速度衝上 Unity Terrain 的陡坡。
///
/// 問題：移動腳本每一步都直接把水平速度設成目標速度。撞上陡坡時，物理引擎沿坡面法線把膠囊推出來，
/// 推力有一部分朝上；下一步水平速度又被拉回全速、垂直速度保留，於是一路「被推」上坡。速度越快爬越高。
///
/// 做法：每步在「前進方向、腳底前方一小段」取樣地形（直接讀 TerrainData，不打射線，很便宜）。
/// 如果前方地面比腳底高、而且坡度超過 maxSlopeDeg，就把水平速度裡「往上坡」的分量扣掉。
/// 沿著崖壁橫移、往下坡走都不受影響。
///
/// 只處理 Unity Terrain。船、建築等一般碰撞體維持原本行為（例如敵人登船的路徑不受影響）。
/// 跳躍、飛行不受限：只要腳底高過前方地面，就不會被擋。
/// </summary>
public static class TerrainSlopeLimiter
{
    private static readonly List<Terrain> s_terrains = new List<Terrain>(64);
    private static int s_cachedFrame = -1;

    /// <summary>
    /// 回傳修正後的水平速度（y 一律為 0）。
    /// </summary>
    /// <param name="horizontalVel">水平速度（相對地面；y 會被忽略）。</param>
    /// <param name="feet">腳底位置（世界座標）。</param>
    /// <param name="probeAhead">往前多遠取樣（不含這一步會移動的距離）。約等於膠囊半徑。</param>
    /// <param name="dt">這一步的時間。</param>
    /// <param name="maxSlopeDeg">超過這個角度就算爬不上去。</param>
    /// <param name="minRise">前方地面至少要比腳底高這麼多才會擋（避免平地上的小起伏誤判、飛行時被擋）。</param>
    /// <param name="hint">上次找到的地形塊，加速查找；會被更新。</param>
    public static Vector3 Limit(Vector3 horizontalVel, Vector3 feet, float probeAhead, float dt,
                                float maxSlopeDeg, float minRise, ref Terrain hint)
    {
        horizontalVel.y = 0f;
        float speed = horizontalVel.magnitude;
        if (speed < 0.01f) return horizontalVel;

        Vector3 dir = horizontalVel / speed;
        Vector3 probe = feet + dir * (probeAhead + speed * dt);

        Terrain t = FindTerrain(probe, hint);
        if (t == null) return horizontalVel;
        hint = t;

        TerrainData td = t.terrainData;
        Vector3 origin = t.GetPosition();
        Vector3 size = td.size;
        float u = (probe.x - origin.x) / size.x;
        float v = (probe.z - origin.z) / size.z;

        // 前方地面沒有高過腳底 → 下坡、平地、或在空中飛過去，不擋
        float groundY = origin.y + td.GetInterpolatedHeight(u, v);
        if (groundY - feet.y < minRise) return horizontalVel;

        // 坡度在可行走範圍內 → 不擋
        Vector3 n = td.GetInterpolatedNormal(u, v);
        if (n.y >= Mathf.Cos(maxSlopeDeg * Mathf.Deg2Rad)) return horizontalVel;

        // 扣掉往上坡的分量（上坡方向 = 法線的水平分量反過來）
        Vector3 uphill = new Vector3(-n.x, 0f, -n.z);
        if (uphill.sqrMagnitude < 0.000001f) return horizontalVel;
        uphill.Normalize();

        float into = Vector3.Dot(horizontalVel, uphill);
        if (into <= 0f) return horizontalVel;
        return horizontalVel - uphill * into;
    }

    private static Terrain FindTerrain(Vector3 p, Terrain hint)
    {
        if (hint != null && hint.isActiveAndEnabled && Contains(hint, p)) return hint;

        // 同一幀只更新一次清單（GetActiveTerrains 不配置記憶體）
        if (s_cachedFrame != Time.frameCount)
        {
            Terrain.GetActiveTerrains(s_terrains);
            s_cachedFrame = Time.frameCount;
        }

        for (int i = 0; i < s_terrains.Count; i++)
        {
            Terrain t = s_terrains[i];
            if (t != null && t.isActiveAndEnabled && Contains(t, p)) return t;
        }
        return null;
    }

    private static bool Contains(Terrain t, Vector3 p)
    {
        TerrainData td = t.terrainData;
        if (td == null) return false;
        Vector3 o = t.GetPosition();
        Vector3 s = td.size;
        return p.x >= o.x && p.x <= o.x + s.x && p.z >= o.z && p.z <= o.z + s.z;
    }
}
