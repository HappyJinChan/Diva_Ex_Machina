using System.Collections;
using UnityEngine;

public class MissileLauncherController : MonoBehaviour
{
    [Tooltip("各發射管。請把每個 launcher 的藍色 Z 軸（forward）朝向出管方向")]
    public Transform[] launchers;

    [Tooltip("每根發射管之間的間隔（秒）")]
    public float launchInterval = 0.1f;

    [Tooltip("出管初速")]
    public float missileLanchSpeed = 80f;

    public GameObject missilePrefab;

    [Tooltip("勾選 = 一律沿世界座標正上方出管（真正的 VLS，載具翻滾時飛彈仍然垂直射出）\n不勾 = 沿發射管的 forward 出管")]
    public bool launchStraightUp = false;

    [Tooltip("傷害歸屬者。留空則自動用這個物件的 root，用來避免飛彈回頭鎖自己")]
    public GameObject attacker;

    public IEnumerator Launch(Transform target)
    {
        if (missilePrefab == null || launchers == null || launchers.Length == 0)
            yield break;

        GameObject owner = (attacker != null) ? attacker : transform.root.gameObject;

        foreach (Transform launcher in launchers)
        {
            if (launcher == null) continue;

            // 出管方向
            Vector3 launchDir = launchStraightUp ? Vector3.up : launcher.forward;

            // 機頭朝出管方向。第二參數給一個跟 launchDir 不平行的軸，避免 LookRotation 退化
            Vector3 rollRef = (Mathf.Abs(Vector3.Dot(launchDir, Vector3.up)) > 0.99f)
                ? launcher.forward
                : Vector3.up;
            Quaternion rot = Quaternion.LookRotation(launchDir, rollRef);

            // 物件池取代 Instantiate。池子缺席時內部會退回 Instantiate，行為不變。
            GameObject go = PrefabPool.Spawn(missilePrefab, launcher.position, rot);
            if (go == null) continue;

            Rigidbody missileRb = go.GetComponent<Rigidbody>();
            if (missileRb != null)
                missileRb.linearVelocity = launchDir * missileLanchSpeed;
            else
                Debug.LogWarning($"{name}: missilePrefab 上沒有 Rigidbody，飛彈不會移動。", go);

            Bullet bullet = go.GetComponent<Bullet>();
            if (bullet != null)
            {
                // 這兩行必須在 Spawn 之後 —— Bullet 在被啟用時會把所有欄位還原成 prefab 值，
                // 先寫會被蓋掉。順序跟原本的 Instantiate 版本一致，不用調整。
                bullet.attacker = owner;
                if (bullet is GuidedMissile guided) guided.SetTarget(target);   // non-guided bullets fly straight
            }
            else
            {
                Debug.LogWarning($"{name}: missilePrefab 上沒有 Bullet，無法指定追蹤目標。", go);
            }

            yield return new WaitForSeconds(launchInterval);
        }
    }
}