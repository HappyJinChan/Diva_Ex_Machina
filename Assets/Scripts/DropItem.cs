using UnityEngine;

/// <summary>
/// 地上的掉落物。掛在掉落物 prefab（ItemObject.dropPrefab）的根物件上。
///
/// 流程：
///   生成（EntityLootDrop → PrefabPool.Spawn → Launch）→ 往上、往四周彈出，受重力落下
///   → 落地停住（落在船甲板上就黏著甲板跟船走），上下浮動、自轉
///   → 生成 Magnet Delay 秒之後，玩家進入 Magnet Radius 就加速飛向玩家（之後不會再放開）
///   → 碰到玩家的 Trigger → PlayerPickup 收進背包 → 回到物件池。
///   沒被吸走的話，Lifetime 秒後自己消失。
///
/// prefab 需要：勾 Is Trigger 的 Collider、Tag = "DropItem"、Kinematic Rigidbody（移動 Trigger 比較便宜）。
/// 玩家那邊需要 PlayerPickup（見 PlayerPickup 的說明）。
/// </summary>
public class DropItem : MonoBehaviour
{
    [Tooltip("這個掉落物代表的物品。由 EntityLootDrop 生成時指定；直接擺在場景裡的掉落物在這裡設定。")]
    public ItemObject item;

    [Tooltip("數量。")]
    [Min(1)] public int amount = 1;

    [Header("Motion")]
    [SerializeField] private float gravity = 25f;
    [Tooltip("落地後離地多高（公尺）。")]
    [SerializeField, Min(0f)] private float hoverHeight = 0.6f;
    [SerializeField, Min(0f)] private float bobAmplitude = 0.15f;
    [SerializeField, Min(0f)] private float bobSpeed = 2.5f;
    [Tooltip("自轉速度（度/秒）。")]
    [SerializeField] private float spinSpeed = 90f;
    [Tooltip("可以落在哪些層上。Nothing = Default / Ground / Mobile Platform / Obstacle / Defence Fortifications。")]
    [SerializeField] private LayerMask groundMask;

    [Header("Magnet")]
    [Tooltip("生成後幾秒才開始吸附（先讓玩家看到它噴出來）。")]
    [SerializeField, Min(0f)] private float magnetDelay = 0.5f;
    [Tooltip("玩家在這個距離（公尺）內就開始吸過去。")]
    [SerializeField, Min(0f)] private float magnetRadius = 30f;
    [SerializeField, Min(0f)] private float magnetAcceleration = 120f;
    [SerializeField, Min(1f)] private float magnetMaxSpeed = 80f;

    [Header("Lifetime")]
    [Tooltip("沒被撿走的話，幾秒後消失。0 = 永遠不消失。")]
    [SerializeField, Min(0f)] private float lifetime = 120f;

    private enum State { Flying, Landed, Magnet }

    private State _state;
    private Vector3 _velocity;
    private float _spawnTime;
    private AimSpot _landSpot;      // 落地點（可能黏在甲板上）
    private float _bobPhase;
    private bool _collected;
    private static bool s_FallbackWarned;

    /// <summary>已經被收走（避免同一幀被觸發兩次）。</summary>
    public bool IsCollected => _collected;

    private void OnEnable()
    {
        _state = State.Flying;
        _velocity = Vector3.zero;
        _spawnTime = Time.time;
        _landSpot = null;
        _collected = false;
        _bobPhase = Random.value * Mathf.PI * 2f;
        if (groundMask.value == 0)
        {
            int m = LayerMask.GetMask("Default", "Ground", "Mobile Platform", "Obstacle", "Defence Fortifications");
            groundMask = m != 0 ? m : Physics.DefaultRaycastLayers;
        }
    }

    /// <summary>EntityLootDrop 生成後呼叫：指定物品、數量、彈出的初速。</summary>
    public void Launch(ItemObject newItem, int newAmount, Vector3 velocity)
    {
        item = newItem;
        amount = Mathf.Max(1, newAmount);
        _velocity = velocity;
        _state = State.Flying;
    }

