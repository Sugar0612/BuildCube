using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>化学物质候选（确认卡片按钮 / 反应台条目）</summary>
public class ChemCandidate
{
    public string nameZh = "";
    public string nameEn = "";
    public string formula = "";
    public int cid;             // PubChem CID（>0 表示已在 PubChem 命中）
    public string mw = "";
    public bool validated;      // 已在 PubChem 校验
    public float coefficient = 1f; // 反应方程式系数（产物用）

    public string DisplayName()
    {
        if (!string.IsNullOrEmpty(nameZh)) return nameZh;
        if (!string.IsNullOrEmpty(nameEn)) return nameEn;
        return string.IsNullOrEmpty(formula) ? "未知物质" : formula;
    }
}

/// <summary>GLM 反应分析结果</summary>
public class ReactionResult
{
    public bool feasible;
    public string equation = "";
    public string type = "";
    public string conditions = "";
    public string energy = "";
    public string note = "";
    public List<ChemCandidate> products = new List<ChemCandidate>();
}

/// <summary>
/// 化学链路编排：
/// - InterpretAsync：用户输入（语音文本/键盘输入）→ 候选物质列表（PubChem 直查快路径 + GLM 解析，逐个校验）；
/// - ResolveAsync：候选 → 分子结构（本地缓存 → PubChem 3D SDF → GLM 估算兜底）；
/// - ReactAsync：多反应物 → 配平方程式/产物/条件/定性可行性。
/// </summary>
public static class ChemistryLLM
{
    const string InterpretPrompt =
        "你是化学物质名称解析模块。用户输入可能来自语音识别（含错别字/同音字）或手动输入，" +
        "可能是：中文俗名/学名/音译名、英文名、分子式。\n" +
        "任务：判断用户最可能指哪些化学物质，按可能性从高到低给出最多3个候选。\n" +
        "只输出json对象：{\"candidates\":[{\"name_zh\":\"乙醇\",\"name_en\":\"ethanol\",\"formula\":\"C2H6O\"}]}\n" +
        "规则：\n" +
        "- formula 用 Hill 规则（含碳时 C 第一 H 第二，其余元素字母序；不含碳时全按字母序），如 C2H6O、CH4O、H2O。\n" +
        "- 语音输入含同音字/错别字时按读音推断最可能的化学物质（如“以纯”→乙醇）。\n" +
        "- 不确定也要给出最可能的候选；完全无法判断时输出 {\"candidates\":[]}。\n" +
        "- 禁止输出解释、前后缀或代码块标记。";

    const string EstimatePrompt =
        "你是分子三维结构估算模块。为指定化学物质估算球棍模型数据。\n" +
        "只输出json对象：{\"atoms\":[{\"e\":\"C\",\"x\":0.00,\"y\":0.00,\"z\":0.00}],\"bonds\":[{\"a\":1,\"b\":2,\"t\":1}]}\n" +
        "规则：\n" +
        "- 坐标单位为埃；按 VSEPR 构型给出合理 3D 坐标（键长=共价半径之和，键角按杂化类型：sp3≈109.5°、sp2≈120°、sp≈180°）。\n" +
        "- 所有氢原子必须列出。bonds 的 a、b 为 atoms 序号（从1开始），t 为键级 1/2/3。\n" +
        "- 禁止输出解释或代码块标记。";

    const string ReactPrompt =
        "你是化学反应分析模块。给定若干反应物，判断它们之间最可能发生的反应并做定性可行性评估。\n" +
        "只输出json对象：\n" +
        "{\"feasible\":true,\"equation\":\"2H2+O2=2H2O\",\"type\":\"化合反应\",\"conditions\":\"点燃\",\"energy\":\"放热\"," +
        "\"note\":\"定性说明（可行性/副反应/注意事项）\",\"products\":[{\"name_zh\":\"水\",\"name_en\":\"water\",\"formula\":\"H2O\",\"coefficient\":2}]}\n" +
        "规则：\n" +
        "- equation 用最简整数系数配平，分子式写法与输入的 formula 一致。\n" +
        "- feasible=false 时 equation 留空字符串，note 说明原因。\n" +
        "- products 只列主要产物（最多4个），coefficient 为对应系数。\n" +
        "- 若需要催化剂/特定条件才能进行，写在 conditions；常温常压无法进行的要明说。\n" +
        "- 禁止输出解释或代码块标记。";

