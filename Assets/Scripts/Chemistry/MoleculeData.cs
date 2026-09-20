using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

/// <summary>分子结构数据来源</summary>
public enum MoleculeSource
{
    PubChem, // PubChem 数据库（已校验）
    GLM,     // GLM 估算（未经数据库校验，展示时需标注）
}

/// <summary>原子：元素符号 + 3D 坐标（单位：埃）</summary>
[Serializable]
public class ChemAtom
{
    public string element;
    public Vector3 pos;

    public ChemAtom() { }
    public ChemAtom(string element, float x, float y, float z)
    {
        this.element = element;
        pos = new Vector3(x, y, z);
    }
}

/// <summary>化学键：两端原子序号（1 起）+ 键级（1 单键 / 2 双键 / 3 三键，芳香键按单键渲染）</summary>
[Serializable]
public class ChemBond
{
    public int a;
    public int b;
    public int order;

    public ChemBond() { }
    public ChemBond(int a, int b, int order) { this.a = a; this.b = b; this.order = order; }
}

/// <summary>
/// 分子：名称/分子式/分子量 + 原子表与键表（球棍模型数据）。
/// 可从 PubChem 的 SDF（V2000 Molfile）解析，也可由 GLM 估算生成；
/// 支持与 SimpleJSON 互转（本地缓存持久化）。
/// </summary>
[Serializable]
public class Molecule
{
    public string nameZh = "";
    public string nameEn = "";
    public string formula = "";
    public string mw = "";     // 分子量（字符串，保留数据库原始精度）
    public int cid;            // PubChem CID（GLM 估算为 0）
    public MoleculeSource source = MoleculeSource.PubChem;
    public bool fromCache;     // 本次运行时是否命中本地缓存（不参与缓存序列化判断）

    public List<ChemAtom> atoms = new List<ChemAtom>();
    public List<ChemBond> bonds = new List<ChemBond>();

    public string DisplayName()
    {
        if (!string.IsNullOrEmpty(nameZh)) return nameZh;
        if (!string.IsNullOrEmpty(nameEn)) return nameEn;
        return string.IsNullOrEmpty(formula) ? "未知物质" : formula;
    }

    /// <summary>原子坐标包围盒最大边（埃）；无原子返回 0</summary>
    public float GeometryExtent()
    {
        if (atoms == null || atoms.Count == 0) return 0f;
        var min = Vector3.positiveInfinity;
        var max = Vector3.negativeInfinity;
        foreach (var a in atoms)
        {
            min = Vector3.Min(min, a.pos);
            max = Vector3.Max(max, a.pos);
        }
        return Mathf.Max(max.x - min.x, Mathf.Max(max.y - min.y, max.z - min.z));
    }

    /// <summary>
    /// 结构几何是否有效（非退化）。GLM 估算可能返回所有原子挤在同一点的坐标，
    /// 渲染后会缩成一团几乎不可见的小点却照样报"构建完成"——这类数据必须在
    /// 入缓存/渲染前拦下。
    /// </summary>
    public bool HasValidGeometry() => GeometryExtent() >= 0.05f;

    /// <summary>元素统计（如 "C 2 H 6 O 1"），调试用</summary>
    public string Composition()
    {
        var count = new SortedDictionary<string, int>();
        foreach (var a in atoms)
        {
            if (count.ContainsKey(a.element)) count[a.element]++;
            else count[a.element] = 1;
        }
        var sb = new StringBuilder();
        foreach (var kv in count) sb.Append(kv.Key).Append(kv.Value).Append(' ');
        return sb.ToString().TrimEnd();
    }

    // ---------------- SDF（V2000 Molfile）解析 ----------------

