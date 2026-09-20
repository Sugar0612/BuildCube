using UnityEngine;

/// <summary>
/// 化学方程式舞台（智能球上方，竖直面向用户）：
/// 左侧一组竖直"相框"为反应物槽，反应后右侧出现产物槽，中间是「=」徽章，框间是「+」徽章。
/// 渐进出现：一开始只有 1 个相框；分子填入后才出现加号和下一个相框（最多 4 个反应物）；
/// 触发反应后出现「=」，产物解析成功一个就出现一个产物相框。
/// 每个分子下方有名字标签（中文名 + 分子式）。
/// 交互：手柄射线悬停高亮，按住扳机/握把拖动可在水平面自由摆位（分子实时跟随）。
/// 朝向策略：舞台根节点永不旋转（它挂在智能球对象上）；每个相框/徽章各自原地绕自身
/// 竖轴转向用户 —— 分子不在相框子级，粒子保持自由（独立自转、不随 UI 旋转漂移）。
/// </summary>
public class EquationBench : MonoBehaviour
{
    public const int ReactantSlots = 4;  // 反应物槽（与 MoleculeTray.MaxSlots 一致）
    public const int ProductSlots = 4;   // 产物槽
    public const int TotalSlots = ReactantSlots + ProductSlots;

    [Header("舞台布局")]
    [Tooltip("舞台相对智能球中心的高度（米）：高于状态面板与名字标签，杜绝重叠")]
    public float benchHeight = 0.82f;
    [Tooltip("相邻插槽默认间距（米）")]
    public float slotSpacing = 0.56f;
    [Tooltip("相框尺寸（宽×高，米）")]
    public Vector2 frameSize = new Vector2(0.40f, 0.46f);
    [Tooltip("边框条粗细（米）")]
    public float barThickness = 0.012f;
    [Tooltip("「=」徽章两侧预留宽度（米）")]
    public float equalsGap = 0.66f;

    [Header("交互")]
    [Tooltip("射线拾取框的半径（米）")]
    public float dragRadius = 0.2f;
    [Tooltip("拖动范围：X（舞台局部坐标）")]
    public Vector2 dragRangeX = new Vector2(-1.7f, 1.7f);
    [Tooltip("拖动范围：Z（舞台局部坐标）")]
    public Vector2 dragRangeZ = new Vector2(-1.0f, 1.0f);
    [Tooltip("相框/徽章转向用户的平滑速度")]
    public float faceLerp = 5f;

    public Transform[] Anchors { get; private set; }

    Transform[] anchors;
    Material[] edgeMats;
    TextMesh[] nameLabels;
    bool[] filled, hovered;
    Transform[] plusMarks;   // 反应物间 3 个 + 产物间 3 个
    Transform equalMark;
    float time;

    // 舞台当前形态（变化时重新排布）
    int visibleReactants = 1; // 渐进：初始只有 1 个反应物相框
    int visibleProducts = 0;
    bool showEquals = false;
    bool layoutDirty = true;

    int dragIndex = -1;
    UIRayPointer dragSource;

    void Awake()
    {
        anchors = new Transform[TotalSlots];
        Anchors = anchors;
        edgeMats = new Material[TotalSlots];
        nameLabels = new TextMesh[TotalSlots];
        filled = new bool[TotalSlots];
        hovered = new bool[TotalSlots];

        for (int i = 0; i < TotalSlots; i++)
        {
            var go = new GameObject($"Slot{i}");
            go.transform.SetParent(transform, false);
            anchors[i] = go.transform;
            edgeMats[i] = BuildFrame(go.transform);
            nameLabels[i] = BuildNameLabel(go.transform);

            if (i >= visibleReactants) go.SetActive(false); // 渐进：先只显示第 1 个
        }

        plusMarks = new Transform[TotalSlots - 2]; // 反应物间 3 + 产物间 3
        for (int i = 0; i < plusMarks.Length; i++)
        {
            plusMarks[i] = BuildBadge(plus: true);
            plusMarks[i].gameObject.SetActive(false);
        }
        equalMark = BuildBadge(plus: false);
        equalMark.gameObject.SetActive(false);
    }

    // ---------------- 视觉构件 ----------------

