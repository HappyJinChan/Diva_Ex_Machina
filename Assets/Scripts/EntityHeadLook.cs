using System;
using UnityEngine;

/// <summary>
/// 頭部注視：讓實體的頭（連同脖子）自然地看向 Brain 指定的目標。
///
/// 掛在有 Animator 的物件上。在 LateUpdate（動畫套用之後）把轉向依比例分給
/// 「頸椎鏈」上的每一節骨頭：越靠近頭的骨頭通常分越多，最後一節（頭）補足剩下的角度，
/// 所以頭會準確對準目標，脖子則跟著轉一部分。動畫本身的擺動會保留。
///
/// 通用設定：
///   · 頸椎有幾節就在 Bones 裡加幾個，從脖子根部排到頭（最後一個必須是頭）
///   · 每節的 Share 是分配比例，會自動正規化（不用剛好加總為 1）
///   · 目標來自 ModularEntityBrain.LookTarget，換目標來源只要改 Brain
///   · 臉朝向：預設以 Facing Reference 的正前方（Z 軸）當作頭在預設姿勢下的朝向
/// </summary>
public class EntityHeadLook : MonoBehaviour
{
    [Serializable]
    public struct LookBone
    {
        public Transform bone;
        [Min(0f)] public float share;
    }

    [Header("頸椎鏈（從脖子根部排到頭，最後一個是頭）")]
    [SerializeField] private LookBone[] bones = new LookBone[0];

    [Header("參考")]
    [Tooltip("提供 LookTarget 的 Brain。留空會往父物件找。")]
    [SerializeField] private ModularEntityBrain brain;
    [Tooltip("身體的正前方。留空 = 這個物件本身。左右 / 上下的角度限制以它為基準。")]
    [SerializeField] private Transform facingReference;

    [Header("角度限制（相對於身體正前方）")]
    [SerializeField, Range(0f, 179f)] private float maxYaw = 70f;
    [SerializeField, Range(0f, 89f)] private float maxPitchUp = 35f;
    [SerializeField, Range(0f, 89f)] private float maxPitchDown = 30f;
    [Tooltip("目標超出這個左右角度（例如在正後方）就不看，避免頭卡在極限抖動。")]
    [SerializeField, Range(0f, 180f)] private float giveUpYaw = 120f;

    [Header("手感")]
    [Tooltip("轉頭的最大角速度（度/秒）。")]
    [SerializeField] private float turnSpeed = 240f;
    [Tooltip("開始看 / 停止看的淡入淡出時間（秒）。")]
    [SerializeField] private float blendTime = 0.25f;
    [Tooltip("整體強度。0 = 完全照動畫，1 = 完全看向目標。")]
    [SerializeField, Range(0f, 1f)] private float weight = 1f;
    [Tooltip("模型不在畫面上時跳過計算。")]
    [SerializeField] private bool skipWhenInvisible = true;

    private Transform _head;
    private Vector3 _faceLocal;          // 臉朝向在頭骨頭自身座標中的方向（從預設姿勢算出）
    private Renderer _renderer;
    private float _blend;
    private float _yaw, _pitch;
    private bool _ready;

    // 目標的瞄準點快取（目標沒換就不重新找碰撞體）
    private Transform _cachedTarget;
    private Collider _cachedCollider;

    private void Awake()
    {
        if (facingReference == null) facingReference = transform;
        if (brain == null) brain = GetComponentInParent<ModularEntityBrain>();
        _renderer = GetComponentInChildren<Renderer>();

        if (bones == null || bones.Length == 0 || bones[bones.Length - 1].bone == null)
        {
            Debug.LogWarning($"[EntityHeadLook] {name}：頸椎鏈是空的，或最後一節（頭）沒有指定，停用。", this);
            enabled = false;
            return;
        }
        if (brain == null)
            Debug.LogWarning($"[EntityHeadLook] {name}：找不到 ModularEntityBrain，頭不會轉。", this);

        // Awake 時骨頭還是 prefab 的預設姿勢：把身體的正前方換算成頭骨頭座標中的臉朝向，
        // 之後不論骨頭的軸向怎麼設定都能用。
        _head = bones[bones.Length - 1].bone;
        _faceLocal = _head.InverseTransformDirection(facingReference.forward).normalized;
        _ready = true;
    }

