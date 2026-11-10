using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// 遊戲結束判定。掛在終點線物件上（有 Box Collider 的那個）。
///
///   · 勝利：貝努鳥號（LandshipNavigation.realShip 底下任何一個 Collider）碰到這個物件的 Box Collider → 載入 Victory Scene Index。
///   · 失敗：核心（CoreStats）或玩家（PlayerStats）的血量 ≤ 0 → 載入 Defeat Scene Index。
///     兩個同時發生時以失敗為準（先檢查失敗）。
///
/// 終點線的判定用 Physics.OverlapBox 每個物理步查一次，不靠 OnTriggerEnter：
/// 不受 Layer Collision Matrix 影響，船是 kinematic 也沒關係。Box Collider 建議勾 Is Trigger（不會擋到東西）。
///
/// 場景用 Build Settings（File > Build Profiles 的 Scene List）裡的編號。
/// 載入前會把 timeScale 還原成 1、解鎖滑鼠游標（結果畫面要能點按鈕）。
/// </summary>
[RequireComponent(typeof(BoxCollider))]
public class GameOver : MonoBehaviour
{
    [Header("Scenes (Build Index)")]
    [SerializeField, Min(0)] private int victorySceneIndex = 1;
    [SerializeField, Min(0)] private int defeatSceneIndex = 2;

    [Tooltip("判定之後等幾秒才切場景（讓爆炸、到達的畫面播一下）。不受 timeScale 影響。")]
    [SerializeField, Min(0f)] private float loadDelay = 2f;

    [Header("References（留空會自動找）")]
    [Tooltip("船的根物件。留空 = LandshipNavigation.Instance.realShip。")]
    [SerializeField] private Transform ship;
    [Tooltip("留空 = CoreStats.Instance。")]
    [SerializeField] private CoreStats core;

    [Header("Debug")]
    [SerializeField] private bool logResult = true;

    private BoxCollider _box;
    private bool _ended;
    private float _loadAt;
    private int _pendingScene = -1;
    private readonly Collider[] _hits = new Collider[32];

    private void Awake()
    {
        _box = GetComponent<BoxCollider>();
    }

    private void Update()
    {
        if (_ended)
        {
            if (_pendingScene >= 0 && Time.unscaledTime >= _loadAt) Load(_pendingScene);
            return;
        }

        // 失敗：核心或玩家血量歸零
        CoreStats c = core != null ? core : CoreStats.Instance;
        if (c != null && c.Health <= 0f) { End(defeatSceneIndex, "核心被摧毀"); return; }

        PlayerStats p = PlayerStats.Instance;
        if (p != null && p.currentHealth <= 0f) { End(defeatSceneIndex, "玩家被擊倒"); return; }
    }

    private void FixedUpdate()
    {
        if (_ended || _box == null) return;

        Transform s = ResolveShip();
        if (s == null) return;

        // Box Collider 的世界範圍（含物件縮放、旋轉）
        Transform t = _box.transform;
        Vector3 center = t.TransformPoint(_box.center);
        Vector3 half = Vector3.Scale(_box.size * 0.5f, Abs(t.lossyScale));
        int n = Physics.OverlapBoxNonAlloc(center, half, _hits, t.rotation, ~0, QueryTriggerInteraction.Ignore);

        for (int i = 0; i < n; i++)
        {
            Collider h = _hits[i];
            if (h == null || h == _box) continue;
            if (h.transform.IsChildOf(s)) { End(victorySceneIndex, "貝努鳥號抵達終點"); return; }
        }
    }

    private Transform ResolveShip()
    {
        if (ship != null) return ship;
        LandshipNavigation nav = LandshipNavigation.Instance;
        if (nav != null && nav.realShip != null) ship = nav.realShip;
        return ship;
    }

    private void End(int sceneIndex, string reason)
    {
        _ended = true;

        if (sceneIndex < 0 || sceneIndex >= SceneManager.sceneCountInBuildSettings)
        {
            Debug.LogError($"GameOver: {reason}，但場景編號 {sceneIndex} 不在 Build Settings 裡" +
                           $"（目前共 {SceneManager.sceneCountInBuildSettings} 個場景）", this);
            return;
        }

        if (logResult) Debug.Log($"GameOver: {reason} → {loadDelay:0.#} 秒後載入場景 {sceneIndex}", this);
        _pendingScene = sceneIndex;
        _loadAt = Time.unscaledTime + loadDelay;
    }

    private static void Load(int sceneIndex)
    {
        Time.timeScale = 1f;                       // 頓幀 / 暫停中結束時，下一個場景不會卡住
        Cursor.lockState = CursorLockMode.None;    // 結果畫面要能點按鈕
        Cursor.visible = true;
        SceneManager.LoadScene(sceneIndex);
    }

    private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    private void OnDrawGizmos()
    {
        BoxCollider b = _box != null ? _box : GetComponent<BoxCollider>();
        if (b == null) return;
        Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.25f);
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(b.center, b.size);
        Gizmos.color = new Color(0.3f, 1f, 0.4f, 0.9f);
        Gizmos.DrawWireCube(b.center, b.size);
    }
}
