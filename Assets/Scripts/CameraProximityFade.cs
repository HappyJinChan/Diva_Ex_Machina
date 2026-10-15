using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 鏡頭離角色太近時，讓角色迅速「網點淡出」，避免鏡頭穿進模型裡面。
///
/// 用法：掛在玩家身上（任何一層都可以），把角色模型的根物件拖進 Model Root。
/// 角色的材質必須使用 "Toon/Toon (Motion Vectors)"（Assets/Shaders/UnityToonMotionVectors.shader），
/// 這個 shader 才有網點淡出；其他 shader 會忽略這個效果。
///
/// 作法：
///   · 每幀量「鏡頭 → 角色」的距離：有指定 Proximity Collider 就量到它表面最近的點，
///     沒有的話量到 Focus Point（例如胸口）。
///   · 距離 ≤ Fade End Distance 時完全消失，≥ Fade Start Distance 時完全顯示，中間線性過渡，
///     再用 Fade Speed 平滑追上（快速淡入、快速淡出）。
///   · 用 MaterialPropertyBlock 把 _DitherFade 寫進每個 Renderer，不會改到材質資產。
///   · 武器、裝甲是執行時才生成的，所以每隔 Rescan Interval 秒重新收集一次 Renderer。
///
/// 影子（ShadowCaster）不會淡出：角色消失時地上的影子還在，比較不突兀。
/// </summary>
[DefaultExecutionOrder(1000)]   // 盡量排在鏡頭（CinemachineBrain）更新之後
public class CameraProximityFade : MonoBehaviour
{
    [Header("References")]
    [Tooltip("要淡出的角色模型根物件。底下所有的 MeshRenderer / SkinnedMeshRenderer（含之後裝上的武器、裝甲）都會一起淡出。")]
    [SerializeField] private Transform modelRoot;

    [Tooltip("量距離用的碰撞體（例如玩家的 CapsuleCollider）。量到它表面最近的點，比量到一個點更準。\n" +
             "留空的話改用 Focus Point。")]
    [SerializeField] private Collider proximityCollider;

    [Tooltip("沒有 Proximity Collider 時，量到這個點的距離（建議放在胸口）。也留空就用這個物件自己的位置。")]
    [SerializeField] private Transform focusPoint;

    [Tooltip("留空 = Camera.main。")]
    [SerializeField] private Camera targetCamera;

    [Header("Distance")]
    [Tooltip("鏡頭距離小於這個值開始淡出（公尺）。")]
    [Min(0f)] [SerializeField] private float fadeStartDistance = 1.2f;

    [Tooltip("鏡頭距離小於這個值完全消失（公尺）。")]
    [Min(0f)] [SerializeField] private float fadeEndDistance = 0.5f;

    [Header("Speed")]
    [Tooltip("淡出 / 淡入的速度（每秒變化量，1 = 一秒從完全顯示到完全消失）。越大越快。")]
    [Min(0.01f)] [SerializeField] private float fadeSpeed = 8f;

    [Header("Renderers")]
    [Tooltip("每隔幾秒重新收集一次 Renderer（裝備換了也能跟上）。0 = 只在啟用時收集一次。")]
    [Min(0f)] [SerializeField] private float rescanInterval = 1f;

    private static readonly int DitherFadeId = Shader.PropertyToID("_DitherFade");

    private readonly List<Renderer> _renderers = new List<Renderer>();
    private readonly List<Renderer> _scanBuffer = new List<Renderer>();
    private MaterialPropertyBlock _mpb;
    private float _fade;          // 目前的淡出量 0～1
    private float _appliedFade = -1f;
    private float _rescanTimer;

    /// <summary>目前的淡出量（0 = 完全顯示，1 = 完全消失）。</summary>
    public float CurrentFade => _fade;

    private void Awake()
    {
        _mpb = new MaterialPropertyBlock();
        if (modelRoot == null) modelRoot = transform;
    }

    private void OnEnable()
    {
        Rescan();
        _rescanTimer = rescanInterval;
    }

    private void OnDisable()
    {
        // 停用時恢復完全顯示，免得角色卡在半透明
        _fade = 0f;
        Apply(0f, force: true);
    }

    private void LateUpdate()
    {
        if (rescanInterval > 0f)
        {
            _rescanTimer -= Time.unscaledDeltaTime;
            if (_rescanTimer <= 0f)
            {
                _rescanTimer = rescanInterval;
                if (Rescan()) _appliedFade = -1f;   // 有新的 Renderer：重新寫一次
            }
        }

        Camera cam = targetCamera != null ? targetCamera : Camera.main;
        if (cam == null) return;

        float target = ComputeTargetFade(cam.transform.position);

        // 用 unscaledDeltaTime：hitstop 時間凍結時也照常淡出，鏡頭不會卡在模型裡
        _fade = Mathf.MoveTowards(_fade, target, fadeSpeed * Time.unscaledDeltaTime);

        Apply(_fade, force: false);
    }

    private float ComputeTargetFade(Vector3 camPos)
    {
        float distance;
        if (proximityCollider != null && proximityCollider.enabled)
        {
            // 鏡頭在碰撞體裡面時 ClosestPoint 回傳鏡頭自己的位置 → 距離 0 → 完全消失
            Vector3 closest = proximityCollider.ClosestPoint(camPos);
            distance = Vector3.Distance(camPos, closest);
        }
        else
        {
            Vector3 p = focusPoint != null ? focusPoint.position : transform.position;
            distance = Vector3.Distance(camPos, p);
        }

        float start = Mathf.Max(fadeStartDistance, fadeEndDistance + 0.0001f);
        // distance ≥ start → 0；distance ≤ end → 1
        return 1f - Mathf.Clamp01((distance - fadeEndDistance) / (start - fadeEndDistance));
    }

    private void Apply(float fade, bool force)
    {
        if (!force && Mathf.Abs(fade - _appliedFade) < 0.001f) return;
        _appliedFade = fade;

        for (int i = 0; i < _renderers.Count; i++)
        {
            Renderer r = _renderers[i];
            if (r == null) continue;

            // 先讀回來再寫：其他系統若也用了 MaterialPropertyBlock，不會被蓋掉
            r.GetPropertyBlock(_mpb);
            _mpb.SetFloat(DitherFadeId, fade);
            r.SetPropertyBlock(_mpb);
        }
    }

    /// <summary>重新收集 modelRoot 底下的 Renderer。清單有變動時回傳 true。</summary>
    private bool Rescan()
    {
        if (modelRoot == null) return false;

        _scanBuffer.Clear();
        modelRoot.GetComponentsInChildren(true, _scanBuffer);

        // 只要網格類的 Renderer；粒子、拖尾、LineRenderer 不處理
        for (int i = _scanBuffer.Count - 1; i >= 0; i--)
        {
            Renderer r = _scanBuffer[i];
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer))
                _scanBuffer.RemoveAt(i);
        }

        bool changed = _scanBuffer.Count != _renderers.Count;
        if (!changed)
        {
            for (int i = 0; i < _scanBuffer.Count; i++)
            {
                if (_scanBuffer[i] != _renderers[i]) { changed = true; break; }
            }
        }

        if (changed)
        {
            _renderers.Clear();
            _renderers.AddRange(_scanBuffer);
        }
        return changed;
    }

    private void OnValidate()
    {
        if (fadeStartDistance < fadeEndDistance) fadeStartDistance = fadeEndDistance;
    }
}
