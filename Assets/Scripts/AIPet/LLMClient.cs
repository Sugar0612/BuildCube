using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
// 注：SimpleJSON 的类型（JSONNode/JSONObject/JSONArray）位于全局命名空间，无需 using

/// <summary>
/// 智谱 GLM 对话 API 客户端（OpenAI 兼容接口）。
/// 提供通用的流式对话入口 AskRawAsync：SSE 流式 + 空闲看门狗 + 分级降级重试，
/// 供化学解析（ChemistryLLM）等模块复用。
/// </summary>
public static class LLMClient
{
    public const string Endpoint = "https://open.bigmodel.cn/api/paas/v4/chat/completions";

    /// <summary>空闲看门狗（秒）：流式模式下连续这么久没有收到任何新字节才判定失败。
    /// 思维链生成期间 token 持续推送，正常永远不会触发；只有网络真断了才会超时。</summary>
    public const int TimeoutSeconds = 300;

    /// <summary>中止纪元：AbortAll() 递增后在途请求立即中断，且不再走降级重试。
    /// 「停止思考」按钮用——思考时间过长时用户可手动打断。</summary>
    static int abortEpoch = 0;

    /// <summary>中止所有在途流式请求；中止之后的调用按新纪元正常运行</summary>
    public static void AbortAll()
    {
        abortEpoch++;
        Debug.Log("[GLM] AbortAll：中止在途请求");
    }

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
    /// 通用对话：自定义 system 提示词 + 用户文本，返回模型回复内容。失败时返回 null 并输出错误。
    /// onProgress：流式期间每 0.5 秒上报一次思考进度（可传 null）。
    /// jsonMode=true 时启用 response_format=json_object（要求提示词中含 "json" 字样）。
    /// </summary>
    public static async Task<string> AskRawAsync(string apiKey, string systemPrompt, string userText,
                                                 string model, Action<ThinkProgress> onProgress = null,
                                                 int maxTokens = 4000, double temperature = 0.1,
                                                 bool jsonMode = true)
    {
        var body = new JSONObject();
        body["model"] = model;
        body["temperature"] = temperature;
        body["max_tokens"] = maxTokens;
        body["stream"] = true; // 流式：思维链 token 持续推送，连接不空闲 → 不会被网关掐断

        // 关闭深度思考：跳过思维链直接输出答案，大幅缩短响应时间。
        // 注意：glm-5.3 系列（含 flash）不支持 disabled——参数被忽略，思维链照常输出
        // 且与答案共享 max_tokens 预算，因此 5.3 系列必须给足 token。
        bool supportsThinkingDisabled = model.IndexOf("5.3", StringComparison.Ordinal) < 0;
        if (supportsThinkingDisabled)
        {
            var thinking = new JSONObject();
            thinking["type"] = "disabled";
            body["thinking"] = thinking;
        }

        // JSON 模式：强制模型输出合法 JSON（提示词中已含"json"字样）
        if (jsonMode)
        {
            var fmt = new JSONObject();
            fmt["type"] = "json_object";
            body["response_format"] = fmt;
        }

        var messages = new JSONArray();
        var sys = new JSONObject();
        sys["role"] = "system";
        sys["content"] = systemPrompt;
        messages.Add(sys);
        var usr = new JSONObject();
        usr["role"] = "user";
        usr["content"] = userText;
        messages.Add(usr);
        body["messages"] = messages;

        int epoch = abortEpoch; // 捕获纪元：AbortAll 后本调用立即放弃（含降级重试）

        // 分级降级重试：
        // 1. 初始配置
        // 2. 去 response_format（个别模型不支持 JSON 模式）
        // 3. 去 thinking（仅非 5.3 模型：关思考失败时宁可慢也要拿到结果）
        string reply = await SendAsync(apiKey, body.ToString(), onProgress, 1);
        if (reply == null && jsonMode && epoch == abortEpoch)
        {
            body.Remove("response_format");
            reply = await SendAsync(apiKey, body.ToString(), onProgress, 2);
        }
        if (reply == null && supportsThinkingDisabled && epoch == abortEpoch)
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
            int epoch = abortEpoch;

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
                if (epoch != abortEpoch)
                {
                    // 用户主动中止（停止思考按钮）
                    req.Abort();
                    Debug.Log("[GLM] 流式请求被用户中止");
                    return null;
                }
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

}

