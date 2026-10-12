using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Splines;

[RequireComponent(typeof(Rigidbody))]
public class MoveWithVelocity : MonoBehaviour
{
    public SplineContainer spline;
    [Range(0f, 1f)] public float t = 0f;
    public float speed = 5f;
    public bool loop = true;


    public bool reverse = false;

    public bool startFromNearestPoint = true;

    [Header("Steering")]
    public float maxAccel = 50f;
    public float lookAhead = 0.01f;

    [Header("Facing")]
    public bool faceMoveDirection = true;
    public float turnSpeed = 12f;
    public bool keepUpright = true;
    public float minSpeedToTurn = 0.05f;

    private Rigidbody rb;
    private float approxSplineLength = 10f;

    [Header("Camera Rotation Sync")]
    public Transform cameraTransform;

    [Header("Ignore Spline Height")]
    public float fixedHeight = 1.6f;   // ��T�w�� Y�]�@�ɰ��ס^


    public bool pinHeight = true;


    public bool zeroVerticalVelocity = true;

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        approxSplineLength = spline != null ? Mathf.Max(0.01f, spline.CalculateLength()) : 10f;
    }

    void Start()
    {
        if (startFromNearestPoint) SnapToNearestPoint();
    }

    /// <summary>�� t �]�� spline �W����ثe��m�̪��I�C</summary>
    [ContextMenu("Snap t To Nearest Point")]
    private void SnapToNearestPoint()
    {
        if (spline == null) return;

        Vector3 worldPos = (rb != null) ? rb.position : transform.position;

        // GetNearestPoint �Ϊ��O spline ���ϰ�y��
        float3 local = (float3)spline.transform.InverseTransformPoint(worldPos);
        SplineUtility.GetNearestPoint(spline.Spline, local, out float3 _, out float nearestT);
        t = nearestT;
    }

    void FixedUpdate()
    {
        if (spline == null) return;

        float dir = reverse ? -1f : 1f;

        float dt = (speed / approxSplineLength) * Time.fixedDeltaTime;
        t += dt * dir;

        if (loop) t = Mathf.Repeat(t, 1f);
        else t = Mathf.Clamp01(t);

        float tAhead = loop ? Mathf.Repeat(t + lookAhead * dir, 1f) : Mathf.Clamp01(t + lookAhead * dir);

        Vector3 targetPos = spline.EvaluatePosition(tAhead);
        targetPos.y = fixedHeight;   // �� ���� spline �� y�A��w�b�T�w����

        Vector3 toTarget = (targetPos - rb.position);
        toTarget.y = 0f;             // �� �u�Τ�����q�Ⲿ�ʤ�V�Ay ���ѻP�X��
        Vector3 desiredVel = toTarget.sqrMagnitude > 0.000001f ? toTarget.normalized * speed : Vector3.zero;

        Vector3 dv = desiredVel - rb.linearVelocity;

        // dv.y �b�o�̨�N�O�d 0 ���~���Ȥ����B�z �X�X ������ѤU���� PinHeight �w�ѡC
        // �쥻�� dv.y �i ClampMagnitude ���ӰƧ@�ΡGClampMagnitude �Y���O��ӦV�q�A
        // �_�B�M���s�ɤ���� dv/dt ���W�L maxAccel�]15/0.02 = 750 vs 50�^�A
        // ��ӦV�q�|�Q�Y��� 1/15 �X�X �������q�]�@�_�Q�d�z�A
        // ���G�O�̻ݭn�����}�����ɭԡA���O�Ϧӳ̤p�C
        dv.y = 0f;

        Vector3 accel = Vector3.ClampMagnitude(dv / Time.fixedDeltaTime, maxAccel);
        rb.AddForce(accel, ForceMode.Acceleration);

        if (faceMoveDirection)
        {
            Vector3 v = rb.linearVelocity;

            if (v.sqrMagnitude > (minSpeedToTurn * minSpeedToTurn))
            {
                Vector3 forward = v.normalized;

                if (keepUpright)
                {
                    forward = Vector3.ProjectOnPlane(forward, Vector3.up).normalized;
                    // �쥻�o�̬O return �X�X ���|�s�P�U����������@�_���L�C
                    // �令�u���L��V�A������ӱ`����C
                    if (forward.sqrMagnitude >= 0.000001f)
                        ApplyFacing(forward);
                }
                else
                {
                    ApplyFacing(forward);
                }
            }
        }

        PinHeight();
    }

    private void ApplyFacing(Vector3 forward)
    {
        Quaternion oldRot = rb.rotation;
        Quaternion targetRot = Quaternion.LookRotation(forward, Vector3.up);
        Quaternion newRot = Quaternion.Slerp(oldRot, targetRot, turnSpeed * Time.fixedDeltaTime);

        // Apply rotation to rigidbody
        rb.MoveRotation(newRot);

        // Sync camera by the same delta rotation
        if (cameraTransform != null)
        {
            Quaternion delta = newRot * Quaternion.Inverse(oldRot);
            cameraTransform.rotation = delta * cameraTransform.rotation;
        }
    }

    /// <summary>
    /// �Ⱚ�װv�^ fixedHeight�A�òM�������t�סC
    ///
    /// ��b FixedUpdate �̫�G�o�@�B�Ҧ��|�v�T rb ���ާ@�������F�A
    /// �̫�g�J���Ȥ~�O�o�� physics step �u���ͮĪ��ȡC
    /// </summary>
    private void PinHeight()
    {
        if (pinHeight)
        {
            Vector3 p = rb.position;
            if (p.y != fixedHeight)
            {
                p.y = fixedHeight;
                rb.position = p;
            }
        }

        if (zeroVerticalVelocity)
        {
            Vector3 v = rb.linearVelocity;
            if (v.y != 0f)
            {
                v.y = 0f;
                rb.linearVelocity = v;
            }
        }
    }

    /// <summary>
    /// �� fixedHeight �]���ثe����ڰ��סC
    ///
    /// �γ~�G��}���L����A�A�Q�H�u�{�b����m�v���ǭ��s�w�q��ǰ��סA
    /// �Ӥ��O�^��Y�Ӥw�g�����D�藍�諸�¼ƭȡC
    /// ������M�s�������Ρ]Inspector �k����^�C
    /// </summary>
    [ContextMenu("Set Fixed Height From Current Y")]
    private void SetFixedHeightFromCurrent()
    {
        fixedHeight = transform.position.y;
        Debug.Log($"[MoveWithVelocity] fixedHeight �]�� {fixedHeight}", this);
    }
}