using UnityEngine;

public class FlameEffect : MonoBehaviour
{
    public enum FlameKind
    {
        Normal,
        Boosted,
        Melee
    }
    [Header("Dash VFX")]
    [SerializeField] private float dashOffDelay = 0.2f; // 主人要的延遲時間
    private float _dashLingerUntil = 0f;
    [Header("Type")]
    [SerializeField] private FlameKind flameKind = FlameKind.Normal;

    [Header("Auto Find")]
    [SerializeField] private PlayerMovement playerMovement;

    [Header("What to Toggle")]
    [Tooltip("拖入此火焰要控制的所有 ParticleSystem（同一組一起開關）")]
    [SerializeField] private ParticleSystem[] particles;

    [Header("Tuning")]
    [Tooltip("下落判定：y 速度低於此值才算 falling，避免微小抖動切換")]
    [SerializeField] private float fallingVelocityThreshold = -0.1f;

    private bool _isOn;

    [Header("Boost On Dash")]
    [Tooltip("ThrusterFlameControllers that get Boost() when a dash starts. Leave empty to auto-find them in children.")]
    [SerializeField] private ThrusterFlameController[] thrusterFlames;
    private bool _wasDashing;

    [Header("Assault Boost")]
    [Tooltip("突擊推進中，這組火焰（Boosted）的油門 0～1。推進結束後恢復成原本的油門。")]
    [Range(0f, 1f)] [SerializeField] private float assaultBoostThrottle = 1f;
    private bool _wasAssaultBoosting;
    private float[] _baseThrottles;   // 推進前的油門，結束時還原

    private void Awake()
    {
        if (thrusterFlames == null || thrusterFlames.Length == 0)
            thrusterFlames = GetComponentsInChildren<ThrusterFlameController>(true);

        if (playerMovement == null)
            playerMovement = GetComponentInParent<PlayerMovement>(true);

        SetEffectActive(false, clear: true);
        _isOn = false;
    }

    private void OnEnable()
    {
        if (playerMovement == null)
            playerMovement = GetComponentInParent<PlayerMovement>(true);

        // 啟用時先關一次，避免 prefab PlayOnAwake 造成剛生成就亮錯
        SetEffectActive(false, clear: true);
        _isOn = false;
    }

    private void Update()
    {
        if (playerMovement == null)
        {
            // 角色層級若變動，給一次自動補救
            playerMovement = GetComponentInParent<PlayerMovement>(true);
            if (playerMovement == null) { EnsureOff(); return; }
        }

        // 狀態
        bool grounded = playerMovement.IsGrounded;
        bool flying = playerMovement.IsFlyingActive;

        // 近戰衝刺也算 Dash（IsDashActive 只涵蓋一般 Dash，跟下面規矩寫的「Dash / MeleeDash」對不上）
        bool dash = playerMovement.IsDashActive || playerMovement.IsMeleeDashing;
        bool assaultBoost = playerMovement.IsAssaultBoosting;

        if (dash)
        {
            _dashLingerUntil = Time.time + dashOffDelay;
        }

        bool dashWithLinger = dash || Time.time < _dashLingerUntil;

        float vy = playerMovement.VerticalVelocity;
        bool falling = !grounded && !flying && vy < fallingVelocityThreshold;

        // 規矩（優先級由高到低）：
        // 1) Dash / MeleeDash：只開 Melee flame
        // 2) 地上：全關
        // 3) 飛行：只開 Boosted flame
        // 4) 下落：只開 Normal flame
        // 0) 突擊推進：只開 Boosted flame（油門 assaultBoostThrottle），優先於以上全部
        bool shouldOn = false;

        if (assaultBoost)
        {
            shouldOn = (flameKind == FlameKind.Boosted);
        }
        else if (dashWithLinger )
        {
            shouldOn = (flameKind == FlameKind.Melee);
        }
        else if (grounded)
        {
            shouldOn = false;
        }
        else if (flying)
        {
            shouldOn = (flameKind == FlameKind.Boosted);
        }
        else if (falling)
        {
            shouldOn = (flameKind == FlameKind.Normal);
        }
        else
        {
            shouldOn = false;
        }

        ApplyDesired(shouldOn);

        // Dash just started (rising edge): Boost the flames that are shown during a dash.
        // After ApplyDesired so the flame is already playing when the burst is added.
        if (dash && !_wasDashing && flameKind == FlameKind.Melee)
        {
            foreach (var t in thrusterFlames)
                if (t != null) t.Boost();
        }
        _wasDashing = dash;

        // 突擊推進開始 / 結束：Boosted 油門拉高、點火爆發；結束時還原油門
        if (flameKind == FlameKind.Boosted && assaultBoost != _wasAssaultBoosting)
            OnAssaultBoostChanged(assaultBoost);
        _wasAssaultBoosting = assaultBoost;
    }

    private void OnAssaultBoostChanged(bool on)
    {
        if (thrusterFlames == null) return;

        if (_baseThrottles == null || _baseThrottles.Length != thrusterFlames.Length)
            _baseThrottles = new float[thrusterFlames.Length];

        for (int i = 0; i < thrusterFlames.Length; i++)
        {
            var t = thrusterFlames[i];
            if (t == null) continue;

            if (on)
            {
                _baseThrottles[i] = t.Throttle;
                t.SetThrottle(assaultBoostThrottle);
                t.Boost();   // 點火瞬間：衝擊環 + 火花 + 短暫加長
            }
            else
            {
                t.SetThrottle(_baseThrottles[i]);
            }
        }
    }

    private void ApplyDesired(bool on)
    {
        if (_isOn == on) return;
        _isOn = on;

        // off 時 clear，避免殘留
        SetEffectActive(on, clear: !on);
    }

    private void EnsureOff()
    {
        if (!_isOn) return;
        _isOn = false;
        SetEffectActive(false, clear: true);
    }

    private void SetEffectActive(bool on, bool clear)
    {
        if (particles == null) return;

        foreach (var ps in particles)
        {
            if (ps == null) continue;

            if (on)
            {
                if (!ps.isPlaying) ps.Play(true);
            }
            else
            {
                if (ps.isPlaying)
                {
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
                }
                else if (clear)
                {
                    //ps.Clear(true);
                }
            }
        }
    }
}
