using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 分子列表面板（跟随用户右手斜前方）：
/// 槽位渐进出现——一开始只有 1 行，分子填入后才出现下一行（最多 4 个反应物）。
/// 按钮组：＋添加分子 / ⌨键盘输入 / ⚗开始反应（≥2 个反应物可用）/ 清空展示台。
/// 底部展示反应结果（配平方程式 / 类型 / 条件 / 可行性说明 + 免责声明）。
/// 手柄射线操作。
/// </summary>
public class MoleculeTray : MonoBehaviour
{
    public const int MaxSlots = 4;

    public event System.Action AddRequested;
    /// <summary>请求键盘输入（右侧面板的「键盘输入」按钮）</summary>
    public event System.Action KeyboardInputRequested;
    /// <summary>请求开始反应（≥2 个反应物时可用）</summary>
    public event System.Action ReactRequested;
    /// <summary>台上有分子被移除或清空后触发（3D 场景需要同步重建/清场）</summary>
    public event System.Action TrayChanged;

    [Header("面板位置")]
    [Tooltip("是否跟随用户（默认关：面板固定在智能球右侧，稳定可预期）")]
    public bool followUser = false;
    [Tooltip("固定模式：面板相对宠物中心的偏移（智能球右侧、避开上方的方程式舞台）")]
    public Vector3 panelOffset = new Vector3(1.08f, 0f, 0f);
    [Tooltip("跟随模式：面板与用户的距离（米）")]
    public float followDistance = 1.05f;
    [Tooltip("跟随方向的向前权重（越大越靠正前方，越容易看全）")]
    public float forwardBias = 0.9f;
    [Tooltip("跟随方向的侧向权重")]
    public float lateralBias = 0.45f;
    [Tooltip("跟随模式：相对视线的高度偏移（米）")]
    public float followHeight = -0.1f;

    class Slot
    {
        public Molecule mol;
        public Text label;
        public ChemButton removeBtn;
        public GameObject row; // label+按钮共同的显示开关（渐进行）
    }

    readonly List<Slot> slots = new List<Slot>();
    RectTransform canvasRt;
    Text hintText;
    Text resultText;
    ChemButton reactBtn;
    bool posSnapped;
    int visibleRows = 1; // 渐进：初始只显示 1 行

    const float CanvasW = 680f, CanvasH = 940f;
    const float WidthMeters = 0.6f;

    void Awake()
    {
        BuildCanvas();
        if (followUser) canvasRt.SetParent(null); // 跟随模式：脱离宠物世界空间驱动
        else canvasRt.localPosition = panelOffset; // 固定模式：挂在宠物右侧
    }

    public int Count
    {
        get
        {
            int n = 0;
            foreach (var s in slots) if (s.mol != null) n++;
            return n;
        }
    }

    public bool IsFull => Count >= MaxSlots;

    /// <summary>当前已上台的分子（按槽位顺序，跳过空位）</summary>
    public List<Molecule> Molecules()
    {
        var list = new List<Molecule>();
        foreach (var s in slots) if (s.mol != null) list.Add(s.mol);
        return list;
    }

    /// <summary>按槽位索引返回分子数组（空位为 null；索引与方程式舞台反应物插槽一致）</summary>
    public Molecule[] MoleculesBySlot()
    {
        var arr = new Molecule[slots.Count];
        for (int i = 0; i < slots.Count; i++)
            arr[i] = slots[i].mol;
        return arr;
    }

    /// <summary>当前反应物转为候选列表（GLM 反应分析请求用）</summary>
    public List<ChemCandidate> Candidates()
    {
        var list = new List<ChemCandidate>();
        foreach (var s in slots)
            if (s.mol != null)
                list.Add(new ChemCandidate
                {
                    nameZh = s.mol.nameZh,
                    nameEn = s.mol.nameEn,
                    formula = s.mol.formula,
                    cid = s.mol.cid,
                    mw = s.mol.mw,
                    validated = true,
                });
        return list;
    }

    /// <summary>加入分子到第一个空位；满员返回 false（触发 TrayChanged 同步全息台）</summary>
    public bool TryAdd(Molecule mol)
    {
        if (mol == null) return false;
        foreach (var s in slots)
        {
            if (s.mol != null) continue;
            s.mol = mol;
            RefreshRows();
            TrayChanged?.Invoke();
            return true;
        }
        return false;
    }

    public void Clear()
    {
        bool had = Count > 0;
        foreach (var s in slots) s.mol = null;
        if (resultText != null) resultText.text = "";
        RefreshRows();
        if (had) TrayChanged?.Invoke();
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
        sb.Append("※ AI 定性估算，仅供实验前探索").AppendLine();
        resultText.text = sb.ToString().TrimEnd();
    }

