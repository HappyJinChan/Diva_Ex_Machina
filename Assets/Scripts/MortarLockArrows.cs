using System.Collections.Generic;
using MoreMountains.Feedbacks;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 迫擊砲「對敵鎖定」的目標標記：每個鎖定的敵人頭上一個箭頭（螢幕空間 UI，大小固定，不會被地形擋住）。
///
/// 狀態（顏色 / 圖案）：
///   · 鎖定中（還沒放開左鍵）：Locking Color。新鎖到的那一下會彈一下（Pop）。
///   · 已開火：Fired Color。
///   · 目標死了、砲彈還在飛：停在它最後的位置，換成 Lost Sprite（沒設就沿用箭頭）＋ Lost Color。
///   從開火到最後一顆砲彈落地都會顯示（SupplyManager.GetMarkedTargets）。
///   每鎖到一個新目標（箭頭出現）時播放 Lock Feedback（MMF Player，例如鎖定音效）。
///   目標在鏡頭後方或畫面外時不顯示。
///
/// 設定：
///   1. 在 Combat UI Canvas 底下建一個撐滿全螢幕的空物件（例如 "Lock Arrows"），掛這個元件。
///      不要放在 Mortar HUD 底下：開火後鎖定模式就結束、Mortar HUD 會被關掉，但標記要留到炮擊結束。
///   2. 底下放一個 Image（你的箭頭圖），錨點設在中心，箭頭尖端朝下的話 Pivot 設在 (0.5, 0)，
///      拖進 Arrow Template。它是樣板，會被隱藏，每個目標複製一份。
/// </summary>
public class MortarLockArrows : MonoBehaviour
{
    [SerializeField] private SupplyManager supplyManager;

    [Tooltip("箭頭樣板（Image）。放在這個物件底下，執行時會被隱藏、複製。")]
    [SerializeField] private Image arrowTemplate;

    [Header("Position")]
    [Tooltip("用目標模型的最高點（Renderer 的包圍盒頂端）當基準；關掉 = 用瞄準點（AimPoint）。")]
    [SerializeField] private bool useBoundsTop = true;

    [Tooltip("基準點再往上幾公尺。")]
    [SerializeField] private float worldHeightOffset = 1f;

    [Tooltip("投影到畫面後再往上幾像素（Canvas 單位）。")]
    [SerializeField] private float screenOffset = 12f;

    [Header("Colors")]
    [SerializeField] private Color lockingColor = Color.white;
    [SerializeField] private Color firedColor = new Color(1f, 0.45f, 0.1f, 1f);
    [SerializeField] private Color lostColor = new Color(1f, 0.45f, 0.1f, 0.6f);

    [Tooltip("目標死掉後（砲彈還在飛）改用的圖，例如 ✕。留空 = 沿用箭頭。")]
    [SerializeField] private Sprite lostSprite;

    [Header("Feedback")]
    [Tooltip("鎖到新目標、箭頭出現時播放（例如鎖定音效）。")]
    [SerializeField] private MMF_Player lockFeedback;

    [Header("Pop（剛鎖到時）")]
    [SerializeField, Min(1f)] private float popScale = 1.5f;
    [SerializeField, Min(0.01f)] private float popDuration = 0.15f;

    private sealed class Arrow
    {
        public RectTransform rect;
        public Image image;
        public float bornTime;
        public bool used;
    }

    private readonly Dictionary<int, Arrow> _active = new Dictionary<int, Arrow>();
    private readonly Stack<Arrow> _pool = new Stack<Arrow>();
    private readonly List<SupplyManager.MarkedTarget> _marks = new List<SupplyManager.MarkedTarget>();
    private readonly List<int> _toRemove = new List<int>();
    private readonly Dictionary<Transform, Renderer> _rendererCache = new Dictionary<Transform, Renderer>();

    private Canvas _canvas;
    private RectTransform _parent;
    private Sprite _arrowSprite;

    private void Awake()
    {
        _canvas = GetComponentInParent<Canvas>();
        if (arrowTemplate != null)
        {
            _parent = arrowTemplate.rectTransform.parent as RectTransform;
            _arrowSprite = arrowTemplate.sprite;
            arrowTemplate.gameObject.SetActive(false);
        }
    }

    private void OnDisable()
    {
        // 介面切走時收掉全部，回來重新長
        foreach (var kv in _active) Release(kv.Value);
        _active.Clear();
    }

