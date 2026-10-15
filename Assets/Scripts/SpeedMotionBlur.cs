using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class SpeedMotionBlur : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private PlayerMovement playerMovement;
    [SerializeField] private Volume volume;   // 拖你那顆 Global Volume 進來

    [Header("Dash Motion Blur")]
    [Tooltip("dash 時的模糊強度")]
    [Range(0f, 1f)][SerializeField] private float dashIntensity = 0.8f;
    [Tooltip("進入模糊的速度（越大衝進去越快）")]
    [SerializeField] private float blurInLerp = 20f;
    [Tooltip("退出模糊的速度（越小尾巴拖越長）")]
    [SerializeField] private float blurOutLerp = 8f;

    [Header("Assault Boost Motion Blur")]
    [Tooltip("突擊推進也套用模糊。往前高速移動時，畫面邊緣的移動量比中心大，自然形成「中心清楚、邊緣糊」。")]
    [SerializeField] private bool includeAssaultBoost = true;

    [Tooltip("突擊推進的模糊強度。推進會持續一段時間，太高會看不清楚前方。")]
    [Range(0f, 1f)][SerializeField] private float assaultBoostIntensity = 0.6f;

    [Header("Melee Dash Motion Blur")]
    [Tooltip("近戰突進也套用模糊")]
    [SerializeField] private bool includeMeleeDash = true;

    [Tooltip("近戰突進的模糊強度。突進比 dash 短很多，稍微調低一點比較不會太搶眼。")]
    [Range(0f, 1f)][SerializeField] private float meleeDashIntensity = 0.6f;

    [Tooltip("模糊延續到整段攻擊結束（滯空期間），而不是只有突進那 0.1~0.2 秒。\n" +
             "突進很短，只跟突進的話模糊會一閃就沒了。")]
    [SerializeField] private bool holdThroughMeleeHover = false;

    private MotionBlur _mb;

    void Start()
    {
        if (volume != null && volume.profile != null)
            volume.profile.TryGet(out _mb);
    }

    void LateUpdate()
    {
        if (_mb == null || playerMovement == null) return;

        float target = 0f;
        bool active = false;

        // 一般 dash 優先：兩者同時發生時（dash 取消近戰）以 dash 的強度為準
        if (playerMovement.IsDashActive)
        {
            target = dashIntensity;
            active = true;
        }
        else if (includeAssaultBoost && playerMovement.IsAssaultBoosting)
        {
            target = assaultBoostIntensity;
            active = true;
        }
        else if (includeMeleeDash && IsMeleeBlurActive())
        {
            target = meleeDashIntensity;
            active = true;
        }

        // 進場快、退場慢，尾巴比較好看
        float lerp = active ? blurInLerp : blurOutLerp;
        _mb.intensity.value = Mathf.Lerp(_mb.intensity.value, target, lerp * Time.deltaTime);
    }

    private bool IsMeleeBlurActive()
    {
        if (playerMovement.IsMeleeDashing) return true;

        return holdThroughMeleeHover && playerMovement.IsMeleeHovering;
    }
}