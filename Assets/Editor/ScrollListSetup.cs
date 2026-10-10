using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 把一個用 LayoutGroup 排列的清單變成可以捲動的清單。
///
/// 用法：在 Hierarchy 選取「清單本體」（按鈕直接長在它底下的那個物件，
/// 例如 Inventory BG / Item Window），然後 Tools > UI > Make Selected List Scrollable。
///
/// 做的事：
///   1. 在清單原本的位置建立 Viewport（RectMask2D），超出範圍的按鈕會被裁掉。
///   2. 清單移進 Viewport，改成「頂端對齊、寬度撐滿」，再加 ContentSizeFitter
///      讓高度跟著按鈕數量長 —— 程式產生按鈕的方式完全不用改，
///      InventoryManager.itemsButtonParent 指向的還是同一個物件。
///   3. 父物件加上 ScrollRect（滑鼠滾輪、拖曳都能捲）。
///   4. 父物件底下如果有名為「Slider」的圖片（清單旁邊那條白色長條），
///      把它變成捲軸：長條當軌道，裡面新增一個深色的 Handle。
///
/// 全部支援 Undo（Ctrl+Z 一次復原）。每個場景各自執行一次。
/// </summary>
public static class ScrollListSetup
{
    private const string MenuPath = "Tools/UI/Make Selected List Scrollable";

    // 軌道的名字。找不到就只做捲動、不做捲軸。
    private const string TrackName = "Slider";

    // 滑鼠滾輪每一格捲動的距離（UI 單位）。大約一個按鈕的高度。
    private const float DefaultScrollSensitivity = 35f;

    // Handle 比軌道每邊內縮多少（UI 單位）
    private const float HandleInset = 2f;

    private static readonly Color HandleColor = new Color(0.25f, 0.25f, 0.25f, 1f);

    [MenuItem(MenuPath, true)]
    private static bool Validate()
    {
        var rt = Selection.activeTransform as RectTransform;
        return rt != null && rt.parent is RectTransform && !EditorApplication.isPlaying;
    }

    [MenuItem(MenuPath)]
    private static void Run()
    {
        var content = (RectTransform)Selection.activeTransform;
        var host = (RectTransform)content.parent;

        if (host.GetComponent<ScrollRect>() != null || content.parent.GetComponent<RectMask2D>() != null)
        {
            EditorUtility.DisplayDialog("Make List Scrollable",
                "這個清單看起來已經設定過捲動了（父物件已經有 ScrollRect 或 RectMask2D）。", "OK");
            return;
        }

        if (content.GetComponent<LayoutGroup>() == null)
        {
            bool go = EditorUtility.DisplayDialog("Make List Scrollable",
                $"「{content.name}」上沒有 LayoutGroup，按鈕不會自動排列，高度也算不出來。\n\n確定要繼續嗎？",
                "繼續", "取消");
            if (!go) return;
        }

        Undo.SetCurrentGroupName("Make List Scrollable");
        int undoGroup = Undo.GetCurrentGroup();

        RectTransform viewport = CreateViewport(content, host);
        SetupContent(content, viewport);
        ScrollRect scroll = SetupScrollRect(host, content, viewport);
        Scrollbar bar = SetupScrollbar(host, content, viewport);

        if (bar != null)
        {
            scroll.verticalScrollbar = bar;
            scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.Permanent;
        }

        Undo.CollapseUndoOperations(undoGroup);
        EditorSceneManager.MarkSceneDirty(host.gameObject.scene);
        Selection.activeTransform = host;

        Debug.Log($"[ScrollListSetup] 「{content.name}」已經可以捲動" +
                  (bar != null ? $"，捲軸使用「{bar.name}」。" : $"。父物件底下找不到「{TrackName}」，沒有建立捲軸（滾輪與拖曳仍可捲動）。"),
                  host);
    }

