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

    /// <summary>空闲看门狗（秒）：流式模式下连续这么久没有收到任何新字节才判定失败。
    /// 思维链生成期间 token 持续推送，正常永远不会触发；只有网络真断了才会超时。</summary>
    public const int TimeoutSeconds = 300;

    public const string SystemPrompt =
        "你是MR粒子构建助手的3D形状规划模块。把用户说的物体拆解成基础几何体组成的3D模型。\n" +
        "只输出json对象：{\"parts\":[...]}，禁止任何解释、前后缀、代码块标记。parts 数组元素格式：\n" +
        "{\"shape\":\"sphere|cube|cylinder|cone|torus|pyramid\",\"pos\":[x,y,z],\"rot\":[x,y,z],\"scale\":[x,y,z],\"color\":[r,g,b]}\n" +
        "如果用户说的物体你不知道是什么、或是虚构/不存在的名词，直接输出 {\"parts\":[]}，不要编造。\n" +
        "规则：\n" +
        "- Y轴向上，原点在模型几何中心，整体容纳在±0.35的立方体内。\n" +
        "- scale含义：sphere=半径；cube=半边长；cylinder=沿自身Y轴[半径,半高,半径]；cone=尖朝自身+Y[底半径,半高,半径]；torus=环轴为Y[大半径,管半径,忽略]；pyramid=尖朝自身+Y[半底边,半高,半底边]。\n" +
        "- rot是欧拉角（度）。color是0~1浮点RGB。\n" +
        "- 部件数：简单物体3~6个，复杂物体最多10个。用大部件搭主体轮廓，少量小部件做特征，不要逐细节堆部件。\n" +
        "- 部件之间相连成整体，不要悬浮。所有数字只保留2位小数。\n";
        // "示例1——托卡马克装置（真空室+环形分布的磁场线圈+顶部底部供电柱）：\n" +
        // "[{\"shape\":\"torus\",\"pos\":[0,0,0],\"rot\":[0,0,0],\"scale\":[0.16,0.045,0.045],\"color\":[0.9,0.3,0.1]},{\"shape\":\"torus\",\"pos\":[0,0,0],\"rot\":[0,0,0],\"scale\":[0.22,0.012,0.012],\"color\":[0.35,0.4,0.5]},{\"shape\":\"cylinder\",\"pos\":[0,0.3,0],\"rot\":[0,0,0],\"scale\":[0.04,0.06,0.04],\"color\":[0.4,0.5,0.6]},{\"shape\":\"cylinder\",\"pos\":[0,-0.3,0],\"rot\":[0,0,0],\"scale\":[0.04,0.06,0.04],\"color\":[0.4,0.5,0.6]},{\"shape\":\"cylinder\",\"pos\":[0.16,0,0],\"rot\":[0,180,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[-0.16,0,0],\"rot\":[0,0,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0,0,0.16],\"rot\":[0,90,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0,0,-0.16],\"rot\":[0,-90,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0.11,0,0.11],\"rot\":[0,135,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[-0.11,0,0.11],\"rot\":[0,45,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[0.11,0,-0.11],\"rot\":[0,-135,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]},{\"shape\":\"cylinder\",\"pos\":[-0.11,0,-0.11],\"rot\":[0,-45,90],\"scale\":[0.014,0.05,0.014],\"color\":[0.5,0.55,0.6]}]\n" +
        // "示例2——埃菲尔铁塔（4条斜柱+两层平台+塔尖天线）：\n" +
        // "[{\"shape\":\"cylinder\",\"pos\":[0.12,-0.2,0.12],\"rot\":[8,45,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.12,-0.2,0.12],\"rot\":[-8,-45,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[0.12,-0.2,-0.12],\"rot\":[-8,135,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.12,-0.2,-0.12],\"rot\":[8,-135,0],\"scale\":[0.02,0.16,0.02],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cube\",\"pos\":[0,-0.12,0],\"rot\":[0,0,0],\"scale\":[0.16,0.008,0.16],\"color\":[0.6,0.5,0.4]},{\"shape\":\"cylinder\",\"pos\":[0.05,0,0.05],\"rot\":[5,45,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.05,0,0.05],\"rot\":[-5,-45,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[0.05,0,-0.05],\"rot\":[-5,135,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[-0.05,0,-0.05],\"rot\":[5,-135,0],\"scale\":[0.012,0.1,0.012],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cube\",\"pos\":[0,0.08,0],\"rot\":[0,0,0],\"scale\":[0.07,0.006,0.07],\"color\":[0.6,0.5,0.4]},{\"shape\":\"cone\",\"pos\":[0,0.16,0],\"rot\":[0,0,0],\"scale\":[0.04,0.12,0.04],\"color\":[0.55,0.45,0.35]},{\"shape\":\"cylinder\",\"pos\":[0,0.3,0],\"rot\":[0,0,0],\"scale\":[0.005,0.05,0.005],\"color\":[0.8,0.8,0.85]}]\n" +
        // "示例3——直线中子加速器（长真空管道+聚焦磁铁环+靶室）：\n" +
        // "[{\"shape\":\"cylinder\",\"pos\":[0,0,0],\"rot\":[0,0,90],\"scale\":[0.04,0.3,0.04],\"color\":[0.8,0.8,0.85]},{\"shape\":\"torus\",\"pos\":[-0.2,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"torus\",\"pos\":[-0.07,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"torus\",\"pos\":[0.06,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"torus\",\"pos\":[0.19,0,0],\"rot\":[0,0,90],\"scale\":[0.06,0.018,0.018],\"color\":[0.2,0.4,0.9]},{\"shape\":\"sphere\",\"pos\":[0.32,0,0],\"rot\":[0,0,0],\"scale\":[0.07,0.07,0.07],\"color\":[0.9,0.75,0.1]},{\"shape\":\"cylinder\",\"pos\":[-0.33,0,0],\"rot\":[0,0,90],\"scale\":[0.025,0.04,0.025],\"color\":[0.5,0.5,0.55]}]";

    /// <summary>思考进度（流式期间实时上报，主线程回调）</summary>
    public class ThinkProgress
    {
        public float elapsed;      // 已耗时（秒）
        public int reasoningChars; // 思维链已生成字符数
        public int answerChars;    // 正式答案已生成字符数
        public string tail;        // 思维链最近片段（单行截断）
        public int attempt = 1;    // 第几次尝试（1 起）：降级重试后递增，UI 据此提示"重试中"
    }

    /// <summary>
    /// 发送指令文本，返回模型回复内容。失败时返回 null 并输出错误。
    /// onProgress：流式期间每 0.5 秒上报一次思考进度（可传 null）。
    /// </summary>
    public static async Task<string> AskAsync(string apiKey, string userText, string model,
                                               System.Action<ThinkProgress> onProgress = null,
                                               int maxTokens = 8000)
    {
        var body = new JSONObject();
        body["model"] = model;
        body["temperature"] = 0.0;
        body["max_tokens"] = maxTokens;
        body["stream"] = true; // 流式：思维链 token 持续推送，连接不空闲 → 不会被网关掐断

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
        string reply = await SendAsync(apiKey, body.ToString(), onProgress, 1);
        if (reply == null)
        {
            body.Remove("response_format");
            reply = await SendAsync(apiKey, body.ToString(), onProgress, 2);
        }
        if (reply == null)
        {
            body.Remove("thinking");
            reply = await SendAsync(apiKey, body.ToString(), onProgress, 3);
        }
        return reply;
    }

    static async Task<string> SendAsync(string apiKey, string jsonBody,
                                        System.Action<ThinkProgress> onProgress,
                                        int attempt = 1)
    {
        using (var req = new UnityWebRequest(Endpoint, "POST"))
        {
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            req.SetRequestHeader("Authorization", "Bearer " + apiKey);
            req.timeout = 0; // 关闭整体超时，改用空闲看门狗（流式期间字节持续到达，整体计时没有意义）

            var op = req.SendWebRequest();

            // 空闲看门狗：连续 TimeoutSeconds 没有收到新字节才中止（网络真断）。
            // 流式思维链会持续推送数据，正常长思考不会触发。
            long lastBytes = 0;
            float idle = 0f;
            float reportT = 0f;
            float startT = Time.realtimeSinceStartup;
            var sse = new SseAccumulator();
            while (!op.isDone)
            {
                await Task.Delay(100);
                long got = (long)req.downloadedBytes;
                if (got != lastBytes) { lastBytes = got; idle = 0f; }
                else
                {
                    idle += 0.1f;
                    if (idle >= TimeoutSeconds)
                    {
                        Debug.LogWarning($"[GLM] 流式连接空闲超过 {TimeoutSeconds}s，中止请求");
                        req.Abort();
                        break;
                    }
                }

                // 每 0.5 秒解析一次已到达的 SSE 数据，上报思考进度
                if (onProgress != null)
                {
                    reportT += 0.1f;
                    if (reportT >= 0.5f)
                    {
                        reportT = 0f;
                        try
                        {
                            sse.Feed(req.downloadHandler.text);
                            onProgress(new ThinkProgress
                            {
                                elapsed = Time.realtimeSinceStartup - startT,
                                reasoningChars = sse.ReasoningLength,
                                answerChars = sse.ContentLength,
                                tail = sse.ReasoningTail(40),
                                attempt = attempt,
                            });
                        }
                        catch (Exception e) { Debug.LogWarning($"[GLM] 进度解析异常: {e.Message}"); }
                    }
                }
            }

            if (req.result != UnityWebRequest.Result.Success)
            {
                Debug.LogWarning($"[GLM] 请求失败: {req.error} {req.downloadHandler?.text}");
                return null;
            }

            return ParseSseContent(req.downloadHandler.text);
        }
    }

    /// <summary>
    /// 解析 SSE 流式响应：逐行取 "data: {...}" 的 choices[0].delta.content 拼接成完整回复。
    /// 思维链增量在 reasoning_content 字段，不拼接。
    /// </summary>
    static string ParseSseContent(string sse)
    {
        if (string.IsNullOrEmpty(sse)) return null;
        var sb = new StringBuilder();
        int start = 0;
        while (start < sse.Length)
        {
            int end = sse.IndexOf('\n', start);
            if (end < 0) end = sse.Length;
            var line = sse.Substring(start, end - start).Trim();
            start = end + 1;

            if (!line.StartsWith("data:")) continue;
            var payload = line.Substring(5).Trim();
            if (payload == "[DONE]") break;
            if (payload.Length == 0) continue;

            try
            {
                var json = JSONNode.Parse(payload);
                var delta = json?["choices"]?[0]?["delta"]?["content"];
                if (!string.IsNullOrEmpty(delta?.Value)) sb.Append(delta.Value);
            }
            catch (Exception) { /* 跳过半行/心跳帧等非 JSON 片段 */ }
        }
        var content = sb.ToString().Trim();
        return content.Length > 0 ? content : null;
    }

    /// <summary>
    /// SSE 增量累积器：只处理已到达数据中的完整行，分别累积
    /// reasoning_content（思维链）与 content（正式答案），供进度展示。
    /// </summary>
    sealed class SseAccumulator
    {
        int consumed;
        readonly StringBuilder reasoning = new StringBuilder();
        readonly StringBuilder content = new StringBuilder();

        public int ReasoningLength => reasoning.Length;
        public int ContentLength => content.Length;

        public void Feed(string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            int lastNl = s.LastIndexOf('\n');
            if (lastNl < consumed) return;           // 没有新的完整行
            string chunk = s.Substring(consumed, lastNl + 1 - consumed);
            consumed = lastNl + 1;

            int start = 0;
            while (start < chunk.Length)
            {
                int end = chunk.IndexOf('\n', start);
                if (end < 0) end = chunk.Length;
                var line = chunk.Substring(start, end - start).Trim();
                start = end + 1;

                if (!line.StartsWith("data:")) continue;
                var payload = line.Substring(5).Trim();
                if (payload.Length == 0 || payload == "[DONE]") continue;
                try
                {
                    var json = JSONNode.Parse(payload);
                    var delta = json?["choices"]?[0]?["delta"];
                    var rc = delta?["reasoning_content"]?.Value;
                    if (!string.IsNullOrEmpty(rc)) reasoning.Append(rc);
                    var cc = delta?["content"]?.Value;
                    if (!string.IsNullOrEmpty(cc)) content.Append(cc);
                }
                catch (Exception) { /* 半行/心跳帧忽略 */ }
            }
        }

        /// <summary>思维链末尾片段（去换行，用于展示"AI 正在想什么"）</summary>
        public string ReasoningTail(int maxChars)
        {
            int len = reasoning.Length;
            if (len == 0) return "";
            int take = Math.Min(maxChars, len);
            return reasoning.ToString(len - take, take)
                             .Replace("\r", "").Replace("\n", " ").Trim();
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
