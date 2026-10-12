using UnityEngine;

/// <summary>
/// 實體近戰的判定形狀。
///
/// 掛在爪子、頭之類的骨頭底下的空物件上，再加上 Sphere / Box / Capsule Collider 決定形狀與大小。
/// 判定會跟著骨頭（動畫）移動。
///
/// 這裡的 Collider 只拿來「描述形狀」：啟動時會被設成 Trigger 並停用，
/// 不參與物理、不會被子彈打到、也不受層碰撞矩陣影響。
/// 判定開啟期間，由 ModularEntityMeleeManager 每個物理步用這些形狀做 Overlap 查詢。
///
/// 衝刺很快時，每個物理步會移動 0.5～1 公尺，形狀太小可能從目標身上跳過去 —— 做大一點比較保險。
/// </summary>
public class EntityMeleeHitbox : MonoBehaviour
{
    [Tooltip("判定形狀。留空會自動抓本物件上所有的 Collider（只支援 Sphere / Box / Capsule）。")]
    [SerializeField] private Collider[] shapes;

    private static readonly Collider[] s_Temp = new Collider[32];

    private void Awake()
    {
        if (shapes == null || shapes.Length == 0)
            shapes = GetComponents<Collider>();

        foreach (Collider c in shapes)
        {
            if (c == null) continue;
            c.isTrigger = true;
            c.enabled = false;   // 只當形狀用，不參與物理
        }

        if (shapes.Length == 0)
            Debug.LogWarning($"[EntityMeleeHitbox] '{name}' 上沒有任何 Collider，這個判定永遠不會命中。", this);
    }

    /// <summary>用所有形狀做重疊查詢，結果（不重複）寫進 results，回傳數量。</summary>
    public int Overlap(Collider[] results, LayerMask mask)
    {
        int count = 0;
        if (shapes == null) return 0;

        foreach (Collider shape in shapes)
        {
            if (shape == null) continue;
            int n = OverlapShape(shape, mask);
            for (int i = 0; i < n && count < results.Length; i++)
            {
                Collider hit = s_Temp[i];
                if (hit == null) continue;
                bool dup = false;
                for (int k = 0; k < count; k++)
                    if (results[k] == hit) { dup = true; break; }
                if (!dup) results[count++] = hit;
            }
        }
        return count;
    }

    private static int OverlapShape(Collider shape, LayerMask mask)
    {
        Transform t = shape.transform;
        Vector3 s = t.lossyScale;
        s = new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));
        const QueryTriggerInteraction q = QueryTriggerInteraction.Collide;

        switch (shape)
        {
            case SphereCollider sc:
            {
                Vector3 center = t.TransformPoint(sc.center);
                float r = sc.radius * Mathf.Max(s.x, Mathf.Max(s.y, s.z));
                return Physics.OverlapSphereNonAlloc(center, r, s_Temp, mask, q);
            }
            case BoxCollider bc:
            {
                Vector3 center = t.TransformPoint(bc.center);
                Vector3 half = Vector3.Scale(bc.size * 0.5f, s);
                return Physics.OverlapBoxNonAlloc(center, half, s_Temp, t.rotation, mask, q);
            }
            case CapsuleCollider cc:
            {
                GetCapsule(cc, s, out Vector3 p0, out Vector3 p1, out float r);
                return Physics.OverlapCapsuleNonAlloc(p0, p1, r, s_Temp, mask, q);
            }
        }
        return 0;
    }

    private static void GetCapsule(CapsuleCollider cc, Vector3 s, out Vector3 p0, out Vector3 p1, out float radius)
    {
        Transform t = cc.transform;
        Vector3 axis;
        float axisScale, radiusScale;
        switch (cc.direction)
        {
            case 0: axis = t.right; axisScale = s.x; radiusScale = Mathf.Max(s.y, s.z); break;
            case 2: axis = t.forward; axisScale = s.z; radiusScale = Mathf.Max(s.x, s.y); break;
            default: axis = t.up; axisScale = s.y; radiusScale = Mathf.Max(s.x, s.z); break;
        }
        radius = cc.radius * radiusScale;
        float half = Mathf.Max(0f, cc.height * axisScale * 0.5f - radius);
        Vector3 center = t.TransformPoint(cc.center);
        p0 = center + axis * half;
        p1 = center - axis * half;
    }

    private void OnDrawGizmos()
    {
        Collider[] list = (shapes != null && shapes.Length > 0) ? shapes : GetComponents<Collider>();
        Gizmos.color = new Color(1f, 0.3f, 0.2f, 0.9f);
        foreach (Collider c in list)
        {
            if (c == null) continue;
            Transform t = c.transform;
            switch (c)
            {
                case SphereCollider sc:
                    Vector3 s = t.lossyScale;
                    Gizmos.DrawWireSphere(t.TransformPoint(sc.center),
                        sc.radius * Mathf.Max(Mathf.Abs(s.x), Mathf.Max(Mathf.Abs(s.y), Mathf.Abs(s.z))));
                    break;
                case BoxCollider bc:
                    Gizmos.matrix = t.localToWorldMatrix;
                    Gizmos.DrawWireCube(bc.center, bc.size);
                    Gizmos.matrix = Matrix4x4.identity;
                    break;
                case CapsuleCollider cc:
                    Vector3 ls = t.lossyScale;
                    GetCapsule(cc, new Vector3(Mathf.Abs(ls.x), Mathf.Abs(ls.y), Mathf.Abs(ls.z)), out Vector3 p0, out Vector3 p1, out float r);
                    Gizmos.DrawWireSphere(p0, r);
                    Gizmos.DrawWireSphere(p1, r);
                    Gizmos.DrawLine(p0, p1);
                    break;
            }
        }
    }
}
