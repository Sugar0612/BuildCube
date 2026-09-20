using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 手柄射线指针：从手柄位姿发出射线点选世界空间化学 UI（自绘命中测试，不依赖 XRI 事件系统）。
/// 位姿经 Input System 读取（&lt;XRController&gt;/devicePosition|deviceRotation），挂在 XR Origin 的
/// Camera Offset 下与 XRI TrackedPoseDriver 同原理映射到世界空间。
/// 编辑器无 XR 设备时自动回退：鼠标位置 + 左键点击（仅右手指针启用）。
/// </summary>
public class UIRayPointer : MonoBehaviour
{
    public Color rayColor = new Color(0.35f, 0.85f, 1f, 0.95f);
    public float maxDistance = 8f;

    InputAction posAct, rotAct, selAct, gripAct;
    LineRenderer line;
    Material lineMat;
    IChemRayTarget hovered;
    bool pressed;
    bool rightHand;

    /// <summary>当前是否按住扳机/握把（供插槽拖拽等 3D 交互复用）</summary>
    public bool IsPressed => pressed;

    /// <summary>所有活动指针（EquationBench 等做 3D 射线交互时复用其位姿与按键状态）</summary>
    public static readonly List<UIRayPointer> All = new List<UIRayPointer>();

    /// <summary>在追踪空间父节点（Camera Offset）下创建左/右手射线指针</summary>
    public static UIRayPointer Create(Transform trackingSpace, bool rightHand)
    {
        var go = new GameObject(rightHand ? "ChemRay_R" : "ChemRay_L");
        go.transform.SetParent(trackingSpace, false);
        var p = go.AddComponent<UIRayPointer>();
        p.rightHand = rightHand;
        p.Setup(rightHand);
        return p;
    }

    void Setup(bool right)
    {
        string hand = right ? "RightHand" : "LeftHand";
        posAct = new InputAction(null, InputActionType.Value, $"<XRController>{{{hand}}}/devicePosition");
        rotAct = new InputAction(null, InputActionType.Value, $"<XRController>{{{hand}}}/deviceRotation");
        selAct = new InputAction(null, InputActionType.Button, $"<XRController>{{{hand}}}/{{TriggerButton}}");
        gripAct = new InputAction(null, InputActionType.Button, $"<XRController>{{{hand}}}/{{GripButton}}");
        posAct.Enable();
        rotAct.Enable();
        selAct.Enable();
        gripAct.Enable();

        line = gameObject.AddComponent<LineRenderer>();
        line.positionCount = 2;
        line.startWidth = line.endWidth = 0.0028f;
        line.useWorldSpace = true;
        lineMat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        if (lineMat.HasProperty("_BaseColor")) lineMat.SetColor("_BaseColor", rayColor);
        line.material = lineMat;
        All.Add(this);
    }

    void OnDestroy()
    {
        All.Remove(this);
        if (hovered != null) hovered.OnRayHover(false);
        posAct?.Disable();
        rotAct?.Disable();
        selAct?.Disable();
        gripAct?.Disable();
        posAct?.Dispose();
        rotAct?.Dispose();
        selAct?.Dispose();
        gripAct?.Dispose();
        if (lineMat != null) Destroy(lineMat);
    }

    void Update()
    {
        Vector3 origin;
        Vector3 dir;
        bool havePose = false;

        bool xrActive = posAct != null && posAct.activeControl != null
                                   && rotAct != null && rotAct.activeControl != null;
        if (xrActive)
        {
            // 手柄追踪空间位姿 → 直接映射 local 位姿（父链 = XR Origin/Camera Offset）
            transform.localPosition = posAct.ReadValue<Vector3>();
            transform.localRotation = rotAct.ReadValue<Quaternion>();
            origin = transform.position;
            dir = transform.forward;
            havePose = true;
        }
        else if (rightHand) // 编辑器回退：仅右手指针处理鼠标
        {
            var cam = Camera.main;
            var mouse = Mouse.current;
            if (cam != null && mouse != null)
            {
                var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                origin = ray.origin;
                dir = ray.direction;
                transform.position = origin;
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
                havePose = true;
            }
            else { origin = default; dir = default; }
        }
        else { origin = default; dir = default; }

        if (!havePose)
        {
            if (line != null) line.positionCount = 0;
            return;
        }
        line.positionCount = 2;

        // 命中测试：把射线变换到各按钮局部空间，与按钮平面（z=0）求交后做矩形测试，取最近
        var rayWs = new Ray(origin, dir);
        IChemRayTarget hit = null;
        var hitPoint = origin + dir * maxDistance;
        float best = float.MaxValue;
        foreach (var t in ChemRayRegistry.Targets)
        {
            if (t.RT == null || !t.RT.gameObject.activeInHierarchy) continue;
            if (RayRectIntersect(t.RT, rayWs, out float d, out var wp) && d < best)
            {
                best = d;
                hit = t;
                hitPoint = wp;
            }
        }

        if (!ReferenceEquals(hit, hovered))
        {
            hovered?.OnRayHover(false);
            hovered = hit;
            hovered?.OnRayHover(true);
        }

        line.SetPosition(0, origin);
        line.SetPosition(1, hovered != null ? hitPoint : origin + dir * maxDistance);

        bool nowPressed = (selAct != null && selAct.IsPressed()) || (gripAct != null && gripAct.IsPressed());
        if (!xrActive && rightHand && Mouse.current != null && Mouse.current.leftButton.isPressed)
            nowPressed = true; // 编辑器鼠标点击
        if (nowPressed && !pressed) hovered?.OnRayClick();
        pressed = nowPressed;
    }

    static bool RayRectIntersect(RectTransform rt, Ray ray, out float dist, out Vector3 worldPoint)
    {
        dist = 0f;
        worldPoint = default;
        var toLocal = rt.transform.worldToLocalMatrix;
        Vector3 lo = toLocal.MultiplyPoint3x4(ray.origin);
        Vector3 ld = toLocal.MultiplyVector(ray.direction);
        if (Mathf.Abs(ld.z) < 1e-6f) return false; // 与按钮平面平行
        float t = -lo.z / ld.z;
        if (t < 0f || t > 60f) return false;
        Vector3 p = lo + ld * t;
        var rect = rt.rect;
        if (Mathf.Abs(p.x) <= rect.width * 0.5f && Mathf.Abs(p.y) <= rect.height * 0.5f)
        {
            dist = t;
            worldPoint = ray.origin + ray.direction * t;
            return true;
        }
        return false;
    }
}
