using UnityEngine;

/// <summary>
/// 恐狼的指揮陣形：三隻部下，以目標的「視線」為基準包圍站位。
///
///   C（背後，最優先）：永遠追在目標視線的正後方。
///   A（左手）：追在目標視線的左手方向。
///   B（對角）：在目標的右手方向，隔著目標和 A 相對。
///   部下不足三隻時依 C > A > B 的順序填位置；陣亡後沒人可補時往前遞補（基底處理）。
///
/// 只覆寫站位，出招交給部下自己的近戰判斷。
///
/// 視線：目標是玩家時用鏡頭的水平方向；其他目標用它自己的 forward。
/// </summary>
public class PincerCommand : EntityCommander
{
    private const int SlotBack = 0;    // C
    private const int SlotLeft = 1;    // A
    private const int SlotRight = 2;   // B

    [Header("Pincer: Formation")]
    [Tooltip("C 站在目標視線後方多遠（公尺）。")]
    [SerializeField, Min(0f)] private float backDistance = 10f;
    [Tooltip("A / B 站在目標左右多遠（公尺）。")]
    [SerializeField, Min(0f)] private float flankDistance = 12f;
    [Tooltip("目標是玩家時，用鏡頭的水平方向當作視線；否則用目標自己的 forward。")]
    [SerializeField] private bool useCameraForPlayer = true;

    protected override int SlotCount => 3;

    private Camera _camera;

    protected override Vector3 GetSlotPosition(int slot)
    {
        GetViewBasis(out Vector3 forward, out Vector3 right);
        Vector3 t = target != null ? target.position : transform.position;

        switch (slot)
        {
            case SlotBack:  return t - forward * backDistance;
            case SlotLeft:  return t - right * flankDistance;
            case SlotRight: return t + right * flankDistance;
            default:        return t;
        }
    }

    protected override bool TryGetSlotDestination(int slot, ModularEntityBrain unit, out Vector3 destination)
    {
        destination = GetSlotPosition(slot);
        return true;
    }

    /// <summary>目標的視線（水平化）與右手方向。</summary>
    private void GetViewBasis(out Vector3 forward, out Vector3 right)
    {
        Vector3 f = Vector3.zero;
        if (target != null)
        {
            if (useCameraForPlayer && target.CompareTag("Player"))
            {
                if (_camera == null) _camera = Camera.main;
                if (_camera != null) f = _camera.transform.forward;
            }
            f.y = 0f;
            if (f.sqrMagnitude < 0.0001f) f = target.forward;
        }

        f.y = 0f;
        if (f.sqrMagnitude < 0.0001f) f = Vector3.forward;
        forward = f.normalized;
        right = Vector3.Cross(Vector3.up, forward);
    }
}