    // ───────────────────────── 1. Viewport ─────────────────────────
    // 佔用清單原本的位置與大小，負責裁切超出範圍的按鈕。
    private static RectTransform CreateViewport(RectTransform content, RectTransform host)
    {
        var go = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        Undo.RegisterCreatedObjectUndo(go, "Create Viewport");
        go.layer = content.gameObject.layer;

        var vp = (RectTransform)go.transform;
        vp.SetParent(host, false);
        vp.SetSiblingIndex(content.GetSiblingIndex());

        vp.anchorMin = content.anchorMin;
        vp.anchorMax = content.anchorMax;
        vp.pivot = content.pivot;
        vp.anchoredPosition = content.anchoredPosition;
        vp.sizeDelta = content.sizeDelta;
        vp.localRotation = Quaternion.identity;
        vp.localScale = Vector3.one;

        return vp;
    }

    // ───────────────────────── 2. Content ─────────────────────────
    // 移進 Viewport，頂端對齊、寬度撐滿；高度交給 ContentSizeFitter。
    private static void SetupContent(RectTransform content, RectTransform viewport)
    {
        Undo.SetTransformParent(content, viewport, "Move List Into Viewport");
        Undo.RecordObject(content, "Setup List Content");

        content.anchorMin = new Vector2(0f, 1f);
        content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0.5f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;   // 高度由下面的 ContentSizeFitter 決定
        content.localRotation = Quaternion.identity;
        content.localScale = Vector3.one;

        var fitter = content.GetComponent<ContentSizeFitter>();
        if (fitter == null) fitter = Undo.AddComponent<ContentSizeFitter>(content.gameObject);
        else Undo.RecordObject(fitter, "Setup Content Size Fitter");

        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
    }

    // ───────────────────────── 3. ScrollRect ─────────────────────────
    private static ScrollRect SetupScrollRect(RectTransform host, RectTransform content, RectTransform viewport)
    {
        var scroll = Undo.AddComponent<ScrollRect>(host.gameObject);

        scroll.content = content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.vertical = true;

        // Clamped：捲到底就停，不會像手機那樣拉過頭再彈回來
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.inertia = true;
        scroll.decelerationRate = 0.135f;
        scroll.scrollSensitivity = GuessScrollSensitivity(content);

        return scroll;
    }

    // 清單裡已經有按鈕（例如手動擺的範例）就用第一個按鈕的高度，否則用預設值
    private static float GuessScrollSensitivity(RectTransform content)
    {
        if (content.childCount > 0 && content.GetChild(0) is RectTransform first && first.rect.height > 1f)
            return first.rect.height;
        return DefaultScrollSensitivity;
    }

    // ───────────────────────── 4. Scrollbar ─────────────────────────
    // 把旁邊那條白色長條（Slider）變成捲軸：長條本身當軌道，裡面加一個 Handle。
    private static Scrollbar SetupScrollbar(RectTransform host, RectTransform content, RectTransform viewport)
    {
        RectTransform track = null;
        foreach (Transform child in host)
        {
            if (child == viewport || child == content) continue;
            if (child.name == TrackName && child is RectTransform rt)
            {
                track = rt;
                break;
            }
        }
        if (track == null) return null;

        var existing = track.GetComponent<Scrollbar>();
        if (existing != null) return existing;

        // Handle：軌道裡的深色方塊。Scrollbar 會自己改它的 anchor 來表示位置與長度，
        // 這裡只決定內縮多少。
        var handleGO = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        Undo.RegisterCreatedObjectUndo(handleGO, "Create Scrollbar Handle");
        handleGO.layer = track.gameObject.layer;

        var handle = (RectTransform)handleGO.transform;
        handle.SetParent(track, false);
        handle.anchorMin = Vector2.zero;
        handle.anchorMax = Vector2.one;
        handle.pivot = new Vector2(0.5f, 0.5f);
        handle.anchoredPosition = Vector2.zero;
        handle.sizeDelta = new Vector2(-HandleInset * 2f, -HandleInset * 2f);

        var handleImage = handleGO.GetComponent<Image>();
        handleImage.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        handleImage.type = Image.Type.Sliced;
        handleImage.color = HandleColor;

        var bar = Undo.AddComponent<Scrollbar>(track.gameObject);
        bar.handleRect = handle;
        bar.targetGraphic = handleImage;
        bar.direction = Scrollbar.Direction.BottomToTop;
        bar.value = 1f;   // 1 = 最上面

        return bar;
    }
}