    private void Update()
    {
        if (_collected) return;
        float dt = Time.deltaTime;

        if (_state != State.Magnet && lifetime > 0f && Time.time - _spawnTime > lifetime)
        {
            PrefabPool.Despawn(gameObject);
            return;
        }

        PlayerPickup picker = PlayerPickup.Instance;
        Vector3 target = picker != null ? picker.CollectPoint : Vector3.zero;

        // 吸附：開始之後就不再放開（玩家跑出半徑也一樣追過去）
        if (_state != State.Magnet && picker != null && Time.time - _spawnTime >= magnetDelay &&
            (target - transform.position).sqrMagnitude <= magnetRadius * magnetRadius)
        {
            _state = State.Magnet;
            if (_landSpot != null) _velocity = _landSpot.Velocity;   // 從地上起飛：從靜止（甲板上帶著船速）開始加速
        }

        switch (_state)
        {
            case State.Flying: UpdateFlying(dt); break;
            case State.Landed: UpdateLanded(); break;
            case State.Magnet: UpdateMagnet(dt, picker, target); break;
        }

        transform.Rotate(0f, spinSpeed * dt, 0f, Space.World);
    }

    private void UpdateFlying(float dt)
    {
        _velocity += Vector3.down * gravity * dt;
        Vector3 from = transform.position;
        Vector3 step = _velocity * dt;

        // 往下掉的時候才檢查落地；射線多往下看 hoverHeight，停在離地 hoverHeight 的高度
        if (_velocity.y <= 0f &&
            Physics.Raycast(from, step.normalized, out RaycastHit hit, step.magnitude + hoverHeight, groundMask, QueryTriggerInteraction.Ignore))
        {
            _landSpot = AimSpot.FromHit(hit);
            _state = State.Landed;
            UpdateLanded();
            return;
        }
        transform.position = from + step;
    }

    private void UpdateLanded()
    {
        float bob = Mathf.Sin(Time.time * bobSpeed + _bobPhase) * bobAmplitude;
        transform.position = _landSpot.Position + Vector3.up * (hoverHeight + bob);
    }

    private void UpdateMagnet(float dt, PlayerPickup picker, Vector3 target)
    {
        if (picker == null) { _state = _landSpot != null ? State.Landed : State.Flying; return; }

        Vector3 to = target - transform.position;
        float dist = to.magnitude;
        Vector3 dir = dist > 0.001f ? to / dist : Vector3.up;

        // 直線加速飛向目標；最後一段不會衝過頭（到了就停在目標點）
        float speed = Mathf.Min(magnetMaxSpeed, _velocity.magnitude + magnetAcceleration * dt);
        _velocity = dir * speed;
        Vector3 step = _velocity * dt;

        if (step.magnitude >= dist)
        {
            transform.position = target;
            // 正常情況 PlayerPickup 的 Trigger 會先收走；到了目標點還沒被收，代表 Trigger 設定有問題 → 直接交給它
            if (!s_FallbackWarned)
            {
                s_FallbackWarned = true;
                Debug.LogWarning("DropItem: 掉落物飛到玩家身上了，但 PlayerPickup 的 Trigger 沒有觸發。" +
                                 "請檢查：掉落物有勾 Is Trigger 的 Collider、Tag = DropItem、" +
                                 "玩家有 Collider + Rigidbody、Layer Collision Matrix 兩邊的層會互相碰撞。這次先直接收進背包。", this);
            }
            picker.Collect(this);
            return;
        }
        transform.position += step;
    }

    /// <summary>PlayerPickup 收走時呼叫。回傳 false = 已經被收過。</summary>
    public bool MarkCollected()
    {
        if (_collected) return false;
        _collected = true;
        return true;
    }
}