    /// <summary>竖直相框：4 条边 + 4 角加强角标（游戏 HUD 风）。返回边框共享材质</summary>
    Material BuildFrame(Transform parent)
    {
        float w = frameSize.x, h = frameSize.y, t = barThickness;
        var edgeMat = MakeMat(DimColor());

        Bar(parent, "EdgeT", new Vector2(w, t), new Vector2(0f, h * 0.5f), edgeMat);
        Bar(parent, "EdgeB", new Vector2(w, t), new Vector2(0f, -h * 0.5f), edgeMat);
        Bar(parent, "EdgeL", new Vector2(t, h), new Vector2(-w * 0.5f, 0f), edgeMat);
        Bar(parent, "EdgeR", new Vector2(t, h), new Vector2(w * 0.5f, 0f), edgeMat);

        var cornerMat = MakeMat(new Color(0.55f, 0.95f, 1f, 0.92f));
        float cl = 0.075f, ct = 0.018f;
        for (int cx = -1; cx <= 1; cx += 2)
            for (int cy = -1; cy <= 1; cy += 2)
            {
                var corner = new GameObject($"Corner{cx}{cy}").transform;
                corner.SetParent(parent, false);
                corner.localPosition = new Vector3(cx * w * 0.5f, cy * h * 0.5f, -0.004f);
                Bar(corner, "H", new Vector2(cl, ct), new Vector2(-cx * cl * 0.5f, 0f), cornerMat);
                Bar(corner, "V", new Vector2(ct, cl), new Vector2(0f, -cy * cl * 0.5f), cornerMat);
            }

        return edgeMat;
    }

    /// <summary>分子名字标签（相框正下方，随框面向用户；字号加大保证可读）</summary>
    TextMesh BuildNameLabel(Transform parent)
    {
        var go = new GameObject("Name");
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(0f, -frameSize.y * 0.5f - 0.07f, -0.004f);
        var tm = go.AddComponent<TextMesh>();
        tm.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        tm.fontSize = 52;
        tm.characterSize = 0.009f; // ~4.7cm 高的字，MR 中距离可读
        tm.anchor = TextAnchor.UpperCenter;
        tm.alignment = TextAlignment.Center;
        tm.color = new Color(0.88f, 0.98f, 1f, 0.98f);
        tm.text = "";
        var mr = go.GetComponent<MeshRenderer>();
        if (tm.font != null) mr.sharedMaterial = tm.font.material;
        return tm;
    }

    /// <summary>游戏风格徽章：菱形底板 + 粗亮横条（+ 为十字，= 为双横）</summary>
    Transform BuildBadge(bool plus)
    {
        var badge = new GameObject(plus ? "PlusBadge" : "EqualsBadge").transform;
        badge.SetParent(transform, false);

        var back = Bar(badge, "Back", new Vector2(0.16f, 0.16f), Vector2.zero,
            MakeMat(new Color(0.05f, 0.16f, 0.38f, 0.6f)));
        back.localRotation = Quaternion.Euler(0f, 0f, 45f);
        back.localPosition += Vector3.forward * 0.006f;

        var barMat = MakeMat(plus
            ? new Color(1f, 0.78f, 0.28f, 0.96f)
            : new Color(0.45f, 1f, 0.75f, 0.96f));

        if (plus)
        {
            Bar(badge, "H", new Vector2(0.11f, 0.032f), Vector2.zero, barMat);
            Bar(badge, "V", new Vector2(0.032f, 0.11f), Vector2.zero, barMat);
        }
        else
        {
            Bar(badge, "H1", new Vector2(0.11f, 0.03f), new Vector2(0f, 0.026f), barMat);
            Bar(badge, "H2", new Vector2(0.11f, 0.03f), new Vector2(0f, -0.026f), barMat);
        }
        return badge;
    }

    static Transform Bar(Transform parent, string name, Vector2 size, Vector2 localXY, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(go.GetComponent<Collider>());
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = new Vector3(localXY.x, localXY.y, 0f);
        go.transform.localScale = new Vector3(size.x, size.y, 1f);
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go.transform;
    }

    static Material MakeMat(Color color)
    {
        var mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        if (mat.HasProperty("_Surface")) mat.SetFloat("_Surface", 1f);
        if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetInt("_ZWrite", 0);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.EnableKeyword("_BLENDMODE_ALPHA");
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.color = color;
        return mat;
    }

    // ---------------- 舞台形态（渐进出现） ----------------

    /// <summary>
    /// 设置舞台形态：可见反应物相框数（含待填的下一个）、可见产物相框数、是否显示「=」。
    /// 任一值变化即触发重新排布（相框回到默认居中布局）。
    /// </summary>
    public void SetStage(int reactantFrames, int productFrames, bool equals)
    {
        int nR = Mathf.Clamp(reactantFrames, 1, ReactantSlots);
        int nP = Mathf.Clamp(productFrames, 0, ProductSlots);
        if (nR != visibleReactants || nP != visibleProducts || equals != showEquals)
        {
            visibleReactants = nR;
            visibleProducts = nP;
            showEquals = equals;
            layoutDirty = true;
        }
    }