    // ---------------- 输入解析 → 候选列表 ----------------

    /// <summary>
    /// 解析用户输入为候选物质列表（最多 3 个，尽量带 PubChem 校验信息）。
    /// 快路径：输入本身像英文名/分子式时直接查 PubChem，免去 GLM 等待；
    /// 中文/语音文本走 GLM 解析后再逐个校验。
    /// </summary>
    public static async Task<List<ChemCandidate>> InterpretAsync(string apiKey, string model, string input,
                                                                 float sttConfidence = 0f,
                                                                 Action<LLMClient.ThinkProgress> onProgress = null)
    {
        var result = new List<ChemCandidate>();
        var s = (input ?? "").Trim();
        if (s.Length == 0) return result;

        // 快路径：纯 ASCII 输入（手输英文名/分子式的常见场景）
        List<ChemCandidate> fast = null;
        if (IsAscii(s)) fast = await FastCandidates(s);

        if (fast == null || fast.Count == 0)
        {
            var userText = "用户输入：" + s;
            if (sttConfidence > 0f && sttConfidence < 0.6f)
                userText += "\n（语音识别置信度较低，注意同音字/错别字，酌情多列候选）";

            string reply = null;
            try
            {
                reply = await LLMClient.AskRawAsync(apiKey, InterpretPrompt, userText, model, onProgress, 4000, 0.1);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Chem] GLM 解析异常: {e.Message}");
            }
            foreach (var c in ParseCandidates(reply))
                if (result.Count < 3) result.Add(c);
        }

        if (fast != null) result.InsertRange(0, fast); // PubChem 快路径候选优先（已校验）

        // 去重（按 CID 或 分子式+英文名）
        var uniq = new List<ChemCandidate>();
        foreach (var c in result)
        {
            bool dup = false;
            foreach (var u in uniq)
                if (SameCandidate(c, u)) { dup = true; break; }
            if (!dup) uniq.Add(c);
        }
        if (uniq.Count > 3) uniq.RemoveRange(3, uniq.Count - 3);

        // 逐个 PubChem 校验（补 CID/分子式/分子量，供按钮展示与后续结构获取）
        var tasks = new Task[uniq.Count];
        for (int i = 0; i < uniq.Count; i++) tasks[i] = ValidateAsync(uniq[i]);
        try { await Task.WhenAll(tasks); }
        catch (Exception) { /* ValidateAsync 内部已兜底 */ }

