using UnityEngine;

/// <summary>
/// 爆炸特效 prefab 的根元件（由 Tools > Diva Ex Machina > Explosion Builder 產生）。
///
/// 粒子本身都是「一次性」的：每個圖層在 0 秒時 Burst 一次，啟用（或從物件池取出）時自動重播。
/// 這個元件只負責粒子做不到的兩件事：
///   1. 閃光燈光：爆炸瞬間照亮周圍，然後快速衰減。燈光顏色跟著核心閃光的顏色，
///      所以用 ColorPicker / EffectColorController 改色時，燈光也會一起變。
///   2. （可選）播完自動回收：見 despawnWhenFinished。
///
/// 圖層顏色分組（EffectColorController）：
///   1. Core      - 閃光、星芒、火球核心
///   2. Fire      - 火球
///   3. Smoke     - 煙
///   4. Sparks    - 火花、餘燼、衝擊環
/// </summary>
[DisallowMultipleComponent]
public class ExplosionEffect : MonoBehaviour, IPooled
{
    [Header("Flash Light")]
    [Tooltip("爆炸瞬間的點光源。留空就沒有燈光（例如大量同時爆炸時，為了效能可以關掉）。")]
    [SerializeField] private Light flashLight;

    [Tooltip("燈光最亮時的強度。")]
    [SerializeField, Min(0f)] private float lightPeakIntensity = 8f;

    [Tooltip("燈光從最亮衰減到 0 的時間（秒）。")]
    [SerializeField, Min(0.01f)] private float lightDuration = 0.35f;

    [Tooltip("燈光顏色跟著這個粒子層的 Start Color（通常是核心閃光）。留空就用燈光本身的顏色。")]
    [SerializeField] private ParticleSystem lightColorSource;

    [Tooltip("燈光閃爍程度。0 = 平滑衰減；越大越像電弧那樣忽明忽暗（Lightning 類型預設 0.7、Fire 0.15）。")]
    [SerializeField, Range(0f, 1f)] private float lightFlicker = 0f;

    [Header("Lifetime")]
    [Tooltip("播完後自動回收（有物件池就回池子，沒有就 Destroy）。\n\n" +
             "· 給 Bullet 的 Hit Effect 用時請保持關閉 —— Bullet 已經會自己排程回收，兩邊都排會互相干擾。\n" +
             "· 給只負責生成、不負責回收的程式用時請打開，例如 BuildingStats 的 Destroy Effect，\n" +
             "  或直接拖進場景、用 Instantiate 生成的情況。")]
    [SerializeField] private bool despawnWhenFinished = false;

    private float _lightTimer = -1f;
    private float _baseLightRange = -1f;   // prefab 原本的燈光範圍（ScaleLightRange 放大後，回池子時還原）
    private Vector3 _baseLocalScale;        // prefab 原本的縮放（被放大後，回池子時還原）

    /// <summary>這個特效會不會自己回收。生成它的程式用來判斷要不要自己排程回收。</summary>
    public bool DespawnWhenFinished => despawnWhenFinished;

    private void Awake()
    {
        if (flashLight != null) _baseLightRange = flashLight.range;
        _baseLocalScale = transform.localScale;
    }

    /// <summary>
    /// 燈光範圍 = 原本 × k。燈光不吃 Transform 縮放，特效整體被放大 / 縮小時另外呼叫這個。
    /// 回池子時還原（連同 Transform 縮放），所以不會影響下一個拿到這個實例的人（例如子彈的爆炸）。
    /// </summary>
    public void ScaleLightRange(float k)
    {
        if (flashLight != null && _baseLightRange > 0f)
            flashLight.range = _baseLightRange * Mathf.Max(0f, k);
    }

    private void OnEnable()
    {
        // 物件池重新取出時也會跑到這裡 → 粒子由 Play On Awake 自動重播，燈光在這裡重新點亮
        if (flashLight != null)
        {
            if (lightColorSource != null)
                flashLight.color = lightColorSource.main.startColor.color;

            _lightTimer = 0f;
            flashLight.intensity = lightPeakIntensity;
            flashLight.enabled = true;
        }
    }

    private void Start()
    {
        // 不是物件池生出來的（直接 Instantiate、或擺在場景裡）→ 自己排程銷毀
        if (despawnWhenFinished && GetComponent<PooledInstance>() == null)
            Destroy(gameObject, GetDuration());
    }

    public void OnSpawned()
    {
        // 物件池生出來的 → 播完回池子。
        // 用 OnSpawned 而不是 OnEnable：池子預熱時也會啟用 / 停用物件，那時不能排程回收。
        if (despawnWhenFinished)
            PrefabPool.Despawn(gameObject, GetDuration());
    }

    public void OnDespawned()
    {
        _lightTimer = -1f;
        if (flashLight != null)
        {
            flashLight.enabled = false;
            if (_baseLightRange > 0f) flashLight.range = _baseLightRange;
        }
        transform.localScale = _baseLocalScale;
    }

    private void Update()
    {
        if (_lightTimer < 0f || flashLight == null) return;

        _lightTimer += Time.deltaTime;
        float t = _lightTimer / lightDuration;

        if (t >= 1f)
        {
            flashLight.intensity = 0f;
            flashLight.enabled = false;
            _lightTimer = -1f;
            return;
        }

        // 一開始掉得快、尾巴拖得長，比線性衰減更像真實的閃光
        float k = 1f - t;
        float flicker = lightFlicker > 0f ? 1f - lightFlicker * Random.value : 1f;
        flashLight.intensity = lightPeakIntensity * k * k * flicker;
    }

    /// <summary>整個爆炸播完需要的時間：所有粒子層中「延遲 + 持續時間 + 最長粒子壽命」的最大值。</summary>
    public float GetDuration()
    {
        float longest = lightDuration;
        ParticleSystem[] systems = GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < systems.Length; i++)
        {
            var main = systems[i].main;
            if (main.loop) continue;

            float span = main.startDelay.constantMax + main.duration + main.startLifetime.constantMax;
            if (span > longest) longest = span;
        }
        return longest;
    }
}