    /// <summary>同步各插槽占据状态与名字标签（数组长度 8：前 4 反应物 + 后 4 产物）</summary>
    public void SetFilled(Molecule[] bySlot)
    {
        for (int i = 0; i < filled.Length; i++)
        {
            var mol = bySlot != null && i < bySlot.Length ? bySlot[i] : null;
            filled[i] = mol != null;
            if (nameLabels[i] != null)
            {
                string label = mol == null ? "" : $"{mol.DisplayName()} {mol.formula}";
                if (mol != null && mol.source == MoleculeSource.GLM)
                    label += "≈"; // AI 估算标记
                nameLabels[i].text = label;
            }
        }
    }

    /// <summary>按当前形态重新排布全部相框与徽章（居中：反应物 [+] … [=] 产物）</summary>
    void Relayout()
    {
        float fw = frameSize.x;
        float wR = (visibleReactants - 1) * slotSpacing + fw;
        float wP = visibleProducts > 0 ? (visibleProducts - 1) * slotSpacing + fw : 0f;
        float eqW = showEquals ? equalsGap : 0f;
        float total = wR + eqW + wP;
        float x = -total * 0.5f + fw * 0.5f;

        for (int i = 0; i < ReactantSlots; i++)
        {
            bool vis = i < visibleReactants;
            anchors[i].gameObject.SetActive(vis);
            if (vis)
            {
                anchors[i].localPosition = new Vector3(x, benchHeight, 0f);
                x += slotSpacing;
            }
        }
        float rightEdgeR = -total * 0.5f + wR;
        float eqX = rightEdgeR + eqW * 0.5f;
        float xP = rightEdgeR + eqW + fw * 0.5f;
        for (int j = 0; j < ProductSlots; j++)
        {
            bool vis = j < visibleProducts;
            anchors[ReactantSlots + j].gameObject.SetActive(vis);
            if (vis)
            {
                anchors[ReactantSlots + j].localPosition = new Vector3(xP, benchHeight, 0f);
                xP += slotSpacing;
            }
        }

        // 徽章可见性：相邻可见框之间显示「+」（反应物区与产物区各自内部），「=」按需
        for (int i = 0; i < ReactantSlots - 1; i++)
            plusMarks[i].gameObject.SetActive(visibleReactants > 1 && i < visibleReactants - 1);
        for (int j = 0; j < ProductSlots - 1; j++)
            plusMarks[ReactantSlots - 1 + j].gameObject.SetActive(visibleProducts > 1 && j < visibleProducts - 1);
        equalMark.gameObject.SetActive(showEquals);
    }

    // ---------------- 每帧更新 ----------------

