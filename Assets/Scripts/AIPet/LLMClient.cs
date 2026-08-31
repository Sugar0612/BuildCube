using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
// 注：SimpleJSON 的类型（JSONNode/JSONObject/JSONArray）位于全局命名空间，无需 using

/// <summary>
/// 智谱 GLM 对话 API 客户端（OpenAI 兼容接口）。
/// 把"构建托卡马克"这类开放指令交给 GLM，返回由基础几何体组成的蓝图 JSON，
/// 本地粒子按蓝图组装成复杂模型。
/// </summary>
public static class LLMClient
{
    public const string Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";

    /// <summary>请求超时（秒）。思考型模型（glm-5.3 系列等）思维链+蓝图生成实测约 120s+，须给足余量。</summary>
    public const int TimeoutSeconds = 240;

    public const string SystemPrompt =
        "你是MR粒子构建助手的3D形状规划模块。用户说“构建XXX”，你要把XXX拆解成基础几何体组成的3D模型。\n" +
        "只输出json对象：{\"parts\":[...]}，禁止任何解释、前后缀、代码块标记。parts 数组元素格式：\n" +
        "{\"shape\":\"sphere|cube|cylinder|cone|torus|pyramid\",\"pos\":[x,y,z],\"rot\":[x,y,z],\"scale\":[x,y,z],\"color\":[r,g,b]}\n" +
        "重要——如果用户说的物体你不知道是什么、或是虚构/不存在的名词，直接输出 {\"parts\":[]}，不要编造。\n" +
        "坐标与尺寸规则：\n" +
        "- 坐标系：Y轴向上。原点在模型几何中心。整体必须容纳在X∈[-0.35,0.35]、Y∈[-0.35,0.35]、Z∈[-0.35,0.35]的立方体内。\n" +
        "- scale 含义：sphere=半径（只看x，y z忽略）；cube=半边长（x y z可不同做长方体）；cylinder=沿自身Y轴的柱体，[半径,半高,半径]；cone=尖朝自身+Y，[底半径,半高,底半径]；torus=默认平躺在XZ面（环轴为Y），[大半径,管半径,忽略]；pyramid=尖朝自身+Y，[半底边,半高,半底边]。\n" +
        "- rot 是欧拉角（度），用于把部件从默认朝向转到需要的朝向。\n" +
        "- color 为0~1浮点RGB，配色贴近真实物体。\n" +
        "结构规则（重要）：\n" +
        "1. 先想清楚目标物体的真实结构，再规划部件：主体轮廓用什么大部件，特征细节用什么小部件。先用文字想清楚整体比例（高:宽:深），再按比例落坐标。\n" +
        "2. 高塔/建筑类：底部宽、顶部窄，由下至上逐层缩小。\n" +
        "3. 环形装置类：主体用torus，附加装置围绕主环均匀分布。\n" +
        "4. 长条/管道类：用细长cylinder或cube旋转到目标方向。\n" +
        "5. 部件数量：简单物体4~8个，复杂结构可用8~24个。对称结构必须给足对称部件（左右各一、或环形均匀分布6~12个）。\n" +
        "6. 部件之间要相连成整体，不要悬浮。\n";
        // "示例1——托卡马克装置（真空室+环形分布的磁场线圈+顶部底部供电柱）：\n" +
        // "[{\"shape\":\"torus\",\"pos\":[0,0,0],\"rot\":[0,0,0],\"scale\":[0.16,0.045,0.045],\"color\":[0.9,0.3,0.1]},{\"shape\":\"torus\",\"pos\":[0,0,0],\"rot\":[0,0,0],\"scale\":[0.22,0.012,0.012],\"color\":[0.35,0.4,0.5]},{\"shape\":\"cylinder\",\"pos\":[0,0.3,0],\"rot\":[0,0,0],\"scale\":[0.04,0.06,0.04],\"color\":[0.4,0.5,0.6]},{\"shape\":\"cylinder\",\"pos\":[0,-0.3,0],\"rot\":[0,0,0],\"scale\":[0.04,0.06,0.04],\"color\":[0.4,0.5,0.6]},{\"shape\":\"cylinder\",\"pos\":[0.16,0,0],\"rot\":[0,180,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[-0.16,0,0],\"rot\":[0,0,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0,0,0.16],\"rot\":[0,90,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0,0,-0.16],\"rot\":[0,-90,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0.11,0,0.11],\"rot\":[0,135,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[-0.11,0,0.11],\"rot\":[0,45,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0.11,0,-0.11],\"rot\":[0,-135,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[-0.11,0,-0.11],\"rot\":[0,-45,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]}]\n" +
        // "示例2——埃菲尔铁塔（4条斜柱+两层平台+塔尖天线）：\n" +
        // "[{\"shape\":\"cylinder\",\"pos\":[0.12,-0.2,0.12],\"rot\":[8,45,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.12,-0.2,0.12],\"rot\":[-8,-45,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[0.12,-0.2,-0.12],\"rot\":[-8,135,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.12,-0.2,-0.12],\"rot\":[8,-135,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cube\",\"pos\":[0,-0.12,0],\"rot\":[0,0,0],\"scale\":[0.16,0.008,0.16],\"color\":[0.6,0.5,0.4]},{\"shape\":\"cylinder\",\"pos\":[0.05,0,0.05],\"rot\":[5,45,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.05,0,0.05],\"rot\":[-5,-45,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[0.05,0,-0.05],\"rot\":[-5,135,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.05,0,-0.05],\"rot\":[5,-135,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cube\",\"pos\":[0,0.08,0],\"rot\":[0,0,0],\"scale\":[0.07,0.006,0.07],\"color\":[0.6,0.5,0.4]},{\"shape\":\"cone\",\"pos\":[0,0.16,0],\"rot\":[0,0,0],\"scale\":[0.04,0.12,0.04],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[0,0.3,0],\"rot\":[0,0,0],\"scale\":[0.005,0.05,0.005],\"color\":[0.8,0.8,0.85]}]\n" +
        // "示例3——直线中子加速器（长真空管道+聚焦磁铁环+靶室）：\n" +
        // "[{\"shape\":\"cylinder\",\"pos\":[0,0,0],\"rot\":[0,0,90],\"scale\":[0.04,0.3,0.04],\"color\":[0.8,0.8,0.85]},{\"shape\":\"torus\",\"pos\":[-0.2,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"torus\",\"pos\":[-0.07,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"torus\",\"pos\":[0.06,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"torus\",\"pos\":[0.19,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"sphere\",\"pos\":[0.32,0,0],\"rot\":[0,0,0],\"scale\":[0.07,0.07,0.07],\"color\":[0.9,0.75,0.1]},{\"shape\":\"cylinder\",\"pos\":[-0.33,0,0],\"rot\":[0,0,90],\"scale\":[0.025,0.04,0.025],\"color\":[0.5,0.5,0.55]}]";