    /// <summary>
    /// 解析 MDL Molfile V2000（PubChem SDF 的首个记录）。
    /// 固定列：原子行 x[0,10) y[10,20) z[20,30) 元素符号[31,34)；键行 a[0,3) b[3,6) 键级[6,9)。
    /// 不支持 V3000（PubChem 小分子默认输出 V2000）。
    /// </summary>
    public static Molecule ParseSdf(string sdf)
    {
        if (string.IsNullOrWhiteSpace(sdf)) return null;
        var lines = sdf.Replace("\r\n", "\n").Split('\n');

        int counts = -1;
        for (int i = 0; i < Math.Min(lines.Length, 8); i++)
            if (lines[i].Contains("V2000")) { counts = i; break; }
        if (counts < 0) return null;

        int na = SdfInt(lines[counts], 0, 3);
        int nb = SdfInt(lines[counts], 3, 3);
        if (na <= 0 || na > 300 || nb < 0) return null;

        var mol = new Molecule();
        int atomBase = counts + 1;

        for (int i = 0; i < na; i++)
        {
            int li = atomBase + i;
            if (li >= lines.Length) return null;
            var l = lines[li];
            if (l.Length >= 34)
            {
                var atom = new ChemAtom(
                    l.Substring(31, 3).Trim(),
                    SdfFloat(l, 0, 10), SdfFloat(l, 10, 10), SdfFloat(l, 20, 10));
                if (string.IsNullOrEmpty(atom.element)) return null;
                mol.atoms.Add(atom);
            }
            else
            {
                // 行异常时回退按空白分词
                var t = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 4) return null;
                mol.atoms.Add(new ChemAtom(t[3], SdfFloat(t[0], 0, t[0].Length),
                    SdfFloat(t[1], 0, t[1].Length), SdfFloat(t[2], 0, t[2].Length)));
            }
        }

        for (int i = 0; i < nb; i++)
        {
            int li = atomBase + na + i;
            if (li >= lines.Length) break;
            var l = lines[li];
            int a, b, t;
            if (l.Length >= 9)
            {
                a = SdfInt(l, 0, 3); b = SdfInt(l, 3, 3); t = SdfInt(l, 6, 3);
            }
            else
            {
                var tk = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (tk.Length < 3) break;
                a = SdfInt(tk[0], 0, tk[0].Length); b = SdfInt(tk[1], 0, tk[1].Length); t = SdfInt(tk[2], 0, tk[2].Length);
            }
            if (a < 1 || b < 1 || a > na || b > na || a == b) continue;
            if (t < 1 || t > 3) t = 1; // 芳香键(4)/配位键等按单键渲染
            mol.bonds.Add(new ChemBond(a, b, t));
        }

