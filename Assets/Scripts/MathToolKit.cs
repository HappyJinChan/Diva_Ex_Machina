using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class MathToolKit
{
    public static bool InterceptionDirection(Vector3 a, Vector3 b, Vector3 vA, float sB, out Vector3 result)
    {
        Vector3 aToB = b - a;
        float dC = aToB.magnitude;
        float alpha = Vector3.Angle(aToB, vA) * Mathf.Deg2Rad;
        float sA = vA.magnitude;
        float r = sA / sB;
        if (SolveQuadratic(1 - r * r, 2 * r * dC * Mathf.Cos(alpha), -(dC * dC), out var root1, out var root2) == 0)
        {
            result = Vector3.zero;
            return false;
        }
        float dA = Mathf.Max(root1, root2);
        float t = dA / sB;
        Vector3 c = a + vA * t;
        result = (c - b).normalized;
        return true;
    }

    public static bool InterceptionPoint(Vector3 a, Vector3 b, Vector3 vA, float sB, out Vector3 result)
    {
        Vector3 aToB = b - a;
        float dC = aToB.magnitude;
        float alpha = Vector3.Angle(aToB, vA) * Mathf.Deg2Rad;
        float sA = vA.magnitude;
        float r = sA / sB;
        if (SolveQuadratic(1 - r * r, 2 * r * dC * Mathf.Cos(alpha), -(dC * dC), out var root1, out var root2) == 0)
        {
            result = Vector3.zero;
            return false;
        }
        float dA = Mathf.Max(root1, root2);
        float t = dA / sB;
        Vector3 c = a + vA * t;
        result = c;
        return true;
    }
    public static bool GetLaunchDirection(
        Vector3 origin,
        Vector3 target,
        float muzzleSpeed,
        out Vector3 result)
    {
        Vector3 toTarget = target - origin;
        Vector3 toTargetXZ = new Vector3(toTarget.x, 0f, toTarget.z);
        float x = toTargetXZ.magnitude;
        float y = toTarget.y;
        float v2 = muzzleSpeed * muzzleSpeed;
        float g = Mathf.Abs(Physics.gravity.y);

        // discriminant
        float insideSqrt = v2 * v2 - g * (g * x * x + 2f * y * v2);
        if (insideSqrt < 0f)
        {
            result = Vector3.zero;
            return false;
        }

        float sqrt = Mathf.Sqrt(insideSqrt);

        // *** pick the LOW-arc by using (v2 - sqrt) instead of (v2 + sqrt) ***
        float tanTheta = (v2 - sqrt) / (g * x);
        float cosT = 1f / Mathf.Sqrt(1f + tanTheta * tanTheta);
        float sinT = tanTheta * cosT;

        Vector3 dirXZ = toTargetXZ.normalized;
        result = dirXZ * cosT + Vector3.up * sinT;
        return true;
    }

    /// <summary>
    /// Moving-target, gravity-and-lead solver.
    /// </summary>
    public static bool GetLaunchDirection(
        Vector3 origin,
        Vector3 targetPos,
        Vector3 targetVel,
        float muzzleSpeed,
        out Vector3 result)
    {
        // �X�X your dynamic version :contentReference[oaicite:1]{index=1}
        const int maxIter = 6;
        result = Vector3.zero;

        float t = (targetPos - origin).magnitude / muzzleSpeed;
        Vector3 launchDir = Vector3.zero;

        for (int i = 0; i < maxIter; i++)
        {
            Vector3 predPos = targetPos + targetVel * t;

            // call the static-target overload
            if (!GetLaunchDirection(origin, predPos, muzzleSpeed, out launchDir))
                return false;

            float v0y = launchDir.y * muzzleSpeed;
            float g = Mathf.Abs(Physics.gravity.y);
            float deltaY = predPos.y - origin.y;
            float underSqrt = v0y * v0y + 2f * g * deltaY;
            if (underSqrt < 0f) underSqrt = 0f;
            t = (v0y + Mathf.Sqrt(underSqrt)) / g;
        }

        result = launchDir.normalized;
        return true;
    }
    /// <summary>
    /// 換裝射出用：發射方向（炮管實際的世界朝向）已經決定，求打到 target 需要的初速。
    /// 只用發射方向的「仰角」，水平方位當作已對準目標（方位的小偏差交給射出艙的追蹤修正）。
    ///
    ///   v² = g·R² / (2·cos²θ·(R·tanθ − Δy))，飛行時間 T = R / (v·cosθ)
    ///   R = 水平距離，Δy = 目標高度 − 起點高度，θ = 發射仰角，g = 重力大小（正數）
    ///
    /// 無解（回傳 false）：R·tanθ ≤ Δy（目標在這條仰角線上方，即頭頂盲區）、
    /// 目標在正上 / 正下方（R ≈ 0）、發射方向接近垂直。
    /// 重力要跟射出艙實際用的一樣（SupplyPod 用 Physics.gravity × gravityScale）。
    /// </summary>
    public static bool SolveLaunchSpeed(Vector3 origin, Vector3 target, Vector3 launchDir, float g,
                                        out float speed, out float flightTime)
    {
        speed = 0f;
        flightTime = 0f;

        Vector3 d = target - origin;
        float R = new Vector2(d.x, d.z).magnitude;
        float dy = d.y;

        float horiz = new Vector2(launchDir.x, launchDir.z).magnitude;
        if (R < 0.01f || horiz < 0.0001f || g <= 0f) return false;

        float tanTheta = launchDir.y / horiz;
        float cosTheta = horiz / launchDir.magnitude;
        float denom = 2f * cosTheta * cosTheta * (R * tanTheta - dy);
        if (denom <= 0f) return false;

        speed = Mathf.Sqrt(g * R * R / denom);
        flightTime = R / (speed * cosTheta);
        return true;
    }

    /// <summary>
    /// 拋物線攔截：算出要打中（移動中的）目標，炮管該朝哪個方向、用多少初速、飛多久。
    /// 給 BallisticTurretController 用，其他要拋射的砲塔 / 敵人也可以直接呼叫。
    ///
    /// 仰角規則：取「目標仰角 + elevationMargin」，再夾在 [minElevationDeg, maxElevationDeg]。
    ///   例：高射砲 min 60、max 85、margin 10 → 平常 60°；目標在頭頂附近時抬高，避開頭頂盲區。
    ///
    /// inheritedVelocity：發射平台（船）的速度。艙的世界速度 = launchDir·speed + inheritedVelocity，
    ///   所以等效於瞄「目標預測位置 − 平台速度·T」。
    /// 移動目標：預測位置 = targetPos + targetVel·T；T 又跟瞄準點有關 → 反覆 iterations 次收斂。
    ///
    /// minSpeed：最低初速。照上面規則算出的初速低於它時，改用「以 minSpeed 打到目標的高弧線仰角」
    ///   （仰角變陡、初速提高到 minSpeed），但不超過 maxElevationDeg；
    ///   已經抬到最高仰角還是低於 minSpeed，就用最高仰角和它算出的初速。0 = 不限制。
    ///
    /// 回傳的 launchDir 是「炮管要指的方向」（不含平台速度）。無解（頭頂盲區、目標在正上方）回傳 false。
    /// </summary>
    public static bool SolveBallisticIntercept(
        Vector3 origin,
        Vector3 targetPos,
        Vector3 targetVel,
        Vector3 inheritedVelocity,
        float minElevationDeg,
        float maxElevationDeg,
        float elevationMarginDeg,
        float g,
        out Vector3 launchDir,
        out float speed,
        out float flightTime,
        int iterations = 6,
        float minSpeed = 0f)
    {
        launchDir = Vector3.zero;
        speed = 0f;
        flightTime = 0f;

        float t = 0f;
        bool solved = false;
        for (int i = 0; i < Mathf.Max(1, iterations); i++)
        {
            Vector3 aim = targetPos + (targetVel - inheritedVelocity) * t;
            Vector3 d = aim - origin;
            Vector3 horiz = new Vector3(d.x, 0f, d.z);
            float r = horiz.magnitude;
            if (r < 0.01f) return solved;   // 目標在正上 / 正下方：沒有水平方位可用

            float targetElev = Mathf.Atan2(d.y, r) * Mathf.Rad2Deg;
            float elev = Mathf.Clamp(Mathf.Max(minElevationDeg, targetElev + elevationMarginDeg),
                                     minElevationDeg, maxElevationDeg);
            float rad = elev * Mathf.Deg2Rad;
            Vector3 dir = horiz / r * Mathf.Cos(rad) + Vector3.up * Mathf.Sin(rad);

            if (!SolveLaunchSpeed(origin, aim, dir, g, out float s, out float tt))
                return false;   // 目標在這條仰角線上方（頭頂盲區）

            // 初速太低 → 改用以 minSpeed 打到目標的高弧線（仰角變陡）
            if (minSpeed > 0f && s < minSpeed &&
                SolveHighArcElevation(origin, aim, minSpeed, g, out float highElev) && highElev > elev)
            {
                elev = Mathf.Min(highElev, maxElevationDeg);
                rad = elev * Mathf.Deg2Rad;
                dir = horiz / r * Mathf.Cos(rad) + Vector3.up * Mathf.Sin(rad);
                if (!SolveLaunchSpeed(origin, aim, dir, g, out s, out tt))
                    return false;
            }

            launchDir = dir;
            speed = s;
            flightTime = tt;
            t = tt;
            solved = true;
        }
        return solved;
    }

    /// <summary>
    /// 初速固定、一律高弧線的攔截（迫擊砲）：由初速反推仰角，算出炮管方向和飛行時間。
    ///
    /// 跟 SolveBallisticIntercept 一樣處理移動目標與平台速度（瞄「目標預測位置 − 平台速度·T」，反覆收斂）。
    /// 仰角落在 [minElevationDeg, maxElevationDeg] 之外就當作打不到：
    ///   超過 max = 目標太近（炮管要接近垂直）；最大射程 ≈ speed² / g（45°）以外也打不到。
    /// </summary>
    public static bool SolveFixedSpeedHighArcIntercept(
        Vector3 origin,
        Vector3 targetPos,
        Vector3 targetVel,
        Vector3 inheritedVelocity,
        float speed,
        float g,
        float minElevationDeg,
        float maxElevationDeg,
        out Vector3 launchDir,
        out float flightTime,
        int iterations = 6)
    {
        launchDir = Vector3.zero;
        flightTime = 0f;

        float t = 0f;
        bool solved = false;
        for (int i = 0; i < Mathf.Max(1, iterations); i++)
        {
            Vector3 aim = targetPos + (targetVel - inheritedVelocity) * t;
            Vector3 d = aim - origin;
            Vector3 horiz = new Vector3(d.x, 0f, d.z);
            float r = horiz.magnitude;
            if (r < 0.01f) return false;   // 正上 / 正下方

            if (!SolveHighArcElevation(origin, aim, speed, g, out float elev)) return false;   // 射程外
            if (elev < minElevationDeg || elev > maxElevationDeg) return false;               // 太近（太陡）

            float rad = elev * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rad);
            launchDir = horiz / r * cos + Vector3.up * Mathf.Sin(rad);
            flightTime = r / (speed * cos);
            t = flightTime;
            solved = true;
        }
        return solved;
    }

    /// <summary>
    /// 初速固定時，打到 target 的「高弧線」仰角（度）。初速不夠、怎樣都打不到時回傳 false。
    ///   tanθ = (v² + √(v⁴ − g(g·R² + 2·Δy·v²))) / (g·R)
    /// （GetLaunchDirection 用的是同一條式子取 − 號 = 低弧線。）
    /// </summary>
    public static bool SolveHighArcElevation(Vector3 origin, Vector3 target, float speed, float g, out float elevationDeg)
    {
        elevationDeg = 0f;
        Vector3 d = target - origin;
        float R = new Vector2(d.x, d.z).magnitude;
        if (R < 0.01f || g <= 0f || speed <= 0f) return false;

        float v2 = speed * speed;
        float disc = v2 * v2 - g * (g * R * R + 2f * d.y * v2);
        if (disc < 0f) return false;

        elevationDeg = Mathf.Atan((v2 + Mathf.Sqrt(disc)) / (g * R)) * Mathf.Rad2Deg;
        return true;
    }

    /// <summary>
    /// 只靠重力、在 time 秒後從 from 到達 to 需要的速度：v = (to − from) / t − ½·g·t。
    /// gravity 是向量（例如 Physics.gravity）。拋物線誘導用：把目前速度往這個速度修正。
    /// </summary>
    public static Vector3 BallisticVelocityToReach(Vector3 from, Vector3 to, float time, Vector3 gravity)
    {
        time = Mathf.Max(0.01f, time);
        return (to - from) / time - 0.5f * time * gravity;
    }

    public static int SolveQuadratic(float a, float b, float c, out float root1, out float root2)
    {
        float discriminant = b * b - 4 * a * c;
        if (discriminant < 0)
        {
            root1 = Mathf.Infinity;
            root2 = -root1;
            return 0;
        }
        root1 = (-b + Mathf.Sqrt(discriminant)) / (2 * a);
        root2 = (-b - Mathf.Sqrt(discriminant)) / (2 * a);
        return discriminant > 0 ? 2 : 1;
    }

    public static Vector3 GetPointAtTargetBack(Transform a, Transform b, float distance = 10f)
    {
        Vector3 offset = b.position - a.position;
        offset.y = 0f;
        if (offset.sqrMagnitude < 0.0001f) return b.position;
        return b.position + offset.normalized * distance;
    }
    public static Vector3 GetPointAtTargetBack(Vector3 a, Vector3 b, float distance = 10f)
    {
        Vector3 offset = b - a;
        offset.y = 0f;
        if (offset.sqrMagnitude < 0.0001f) return b;
        return b + offset.normalized * distance;
    }
    public static Vector3 GetRandomPointInRing(Vector3 a, float outerRadius, float innerRadius)
    {

        // 一樣用 Sqrt 對 r² 均勻取樣,讓點平均鋪滿整個紅環
        float r = Mathf.Sqrt(Random.Range(innerRadius * innerRadius, outerRadius * outerRadius));
        float angle = Random.Range(0f, Mathf.PI * 2f);

        float offsetX = r * Mathf.Cos(angle);
        float offsetZ = r * Mathf.Sin(angle);

        // 放在 XZ 平面,高度沿用中心點 a.y
        return new Vector3(a.x + offsetX, a.y, a.z + offsetZ);
    }
}
