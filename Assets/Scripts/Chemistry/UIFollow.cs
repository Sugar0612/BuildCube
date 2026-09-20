using UnityEngine;

/// <summary>
/// 世界空间面板跟随用户：面板平滑保持在用户左/右手斜前方（随用户转身与走动），
/// 并始终朝向用户。基于主相机（头盔）位姿，在 LateUpdate 中驱动。
/// </summary>
public static class UIFollow
{
    /// <summary>
    /// 计算跟随目标位姿。
    /// side：-1=左手侧，+1=右手侧；forwardBias/lateralBias 为方向权重（自动归一化）。
    /// </summary>
    public static void Compute(Camera cam, float side, float distance, float forwardBias, float lateralBias,
                               float heightOffset, out Vector3 pos, out Quaternion rot)
    {
        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 1e-4f) fwd = Vector3.forward;
        fwd.Normalize();
        Vector3 right = Vector3.Cross(Vector3.up, fwd);

        var dir = fwd * forwardBias + right * (lateralBias * side);
        pos = cam.transform.position + dir.normalized * distance + Vector3.up * heightOffset;
        Vector3 toCam = cam.transform.position - pos;
        rot = toCam.sqrMagnitude > 1e-6f
            ? Quaternion.LookRotation(-toCam.normalized, Vector3.up)
            : cam.transform.rotation;
    }

    /// <summary>每帧驱动 RectTransform 平滑跟随（首次直接贴附目标位）</summary>
    public static void Drive(RectTransform rt, ref bool snapped, float side, float distance,
                             float forwardBias, float lateralBias, float heightOffset, float lerp = 7f)
    {
        var cam = Camera.main;
        if (cam == null || rt == null) return;
        Compute(cam, side, distance, forwardBias, lateralBias, heightOffset, out var pos, out var rot);
        if (!snapped)
        {
            rt.position = pos;
            snapped = true;
        }
        else
        {
            float k = 1f - Mathf.Exp(-lerp * Time.deltaTime);
            rt.position = Vector3.Lerp(rt.position, pos, k);
        }
        rt.rotation = rot;
    }
}
