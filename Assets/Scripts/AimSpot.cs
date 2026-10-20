using UnityEngine;

/// <summary>
/// 一個「可能會動」的瞄準點：地面上的一點，或黏在會動的物體（例如貝努鳥號的甲板）上的一點。
///
///   · 打到沒有 Rigidbody 的東西（地形、靜止的建築）→ 固定的世界座標。
///   · 打到有 Rigidbody 的東西（船、載具）→ 記住在那個物體上的本地座標，
///     之後每次讀 Position 都換算回當下的世界座標，Velocity 是那一點實際的移動速度（含旋轉）。
///     物體被銷毀後停在最後的位置。
///
/// 是 class（參考型別）：多個地方共用同一個點時，最後位置的快取會一起更新。
/// </summary>
public sealed class AimSpot
{
    private readonly Transform _anchor;
    private readonly Rigidbody _anchorRb;
    private readonly Vector3 _local;
    private Vector3 _lastWorld;

    private AimSpot(Vector3 world, Rigidbody anchorRb)
    {
        _lastWorld = world;
        if (anchorRb != null)
        {
            _anchorRb = anchorRb;
            _anchor = anchorRb.transform;
            _local = _anchor.InverseTransformPoint(world);
        }
    }

    /// <summary>固定的世界座標點。</summary>
    public static AimSpot Static(Vector3 world) => new AimSpot(world, null);

    /// <summary>從射線命中建立：打到有 Rigidbody 的東西就黏在它上面。</summary>
    public static AimSpot FromHit(RaycastHit hit) => new AimSpot(hit.point, hit.rigidbody);

    /// <summary>
    /// 被打死的目標最後的位置：往下打一條射線，腳下是會動的平台（Mobile Platform 層，例如甲板）
    /// 就黏在平台上跟著走；不是就是固定點。
    /// </summary>
    public static AimSpot AnchoredBelow(Vector3 world, float maxDistance = 30f)
    {
        int mask = LayerMask.GetMask("Mobile Platform");
        if (mask == 0) mask = ~0;   // 專案裡沒有這個層：什麼都算

        if (Physics.Raycast(world + Vector3.up * 0.5f, Vector3.down, out RaycastHit hit,
                            maxDistance + 0.5f, mask, QueryTriggerInteraction.Ignore)
            && hit.rigidbody != null)
        {
            return new AimSpot(world, hit.rigidbody);   // 黏的是目標原本的位置，不是射線打到的點
        }
        return Static(world);
    }

    /// <summary>黏在會動的物體上（物體還在）。</summary>
    public bool IsMoving => _anchor != null;

    /// <summary>現在的世界座標。</summary>
    public Vector3 Position
    {
        get
        {
            if (_anchor != null) _lastWorld = _anchor.TransformPoint(_local);
            return _lastWorld;
        }
    }

    /// <summary>這一點現在的移動速度（物體的平移 + 旋轉）。固定點 = 0。</summary>
    public Vector3 Velocity => _anchorRb != null ? _anchorRb.GetPointVelocity(Position) : Vector3.zero;
}
