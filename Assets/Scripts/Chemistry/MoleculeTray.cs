using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 分子列表面板（跟随用户右手斜前方固定）：无限长度的可滚动列表——
/// 分子数量不设上限，一直添加一直追加；列表区支持手柄按住拖动滚动与 ▲/▼ 按钮翻动，
/// 新分子上台自动滚到底部。按钮组：＋添加分子 / ⌨键盘输入 / ⚗开始反应 / 清空。
/// 3D 方程式舞台最多呈现前 EquationBench.ReactantSlots 个反应物（相框排布上限），
/// 列表与舞台数据同源；反应分析把全部反应物交给 GLM。
/// </summary>
public class MoleculeTray : MonoBehaviour
{
    public event System.Action AddRequested;
    /// <summary>请求键盘输入（右侧面板的「键盘输入」按钮）</summary>
    public event System.Action KeyboardInputRequested;
    /// <summary>请求开始反应（≥2 个反应物时可用）</summary>
    public event System.Action ReactRequested;
    /// <summary>列表增删/清空后触发（3D 场景同步）</summary>
    public event System.Action TrayChanged;

    [Header("面板位置")]
    [Tooltip("是否跟随用户（默认关：面板固定在智能球右侧，稳定可预期）")]
    public bool followUser = false;
    [Tooltip("固定模式：面板相对宠物中心的偏移（智能球右侧、避开上方的方程式舞台）")]
    public Vector3 panelOffset = new Vector3(1.08f, 0f, 0f);
    [Tooltip("跟随模式：面板与用户的距离（米）")]
    public float followDistance = 1.05f;
    [Tooltip("跟随方向的向前权重")]
    public float forwardBias = 0.9f;
    [Tooltip("跟随方向的侧向权重")]
    public float lateralBias = 0.45f;
    [Tooltip("跟随模式：相对视线的高度偏移（米）")]
    public float followHeight = -0.1f;

    /// <summary>列表数据（无上限）</summary>
    readonly List<Molecule> molecules = new List<Molecule>();

    RectTransform canvasRt;
    RectTransform viewportRt, contentRt;
    Text countText, hintText, resultText;
    ChemButton reactBtn;
    bool posSnapped;

    // 滚动状态
    float scrollY;
    bool listDragging;
    Vector3 dragStartPos;
    float dragStartScroll;
    UIRayPointer dragPointer;

    const float CanvasW = 680f, CanvasH = 940f;
    const float WidthMeters = 0.6f;
    const float RowH = 62f, RowGap = 6f;     // 行高与行距（画布单位）
    const float ViewportH = 400f;            // 列表可视高度

    public int Count => molecules.Count;

    /// <summary>当前全部分子（按加入顺序）</summary>
    public List<Molecule> Molecules() => new List<Molecule>(molecules);

    /// <summary>按顺序返回分子数组（空位概念已取消；3D 舞台自行截取前 N 个）</summary>
    public Molecule[] MoleculesBySlot() => molecules.ToArray();

    /// <summary>全部反应物转为候选列表（GLM 反应分析请求用）</summary>
    public List<ChemCandidate> Candidates()
    {
        var list = new List<ChemCandidate>();
        foreach (var m in molecules)
            list.Add(new ChemCandidate
            {
                nameZh = m.nameZh,
                nameEn = m.nameEn,
                formula = m.formula,
                cid = m.cid,
                mw = m.mw,
                validated = true,
            });
        return list;
    }

    /// <summary>追加分子（无上限；触发 TrayChanged 同步 3D 舞台）</summary>
    public bool TryAdd(Molecule mol)
    {
        if (mol == null) return false;
        molecules.Add(mol);
        RebuildRows();
        ScrollToBottom();
        TrayChanged?.Invoke();
        return true;
    }

    public void Clear()
    {
        bool had = molecules.Count > 0;
        molecules.Clear();
        if (resultText != null) resultText.text = "";
        scrollY = 0f;
        RebuildRows();
        if (had) TrayChanged?.Invoke();
    }

