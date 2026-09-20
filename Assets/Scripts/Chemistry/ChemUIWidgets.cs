using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 化学 UI 通用部件：运行时创建的圆角按钮/文本（无外部资源依赖，风格与 PetStatusUI 一致）。
/// 所有按钮自动注册到 ChemRayRegistry 供手柄射线点选。
/// </summary>
public static class ChemUIWidgets
{
    static Sprite rounded;

    /// <summary>程序化圆角矩形（9-slice），所有面板/按钮共用</summary>
    public static Sprite RoundedSprite()
    {
        if (rounded != null) return rounded;
        const int S = 64, R = 14;
        var tex = new Texture2D(S, S, TextureFormat.RGBA32, false);
        for (int y = 0; y < S; y++)
            for (int x = 0; x < S; x++)
            {
                float cx = Mathf.Min(x, S - 1 - x);
                float cy = Mathf.Min(y, S - 1 - y);
                bool inside;
                if (cx >= R || cy >= R) inside = true;
                else
                {
                    float dx = R - cx, dy = R - cy;
                    inside = dx * dx + dy * dy <= R * R;
                }
                tex.SetPixel(x, y, inside ? Color.white : Color.clear);
            }
        tex.Apply();
        rounded = Sprite.Create(tex, new Rect(0, 0, S, S), new Vector2(0.5f, 0.5f), 100f, 0,
            SpriteMeshType.FullRect, new Vector4(R, R, R, R));
        rounded.name = "ChemRounded";
        return rounded;
    }

    public static Text CreateText(Transform parent, string name, int size, TextAnchor anchor, Color? color = null)
    {
        var go = new GameObject(name, typeof(Text));
        go.transform.SetParent(parent, false);
        var t = go.GetComponent<Text>();
        t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.fontSize = size;
        t.color = color ?? new Color(1f, 1f, 1f, 0.94f);
        t.alignment = anchor;
        t.supportRichText = false;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    /// <summary>把 Text 铺满父矩形（留边距）</summary>
    public static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>创建圆角按钮（Image + 文字），注册射线点选；调用方负责设 anchoredPosition/size</summary>
    public static ChemButton CreateButton(Transform parent, string label, int fontSize, Vector2 size,
                                          Color background, System.Action onClick)
    {
        var go = new GameObject("Btn_" + label, typeof(Image));
        go.transform.SetParent(parent, false);
        var img = go.GetComponent<Image>();
        img.sprite = RoundedSprite();
        img.type = Image.Type.Sliced;
        img.color = background;

        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = size;

        var text = CreateText(go.transform, "Label", fontSize, TextAnchor.MiddleCenter);
        Stretch(text.rectTransform, 10f, 6f, 10f, 6f);
        text.text = label;

        var btn = new ChemButton(rt, img, onClick).WithLabel(text);
        ChemRayRegistry.Register(btn);
        return btn;
    }

    /// <summary>世界空间画布骨架（半透明圆角底板），返回画布 RectTransform</summary>
    public static RectTransform CreateCanvas(Transform parent, string name, Vector2 sizeUnits, float widthMeters, Vector3 localPosition)
    {
        var go = new GameObject(name, typeof(Canvas));
        go.transform.SetParent(parent, false);
        var canvas = go.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = sizeUnits;
        rt.localScale = Vector3.one * (widthMeters / sizeUnits.x);
        rt.localPosition = localPosition;

        var bg = new GameObject("BG", typeof(Image));
        bg.transform.SetParent(go.transform, false);
        var img = bg.GetComponent<Image>();
        img.sprite = RoundedSprite();
        img.type = Image.Type.Sliced;
        img.color = new Color(0.05f, 0.08f, 0.13f, 0.9f);
        Stretch(bg.GetComponent<RectTransform>(), 0f, 0f, 0f, 0f);

        return rt;
    }
}