    void BuildCanvas()
    {
        canvasRt = ChemUIWidgets.CreateCanvas(transform, "MoleculeTrayCanvas",
            new Vector2(CanvasW, CanvasH), WidthMeters, Vector3.zero);
        var root = canvasRt;

        var title = ChemUIWidgets.CreateText(root, "Title", 32, TextAnchor.MiddleCenter,
            new Color(0.75f, 0.95f, 1f, 1f));
        title.text = "分子展示台";
        title.rectTransform.anchoredPosition = new Vector2(0f, 430f);
        title.rectTransform.sizeDelta = new Vector2(600f, 52f);

        // 4 个反应物槽位行（渐进显示：行 GameObject 由 visibleRows 控制）
        for (int i = 0; i < MaxSlots; i++)
        {
            var idx = i;
            var row = new GameObject($"Row{i}", typeof(RectTransform));
            row.transform.SetParent(root, false);
            var rrt = row.GetComponent<RectTransform>();
            rrt.sizeDelta = new Vector2(640f, 56f);
            rrt.anchoredPosition = new Vector2(0f, 364f - i * 62f);

            var slot = new Slot { row = row };
            slot.label = ChemUIWidgets.CreateText(row.transform, "Label", 26, TextAnchor.MiddleLeft);
            ChemUIWidgets.Stretch(slot.label.rectTransform, 6f, 3f, 64f, 3f);
            slot.removeBtn = ChemUIWidgets.CreateButton(row.transform, "×", 30, new Vector2(52f, 48f),
                new Color(0.42f, 0.16f, 0.16f), () => RemoveAt(idx));
            slot.removeBtn.RT.anchoredPosition = new Vector2(294f, 0f);
            slots.Add(slot);
        }

        // 按钮组 2×2：添加/键盘 + 反应/清空
        var addBtn = ChemUIWidgets.CreateButton(root, "＋ 添加分子", 26, new Vector2(290f, 72f),
            new Color(0.16f, 0.34f, 0.55f), () => AddRequested?.Invoke());
        addBtn.RT.anchoredPosition = new Vector2(-162f, 66f);

        var kbBtn = ChemUIWidgets.CreateButton(root, "⌨ 键盘输入", 26, new Vector2(290f, 72f),
            new Color(0.30f, 0.22f, 0.48f), () => KeyboardInputRequested?.Invoke());
        kbBtn.RT.anchoredPosition = new Vector2(162f, 66f);

        reactBtn = ChemUIWidgets.CreateButton(root, "⚗ 开始反应", 26, new Vector2(290f, 72f),
            new Color(0.13f, 0.42f, 0.26f), () => ReactRequested?.Invoke());
        reactBtn.RT.anchoredPosition = new Vector2(-162f, -18f);

        var clearBtn = ChemUIWidgets.CreateButton(root, "🗑 清空展示台", 26, new Vector2(290f, 72f),
            new Color(0.34f, 0.26f, 0.16f), Clear);
        clearBtn.RT.anchoredPosition = new Vector2(162f, -18f);

        // 反应结果区
        var resultTitle = ChemUIWidgets.CreateText(root, "ResultTitle", 24, TextAnchor.MiddleLeft,
            new Color(1f, 0.85f, 0.55f, 0.95f));
        resultTitle.text = "── 反应分析 ──";
        resultTitle.rectTransform.anchoredPosition = new Vector2(0f, -96f);
        resultTitle.rectTransform.sizeDelta = new Vector2(620f, 34f);

        resultText = ChemUIWidgets.CreateText(root, "Result", 23, TextAnchor.UpperLeft,
            new Color(1f, 0.95f, 0.8f, 0.96f));
        resultText.rectTransform.anchoredPosition = new Vector2(0f, -250f);
        resultText.rectTransform.sizeDelta = new Vector2(630f, 290f);

        hintText = ChemUIWidgets.CreateText(root, "Hint", 20, TextAnchor.UpperLeft,
            new Color(0.65f, 0.85f, 0.95f, 0.8f));
        hintText.text = "说「构建 某分子」或点「键盘输入」上台；\n集齐 2 种以上反应物后点「开始反应」";
        hintText.rectTransform.anchoredPosition = new Vector2(0f, -428f);
        hintText.rectTransform.sizeDelta = new Vector2(620f, 76f);

        RefreshRows();
    }

    void RemoveAt(int i)
    {
        if (i < 0 || i >= slots.Count) return;
        if (slots[i].mol == null) return;
        slots[i].mol = null;
        RefreshRows();
        TrayChanged?.Invoke();
    }

    void RefreshRows()
    {
        // 渐进行：显示 已填数量+1（待填下一行），上限 4；分子被移除则收缩
        int want = Mathf.Clamp(Count + 1, 1, MaxSlots);
        visibleRows = want;
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            s.row.SetActive(i < visibleRows);
            s.label.text = s.mol == null
                ? $"{i + 1}.（待放入）"
                : $"{i + 1}. {s.mol.DisplayName()}  {s.mol.formula}";
            s.removeBtn.SetInteractable(s.mol != null);
        }
        if (reactBtn != null) reactBtn.SetInteractable(Count >= 2);
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
