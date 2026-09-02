using UnityEngine;
using Unity.XR.PXR;

/// <summary>
/// 手部粒子交互器：从 PICO 手部追踪读取左右手的掌心和 5 个指尖位置，
/// 传给 ParticlePet 作为斥力碰撞点——粒子靠近手会被推开、绕过手继续运动。
/// 编辑器内按住鼠标左键可用光标模拟一只手（投到宠物前方平面上）。
/// 需要 PXR_ProjectSetting 中 handTracking 开启。
/// </summary>
public class HandParticleInteractor : MonoBehaviour
{
    [Tooltip("目标粒子宠物（留空则取同物体上的 ParticlePet）")]
    public ParticlePet pet;

    [Tooltip("XR Origin（关节坐标是 tracking space，需要经它转世界坐标；留空自动查找）")]
    public Transform xrOrigin;

    // 每只手取 6 个碰撞点：掌心 + 拇指/食指/中指/无名指/小指指尖
    static readonly int[] TrackedJoints =
    {
        (int)HandJoint.JointPalm,
        (int)HandJoint.JointThumbTip,
        (int)HandJoint.JointIndexTip,
        (int)HandJoint.JointMiddleTip,
        (int)HandJoint.JointRingTip,
        (int)HandJoint.JointLittleTip,
    };

    HandJointLocations leftLocs = new HandJointLocations();
    HandJointLocations rightLocs = new HandJointLocations();
    // 12（双手）+ 4（双手柄各 2 点）+ 1（头盔）= 17
    readonly Vector3[] worldPoints = new Vector3[18];

    void Start()
    {
        if (pet == null) pet = GetComponent<ParticlePet>();
        if (xrOrigin == null)
        {
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null) xrOrigin = origin.transform;
        }
    }

    void Update()
    {
        int n = 0;

#if UNITY_EDITOR
        // 编辑器模拟：按住鼠标左键，光标射线投到宠物前方竖直平面 = 模拟手掌位置
        if (Input.GetMouseButton(0) && pet != null)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                Plane plane = new Plane(-cam.transform.forward, pet.transform.position);
                if (plane.Raycast(ray, out float dist))
                {
                    worldPoints[n++] = ray.GetPoint(dist);
                    pet.SetHandPoints(worldPoints, n);
                    return;
                }
            }
        }
#endif

        n += CollectHand(HandType.HandLeft, leftLocs, n);
        n += CollectHand(HandType.HandRight, rightLocs, n);
        n += CollectController(PXR_Input.Controller.LeftController, n);
        n += CollectController(PXR_Input.Controller.RightController, n);
        n += CollectHead(n);
        pet?.SetHandPoints(worldPoints, n);
    }

    /// <summary>收集一只手的关节点并转世界坐标，返回收集到的点数（手不在视野返回 0）</summary>
    int CollectHand(HandType hand, HandJointLocations locs, int offset)
    {
        if (!PXR_HandTracking.GetJointLocations(hand, ref locs)) return 0;
        if (locs.isActive == 0u) return 0;

        int count = 0;
        for (int j = 0; j < TrackedJoints.Length; j++)
        {
            ref var joint = ref locs.jointLocations[TrackedJoints[j]];
            Vector3 p = joint.pose.Position.ToVector3(); // tracking space（XR Origin 局部坐标）
            worldPoints[offset + count++] = xrOrigin != null
                ? xrOrigin.TransformPoint(p)
                : p;
        }
        return count;
    }

    /// <summary>收集一只手柄的 2 个碰撞点（手柄位置 + 指向方向前 8cm），未连接返回 0</summary>
    int CollectController(PXR_Input.Controller controller, int offset)
    {
        if (!PXR_Input.IsControllerConnected(controller)) return 0;

        Vector3 p = PXR_Input.GetControllerPredictPosition(controller, 0);
        Quaternion r = PXR_Input.GetControllerPredictRotation(controller, 0);
        worldPoints[offset] = xrOrigin != null ? xrOrigin.TransformPoint(p) : p;
        Vector3 tip = p + r * Vector3.forward * 0.08f;
        worldPoints[offset + 1] = xrOrigin != null ? xrOrigin.TransformPoint(tip) : tip;
        return 2;
    }

    /// <summary>头盔碰撞点：主相机位置（凑近粒子时像用头推开它们）</summary>
    int CollectHead(int offset)
    {
        var cam = Camera.main;
        if (cam == null) return 0;
        worldPoints[offset] = cam.transform.position;
        return 1;
    }
}
