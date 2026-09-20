using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// 分子本地缓存：首次解析成功的分子（结构数据）持久化到 persistentDataPath，
/// 下次同名/同分子式查询直接命中，秒回且不再消耗网络与 GLM 配额。
/// 目录结构：molecule_cache/index.json（别名→文件映射）+ mol_XXXX.json（分子数据）。
/// </summary>
public static class MoleculeCache
{
    const string DirName = "molecule_cache";

    static Dictionary<string, string> index; // 别名（小写去空格）→ 缓存文件名

    static string Dir => Path.Combine(Application.persistentDataPath, DirName);
    static string IndexPath => Path.Combine(Dir, "index.json");

    /// <summary>别名归一化：忽略大小写与空格（中英文/分子式通用）</summary>
    public static string NormalizeKey(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>按任一别名（中文名/英文名/分子式）查缓存，未命中返回 null。
    /// 命中但几何退化（历史坏数据）时按未命中处理。</summary>
    public static Molecule Get(string alias)
    {
        EnsureLoaded();
        if (index == null || string.IsNullOrWhiteSpace(alias)) return null;
        if (!index.TryGetValue(NormalizeKey(alias), out var file)) return null;
        try
        {
            var json = File.ReadAllText(Path.Combine(Dir, file));
            var mol = Molecule.FromJson(JSONNode.Parse(json));
            if (mol == null) return null;
            if (!mol.HasValidGeometry())
            {
                Debug.LogWarning($"[ChemCache] 缓存中的 {mol.DisplayName()} 几何退化，忽略并重新获取");
                return null;
            }
            mol.fromCache = true;
            return mol;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ChemCache] 读取缓存失败({alias}): {e.Message}");
            return null;
        }
    }

    /// <summary>依次尝试多个别名查缓存（分子式/英文名/中文名）</summary>
    public static Molecule GetAny(params string[] aliases)
    {
        foreach (var a in aliases)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            var m = Get(a);
            if (m != null) return m;
        }
        return null;
    }

    /// <summary>写入缓存：自动以 分子式/英文名/中文名 + 额外别名 建立索引（退化结构拒绝入缓存）</summary>
    public static void Put(Molecule mol, params string[] extraAliases)
    {
        if (mol?.atoms == null || mol.atoms.Count == 0 || !mol.HasValidGeometry())
        {
            Debug.LogWarning($"[ChemCache] 拒绝缓存退化结构: {mol?.DisplayName()}");
            return;
        }
        EnsureLoaded();
        try
        {
            Directory.CreateDirectory(Dir);

            // 文件名由内容哈希决定：同一分子重复写入只更新文件，不产生冗余
            var json = mol.ToJson().ToString();
            var file = "mol_" + FnvHash(mol.formula + "|" + mol.nameEn + "|" + mol.cid) + ".json";
            File.WriteAllText(Path.Combine(Dir, file), json);

            IndexAlias(mol.formula, file);
            IndexAlias(mol.nameEn, file);
            IndexAlias(mol.nameZh, file);
            foreach (var a in extraAliases) IndexAlias(a, file);

            File.WriteAllText(IndexPath, ToIndexJson().ToString());
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ChemCache] 写入缓存失败({mol.DisplayName()}): {e.Message}");
        }
    }

    static void IndexAlias(string alias, string file)
    {
        var key = NormalizeKey(alias);
        if (key.Length == 0) return;
        index[key] = file;
    }

    static void EnsureLoaded()
    {
        if (index != null) return;
        index = new Dictionary<string, string>();
        try
        {
            var path = IndexPath;
            if (File.Exists(path))
            {
                var n = JSONNode.Parse(File.ReadAllText(path));
                var aliases = n["aliases"] as JSONObject;
                if (aliases != null)
                    foreach (var kv in aliases)
                        index[kv.Key] = kv.Value.Value;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ChemCache] 索引读取失败（将重建）: {e.Message}");
            index = new Dictionary<string, string>();
        }
    }

    static JSONNode ToIndexJson()
    {
        var aliases = new JSONObject();
        foreach (var kv in index)
            aliases[kv.Key] = kv.Value;
        var root = new JSONObject();
        root["version"] = 1;
        root["aliases"] = aliases;
        return root;
    }

    static string FnvHash(string s)
    {
        unchecked
        {
            uint h = 2166136261u;
            foreach (char c in s)
            {
                h ^= c;
                h *= 16777619u;
            }
            return h.ToString("x8");
        }
    }
}
