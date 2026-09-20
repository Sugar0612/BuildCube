using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 分子 → 蓝图：把 Molecule（原子表+键表）转换成粒子渲染管线可消费的 ShapePart 列表。
/// 原子 = CPK 配色球（共价半径），化学键 = 圆柱（双键两根平行圆柱、三键三根），
/// 整体居中并归一化到适合 MR 观看的尺寸，随后可直接交给 ParticlePet.BuildBlueprint。
/// </summary>
public static class MoleculeBlueprint
{
    /// <summary>单分子归一化半边长（米）</summary>
    public const float FitHalf = 0.28f;

    /// <summary>键圆柱半径（米，归一化模式）</summary>
    public const float BondRadius = 0.016f;

    /// <summary>分子 → 球棍模型部件列表（整体归一化到 fitHalf；offset 用于多分子排布）</summary>
    public static List<ShapePart> ToShapeParts(Molecule mol, bool hydrogens = true, Vector3 offset = default,
                                               float fitHalf = FitHalf)
    {
        return BuildCore(mol, ScaleK(mol, fitHalf), BondRadius, 0.9f, hydrogens, offset);
    }

    /// <summary>
    /// 统一比例版本（全息展示台用）：1 埃 = metersPerAngstrom 米，所有分子共用同一比例，
    /// 不逐个归一化 —— 保证水分子和大分子之间的真实大小比例，专注呈现结构。
    /// atomRadiusFactor 缩小原子球让化学键可见（小球棍风格）。
    /// </summary>
    public static List<ShapePart> ToShapePartsScaled(Molecule mol, float metersPerAngstrom,
                                                     bool hydrogens = true, Vector3 offset = default,
                                                     float bondRadius = 0.005f, float atomRadiusFactor = 0.6f)
    {
        return BuildCore(mol, Mathf.Max(1e-4f, metersPerAngstrom), bondRadius, atomRadiusFactor, hydrogens, offset);
    }

    /// <summary>包围盒最大边（埃）→ 该分子的显示缩放系数（归一化模式）</summary>
    static float ScaleK(Molecule mol, float fitHalf)
    {
        float ext = mol != null ? mol.GeometryExtent() : 0f;
        return ext > 1e-3f ? fitHalf * 2f / ext : 1f;
    }

    static List<ShapePart> BuildCore(Molecule mol, float k, float bondRadius, float atomRadiusFactor,
                                     bool hydrogens, Vector3 offset)
    {
        var parts = new List<ShapePart>();
        if (mol?.atoms == null || mol.atoms.Count == 0) return parts;

        // 包围盒中心（始终含氢计算，保证开关氢原子时分子不跳动）
        var min = Vector3.positiveInfinity;
        var max = Vector3.negativeInfinity;
        foreach (var a in mol.atoms)
        {
            min = Vector3.Min(min, a.pos);
            max = Vector3.Max(max, a.pos);
        }
        var center = (min + max) * 0.5f;

        int n = mol.atoms.Count;
        var pos = new Vector3[n];
        var visible = new bool[n];
        for (int i = 0; i < n; i++)
        {
            pos[i] = (mol.atoms[i].pos - center) * k + offset;
            visible[i] = hydrogens || !IsHydrogen(mol.atoms[i].element);
        }

        // 原子球
        for (int i = 0; i < n; i++)
        {
            if (!visible[i]) continue;
            var a = mol.atoms[i];
            float r = Mathf.Max(0.006f, ChemTable.CovalentRadius(a.element) * k * atomRadiusFactor);
            parts.Add(new ShapePart(PetShape.Sphere, pos[i], Vector3.zero,
                new Vector3(r, r, r), ChemTable.CpkColor(a.element)));
        }

        // 化学键圆柱（贯穿原子中心，端部藏进球内）
        foreach (var b in mol.bonds)
        {
            if (b.a < 1 || b.b < 1 || b.a > n || b.b > n) continue;
            int ia = b.a - 1, ib = b.b - 1;
            if (!visible[ia] || !visible[ib]) continue;

            var p1 = pos[ia];
            var p2 = pos[ib];
            var d = p2 - p1;
            float len = d.magnitude;
            if (len < 1e-4f) continue;
            var dir = d / len;
            var mid = (p1 + p2) * 0.5f;
            var rot = Quaternion.FromToRotation(Vector3.up, dir).eulerAngles;

            // 多重键平行圆柱的偏移方向：与键轴垂直
            Vector3 side = Mathf.Abs(Vector3.Dot(dir, Vector3.up)) < 0.9f
                ? Vector3.Cross(dir, Vector3.up)
                : Vector3.Cross(dir, Vector3.right);
            side.Normalize();

            if (b.order == 2)
            {
                float off = bondRadius * 2.1f;
                AddBond(parts, mid + side * off, rot, len, bondRadius);
                AddBond(parts, mid - side * off, rot, len, bondRadius);
            }
            else if (b.order == 3)
            {
                float off = bondRadius * 2.4f;
                AddBond(parts, mid, rot, len, bondRadius);
                AddBond(parts, mid + side * off, rot, len, bondRadius);
                AddBond(parts, mid - side * off, rot, len, bondRadius);
            }
            else
            {
                AddBond(parts, mid, rot, len, bondRadius);
            }
        }

        return parts;
    }

    static void AddBond(List<ShapePart> parts, Vector3 mid, Vector3 rot, float len, float radius)
    {
        parts.Add(new ShapePart(PetShape.Cylinder, mid, rot,
            new Vector3(radius, len * 0.5f, radius),
            new Color(0.75f, 0.78f, 0.82f)));
    }

    /// <summary>每部件表面积（粒子分配权重：原子按半径、键按母线，保证小氢原子不被过度分配）</summary>
    public static float[] AreaWeights(List<ShapePart> parts)
    {
        var w = new float[parts.Count];
        for (int i = 0; i < parts.Count; i++)
        {
            var s = parts[i].scale;
            w[i] = parts[i].shape == PetShape.Cylinder
                ? 4f * Mathf.PI * s.x * s.y + 2f * Mathf.PI * s.x * s.x
                : 4f * Mathf.PI * s.x * s.x;
        }
        return w;
    }

    /// <summary>多分子排布：沿 X 轴一行居中排开（供反应物/产物并排展示）</summary>
    public static Vector3[] LayoutOffsets(int count, float fitHalf = FitHalf)
    {
        var arr = new Vector3[Mathf.Max(1, count)];
        float spacing = fitHalf * 2f * 1.3f;
        for (int i = 0; i < arr.Length; i++)
            arr[i] = new Vector3((i - (arr.Length - 1) * 0.5f) * spacing, 0f, 0f);
        return arr;
    }

    static bool IsHydrogen(string element) =>
        string.Equals((element ?? "").Trim(), "H", StringComparison.OrdinalIgnoreCase);
}