    private void LateUpdate()
    {
        if (arrowTemplate == null || _parent == null) return;
        if (supplyManager == null) supplyManager = SupplyManager.Instance;

        Camera cam = PlayerAiming.Instance != null ? PlayerAiming.Instance.MainCamera : Camera.main;
        int count = (supplyManager != null && cam != null) ? supplyManager.GetMarkedTargets(_marks) : 0;

        foreach (var kv in _active) kv.Value.used = false;

        for (int i = 0; i < count; i++)
        {
            SupplyManager.MarkedTarget m = _marks[i];

            if (!_active.TryGetValue(m.id, out Arrow a))
            {
                a = Get();
                a.bornTime = Time.unscaledTime;
                _active[m.id] = a;

                // 新鎖到的目標：播放鎖定回饋（只在鎖定中出現的才算，開火後的狀態變化不會再播）
                if (m.state == SupplyManager.MarkState.Locking && lockFeedback != null)
                    lockFeedback.PlayFeedbacks();
            }
            a.used = true;

            // 位置：目標頭頂（活著）/ 最後的位置（死掉）
            Vector3 world = m.position;
            if (useBoundsTop && m.target != null && TryGetTop(m.target, out float top)) world.y = top;
            world.y += worldHeightOffset;

            Vector3 sp = cam.WorldToScreenPoint(world);
            bool onScreen = sp.z > 0f && sp.x >= 0f && sp.x <= Screen.width && sp.y >= 0f && sp.y <= Screen.height;
            if (!onScreen)
            {
                if (a.image.enabled) a.image.enabled = false;
                continue;
            }

            Camera uiCam = (_canvas != null && _canvas.renderMode != RenderMode.ScreenSpaceOverlay) ? _canvas.worldCamera : null;
            if (RectTransformUtility.ScreenPointToLocalPointInRectangle(_parent, sp, uiCam, out Vector2 local))
                a.rect.anchoredPosition = local + new Vector2(0f, screenOffset) - ParentCenterOffset();

            // 外觀
            bool lost = m.state == SupplyManager.MarkState.Lost;
            a.image.sprite = (lost && lostSprite != null) ? lostSprite : _arrowSprite;
            a.image.color = m.state == SupplyManager.MarkState.Locking ? lockingColor
                          : m.state == SupplyManager.MarkState.Fired ? firedColor
                          : lostColor;

            float t = Mathf.Clamp01((Time.unscaledTime - a.bornTime) / popDuration);
            a.rect.localScale = Vector3.one * Mathf.Lerp(popScale, 1f, 1f - (1f - t) * (1f - t));

            if (!a.image.enabled) a.image.enabled = true;
        }

        // 這一幀沒有用到的（取消鎖定、炮擊結束）收回
        _toRemove.Clear();
        foreach (var kv in _active)
            if (!kv.Value.used) _toRemove.Add(kv.Key);
        foreach (int id in _toRemove)
        {
            Release(_active[id]);
            _active.Remove(id);
        }

        if (_rendererCache.Count > 256) _rendererCache.Clear();
    }

    // anchoredPosition 是相對於錨點；樣板的錨點在父物件中心時，local point 要扣掉「中心相對於 pivot」的偏移
    private Vector2 ParentCenterOffset()
    {
        Rect r = _parent.rect;
        Vector2 anchor = arrowTemplate.rectTransform.anchorMin;   // 錨點（min = max 時）
        return new Vector2(r.x + r.width * anchor.x, r.y + r.height * anchor.y);
    }

    private bool TryGetTop(Transform t, out float top)
    {
        if (!_rendererCache.TryGetValue(t, out Renderer r))
        {
            r = t.GetComponentInChildren<Renderer>();
            _rendererCache[t] = r;
        }
        if (r == null) { top = 0f; return false; }
        top = r.bounds.max.y;
        return true;
    }

    private Arrow Get()
    {
        if (_pool.Count > 0)
        {
            Arrow p = _pool.Pop();
            p.rect.gameObject.SetActive(true);
            return p;
        }

        Image img = Instantiate(arrowTemplate, _parent);
        img.name = "Lock Arrow";
        img.gameObject.SetActive(true);
        img.raycastTarget = false;
        return new Arrow { rect = img.rectTransform, image = img };
    }

    private void Release(Arrow a)
    {
        if (a == null || a.rect == null) return;
        a.rect.gameObject.SetActive(false);
        _pool.Push(a);
    }
}
