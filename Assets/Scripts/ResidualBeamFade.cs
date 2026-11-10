using UnityEngine;

/// <summary>
/// 殘留光束的淡出（由 BulletResidualBeam 生成並設定）。
/// 寬度用 ease-out 擴散，透明度用平方衰減（一開始最亮、很快變淡、尾巴拖一點），時間到自己 Destroy。
/// </summary>
[DisallowMultipleComponent]
public class ResidualBeamFade : MonoBehaviour
{
    private LineRenderer _line;
    private Color _color;
    private float _startWidth;
    private float _endWidth;
    private float _duration;
    private float _time;

    public void Play(LineRenderer line, Color color, float startWidth, float endWidth, float duration)
    {
        _line = line;
        _color = color;
        _startWidth = startWidth;
        _endWidth = endWidth;
        _duration = Mathf.Max(0.01f, duration);
        _time = 0f;
        Apply(0f);
    }

    private void Update()
    {
        if (_line == null) { Destroy(gameObject); return; }

        _time += Time.deltaTime;
        float k = Mathf.Clamp01(_time / _duration);
        Apply(k);

        if (k >= 1f) Destroy(gameObject);
    }

    private void Apply(float k)
    {
        float easeOut = 1f - (1f - k) * (1f - k);
        _line.widthMultiplier = Mathf.Lerp(_startWidth, _endWidth, easeOut);

        Color c = _color;
        c.a *= (1f - k) * (1f - k);
        _line.startColor = c;
        _line.endColor = c;
    }
}
