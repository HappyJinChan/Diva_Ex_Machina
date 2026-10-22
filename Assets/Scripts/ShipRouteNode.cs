using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 母艦航線上的一個點。放在 ShipRouteGraph 底下（子物件）。
///
/// · next：從這個點可以前往的下一個點（單向）。一個 = 普通轉折點；超過一個 = 分叉點。
/// · 只看 X、Z；高度不影響航線。
/// · 編輯方式見 ShipRouteGraph（選取節點後 Inspector 有「往前延伸」「連接」等按鈕）。
/// </summary>
[DisallowMultipleComponent]
public class ShipRouteNode : MonoBehaviour
{
    [Tooltip("從這個點可以前往的下一個點（單向）。超過一個就是分叉。")]
    public List<ShipRouteNode> next = new List<ShipRouteNode>();

    [Tooltip("分叉時，沒有人指定要走哪條的話，預設走 next 的第幾條（從 0 開始）。")]
    [Min(0)] public int defaultBranch = 0;

    public Vector3 Position => transform.position;

    /// <summary>有效（非 null）的出口數。</summary>
    public int ExitCount
    {
        get
        {
            int n = 0;
            for (int i = 0; i < next.Count; i++)
                if (next[i] != null) n++;
            return n;
        }
    }

    public bool IsJunction => ExitCount > 1;
    public bool IsDeadEnd => ExitCount == 0;

    /// <summary>預設出口：defaultBranch 指的那條；它是 null 時取第一條有效的。沒有出口回傳 null。</summary>
    public ShipRouteNode DefaultNext
    {
        get
        {
            if (defaultBranch >= 0 && defaultBranch < next.Count && next[defaultBranch] != null)
                return next[defaultBranch];
            for (int i = 0; i < next.Count; i++)
                if (next[i] != null) return next[i];
            return null;
        }
    }

    /// <summary>隨機一條有效出口。沒有出口回傳 null。</summary>
    public ShipRouteNode RandomNext()
    {
        int count = ExitCount;
        if (count == 0) return null;
        int pick = Random.Range(0, count);
        for (int i = 0; i < next.Count; i++)
        {
            if (next[i] == null) continue;
            if (pick-- == 0) return next[i];
        }
        return null;
    }

    public bool HasExit(ShipRouteNode n) => n != null && next.Contains(n);

#if UNITY_EDITOR
    // 外觀由 ShipRouteGraph 畫；這裡只放一顆幾乎透明的球，讓節點可以在 Scene 視窗直接點選。
    void OnDrawGizmos()
    {
        Vector3 p = transform.position;
        float s = UnityEditor.HandleUtility.GetHandleSize(p) * 0.15f;
        Gizmos.color = new Color(1f, 1f, 1f, 0.01f);
        Gizmos.DrawSphere(p, s);
    }
#endif
}