        return uniq;
    }

    static async Task<List<ChemCandidate>> FastCandidates(string s)
    {
        var list = new List<ChemCandidate>();
        try
        {
            if (LooksLikeFormula(s))
            {
                var hits = await PubChemClient.FastFormula(s, 3);
                foreach (var h in hits)
                    list.Add(new ChemCandidate { nameEn = h.title, formula = h.formula, mw = h.mw, cid = h.cid, validated = true });
            }
            if (list.Count == 0)
            {
                var hit = await PubChemClient.LookupName(s);
                if (hit != null)
                    list.Add(new ChemCandidate { nameEn = string.IsNullOrEmpty(hit.title) ? s : hit.title, formula = hit.formula, mw = hit.mw, cid = hit.cid, validated = true });
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Chem] PubChem 快路径失败: {e.Message}");
        }
        return list;
    }

    /// <summary>为候选补齐 PubChem 校验信息（CID/分子量/规范分子式）</summary>
    static async Task ValidateAsync(ChemCandidate c)
    {
        if (c == null || c.validated || c.cid > 0) { if (c != null) c.validated = true; return; }
        try
        {
            if (!string.IsNullOrWhiteSpace(c.nameEn))
            {
                var hit = await PubChemClient.LookupName(c.nameEn);
                if (hit != null)
                {
                    c.cid = hit.cid;
                    c.mw = hit.mw;
                    if (!string.IsNullOrEmpty(hit.formula)) c.formula = hit.formula;
                    if (string.IsNullOrEmpty(c.nameEn)) c.nameEn = hit.title;
                    c.validated = true;
                    return;
                }
            }
            if (!string.IsNullOrWhiteSpace(c.formula))
            {
                var hits = await PubChemClient.FastFormula(c.formula, 1);
                if (hits.Count > 0)
                {
                    c.cid = hits[0].cid; // 按分子式匹配：取第一个候选（用户确认卡片可再消歧）
                    c.mw = hits[0].mw;
                    c.validated = true;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Chem] 校验失败({c.DisplayName()}): {e.Message}");
        }
    }

    static List<ChemCandidate> ParseCandidates(string reply)
    {
        var list = new List<ChemCandidate>();
        var root = TryParseJson(reply);
        var arr = root?["candidates"] as JSONArray;
        if (arr == null) return list;
        foreach (JSONNode n in arr)
        {
            var c = new ChemCandidate
            {
                nameZh = n["name_zh"]?.Value ?? "",
                nameEn = n["name_en"]?.Value ?? "",
                formula = n["formula"]?.Value ?? "",
            };
            if (c.nameZh.Length > 0 || c.nameEn.Length > 0 || c.formula.Length > 0)
                list.Add(c);
        }
        return list;
    }

    // ---------------- 候选 → 分子结构（缓存 → PubChem → GLM） ----------------

    /// <summary>
    /// 三级解析：本地缓存 → PubChem（3D SDF，缺失降 2D）→ GLM 估算兜底。
    /// 返回 null 表示全部失败。status：可选的过程文本回调（主线程）。
    /// </summary>
    public static async Task<Molecule> ResolveAsync(ChemCandidate c, string apiKey, string model,
                                                    Action<string> status = null)
    {
        if (c == null) return null;

        // 1) 本地缓存：分子式 / 英文名 / 中文名 任一命中即秒回
        var cached = MoleculeCache.GetAny(c.formula, c.nameEn, c.nameZh);
        if (cached != null)
        {
            status?.Invoke($"已从本地缓存加载：{cached.DisplayName()}");
            return cached;
        }

        // 2) PubChem：候选未经校验时先补查 CID
        if (c.cid <= 0) await ValidateAsync(c);
        if (c.cid > 0)
        {
            status?.Invoke("正在下载 PubChem 3D 结构…");
            var sdf = await PubChemClient.FetchSdf(c.cid);
            var mol = Molecule.ParseSdf(sdf);
            if (mol != null && mol.HasValidGeometry())
            {
                mol.nameZh = c.nameZh;
                mol.nameEn = string.IsNullOrEmpty(c.nameEn) ? c.DisplayName() : c.nameEn;
                mol.formula = string.IsNullOrEmpty(c.formula) ? mol.formula : c.formula;
                mol.mw = c.mw;
                mol.cid = c.cid;
                mol.source = MoleculeSource.PubChem;
                MoleculeCache.Put(mol);
                status?.Invoke($"PubChem 校验成功：{mol.DisplayName()}（{mol.atoms.Count} 原子 {mol.bonds.Count} 键）");
                return mol;
            }
            if (mol != null)
                Debug.LogWarning($"[Chem] PubChem 返回退化几何({c.DisplayName()})，转 GLM 估算");
        }

        // 3) GLM 估算兜底（结构未经验证，展示时必须标注）
        status?.Invoke("数据库未收录或下载失败，GLM 估算结构中…");
        return await EstimateStructureAsync(apiKey, model, c);
    }

    /// <summary>GLM 估算分子 3D 结构（兜底路径，source=GLM）</summary>
    public static async Task<Molecule> EstimateStructureAsync(string apiKey, string model, ChemCandidate c,
                                                              Action<LLMClient.ThinkProgress> onProgress = null)
    {
        if (c == null || string.IsNullOrEmpty(c.formula)) return null;
        string reply = null;
        try
        {
            reply = await LLMClient.AskRawAsync(apiKey, EstimatePrompt,
                $"物质：{c.nameEn}（{c.nameZh}）  分子式：{c.formula}", model, onProgress, 6000, 0.2);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Chem] GLM 结构估算异常: {e.Message}");
            return null;
        }

        var root = TryParseJson(reply);
        var ja = root?["atoms"] as JSONArray;
        if (ja == null || ja.Count == 0) return null;

        var mol = new Molecule
        {
            nameZh = c.nameZh,
            nameEn = c.nameEn,
            formula = c.formula,
            mw = c.mw,
            cid = 0,
            source = MoleculeSource.GLM,
        };
        foreach (JSONNode a in ja)
            mol.atoms.Add(new ChemAtom(a["e"]?.Value ?? "C", a["x"]?.AsFloat ?? 0f, a["y"]?.AsFloat ?? 0f, a["z"]?.AsFloat ?? 0f));

        var jb = root["bonds"] as JSONArray;
        int na = mol.atoms.Count;
        if (jb != null)
            foreach (JSONNode b in jb)
            {
                int a1 = b["a"]?.AsInt ?? 0, b1 = b["b"]?.AsInt ?? 0;
                if (a1 < 1 || b1 < 1 || a1 > na || b1 > na || a1 == b1) continue;
                int t = b["t"]?.AsInt ?? 1;
                if (t < 1 || t > 3) t = 1;
                mol.bonds.Add(new ChemBond(a1, b1, t));
            }

        // 几何校验：GLM 可能给出所有原子挤在同一点的退化坐标，直接判失败
        if (!mol.HasValidGeometry())
        {
            Debug.LogWarning($"[Chem] GLM 估算坐标退化({c.DisplayName()})，放弃");
            return null;
        }

        MoleculeCache.Put(mol);
        return mol;
    }

    // ---------------- 反应分析 ----------------

    /// <summary>多反应物 → GLM 反应分析（方程式/产物/条件/定性可行性）</summary>
    public static async Task<ReactionResult> ReactAsync(string apiKey, string model,
                                                        IReadOnlyList<ChemCandidate> reactants,
                                                        Action<LLMClient.ThinkProgress> onProgress = null)
    {
        if (reactants == null || reactants.Count < 2) return null;

        var sb = new StringBuilder("反应物：\n");
        for (int i = 0; i < reactants.Count; i++)
        {
            var r = reactants[i];
            sb.Append(i + 1).Append(") ").Append(r.DisplayName())
              .Append(" / ").Append(string.IsNullOrEmpty(r.nameEn) ? "-" : r.nameEn)
              .Append(" / ").Append(r.formula).Append('\n');
        }

        string reply = null;
        try
        {
            reply = await LLMClient.AskRawAsync(apiKey, ReactPrompt, sb.ToString(), model, onProgress, 6000, 0.1);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[Chem] GLM 反应分析异常: {e.Message}");
            return null;
        }

        var root = TryParseJson(reply);
        if (root == null) return null;

        var result = new ReactionResult
        {
            feasible = root["feasible"]?.AsBool ?? false,
            equation = root["equation"]?.Value ?? "",
            type = root["type"]?.Value ?? "",
            conditions = root["conditions"]?.Value ?? "",
            energy = root["energy"]?.Value ?? "",
            note = root["note"]?.Value ?? "",
        };
        var ja = root["products"] as JSONArray;
        if (ja != null)
            foreach (JSONNode n in ja)
            {
                var c = new ChemCandidate
                {
                    nameZh = n["name_zh"]?.Value ?? "",
                    nameEn = n["name_en"]?.Value ?? "",
                    formula = n["formula"]?.Value ?? "",
                    coefficient = n["coefficient"]?.AsFloat ?? 1f,
                };
                if (c.formula.Length > 0 || c.nameZh.Length > 0 || c.nameEn.Length > 0)
                    result.products.Add(c);
            }
        return result;
    }

    // ---------------- 工具 ----------------

    /// <summary>输入是否像分子式（仅字母数字与常见化学符号，且含大写字母或数字）</summary>
    public static bool LooksLikeFormula(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return Regex.IsMatch(s, @"^[A-Za-z0-9\(\)\[\]\+\-·]+$") && Regex.IsMatch(s, @"\d|[A-Z]");
    }

    static bool IsAscii(string s)
    {
        foreach (char c in s)
            if (c > 127) return false;
        return true;
    }

    static bool SameCandidate(ChemCandidate a, ChemCandidate b)
    {
        if (a.cid > 0 && b.cid > 0) return a.cid == b.cid;
        return string.Equals(a.formula, b.formula, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.nameEn, b.nameEn, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>容错 JSON 解析：剥代码块围栏，截取首个 { 到最后一个 }</summary>
    static JSONNode TryParseJson(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;
        var s = reply.Replace("```json", "").Replace("```", "").Trim();
        try { return JSONNode.Parse(s); }
        catch (Exception)
        {
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try { return JSONNode.Parse(s.Substring(a, b - a + 1)); }
            catch (Exception) { return null; }
        }
    }
}
