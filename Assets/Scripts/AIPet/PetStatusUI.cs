using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 粒子宠物头顶的世界空间状态面板（运行时自动创建，无场景资源依赖）。
/// 显示当前状态、听到的内容与提示信息。
/// </summary>
public class PetStatusUI : MonoBehaviour
{
    [Tooltip("面板中心相对宠物中心的高度（米）")]
    public float heightAbove = 0.45f;

    [Tooltip("面板宽（米）")]
    public float widthMeters = 0.6f;

    Text text;
    RectTransform canvasRt;
    string status = "初始化中…";
    string heard = "";
    string hint = "";

    void Awake()
    {
        var canvasGo = new GameObject("StatusCanvas", typeof(Canvas));
        canvasGo.transform.SetParent(transform, false);

        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        canvasRt = canvasGo.GetComponent<RectTransform>();
        // 画布单位尺寸 600x150，通过缩放映射到米
        canvasRt.sizeDelta = new Vector2(600f, 150f);
        canvasRt.localScale = Vector3.one * (widthMeters / 600f);
        canvasRt.localPosition = new Vector3(0f, heightAbove, 0f);

        var textGo = new GameObject("StatusText", typeof(Text));
        textGo.transform.SetParent(canvasGo.transform, false);
        text = textGo.GetComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.fontSize = 26;
        text.color = new Color(1f, 1f, 1f, 0.92f);
        text.alignment = TextAnchor.MiddleCenter;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = false;

        var trt = textGo.GetComponent<RectTransform>();
        trt.anchorMin = Vector2.zero;
        trt.anchorMax = Vector2.one;
        trt.offsetMin = Vector2.zero;
        trt.offsetMax = Vector2.zero;

        Refresh();
    }

    void LateUpdate()
    {
        // 始终面向主相机
        var cam = Camera.main;
        if (cam != null && canvasRt != null)
        {
            Vector3 toCam = cam.transform.position - canvasRt.position;
            canvasRt.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
        }
    }

    public void SetStatus(string s) { status = s; Refresh(); }
    public void SetHeard(string s) { heard = "「" + s + "」"; Refresh(); }
    public void SetHint(string s) { hint = s; Refresh(); }

    void Refresh()
    {
        if (text == null) return;
        string s = status;
        if (!string.IsNullOrEmpty(heard)) s += "\n" + heard;
        if (!string.IsNullOrEmpty(hint)) s += "\n" + hint;
        text.text = s;
    }
}
