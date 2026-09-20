using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// 化学确认卡片（世界空间，运行时创建）：展示语音识别原文与候选物质按钮（名称+分子式+分子量），
/// 弥补 STT 对化学术语识别不准的问题——用户点选候选、或用系统键盘手动输入、或重说。
/// 位于宠物左下方，与右侧反应台面板错开；每帧面向主相机。
/// </summary>
public class ChemUIController : MonoBehaviour
{
    public event System.Action<ChemCandidate> CandidatePicked;
    public event System.Action Respeak;
    public event System.Action Cancelled;
    public event System.Action<string> ManualSubmitted;
    /// <summary>系统键盘关闭（true=已提交文本；false=取消/失焦），用于恢复录音等收尾</summary>
    public event System.Action<bool> KeyboardClosed;

    public bool Visible { get; private set; }

    RectTransform canvasRt;
    Text heardText, statusText;
    readonly List<GameObject> candRows = new List<GameObject>();
    readonly List<ChemCandidate> pendingCandidates = new List<ChemCandidate>();
    ChemButton manualBtn, respeakBtn, cancelBtn;
    bool posSnapped;

    TouchScreenKeyboard keyboard;
#if UNITY_EDITOR
    bool editorManual;
    string editorText = "";
#endif

    [Header("面板位置")]
    [Tooltip("是否跟随用户（默认关：面板固定在智能球左侧，稳定可预期）")]
    public bool followUser = false;
    [Tooltip("固定模式：面板相对宠物中心的偏移（智能球左侧、避开上方的方程式舞台）")]
    public Vector3 panelOffset = new Vector3(-1.08f, 0f, 0f);
    [Tooltip("跟随模式：面板与用户的距离（米）")]
    public float followDistance = 1.05f;
    [Tooltip("跟随方向的向前权重")]
    public float forwardBias = 0.9f;
    [Tooltip("跟随方向的侧向权重")]
    public float lateralBias = 0.45f;
    [Tooltip("跟随模式：相对视线的高度偏移（米）")]
    public float followHeight = -0.1f;

    const float CanvasW = 700f, CanvasH = 760f;
    const float WidthMeters = 0.62f;

    static readonly Color[] CandColors =
    {
        new Color(0.13f, 0.36f, 0.24f),
        new Color(0.14f, 0.30f, 0.44f),
        new Color(0.30f, 0.24f, 0.42f),
    };

    void Awake()
    {
        BuildCanvas();
        canvasRt.gameObject.SetActive(false); // 默认隐藏，Show 时打开
    }

    void Start()
    {
        // 射线指针：挂在 XR Origin 的 Camera Offset 下（编辑器无 XR 时用鼠标）
        var offset = FindCameraOffset();
        if (offset != null)
        {
            UIRayPointer.Create(offset, true);
            UIRayPointer.Create(offset, false);
        }
        else
        {
            Debug.LogWarning("[ChemUI] 未找到 Camera Offset，手柄射线未创建（编辑器仍可用鼠标）");
        }
    }

    static Transform FindCameraOffset()
    {
        var go = GameObject.Find("XR Origin (VR)/Camera Offset");
        if (go != null) return go.transform;
        foreach (var t in Object.FindObjectsByType<Transform>(FindObjectsSortMode.None))
            if (t.name == "Camera Offset") return t;
        return null;
    }

    void BuildCanvas()
    {
        canvasRt = ChemUIWidgets.CreateCanvas(transform, "ChemConfirmCanvas",
            new Vector2(CanvasW, CanvasH), WidthMeters, Vector3.zero);
        if (followUser) canvasRt.SetParent(null);       // 跟随模式：脱离宠物世界空间驱动
        else canvasRt.localPosition = panelOffset;      // 固定模式：挂在宠物左侧
        var root = canvasRt;

        heardText = ChemUIWidgets.CreateText(root, "Heard", 26, TextAnchor.UpperLeft);
        var hrt = heardText.rectTransform;
        hrt.anchoredPosition = new Vector2(0f, 316f);
        hrt.sizeDelta = new Vector2(650f, 72f);

        statusText = ChemUIWidgets.CreateText(root, "Status", 24, TextAnchor.UpperLeft,
            new Color(0.6f, 0.9f, 1f, 0.95f));
        var srt = statusText.rectTransform;
        srt.anchoredPosition = new Vector2(0f, -212f);
        srt.sizeDelta = new Vector2(650f, 110f);

        manualBtn = ChemUIWidgets.CreateButton(root, "✍ 手动输入", 28, new Vector2(200f, 72f),
            new Color(0.16f, 0.34f, 0.55f), OpenManualInput);
        manualBtn.RT.anchoredPosition = new Vector2(-213f, -88f);

        respeakBtn = ChemUIWidgets.CreateButton(root, "🔄 重说", 28, new Vector2(168f, 72f),
            new Color(0.13f, 0.40f, 0.34f), () => Respeak?.Invoke());
        respeakBtn.RT.anchoredPosition = new Vector2(15f, -88f);

        cancelBtn = ChemUIWidgets.CreateButton(root, "✕ 取消", 28, new Vector2(132f, 72f),
            new Color(0.36f, 0.20f, 0.20f), () => Cancelled?.Invoke());
        cancelBtn.RT.anchoredPosition = new Vector2(216f, -88f);
    }