        return mol.atoms.Count > 0 ? mol : null;
    }

    static int SdfInt(string l, int off, int len) =>
        int.TryParse(Field(l, off, len), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    static float SdfFloat(string l, int off, int len) =>
        float.TryParse(Field(l, off, len), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    static string Field(string l, int off, int len) =>
        off < l.Length ? l.Substring(off, Math.Min(len, l.Length - off)).Trim() : "";

    // ---------------- 缓存序列化（SimpleJSON） ----------------

    public JSONNode ToJson()
    {
        var n = new JSONObject();
        n["nameZh"] = nameZh;
        n["nameEn"] = nameEn;
        n["formula"] = formula;
        n["mw"] = mw;
        n["cid"] = cid;
        n["source"] = source.ToString();
        n["savedAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
        var ja = new JSONArray();
        foreach (var a in atoms)
        {
            var o = new JSONObject();
            o["e"] = a.element;
            o["x"] = a.pos.x; o["y"] = a.pos.y; o["z"] = a.pos.z;
            ja.Add(o);
        }
        n["atoms"] = ja;
        var jb = new JSONArray();
        foreach (var b in bonds)
        {
            var o = new JSONObject();
            o["a"] = b.a; o["b"] = b.b; o["t"] = b.order;
            jb.Add(o);
        }
        n["bonds"] = jb;
        return n;
    }

    public static Molecule FromJson(JSONNode n)
    {
        if (n == null) return null;
        try
        {
            var m = new Molecule
            {
                nameZh = n["nameZh"]?.Value ?? "",
                nameEn = n["nameEn"]?.Value ?? "",
                formula = n["formula"]?.Value ?? "",
                mw = n["mw"]?.Value ?? "",
                cid = n["cid"]?.AsInt ?? 0,
            };
            if (Enum.TryParse(n["source"]?.Value, out MoleculeSource src)) m.source = src;
            var ja = n["atoms"] as JSONArray;
            if (ja != null)
                foreach (JSONNode a in ja)
                    m.atoms.Add(new ChemAtom(a["e"]?.Value ?? "", a["x"]?.AsFloat ?? 0f, a["y"]?.AsFloat ?? 0f, a["z"]?.AsFloat ?? 0f));
            var jb = n["bonds"] as JSONArray;
            if (jb != null)
                foreach (JSONNode b in jb)
                    m.bonds.Add(new ChemBond(b["a"]?.AsInt ?? 0, b["b"]?.AsInt ?? 0, b["t"]?.AsInt ?? 1));
            return m.atoms.Count > 0 ? m : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>元素显示属性表：CPK 配色 + 共价半径（埃）</summary>
public static class ChemTable
{
    /// <summary>CPK 配色（Jmol 惯例，C 用深灰保证在暗背景可见）</summary>
    public static Color CpkColor(string element)
    {
        switch ((element ?? "").Trim().ToUpperInvariant())
        {
            case "H": return new Color(0.96f, 0.96f, 0.96f);
            case "C": return new Color(0.24f, 0.24f, 0.27f);
            case "N": return new Color(0.18f, 0.32f, 0.90f);
            case "O": return new Color(0.94f, 0.16f, 0.16f);
            case "F": return new Color(0.55f, 0.89f, 0.55f);
            case "CL": return new Color(0.15f, 0.85f, 0.25f);
            case "BR": return new Color(0.62f, 0.18f, 0.12f);
            case "I": return new Color(0.55f, 0.10f, 0.55f);
            case "S": return new Color(0.95f, 0.80f, 0.18f);
            case "P": return new Color(0.98f, 0.52f, 0.15f);
            case "SI": return new Color(0.90f, 0.76f, 0.58f);
            case "B": return new Color(1.00f, 0.72f, 0.55f);
            case "NA": return new Color(0.67f, 0.10f, 0.08f);
            case "K": return new Color(0.56f, 0.28f, 0.05f);
            case "MG": return new Color(0.55f, 0.25f, 0.25f);
            case "CA": return new Color(0.28f, 0.62f, 0.58f);
            case "FE": return new Color(0.88f, 0.45f, 0.22f);
            case "CU": return new Color(0.85f, 0.55f, 0.22f);
            case "ZN": return new Color(0.49f, 0.50f, 0.69f);
            case "HE": case "NE": case "AR": return new Color(0.70f, 0.89f, 0.96f);
            default: return new Color(0.88f, 0.45f, 0.76f); // 未知元素：粉紫
        }
    }

    /// <summary>共价半径（埃），未知元素按 0.8 兜底</summary>
    public static float CovalentRadius(string element)
    {
        switch ((element ?? "").Trim().ToUpperInvariant())
        {
            case "H": return 0.31f;
            case "C": return 0.76f;
            case "N": return 0.71f;
            case "O": return 0.66f;
            case "F": return 0.57f;
            case "B": return 0.84f;
            case "SI": return 1.11f;
            case "P": return 1.07f;
            case "S": return 1.05f;
            case "CL": return 1.02f;
            case "BR": return 1.20f;
            case "I": return 1.39f;
            case "NA": return 1.66f;
            case "K": return 2.03f;
            case "MG": return 1.41f;
            case "CA": return 1.76f;
            case "FE": return 1.32f;
            case "CU": return 1.32f;
            case "ZN": return 1.22f;
            default: return 0.80f;
        }
    }
}