    /// <summary>
    /// 发送指令文本，返回模型回复内容。失败时返回 null 并输出错误。
    /// </summary>
    public static async Task<string> AskAsync(string apiKey, string userText, string model, int maxTokens = 8000)
    {
        var body = new JSONObject();
        body["model"] = model;
        body["temperature"] = 0.0;
        body["max_tokens"] = maxTokens;
        body["stream"] = false;

        // 关闭深度思考：跳过思维链直接输出答案，大幅缩短响应时间。
        // 注意：glm-5.3 系列（含 flash）不支持 disabled——参数被忽略，思维链照常输出
        // 且与答案共享 max_tokens 预算，因此 5.3 系列必须给足 token（8000+）。
        bool supportsThinkingDisabled = model.IndexOf("5.3", StringComparison.Ordinal) < 0;
        if (supportsThinkingDisabled)
        {
            var thinking = new JSONObject();
            thinking["type"] = "disabled";
            body["thinking"] = thinking;
        }

        // JSON 模式：强制模型输出合法 JSON（提示词中已含"json"字样）
        var fmt = new JSONObject();
        fmt["type"] = "json_object";
        body["response_format"] = fmt;

        var messages = new JSONArray();
        var sys = new JSONObject();
        sys["role"] = "system";
        sys["content"] = SystemPrompt;
        messages.Add(sys);
        var usr = new JSONObject();
        usr["role"] = "user";
        usr["content"] = "构建" + userText;
        messages.Add(usr);
        body["messages"] = messages;

        // 分级降级重试：
        // 1. 初始配置（glm-5.2：thinking disabled + response_format，最快路径；
        //    glm-5.3 系列：无 thinking + response_format + 大 max_tokens）
        // 2. 去 response_format（个别模型不支持 JSON 模式）
        // 3. 去 thinking（仅非 5.3 模型：关思考失败时宁可慢也要拿到结果）
        string reply = await SendAsync(apiKey, body.ToString());
        if (reply == null)
        {
            body.Remove("response_format");
            reply = await SendAsync(apiKey, body.ToString());
        }
        if (reply == null)
        {
            body.Remove("thinking");
            reply = await SendAsync(apiKey, body.ToString());
        }
        return reply;
    }

