using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 敵人的瞄準點。
///
/// 在敵人 prefab 底下建立一個空物件，掛上這個元件，擺在你希望玩家瞄準的位置
/// （身體中心、弱點、駕駛艙……）。PlayerAiming 的準星、子彈落點與鎖定鏡頭都會對準它。
/// 沒有 AimPoint 的敵人維持原本的行為，瞄準 transform 原點。
///
/// 可以放在會動的骨頭底下（例如 Spine2），瞄準點就會跟著動畫移動。
/// 一個敵人只會用到一個 AimPoint（找到的第一個）。
/// </summary>
[DisallowMultipleComponent]
public class AimPoint : MonoBehaviour
{
    [Tooltip("Scene 視窗中標示用的球體大小（只影響編輯器顯示）。")]
    [SerializeField] private float gizmoRadius = 0.3f;

    // ── 給 AI / 砲塔共用的查詢 ─────────────────────────────────────────
    //
    // 砲塔（FirearmControlSystem / TurretRadar）原本直接瞄 target.position ——
    // 敵人的 pivot 在腳底，所以會打腿。改成跟玩家一樣瞄 AimPoint。

    private static readonly Dictionary<Transform, AimPoint> _cache = new Dictionary<Transform, AimPoint>();
    private const int CacheLimit = 512;

    /// <summary>
    /// 目標的瞄準位置：有 AimPoint 就用它，沒有就退回 transform 原點（跟 PlayerAiming 一樣）。
    /// 結果依 target 快取，不會每幀 GetComponentInChildren。
    /// </summary>
    public static Vector3 PositionOf(Transform target)
    {
        if (target == null) return Vector3.zero;

        if (!_cache.TryGetValue(target, out AimPoint ap))
        {
            // 敵人如果是 Destroy 而不是回收，快取會越長越大；超過上限就清掉重建
            if (_cache.Count >= CacheLimit) _cache.Clear();
            ap = target.GetComponentInChildren<AimPoint>();
            _cache[target] = ap;   // 找不到也記下 null
        }

        return ap != null ? ap.transform.position : target.position;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, gizmoRadius);
        Gizmos.DrawLine(transform.position - Vector3.right * gizmoRadius * 1.6f, transform.position + Vector3.right * gizmoRadius * 1.6f);
        Gizmos.DrawLine(transform.position - Vector3.up * gizmoRadius * 1.6f, transform.position + Vector3.up * gizmoRadius * 1.6f);
        Gizmos.DrawLine(transform.position - Vector3.forward * gizmoRadius * 1.6f, transform.position + Vector3.forward * gizmoRadius * 1.6f);
    }
}
