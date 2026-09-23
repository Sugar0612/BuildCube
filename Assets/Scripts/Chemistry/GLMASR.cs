using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 智谱 GLM-ASR 云端语音识别客户端（OpenAI 兼容 /audio/transcriptions）。
/// 替代 Vosk 小模型的自由识别——化学术语识别精度高一个量级；
/// hotwords 传入化学热词进一步加成。唤醒词仍由 Vosk 离线负责（快且准）。
/// </summary>
public static class GLMASR
{
    public const string Endpoint = "https://open.bigmodel.cn/api/paas/v4/audio/transcriptions";
    public const string Model = "glm-asr-2512";

    /// <summary>化学热词加成（API 上限 100）：指令动词 + 实验室高频物质</summary>
    public static readonly string[] Hotwords =
    {
        // 指令
        "构建", "搭建", "生成", "创建", "清空", "开始反应", "你好",
        // 小分子与气体
        "水", "水分子", "甲烷", "乙烯", "乙炔", "苯", "甲苯",
        "氨气", "氨水", "二氧化碳", "一氧化碳", "氧气", "氢气", "氮气", "氯气",
        // 含氧有机物
        "甲醇", "乙醇", "甲醛", "乙醛", "丙酮", "甲酸", "乙酸", "醋酸",
        "乳酸", "草酸", "柠檬酸", "苯甲酸", "水杨酸", "乙醚", "乙酸乙酯", "苯酚",
        // 酸碱盐
        "硫酸", "盐酸", "硝酸", "磷酸", "氢氧化钠", "氢氧化钾", "碳酸钠", "碳酸氢钠", "碳酸钙",
        "氯化钠", "氯化铁", "硫酸铜", "高锰酸钾", "次氯酸", "双氧水",
        "过氧化氢", "硝酸银",
        // 单质与氧化物
        "硅", "钠", "镁", "铝", "锌", "铁", "铜", "银", "金",
        "氧化钙", "氧化铁", "二氧化硅", "二氧化锰", "三氧化二铁",
        // 糖与生物分子
        "葡萄糖", "蔗糖", "淀粉", "纤维素", "尿素", "氨基酸",
        "胆固醇", "咖啡因", "尼古丁", "吗啡", "阿司匹林", "青霉素",
        // 环状与溶剂
        "环己烷", "吡啶", "呋喃", "萘", "硝基苯", "苯胺", "氯仿",
        "四氯化碳", "二氯甲烷", "二硫化碳",
    };

    static readonly string HotwordsJson = BuildHotwordsJson();

    static string BuildHotwordsJson()
    {
        var arr = new JSONArray();
        foreach (var w in Hotwords) arr.Add(w);
        return arr.ToString();
    }

    /// <summary>上传 16k 单声道 16-bit WAV，返回识别文本；失败/空返回 null 或 ""。</summary>
    public static async Task<string> TranscribeAsync(string apiKey, byte[] wav)
    {
        if (wav == null || wav.Length == 0) return null;
        try
        {
            var form = new List<IMultipartFormSection>
            {
                new MultipartFormFileSection("file", wav, "capture.wav", "audio/wav"),
                new MultipartFormDataSection("model", Model),
                new MultipartFormDataSection("stream", "false"),
                new MultipartFormDataSection("hotwords", HotwordsJson),
                // 化学场景 prompt（官方语义为"上下文转录"）：用示例语境把识别结果拉向化学词汇，
                // 同音字优先落在物质名上（如"以纯"→乙醇、"二痒化碳"→二氧化碳）
                new MultipartFormDataSection("prompt",
                    "化学实验室语音指令转录。说话内容是：化学物质名称或分子式（如水、乙醇、甲醇、二氧化碳、氢氧化钠、硫酸、盐酸、葡萄糖、苯、甲烷、氯化钠、高锰酸钾），或操作指令（构建、生成、搭建、开始反应、清空展示台）。"),
            };
            using (var req = UnityWebRequest.Post(Endpoint, form))
            {
                req.SetRequestHeader("Authorization", "Bearer " + apiKey);
                req.timeout = 20;
                await req.SendWebRequest();
                if (req.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[GLMASR] 请求失败: {req.responseCode} {req.downloadHandler?.text}");
                    return null;
                }
                var root = JSONNode.Parse(req.downloadHandler.text);
                return root?["text"]?.Value;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GLMASR] 异常: {e.Message}");
            return null;
        }
    }
}