    void RemoveAt(int i)
    {
        if (i < 0 || i >= molecules.Count) return;
        molecules.RemoveAt(i);
        RebuildRows();
        TrayChanged?.Invoke();
    }

    /// <summary>展示反应分析结果（面板底部文字区）</summary>
    public void ShowResult(ReactionResult r)
    {
        if (resultText == null || r == null) return;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.IsNullOrEmpty(r.equation) ? "（无可配平方程式）" : r.equation);
        var meta = new List<string>();
        if (!string.IsNullOrEmpty(r.type)) meta.Add(r.type);
        if (!string.IsNullOrEmpty(r.conditions)) meta.Add(r.conditions);
        if (!string.IsNullOrEmpty(r.energy)) meta.Add(r.energy);
        if (meta.Count > 0) sb.AppendLine(string.Join(" | ", meta.ToArray()));
        if (!string.IsNullOrEmpty(r.note)) sb.AppendLine(r.note);
        sb.Append("※ AI 定性估算，仅供实验前探索");
        resultText.text = sb.ToString();
    }

    void Awake()
    {
        BuildCanvas();
        if (followUser) canvasRt.SetParent(null); // 跟随模式：脱离宠物世界空间驱动
        else canvasRt.localPosition = panelOffset; // 固定模式：挂在宠物右侧
    }

    void BuildCanvas()
    {
        canvasRt = ChemUIWidgets.CreateCanvas(transform, "MoleculeTrayCanvas",
            new Vector2(CanvasW, CanvasH), WidthMeters, Vector3.zero);
        var root = canvasRt;

        var title = ChemUIWidgets.CreateText(root, "Title", 32, TextAnchor.MiddleCenter,
            new Color(0.75f, 0.95f, 1f, 1f));
        title.text = "分子列表";
        title.rectTransform.anchoredPosition = new Vector2(0f, 432f);
        title.rectTransform.sizeDelta = new Vector2(600f, 52f);

        countText = ChemUIWidgets.CreateText(root, "Count", 24, TextAnchor.MiddleCenter,
            new Color(0.7f, 0.95f, 1f, 0.9f));
        countText.text = "已上台 0 个分子（无上限）";
        countText.rectTransform.anchoredPosition = new Vector2(0f, 394f);
        countText.rectTransform.sizeDelta = new Vector2(600f, 30f);

        // ---- 滚动列表区：视口（RectMask2D 裁剪超出部分）+ 内容（动态行）----
        var vpGo = new GameObject("ListViewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
        vpGo.transform.SetParent(root, false);
        viewportRt = vpGo.GetComponent<RectTransform>();
        viewportRt.sizeDelta = new Vector2(590f, ViewportH);
        viewportRt.anchoredPosition = new Vector2(-28f, 172f);
        var vpBg = vpGo.GetComponent<Image>();
        vpBg.color = new Color(0.03f, 0.06f, 0.1f, 0.55f);
        vpBg.raycastTarget = false;

        var ctGo = new GameObject("ListContent", typeof(RectTransform));
        ctGo.transform.SetParent(viewportRt, false);
        contentRt = ctGo.GetComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.anchoredPosition = Vector2.zero;
        contentRt.sizeDelta = new Vector2(0f, 0f);

        // ▲ / ▼ 滚动按钮（列表右缘）
        var upBtn = ChemUIWidgets.CreateButton(root, "▲", 30, new Vector2(52f, 90f),
            new Color(0.16f, 0.3f, 0.45f), () => Scroll(-3));
        upBtn.RT.anchoredPosition = new Vector2(298f, 262f);
        var downBtn = ChemUIWidgets.CreateButton(root, "▼", 30, new Vector2(52f, 90f),
            new Color(0.16f, 0.3f, 0.45f), () => Scroll(3));
        downBtn.RT.anchoredPosition = new Vector2(298f, 82f);

        // 按钮组 2×2：添加/键盘 + 反应/清空
        var addBtn = ChemUIWidgets.CreateButton(root, "＋ 添加分子", 26, new Vector2(290f, 66f),
            new Color(0.16f, 0.34f, 0.55f), () => AddRequested?.Invoke());
        addBtn.RT.anchoredPosition = new Vector2(-162f, -72f);

        var kbBtn = ChemUIWidgets.CreateButton(root, "⌨ 键盘输入", 26, new Vector2(290f, 66f),
            new Color(0.30f, 0.22f, 0.48f), () => KeyboardInputRequested?.Invoke());
        kbBtn.RT.anchoredPosition = new Vector2(162f, -72f);

        reactBtn = ChemUIWidgets.CreateButton(root, "⚗ 开始反应", 26, new Vector2(290f, 66f),
            new Color(0.13f, 0.42f, 0.26f), () => ReactRequested?.Invoke());
        reactBtn.RT.anchoredPosition = new Vector2(-162f, -146f);

        var clearBtn = ChemUIWidgets.CreateButton(root, "🗑 清空列表", 26, new Vector2(290f, 66f),
            new Color(0.34f, 0.26f, 0.16f), Clear);
        clearBtn.RT.anchoredPosition = new Vector2(162f, -146f);

        // 反应结果区
        var resultTitle = ChemUIWidgets.CreateText(root, "ResultTitle", 22, TextAnchor.MiddleLeft,
            new Color(1f, 0.85f, 0.55f, 0.95f));
        resultTitle.text = "── 反应分析 ──";
        resultTitle.rectTransform.anchoredPosition = new Vector2(0f, -206f);
        resultTitle.rectTransform.sizeDelta = new Vector2(620f, 30f);

        resultText = ChemUIWidgets.CreateText(root, "Result", 22, TextAnchor.UpperLeft,
            new Color(1f, 0.95f, 0.8f, 0.96f));
        resultText.rectTransform.anchoredPosition = new Vector2(0f, -300f);
        resultText.rectTransform.sizeDelta = new Vector2(630f, 190f);

        hintText = ChemUIWidgets.CreateText(root, "Hint", 20, TextAnchor.UpperLeft,
            new Color(0.65f, 0.85f, 0.95f, 0.8f));
        hintText.text = "语音或键盘可无限添加分子（拖动列表 / ▲▼ 滚动）；\n集齐 2 种以上点「开始反应」";
        hintText.rectTransform.anchoredPosition = new Vector2(0f, -432f);
        hintText.rectTransform.sizeDelta = new Vector2(620f, 70f);

        RebuildRows();
    }

    // ---------------- 列表行 ----------------

    void RebuildRows()
    {
        // 清旧行（连同射线按钮注册）
        for (int i = contentRt.childCount - 1; i >= 0; i--)
        {
            var child = contentRt.GetChild(i);
            for (int k = ChemRayRegistry.Targets.Count - 1; k >= 0; k--)
                if (ChemRayRegistry.Targets[k] is ChemButton cb && cb.RT != null
                    && (cb.RT == child || cb.RT.IsChildOf(child)))
                    ChemRayRegistry.Unregister(cb);
            Destroy(child.gameObject);
        }

        for (int i = 0; i < molecules.Count; i++)
        {
            var idx = i;
            var m = molecules[i];
            var row = new GameObject($"Row{i}", typeof(RectTransform));
            row.transform.SetParent(contentRt, false);
            var rt = row.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(570f, RowH);
            rt.anchoredPosition = new Vector2(0f, -(RowH * 0.5f + i * (RowH + RowGap)));

            var label = ChemUIWidgets.CreateText(row.transform, "Label", 22, TextAnchor.MiddleLeft);
            ChemUIWidgets.Stretch(label.rectTransform, 8f, 3f, 60f, 3f);
            label.verticalOverflow = VerticalWrapMode.Truncate;
            label.text = $"{idx + 1}. {m.DisplayName()}  {m.formula}";

            var removeBtn = ChemUIWidgets.CreateButton(row.transform, "×", 28, new Vector2(48f, 48f),
                new Color(0.42f, 0.16f, 0.16f), () => RemoveAt(idx));
            removeBtn.RT.anchoredPosition = new Vector2(252f, 0f);
        }

        contentRt.sizeDelta = new Vector2(0f, Mathf.Max(0f, ContentHeight()));
        ClampScroll();
        contentRt.anchoredPosition = new Vector2(0f, scrollY);
        if (countText != null)
        {
            countText.text = molecules.Count > 0
                ? $"已上台 {molecules.Count} 个分子（无上限）"
                : "已上台 0 个分子（无上限）";
        }
        if (reactBtn != null) reactBtn.SetInteractable(molecules.Count >= 2);
    }

    float ContentHeight() => molecules.Count * RowH + Mathf.Max(0, molecules.Count - 1) * RowGap;

    float MaxScroll() => Mathf.Max(0f, ContentHeight() - ViewportH);

    void ClampScroll() => scrollY = Mathf.Clamp(scrollY, 0f, MaxScroll());

    void Scroll(int rows)
    {
        scrollY += rows * (RowH + RowGap);
        ClampScroll();
        contentRt.anchoredPosition = new Vector2(0f, scrollY);
    }

    void ScrollToBottom()
    {
        scrollY = MaxScroll();
        contentRt.anchoredPosition = new Vector2(0f, scrollY);
    }

    // ---------------- 手柄拖动滚动 ----------------

    void Update()
    {
        if (viewportRt == null) return;

        if (!listDragging)
        {
            foreach (var p in UIRayPointer.All)
            {
                if (p == null || !p.IsPressed) continue;
                var ray = new Ray(p.transform.position, p.transform.forward);
                if (RayToCanvasLocal(ray, out var local) && InsideViewport(local))
                {
                    listDragging = true;
                    dragPointer = p;
                    dragStartPos = p.transform.position;
                    dragStartScroll = scrollY;
                    break;
                }
            }
        }
        else if (dragPointer == null || !dragPointer.IsPressed)
        {
            listDragging = false;
            dragPointer = null;
        }
        else
        {
            // 手柄竖直位移 → 列表滚动（手向上抬 = 内容上移看后面的行）
            float dyWorld = dragPointer.transform.position.y - dragStartPos.y;
            float dyCanvas = dyWorld / (WidthMeters / CanvasW);
            scrollY = dragStartScroll + dyCanvas;
            ClampScroll();
            contentRt.anchoredPosition = new Vector2(0f, scrollY);
        }
    }

    bool InsideViewport(Vector2 local) =>
        Mathf.Abs(local.x - viewportRt.anchoredPosition.x) < viewportRt.sizeDelta.x * 0.5f
        && Mathf.Abs(local.y - viewportRt.anchoredPosition.y) < viewportRt.sizeDelta.y * 0.5f;

    /// <summary>射线 → 画布局部坐标（画布平面求交）</summary>
    bool RayToCanvasLocal(Ray ray, out Vector2 local)
    {
        local = default;
        if (canvasRt == null) return false;
        var plane = new Plane(-canvasRt.forward, canvasRt.position);
        if (!plane.Raycast(ray, out float d) || d < 0f) return false;
        var p = canvasRt.InverseTransformPoint(ray.origin + ray.direction * d);
        local = new Vector2(p.x, p.y);
        return true;
    }

    void LateUpdate()
    {
        if (followUser)
        {
            UIFollow.Drive(canvasRt, ref posSnapped, 1f, followDistance, forwardBias, lateralBias, followHeight);
        }
        else
        {
            var cam = Camera.main;
            if (cam != null && canvasRt != null)
            {
                Vector3 toCam = cam.transform.position - canvasRt.position;
                canvasRt.rotation = Quaternion.LookRotation(-toCam.normalized, Vector3.up);
            }
        }
    }
}