    /// <summary>展示确认卡片。candidates 最多取 3 个；为空时提示手动输入/重说。</summary>
    public void Show(string heard, List<ChemCandidate> candidates, string note = null)
    {
        canvasRt.gameObject.SetActive(true);
        Visible = true;
        pendingCandidates.Clear();
        if (candidates != null) pendingCandidates.AddRange(candidates);

        heardText.text = "你说的：" + (string.IsNullOrEmpty(heard) ? "（无）" : "「" + heard + "」");

        ClearCandidateRows();
        int n = Mathf.Min(pendingCandidates.Count, 3);
        for (int i = 0; i < n; i++)
        {
            var c = pendingCandidates[i];
            var label = $"{c.DisplayName()}   {c.formula}";
            if (!string.IsNullOrEmpty(c.mw)) label += $"   · {c.mw} g/mol";
            if (!c.validated) label += "（未校验）";
            var idx = i;
            var btn = ChemUIWidgets.CreateButton(canvasRt, label, 26, new Vector2(650f, 78f),
                CandColors[i], () => CandidatePicked?.Invoke(pendingCandidates[idx]));
            btn.RT.anchoredPosition = new Vector2(0f, 216f - i * 94f);
            // 防止长标签溢出按钮边界与下方按钮重叠：纵向截断
            if (btn.LabelText != null) btn.LabelText.verticalOverflow = VerticalWrapMode.Truncate;
            candRows.Add(btn.RT.gameObject);
        }

        string status = n > 0
            ? "请用手柄射线点选候选（编辑器：数字键 1/2/3）"
            : "没识别出化学物质，请手动输入或重说";
        if (!string.IsNullOrEmpty(note)) status = note + "\n" + status;
        statusText.text = status;
    }

    public void Hide()
    {
        Visible = false;
        canvasRt.gameObject.SetActive(false);
        keyboard = null;
#if UNITY_EDITOR
        editorManual = false;
#endif
    }

    public void SetStatus(string s)
    {
        if (statusText != null) statusText.text = s;
    }

    /// <summary>打开手动输入：真机用 PICO 系统键盘，编辑器用 OnGUI 输入窗</summary>
    public void OpenManualInput()
    {
#if !UNITY_EDITOR
        if (TouchScreenKeyboard.isSupported)
        {
            if (keyboard == null || keyboard.status != TouchScreenKeyboard.Status.Visible)
                keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default,
                    false, false, false, false, "输入物质名或分子式，如：乙醇 / ethanol / C2H6O");
            return;
        }
        SetStatus("当前设备不支持系统键盘，请用语音 + 候选按钮");
#else
        editorManual = true;
        editorText = "";
#endif
    }

    void ClearCandidateRows()
    {
        foreach (var go in candRows)
        {
            // 反注册候选按钮（ChemButton 未挂在组件上，按 RectTransform 匹配清理）
            for (int i = ChemRayRegistry.Targets.Count - 1; i >= 0; i--)
            {
                if (ChemRayRegistry.Targets[i] is ChemButton cb && cb.RT != null && cb.RT.gameObject == go)
                    ChemRayRegistry.Unregister(cb);
            }
            if (go != null) Destroy(go);
        }
        candRows.Clear();
    }

    void Update()
    {
        // 系统键盘结果轮询
        if (keyboard != null)
        {
            if (keyboard.status == TouchScreenKeyboard.Status.Done)
            {
                var t = keyboard.text;
                keyboard = null;
                KeyboardClosed?.Invoke(true);
                if (!string.IsNullOrWhiteSpace(t)) ManualSubmitted?.Invoke(t.Trim());
            }
            else if (keyboard.status == TouchScreenKeyboard.Status.Canceled
                     || keyboard.status == TouchScreenKeyboard.Status.LostFocus)
            {
                keyboard = null;
                KeyboardClosed?.Invoke(false);
            }
        }

#if UNITY_EDITOR
        // 编辑器快捷键：1/2/3 选候选，M 手动输入，R 重说，C/Esc 取消
        if (!Visible) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        if (kb.digit1Key.wasPressedThisFrame) PickIndex(0);
        if (kb.digit2Key.wasPressedThisFrame) PickIndex(1);
        if (kb.digit3Key.wasPressedThisFrame) PickIndex(2);
        if (kb.mKey.wasPressedThisFrame) OpenManualInput();
        if (kb.rKey.wasPressedThisFrame) Respeak?.Invoke();
        if (kb.cKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame) Cancelled?.Invoke();
#endif
    }

    void PickIndex(int i)
    {
        if (i < pendingCandidates.Count)
            CandidatePicked?.Invoke(pendingCandidates[i]);
    }

    void LateUpdate()
    {
        if (canvasRt == null || !canvasRt.gameObject.activeSelf) return;
        if (followUser)
        {
            UIFollow.Drive(canvasRt, ref posSnapped, -1f, followDistance, forwardBias, lateralBias, followHeight);
        }
        else
        {
            // 始终面向主相机（与 PetStatusUI 同步策略）
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - canvasRt.position;
                canvasRt.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
            }
        }
    }

#if UNITY_EDITOR
    readonly Rect winRect = new Rect(420f, 220f, 440f, 130f);

    void OnGUI()
    {
        if (!editorManual) return;
        GUI.Box(winRect, "手动输入物质名 / 分子式");
        GUI.SetNextControlName("chem_input");
        editorText = GUI.TextField(new Rect(winRect.x + 14f, winRect.y + 34f, 290f, 28f), editorText);
        GUI.FocusControl("chem_input");
        if (GUI.Button(new Rect(winRect.x + 318f, winRect.y + 32f, 100f, 32f), "确定")
            || (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return))
        {
            var t = editorText.Trim();
            editorManual = false;
            GUIUtility.keyboardControl = 0;
            KeyboardClosed?.Invoke(true);
            if (t.Length > 0) ManualSubmitted?.Invoke(t);
        }
        if (GUI.Button(new Rect(winRect.x + 14f, winRect.y + 74f, 100f, 32f), "取消"))
        {
            editorManual = false;
            GUIUtility.keyboardControl = 0;
            KeyboardClosed?.Invoke(false);
        }
    }
#endif
}
