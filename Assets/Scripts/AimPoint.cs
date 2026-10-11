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

    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(1f, 0.25f, 0.2f, 0.9f);
        Gizmos.DrawWireSphere(transform.position, gizmoRadius);
        Gizmos.DrawLine(transform.position - Vector3.right * gizmoRadius * 1.6f, transform.position + Vector3.right * gizmoRadius * 1.6f);
        Gizmos.DrawLine(transform.position - Vector3.up * gizmoRadius * 1.6f, transform.position + Vector3.up * gizmoRadius * 1.6f);
        Gizmos.DrawLine(transform.position - Vector3.forward * gizmoRadius * 1.6f, transform.position + Vector3.forward * gizmoRadius * 1.6f);
    }
}
