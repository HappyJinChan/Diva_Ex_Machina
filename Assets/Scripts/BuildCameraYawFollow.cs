using Unity.Cinemachine;
using UnityEngine;

/// <summary>
/// 建造視窗的俯視鏡頭跟著船的航向（Yaw）轉，讓船在畫面上永遠朝同一個方向（預設：船頭朝上）。
///
/// 掛在 CinemachineCamera-Building 上。Position Composer 只管位置，不管旋轉；
/// 鏡頭的旋轉原本是固定的世界方向，所以船沿航線轉彎後，在畫面上看起來就是歪的。
/// 這裡每幀把鏡頭的 Yaw 設成船的 Yaw，俯角（X）維持開始時的值，不側傾。
///
/// 注意：CinemachineCamera 的 Rotation Control 要是 None（不然會被 Cinemachine 覆蓋）。
/// </summary>
[DefaultExecutionOrder(-50)]   // 在 CinemachineBrain（LateUpdate）讀取鏡頭姿態之前執行
[RequireComponent(typeof(CinemachineCamera))]
public class BuildCameraYawFollow : MonoBehaviour
{
    [Tooltip("要對齊的船。留空 = 用 CinemachineCamera 的 Tracking Target（Follow）。")]
    public Transform target;

    [Tooltip("額外旋轉（度）。船頭沒有朝上時調這個：90 / 180 / -90。")]
    public float yawOffset = 0f;

    private CinemachineCamera _vcam;
    private float _pitch;

    void Awake()
    {
        _vcam = GetComponent<CinemachineCamera>();
        _pitch = transform.eulerAngles.x;   // 俯角（俯視鏡頭通常是 90）
    }

    void LateUpdate()
    {
        Transform t = target != null ? target : (_vcam != null ? _vcam.Follow : null);
        if (t == null) return;

        Vector3 f = t.forward;
        f.y = 0f;
        if (f.sqrMagnitude < 0.000001f) return;

        float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg + yawOffset;
        transform.rotation = Quaternion.Euler(_pitch, yaw, 0f);
    }
}
