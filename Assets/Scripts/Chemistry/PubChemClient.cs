using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>PubChem 化合物属性（一次查询结果）</summary>
public class PubChemHit
{
    public int cid;
    public string formula;
    public string mw;
    public string title;
}

/// <summary>
/// PubChem PUG REST 客户端（免费、无需 Key）：
/// - LookupName：英文名 → 化合物属性（中文名不解析，需先经 GLM 规范化为英文名）；
/// - FastFormula：分子式 → 候选列表（用于同分异构体消歧，如 C2H6O → 乙醇/二甲醚）；
/// - FetchSdf：CID → 3D 构象 SDF（缺失时降级 2D）。
/// </summary>
public static class PubChemClient
{
    const string Base = "https://pubchem.ncbi.nlm.nih.gov/rest/pug/compound";
    const int TimeoutSec = 20;

    /// <summary>按英文名精确查询，未命中（404）返回 null</summary>
    public static async Task<PubChemHit> LookupName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var url = $"{Base}/name/{Uri.EscapeDataString(name.Trim())}/property/MolecularFormula,MolecularWeight,Title/JSON";
        var root = await GetJson(url);
        var props = root?["PropertyTable"]?["Properties"] as JSONArray;
        if (props == null || props.Count == 0) return null;
        var p = props[0];
        return new PubChemHit
        {
            cid = p["CID"]?.AsInt ?? 0,
            formula = p["MolecularFormula"]?.Value ?? "",
            mw = p["MolecularWeight"]?.Value ?? "",
            title = p["Title"]?.Value ?? "",
        };
    }

    /// <summary>分子式模糊查询，返回前 max 个候选（过滤同位素标记与带电物种）</summary>
    public static async Task<List<PubChemHit>> FastFormula(string formula, int max = 3)
    {
        var result = new List<PubChemHit>();
        if (string.IsNullOrWhiteSpace(formula)) return result;
        var url = $"{Base}/fastformula/{Uri.EscapeDataString(formula.Trim())}/property/MolecularFormula,MolecularWeight,Title/JSON";
        var root = await GetJson(url);
        var props = root?["PropertyTable"]?["Properties"] as JSONArray;
        if (props == null) return result;

        string want = formula.Trim();
        foreach (JSONNode p in props)
        {
            if (result.Count >= max) break;
            var f = p["MolecularFormula"]?.Value ?? "";
            if (f.Contains("+") || f.Contains("-")) continue; // 带电物种
            if (!string.Equals(f, want, StringComparison.OrdinalIgnoreCase)) continue; // 同位素标记排除
            result.Add(new PubChemHit
            {
                cid = p["CID"]?.AsInt ?? 0,
                formula = f,
                mw = p["MolecularWeight"]?.Value ?? "",
                title = p["Title"]?.Value ?? "",
            });
        }
        return result;
    }

    /// <summary>按 CID 拉取 SDF：优先 3D 构象，缺失时降级 2D 平面结构（球棍模型仍可渲染）</summary>
    public static async Task<string> FetchSdf(int cid)
    {
        if (cid <= 0) return null;
        var sdf = await GetText($"{Base}/cid/{cid}/SDF?record_type=3d");
        if (string.IsNullOrWhiteSpace(sdf))
            sdf = await GetText($"{Base}/cid/{cid}/SDF?record_type=2d");
        return string.IsNullOrWhiteSpace(sdf) ? null : sdf;
    }

    static async Task<JSONNode> GetJson(string url)
    {
        var text = await GetText(url);
        if (string.IsNullOrEmpty(text)) return null;
        try { return JSONNode.Parse(text); }
        catch (Exception) { return null; }
    }

    static async Task<string> GetText(string url)
    {
        try
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = TimeoutSec;
                await req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success) return null;
                return req.downloadHandler.text;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[PubChem] 请求失败: {url} → {e.Message}");
            return null;
        }
    }
}
