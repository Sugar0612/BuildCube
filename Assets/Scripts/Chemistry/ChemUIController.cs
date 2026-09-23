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
    /// <summary>「停止思考」按钮按下：中止在途 GLM 请求</summary>
    public event System.Action StopRequested;

    public bool Visible { get; private set; }

    RectTransform canvasRt;
    RectTransform stopRt;
    Text heardText, statusText;
    readonly List<GameObject> candRows = new List<GameObject>();
    readonly List<ChemCandidate> pendingCandidates = new List<ChemCandidate>();
    ChemButton manualBtn, respeakBtn, cancelBtn;
    bool posSnapped;

    TouchScreenKeyboard keyboard;
    float keyboardOpenAt;          // 本次打开键盘的时刻（area 兜底检测的宽限期）
    bool keyboardCloseNotified;    // area 兜底只补发一次关闭事件
    bool keyboardEverShown;        // 本次键盘曾真正出现（出现后的关闭一定是用户手动关，绝不可自动重开）
    bool keyboardOpening;          // 打开请求已发出、等待真正唤起的过渡态（防 Update 里旧引用干扰）
    int keyboardOpenRetries;       // 本次打开的重试次数（PICO 系统拒绝时自动重试）
    float nextKeyboardRetryAt;     // 下次自动重试时刻
    float lastKeyboardCloseAt = -999f; // 上次检测到键盘关闭的时刻（PICO 重开冷却保护）
    float pendingOpenAt = -1f;     // 被推迟的打开请求时刻（冷却/退避到点执行；<0 无请求）

    const int MaxKeyboardRetries = 5;          // PICO 冷却期较长，退避重试覆盖 ~6 秒
    const float KeyboardReopenCooldown = 0.7f; // 手动关闭系统键盘后立刻重开会被系统静默拒绝
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

        BuildStopCanvas();
    }

    /// <summary>「停止思考」按钮画布：与确认卡片同位（思考期间卡片隐藏，位置空出）。
    /// 标签用纯文字——内置字体缺 ⏹ 等符号字形，缺失字形会导致排版偏移不居中。</summary>
    void BuildStopCanvas()
    {
        stopRt = ChemUIWidgets.CreateCanvas(transform, "StopThinkCanvas",
            new Vector2(460f, 150f), 0.44f, panelOffset);
        var btn = ChemUIWidgets.CreateButton(stopRt, "停 止 思 考", 38, new Vector2(440f, 130f),
            new Color(0.45f, 0.14f, 0.14f), () => StopRequested?.Invoke());
        btn.RT.anchoredPosition = Vector2.zero;
        stopRt.gameObject.SetActive(false); // 仅思考期间显示（AIVoicePet.Update 驱动）
    }

    /// <summary>显示/隐藏「停止思考」按钮</summary>
    public void ShowStop(bool show)
    {
        if (stopRt != null && stopRt.gameObject.activeSelf != show)
            stopRt.gameObject.SetActive(show);
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
        keyboardOpening = false;
        pendingOpenAt = -1f; // 卡片收起后不再补开键盘，避免幽灵弹出
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
            // PICO 上 keyboard 实例一旦创建就常驻内存，status 停在 Visible 不再更新；
            // 先显式停用旧实例再丢弃引用。仅置空 C# 引用时 Unity/PICO 仍可能认为旧键盘
            // 正在活动，导致下一次 Open 被静默忽略。
            if (keyboard != null)
                keyboard.active = false;
            keyboard = null;
            keyboardOpening = true;
            keyboardOpenRetries = 0;
            pendingOpenAt = -1f;
            // 手动关闭系统键盘后 PICO 有冷却期：立刻重开会被系统静默拒绝（二次点按钮打不开的根因）
            // → 推迟到冷却结束再打开
            float wait = lastKeyboardCloseAt + KeyboardReopenCooldown - Time.time;
            if (wait > 0f) pendingOpenAt = Time.time + wait;
            else TryOpenKeyboard();
            return;
        }
        SetStatus("当前设备不支持系统键盘，请用语音 + 候选按钮");
#else
        editorManual = true;
        editorText = "";
