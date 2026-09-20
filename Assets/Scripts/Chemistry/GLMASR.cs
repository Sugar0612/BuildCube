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

    /// <summary>化学热词加成（≤100 个）：常见指令动词与课堂/实验室高频物质</summary>
    public static readonly string[] Hotwords =
    {
        "构建", "搭建", "生成", "创建", "清空", "你好",
        "水", "水分子", "甲烷", "乙醇", "甲醇", "乙醛", "乙酸", "丙酮", "乙醚",
        "苯", "甲苯", "苯酚", "苯环", "乙烯", "乙炔", "丙烯",
        "硫酸", "盐酸", "硝酸", "氢氧化钠", "氢氧化钾", "碳酸钠", "碳酸氢钠",
        "氯化钠", "氯化钾", "氨气", "氨水", "二氧化碳", "一氧化碳", "氧气", "氢气", "氮气",
        "葡萄糖", "蔗糖", "淀粉", "蛋白质", "乙酸乙酯", "乙酸乙脂",
        "高锰酸钾", "双氧水", "过氧化氢", "次氯酸", "硫酸铜", "氯化铁", "三氧化二铁",
        "氧化钙", "氧化铁", "二氧化硅", "二氧化锰", "硅", "钠", "镁", "铝", "铁", "铜", "锌", "银", "金",
        "胆固醇", "阿司匹林", "咖啡因", "青霉素", "维生素",
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