    void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        time += dt;
        if (layoutDirty)
        {
            Relayout();
            layoutDirty = false;
        }
        UpdateDrag();
        UpdateColors(dt);
    }

    void LateUpdate()
    {
        // 每个相框/徽章原地绕自身竖轴转向用户（仅旋转自身视觉子级，不动根节点、不牵连粒子）
        var cam = Camera.main;
        if (cam == null) return;
        float k = 1f - Mathf.Exp(-faceLerp * Time.deltaTime);
        foreach (var a in anchors) if (a.gameObject.activeSelf) FaceCamYaw(a, cam, k);
        foreach (var m in plusMarks) if (m != null && m.gameObject.activeSelf) FaceCamYaw(m, cam, k);
        if (equalMark != null && equalMark.gameObject.activeSelf) FaceCamYaw(equalMark, cam, k);

        UpdateMarkers();
    }

    /// <summary>绕自身竖轴原地转向用户（只改变朝向，不改变位置）</summary>
    void FaceCamYaw(Transform t, Camera cam, float k)
    {
        Vector3 dir = t.position - cam.transform.position;
        dir.y = 0f;
        if (dir.sqrMagnitude < 1e-4f) return;
        var target = Quaternion.LookRotation(dir.normalized, Vector3.up);
        t.rotation = Quaternion.Slerp(t.rotation, target, k);
    }

    /// <summary>徽章跟随相邻框中点 / 产物首框相对「=」的方向，带轻微脉动</summary>
    void UpdateMarkers()
    {
        int idx = 0;
        // 反应物间的「+」
        for (int i = 0; i < ReactantSlots - 1; i++)
        {
            var m = plusMarks[idx++];
            if (m == null || !m.gameObject.activeSelf) continue;
            var a = anchors[i].localPosition;
            var b = anchors[i + 1].localPosition;
            m.localPosition = new Vector3((a.x + b.x) * 0.5f, benchHeight, (a.z + b.z) * 0.5f - 0.02f);
            m.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(time * 2.2f + i * 1.1f));
        }
        // 产物间的「+」
        for (int j = 0; j < ProductSlots - 1; j++)
        {
            var m = plusMarks[idx++];
            if (m == null || !m.gameObject.activeSelf) continue;
            var a = anchors[ReactantSlots + j].localPosition;
            var b = anchors[ReactantSlots + j + 1].localPosition;
            m.localPosition = new Vector3((a.x + b.x) * 0.5f, benchHeight, (a.z + b.z) * 0.5f - 0.02f);
            m.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(time * 2.2f + (j + 1) * 1.1f));
        }
        // 「=」：位于反应物区与产物区之间（无产物时贴在最后一个反应物框右侧）
        if (equalMark != null && equalMark.gameObject.activeSelf)
        {
            float wR = (visibleReactants - 1) * slotSpacing + frameSize.x;
            float wP = visibleProducts > 0 ? (visibleProducts - 1) * slotSpacing + frameSize.x : 0f;
            float total = wR + equalsGap + wP;
            float eqX = -total * 0.5f + wR + equalsGap * 0.5f;
            equalMark.localPosition = new Vector3(eqX, benchHeight, -0.02f);
            equalMark.localScale = Vector3.one * (1f + 0.06f * Mathf.Sin(time * 2.2f + 2.2f));
        }
    }

    void UpdateDrag()
    {
        if (dragIndex < 0)
        {
            foreach (var p in UIRayPointer.All)
            {
                if (p == null || !p.IsPressed) continue;
                int hit = PickSlot(new Ray(p.transform.position, p.transform.forward));
                if (hit >= 0)
                {
                    dragIndex = hit;
                    dragSource = p;
                    break;
                }
            }
        }
        else if (dragSource == null || !dragSource.IsPressed)
        {
            dragIndex = -1;
            dragSource = null;
        }
        else
        {
            var ray = new Ray(dragSource.transform.position, dragSource.transform.forward);
            Vector3 center = anchors[dragIndex].position;
            if (Mathf.Abs(ray.direction.y) > 1e-4f)
            {
                float t = (center.y - ray.origin.y) / ray.direction.y;
                if (t > 0f)
                {
                    var worldPt = ray.origin + ray.direction * t;
                    var local = transform.InverseTransformPoint(worldPt);
                    local.x = Mathf.Clamp(local.x, dragRangeX.x, dragRangeX.y);
                    local.z = Mathf.Clamp(local.z, dragRangeZ.x, dragRangeZ.y);
                    local.y = benchHeight;
                    anchors[dragIndex].localPosition = local;
                }
            }
        }

        for (int i = 0; i < anchors.Length; i++)
        {
            hovered[i] = dragIndex == i;
            if (!hovered[i] && dragIndex < 0 && anchors[i].gameObject.activeSelf)
            {
                foreach (var p in UIRayPointer.All)
                {
                    if (p == null) continue;
                    if (PickSlot(new Ray(p.transform.position, p.transform.forward), i) >= 0)
                    {
                        hovered[i] = true;
                        break;
                    }
                }
            }
        }
    }

    int PickSlot(Ray ray, int only = -1)
    {
        int best = -1;
        float bestT = float.MaxValue;
        for (int i = 0; i < anchors.Length; i++)
        {
            if (only >= 0 && i != only) continue;
            if (!anchors[i].gameObject.activeSelf) continue;
            var d = anchors[i].position - ray.origin;
            float along = Vector3.Dot(d, ray.direction);
            if (along <= 0f) continue;
            var closest = ray.origin + ray.direction * along;
            if ((anchors[i].position - closest).sqrMagnitude <= dragRadius * dragRadius && along < bestT)
            {
                best = i;
                bestT = along;
            }
        }
        return best;
    }

    void UpdateColors(float dt)
    {
        float k = 1f - Mathf.Exp(-10f * dt);
        for (int i = 0; i < edgeMats.Length; i++)
        {
            if (edgeMats[i] == null) continue;
            var target = hovered[i] ? HoverColor() : (filled[i] ? FilledColor() : DimColor());
            edgeMats[i].color = Color.Lerp(edgeMats[i].color, target, k);
        }
    }

    Color DimColor() => new Color(0.28f, 0.62f, 1f, 0.42f);
    Color FilledColor() => new Color(0.36f, 0.95f, 1f, 0.85f);
    Color HoverColor() => new Color(0.8f, 1f, 1f, 0.95f);
}