    static async Task<string> SendAsync(string apiKey, string jsonBody)
    {
        using (var req = new UnityWebRequest(Endpoint, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + apiKey);
            req.timeout = TimeoutSeconds;

            await req.SendWebRequest();

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[GLM] 请求失败: {req.error} {req.downloadHandler?.text}");
                return null;
            }

            var json = JSONNode.Parse(req.downloadHandler.text);
            var content = json?["choices"]?[0]?["message"]?["content"];
            return content?.Value?.Trim();
        }
    }

    // ---------------- 蓝图解析 ----------------

    const int MaxParts = 32;          // 防御：部件数上限
    const float PosClamp = 0.8f;      // 防御：位置范围（米，归一化前先放宽）
    const float ScaleClamp = 0.6f;    // 防御：单部件尺寸上限（米，归一化前先放宽）
    const float FitHalf = 0.35f;      // 归一化目标：整体半边长（米）

    /// <summary>
    /// 解析 AI 回复中的蓝图。支持 {"parts":[...]} 包装（JSON 模式）和裸数组两种格式。
    /// 容错：剥离代码块围栏、截取中括号、参数越界钳制。
    /// 返回：null=解析失败；空列表=AI 表示不认识该物体；非空=可构建的部件列表。
    /// </summary>
    public static List<ShapePart> ParseBlueprint(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply)) return null;

        // 剥离 ```json ... ``` 围栏
        string s = reply.Replace("```json", "").Replace("```", "").Trim();

        JSONNode root = null;
        try { root = JSONNode.Parse(s); }
        catch (Exception)
        {
            // 整体不是合法 JSON：截取第一个 { 或 [ 到最后一个 } 或 ] 再试
            int a = s.IndexOfAny(new[] { '[', '{' });
            int b = s.LastIndexOfAny(new[] { ']', '}' });
            if (a < 0 || b <= a) return null;
            try { root = JSONNode.Parse(s.Substring(a, b - a + 1)); }
            catch (Exception e)
            {
                Debug.LogWarning($"[GLM] 蓝图 JSON 解析失败: {e.Message}\n{s}");
                return null;
            }
        }

        // 取 parts 数组：支持 {"parts":[...]} 与裸 [...]
        JSONNode arr = null;
        if (root is JSONArray ja) arr = ja;
        else if (root is JSONObject jo) arr = jo["parts"] as JSONArray;
        if (arr == null) return null;

        // 空数组：AI 表示不认识该物体（{"parts":[]}）
        if (arr.Count == 0) return new List<ShapePart>();

        var parts = new List<ShapePart>();
        foreach (JSONNode node in arr.AsArray)
        {
            if (node == null || node.Count == 0) continue;
            var shape = ParseShapeEnum(node["shape"]?.Value);
            if (shape == null) continue;

            Vector3 pos = ParseVec(node["pos"], Vector3.zero, PosClamp);
            Vector3 rot = ParseVec(node["rot"], Vector3.zero, 360f);
            Vector3 scale = ParseVec(node["scale"], new Vector3(0.1f, 0.1f, 0.1f), ScaleClamp);
            scale = Vector3.Max(scale, new Vector3(0.01f, 0.01f, 0.01f)); // 防止过小

            Vector3 cv = ParseVec(node["color"], new Vector3(0.8f, 0.8f, 0.8f), 1f);
            var color = new Color(cv.x, cv.y, cv.z, 1f);
            if (color.maxColorComponent < 0.1f) color = Color.white; // 防止纯黑不可见

            parts.Add(new ShapePart(shape.Value, pos, rot, scale, color));
            if (parts.Count >= MaxParts) break;
        }

        if (parts.Count == 0) return new List<ShapePart>();
        NormalizeBlueprint(parts);
        return parts;
    }

    /// <summary>
    /// 蓝图归一化：居中 + 等比缩放到 FitHalf 范围。
    /// AI 给的比例经常失调（过大/过小/偏离中心），这里按各部件的包围盒统一修正，
    /// 显著提升模型观感。
    /// </summary>
    static void NormalizeBlueprint(List<ShapePart> parts)
    {
        // 每部件的局部包围盒半径（按形状语义估算）
        Vector3 Extent(ShapePart p) => p.shape switch
        {
            PetShape.Sphere => new Vector3(p.scale.x, p.scale.x, p.scale.x),
            PetShape.Cube => p.scale,
            PetShape.Cylinder => new Vector3(p.scale.x, p.scale.y, p.scale.x),
            PetShape.Cone => new Vector3(p.scale.x, p.scale.y, p.scale.x),
            PetShape.Torus => new Vector3(p.scale.x + p.scale.y, p.scale.y, p.scale.x + p.scale.y),
            PetShape.Pyramid => new Vector3(p.scale.x, p.scale.y, p.scale.z),
            _ => p.scale,
        };

        var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        foreach (var p in parts)
        {
            // 旋转后的包围盒（取旋转体各轴最大投影，简化为保守估计）
            var e = Extent(p);
            var q = Quaternion.Euler(p.rot);
            var xe = Mathf.Abs(Vector3.Dot(q * Vector3.right, Vector3.right)) * e.x
                   + Mathf.Abs(Vector3.Dot(q * Vector3.up, Vector3.right)) * e.y
                   + Mathf.Abs(Vector3.Dot(q * Vector3.forward, Vector3.right)) * e.z;
            var ye = Mathf.Abs(Vector3.Dot(q * Vector3.right, Vector3.up)) * e.x
                   + Mathf.Abs(Vector3.Dot(q * Vector3.up, Vector3.up)) * e.y
                   + Mathf.Abs(Vector3.Dot(q * Vector3.forward, Vector3.up)) * e.z;
            var ze = Mathf.Abs(Vector3.Dot(q * Vector3.right, Vector3.forward)) * e.x
                   + Mathf.Abs(Vector3.Dot(q * Vector3.up, Vector3.forward)) * e.y
                   + Mathf.Abs(Vector3.Dot(q * Vector3.forward, Vector3.forward)) * e.z;
            min = Vector3.Min(min, p.pos - new Vector3(xe, ye, ze));
            max = Vector3.Max(max, p.pos + new Vector3(xe, ye, ze));
        }

        // 居中
        var center = (min + max) * 0.5f;

        // 等比缩放（只在超界时缩小；过小(整体<0.12m)时放大，保证可见）
        var size = max - min;
        float maxAxis = Mathf.Max(size.x, size.y, size.z);
        float k = 1f;
        if (maxAxis > FitHalf * 2f) k = (FitHalf * 2f) / maxAxis;      // 太大 → 缩小
        else if (maxAxis < 0.12f) k = 0.25f / Mathf.Max(maxAxis, 0.01f); // 太小 → 放大到0.25m
        k = Mathf.Clamp(k, 0.05f, 8f);

        if (k != 1f)
            for (int i = 0; i < parts.Count; i++)
            {
                parts[i].pos -= center;
                parts[i].pos *= k;
                parts[i].scale *= k;
            }
        else
            for (int i = 0; i < parts.Count; i++)
                parts[i].pos -= center;

        Debug.Log($"[GLM] 蓝图归一化: {parts.Count} 部件, 原尺寸 {maxAxis:F2}m, 缩放系数 {k:F2}");
    }

    static PetShape? ParseShapeEnum(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        switch (name.Trim().ToLowerInvariant())
        {
            case "sphere": case "球": case "球体": return PetShape.Sphere;
            case "cube": case "box": case "立方体": case "正方体": case "方块": return PetShape.Cube;
            case "cylinder": case "圆柱": case "圆柱体": return PetShape.Cylinder;
            case "cone": case "圆锥": case "圆锥体": return PetShape.Cone;
            case "torus": case "圆环": case "环": return PetShape.Torus;
            case "pyramid": case "金字塔": case "角锥": return PetShape.Pyramid;
            default: return null;
        }
    }

    static Vector3 ParseVec(JSONNode node, Vector3 fallback, float clamp)
    {
        if (node is JSONArray a && a.Count >= 3)
            return new Vector3(
                Mathf.Clamp(a[0].AsFloat, -clamp, clamp),
                Mathf.Clamp(a[1].AsFloat, -clamp, clamp),
                Mathf.Clamp(a[2].AsFloat, -clamp, clamp));
        return fallback;
    }
}
