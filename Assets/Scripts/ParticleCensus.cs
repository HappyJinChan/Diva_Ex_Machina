using System.Collections.Generic;
using System.Text;
using UnityEngine;

/// <summary>
/// 診斷用：統計場景裡的粒子系統，找出 ParticleSystem.Update 的成本從哪裡來。不改任何遊戲行為，查完就可以移除。
///
/// 用法：掛在場景裡任何一個物件上，進 Play 後在元件右上角 ⋮ →「Log Particle Census」，
/// 或勾選 Auto Log，每隔幾秒自動印一次。
///
/// 依物件名稱分組，印出：
///   enabled   啟用中的粒子系統數量（物件啟用 + 元件存在）
///   playing   正在播放（isPlaying）的數量 —— 主執行緒的成本主要看這個
///   emitting  正在發射（isEmitting）的數量
///   alive     目前存活的粒子總數
///   looping   其中勾了 Looping 的數量
/// </summary>
public class ParticleCensus : MonoBehaviour
{
    [SerializeField] private bool autoLog = false;
    [SerializeField, Min(0.5f)] private float interval = 3f;
    [Tooltip("只列出 playing 數量至少這麼多的分組。")]
    [SerializeField, Min(0)] private int minPlayingToList = 1;

    private float _timer;

    private sealed class Group
    {
        public int enabled, playing, emitting, alive, looping;
    }

    private void Update()
    {
        if (!autoLog) return;
        _timer -= Time.unscaledDeltaTime;
        if (_timer > 0f) return;
        _timer = interval;
        LogCensus();
    }

    [ContextMenu("Log Particle Census")]
    public void LogCensus()
    {
        var systems = FindObjectsByType<ParticleSystem>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var groups = new Dictionary<string, Group>();
        int totalPlaying = 0, totalAlive = 0;

        foreach (var ps in systems)
        {
            if (ps == null) continue;
            string key = ps.gameObject.name;
            if (!groups.TryGetValue(key, out Group g)) { g = new Group(); groups[key] = g; }

            g.enabled++;
            if (ps.isPlaying) { g.playing++; totalPlaying++; }
            if (ps.isEmitting) g.emitting++;
            if (ps.main.loop) g.looping++;
            int n = ps.particleCount;
            g.alive += n;
            totalAlive += n;
        }

        var list = new List<KeyValuePair<string, Group>>(groups);
        list.Sort((a, b) => b.Value.playing != a.Value.playing
            ? b.Value.playing.CompareTo(a.Value.playing)
            : b.Value.enabled.CompareTo(a.Value.enabled));

        var sb = new StringBuilder();
        sb.Append($"[ParticleCensus] 啟用中 {systems.Length} 個｜播放中 {totalPlaying} 個｜存活粒子 {totalAlive}\n");
        foreach (var kv in list)
        {
            Group g = kv.Value;
            if (g.playing < minPlayingToList) continue;
            sb.Append($"  {kv.Key}: enabled {g.enabled}, playing {g.playing}, emitting {g.emitting}, alive {g.alive}, looping {g.looping}\n");
        }
        Debug.Log(sb.ToString(), this);
    }
}
