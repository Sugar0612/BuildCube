using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>可被 UIRayPointer 射线点选的 UI 部件（自绘命中测试，不依赖 XRI 事件系统）</summary>
public interface IChemRayTarget
{
    RectTransform RT { get; }
    void OnRayHover(bool hover);
    void OnRayClick();
}

/// <summary>射线可点部件注册表：ChemButton 创建时注册，销毁时反注册</summary>
public static class ChemRayRegistry
{
    static readonly List<IChemRayTarget> targets = new List<IChemRayTarget>();

    public static IReadOnlyList<IChemRayTarget> Targets => targets;

    public static void Register(IChemRayTarget t)
    {
        if (t != null && !targets.Contains(t)) targets.Add(t);
    }

    public static void Unregister(IChemRayTarget t)
    {
        if (t != null) targets.Remove(t);
    }
}

/// <summary>射线点选按钮包装：管理 Image 高亮并提供点击回调（替代 UGUI Button，无需 EventSystem）</summary>
public class ChemButton : IChemRayTarget
{
    public RectTransform RT { get; }
    readonly Image img;
    readonly Color normalColor;
    readonly Color hoverColor;
    string labelText;

    public System.Action onClick;

    public ChemButton(RectTransform rt, Image img, System.Action onClick)
    {
        RT = rt;
        this.img = img;
        this.onClick = onClick;
        normalColor = img.color;
        hoverColor = Color.Lerp(normalColor, Color.white, 0.45f);
    }

    /// <summary>设置按钮文字（需在创建时用 ChemUIWidgets.LabelOf 获取 Text）</summary>
    public string Label
    {
        get => labelText;
        set
        {
            labelText = value;
            if (labelRef != null) labelRef.text = value;
        }
    }

    Text labelRef;

    /// <summary>按钮内文字（可用于调整截断/对齐等细节）</summary>
    public Text LabelText => labelRef;

    public ChemButton WithLabel(Text text)
    {
        labelRef = text;
        return this;
    }

    public void SetInteractable(bool on) => img.color = on ? normalColor : new Color(0.25f, 0.25f, 0.25f, 0.6f);

    public void OnRayHover(bool hover) => img.color = hover ? hoverColor : normalColor;

    public void OnRayClick() => onClick?.Invoke();
}