    private void OnEnable()
    {
        _blend = 0f;
        _yaw = _pitch = 0f;
    }

    private void LateUpdate()
    {
        if (!_ready) return;
        if (skipWhenInvisible && _renderer != null && !_renderer.isVisible) return;

        float dt = Time.deltaTime;
        Transform target = brain != null ? brain.LookTarget : null;

        // ── 目標在身體座標中的 yaw / pitch ──
        bool wantLook = false;
        float wantYaw = 0f, wantPitch = 0f;
        if (target != null && target.gameObject.activeInHierarchy)
        {
            Vector3 dir = facingReference.InverseTransformDirection(AimPointOf(target) - _head.position);
            if (dir.sqrMagnitude > 0.0001f)
            {
                dir.Normalize();
                float yaw = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
                float pitch = Mathf.Asin(Mathf.Clamp(dir.y, -1f, 1f)) * Mathf.Rad2Deg;
                if (Mathf.Abs(yaw) <= giveUpYaw)
                {
                    wantLook = true;
                    wantYaw = Mathf.Clamp(yaw, -maxYaw, maxYaw);
                    wantPitch = Mathf.Clamp(pitch, -maxPitchDown, maxPitchUp);
                }
            }
        }

        // ── 平滑：淡入淡出＋轉頭速度上限；沒有目標時回正 ──
        _blend = Mathf.MoveTowards(_blend, wantLook ? 1f : 0f, dt / Mathf.Max(0.01f, blendTime));
        float step = turnSpeed * dt;
        _yaw = Mathf.MoveTowardsAngle(_yaw, wantLook ? wantYaw : 0f, step);
        _pitch = Mathf.MoveTowardsAngle(_pitch, wantLook ? wantPitch : 0f, step);

        float w = _blend * weight;
        if (w <= 0.001f) return;

        Vector3 lookWorld = facingReference.TransformDirection(Quaternion.Euler(-_pitch, _yaw, 0f) * Vector3.forward);

        // ── 依比例把轉向分給每一節（從根部到頭）──
        float remaining = 0f;
        int valid = 0;
        for (int i = 0; i < bones.Length; i++)
        {
            if (bones[i].bone == null) continue;
            remaining += bones[i].share;
            valid++;
        }
        bool equalShare = remaining <= 0f;          // 全部填 0 → 平均分配
        if (equalShare) remaining = valid;

        for (int i = 0; i < bones.Length; i++)
        {
            Transform b = bones[i].bone;
            if (b == null) continue;
            float s = equalShare ? 1f : bones[i].share;
            if (s <= 0f) continue;

            // 前面的骨頭轉過之後頭會跟著動，所以每一節都用「目前的臉朝向」重算
            Vector3 faceNow = _head.TransformDirection(_faceLocal);
            Quaternion full = Quaternion.FromToRotation(faceNow, lookWorld);
            b.rotation = Quaternion.Slerp(Quaternion.identity, full, (s / remaining) * w) * b.rotation;
            remaining -= s;
            if (remaining <= 0f) break;
        }
    }

    /// <summary>目標的注視點：有碰撞體看碰撞體中心，否則看原點。</summary>
    private Vector3 AimPointOf(Transform target)
    {
        if (target != _cachedTarget)
        {
            _cachedTarget = target;
            _cachedCollider = target.GetComponentInChildren<Collider>();
        }
        if (_cachedCollider != null && _cachedCollider.enabled)
            return _cachedCollider.bounds.center;
        return target.position;
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (bones == null) return;
        for (int i = 0; i < bones.Length; i++)
            if (bones[i].share < 0f) bones[i].share = 0f;
    }
#endif
}
