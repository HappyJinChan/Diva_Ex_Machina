using System.Collections;
using UnityEngine;

/// <summary>
/// 讓同一種敵人的動畫不要整齊劃一。
///
/// 掛在有 Animator 的物件上（獵犬是 Wolf Mesh）。每次生成 / 從物件池取出時：
///   · 從目前狀態的隨機時間點開始播放（每隻的步伐相位不同）
///   · 播放速度乘上一個小範圍的隨機倍率（相位會隨時間慢慢錯開，不會又同步回去）
///
/// 只在啟用時做一次，之後沒有每幀成本。
/// </summary>
[RequireComponent(typeof(Animator))]
public class AnimatorRandomizer : MonoBehaviour
{
    [Tooltip("開始時跳到目前動畫的隨機時間點。")]
    [SerializeField] private bool randomStartTime = true;

    [Tooltip("播放速度的隨機倍率範圍（x = 最小, y = 最大）。\n" +
             "差太多會看得出腳在地上滑，建議在 0.9 ~ 1.1 之間。")]
    [SerializeField] private Vector2 speedRange = new Vector2(0.92f, 1.08f);

    [Tooltip("要處理的 Animator 圖層。")]
    [SerializeField] private int layer = 0;

    private Animator _animator;
    private float _baseSpeed = 1f;
    private bool _baseSpeedCaptured;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
    }

    private void OnEnable()
    {
        if (_animator == null) return;

        if (!_baseSpeedCaptured)
        {
            _baseSpeed = _animator.speed;
            _baseSpeedCaptured = true;
        }

        float lo = Mathf.Min(speedRange.x, speedRange.y);
        float hi = Mathf.Max(speedRange.x, speedRange.y);
        _animator.speed = _baseSpeed * Random.Range(lo, hi);

        if (randomStartTime)
            StartCoroutine(RandomizeStartTime());
    }

    // Animator 剛啟用的那一幀還沒進入預設狀態，等一幀再跳時間點
    private IEnumerator RandomizeStartTime()
    {
        yield return null;

        if (_animator == null || !_animator.isActiveAndEnabled || _animator.runtimeAnimatorController == null)
            yield break;
        if (layer < 0 || layer >= _animator.layerCount)
            yield break;

        AnimatorStateInfo state = _animator.GetCurrentAnimatorStateInfo(layer);
        _animator.Play(state.fullPathHash, layer, Random.value);
    }
}