#endif
    }

    /// <summary>实际执行 TouchScreenKeyboard.Open，PICO 系统拒绝时退避重试</summary>
    void TryOpenKeyboard()
    {
#if !UNITY_EDITOR
        pendingOpenAt = -1f;
        keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default,
            false, false, false, false, "输入物质名或分子式，如：乙醇 / ethanol / C2H6O");
        keyboardOpenAt = Time.time;
        keyboardCloseNotified = false;
        keyboardEverShown = false;
        keyboardOpening = false;
        // PICO 静默拒绝检测起点：Open 返回实例但 status 立即失败，或 area 一直为 0（未真正唤起）
        nextKeyboardRetryAt = Time.time + 0.5f;
#endif
    }

    /// <summary>键盘任一关闭路径的统一收尾（记录关闭时刻用于重开冷却）</summary>
    void NotifyKeyboardClosed()
    {
        keyboard = null;
        keyboardOpening = false;
        pendingOpenAt = -1f;
        keyboardOpenRetries = 0;
        lastKeyboardCloseAt = Time.time;
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
        // 被推迟的键盘打开请求到点执行（重开冷却 / 退避重试）
        if (pendingOpenAt > 0f && Time.time >= pendingOpenAt)
        {
            pendingOpenAt = -1f;
            if (keyboardOpening) TryOpenKeyboard();
        }

        // 系统键盘结果轮询（keyboardOpening 期间跳过，避免旧引用覆盖新实例）
        if (keyboard != null && !keyboardOpening)
        {
            var st = keyboard.status;
            if (st == TouchScreenKeyboard.Status.Visible && TouchScreenKeyboard.area.height > 0)
                keyboardEverShown = true;

            // PICO 静默拒绝自动重试：仅当键盘从未真正出现时才重试——
            // 出现过之后的关闭一定是用户手动关闭，自动重开会打断用户操作；
            // 打开超过 2 秒仍报 Visible 视为已成功（防止 PICO area 不上报导致误重试）。
            bool openRejected = st == TouchScreenKeyboard.Status.Canceled
                                || st == TouchScreenKeyboard.Status.LostFocus
                                || (st == TouchScreenKeyboard.Status.Visible
                                    && TouchScreenKeyboard.area.height <= 0);
            if (!keyboardEverShown
                && Time.time - keyboardOpenAt < 2f
                && keyboardOpenRetries < MaxKeyboardRetries
                && Time.time >= nextKeyboardRetryAt
                && openRejected)
            {
                keyboardOpenRetries++;
                Debug.Log($"[ChemUI] 键盘未唤起，退避重试 {keyboardOpenRetries}/{MaxKeyboardRetries}");
                keyboard = null;
                keyboardOpening = true;
                // 退避间隔逐步拉长（1.0/1.4/1.8/2.2/2.6s），覆盖 PICO 重开冷却窗口
                pendingOpenAt = Time.time + 0.6f + 0.4f * keyboardOpenRetries;
                nextKeyboardRetryAt = pendingOpenAt;
                return;
            }

            if (st == TouchScreenKeyboard.Status.Done)
            {
                var t = keyboard.text;
                NotifyKeyboardClosed();
                KeyboardClosed?.Invoke(true);
                if (!string.IsNullOrWhiteSpace(t)) ManualSubmitted?.Invoke(t.Trim());
            }
            else if (st == TouchScreenKeyboard.Status.Canceled
                     || st == TouchScreenKeyboard.Status.LostFocus)
            {
                NotifyKeyboardClosed();
                KeyboardClosed?.Invoke(false);
            }
            else if (st == TouchScreenKeyboard.Status.Visible
                     && !keyboardCloseNotified
                     && Time.time - keyboardOpenAt > 1.5f
                     && TouchScreenKeyboard.area.height <= 0)
            {
                // PICO 兜底：status 停留 Visible 但键盘实际已收回（area 归零）→ 补发关闭事件，
                // 让上层恢复麦克风/录音；引用保留以防 status 后续再变 Done（提交仍可送达）
                keyboardCloseNotified = true;
                lastKeyboardCloseAt = Time.time;
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
        if (canvasRt != null && canvasRt.gameObject.activeSelf)
        {
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

        // 停止按钮同样面向用户
        if (stopRt != null && stopRt.gameObject.activeSelf)
        {
            var cam = Camera.main;
            if (cam != null)
            {
                Vector3 toCam = cam.transform.position - stopRt.position;
                stopRt.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
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
