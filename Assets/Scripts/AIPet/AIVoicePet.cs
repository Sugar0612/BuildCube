using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// AI 语音宠物编排器：
/// 1. Vosk 离线识别持续监听：待机时用限定语法精准检测“你好”，唤醒后切自由识别；
/// 2. 唤醒后聆听“构建xxx”指令，语音播报（Android TTS）回应；
/// 3. 指令全部交给 GLM 规划成几何体蓝图（无本地形状兜底）；
/// 4. 驱动 ParticlePet 呈现 待机→唤醒→思考→构建 的形态变化。
/// </summary>
public class AIVoicePet : MonoBehaviour
{
    [Header("语音识别（Vosk 离线）")]
    [Tooltip("StreamingAssets 下的模型文件名")]
    public string voskModelPath = "vosk-model-small-cn-0.22.zip";
    [Tooltip("待机时是否启用限定语法（只听唤醒词，识别更准）")]
    public bool useWakeGrammar = true;

    [Header("语音播报（TTS）")]
    [Tooltip("是否启用语音播报（真机 Android 系统 TTS）")]
    public bool enableTTS = true;

    [Header("GLM 大模型")]
    [Tooltip("GLM 模型名")]
    public string glmModel = "glm-5.3-flash";
    [Tooltip("智谱开放平台 API Key（open.bigmodel.cn，必填——所有构建都由 GLM 完成）")]
    public string glmApiKey = "";

    [Header("行为参数")]
    [Tooltip("唤醒后等待指令的时长（秒）")]
    public float listenTimeout = 12f;
    [Tooltip("构建完成后保持展示的时长（秒）；展示中说“你好”可提前打断进入下一轮聆听")]
    public float builtHoldTime = 45f;

    public ParticlePet Pet { get; private set; }

    PetStatusUI ui;
    VoskSpeechToText vosk;
    AndroidTTS tts;
    float wakeDeadline;
    float builtAt = -999f;
    bool aiBusy;
    bool grammarActive;    // 当前是否处于唤醒词语法模式
    string lastBuildLabel = ""; // 构建完成提示用

    // 唤醒词“你好”（含同音容错写法）
    static readonly string[] WakeWords = { "你好", "您好", "拟好" };
    static readonly string[] BuildVerbs = { "构建", "建造", "搭建", "创建", "生成" };

    // 待机限定语法：唤醒词 + 一句话指令（动词+常见物体，供待机时直接说完整指令）
    static readonly string[] IdleGrammar =
    {
        "你好", "您好",
        "构建立方体", "构建方块", "构建正方体",
        "构建球体", "构建圆球", "构建球",
        "构建圆柱", "构建圆柱体",
        "构建圆锥", "构建圆锥体",
        "构建圆环", "构建圆环体",
        "构建金字塔", "构建角锥",
    };

    void Awake()
    {
        Pet = GetComponent<ParticlePet>();
        ui = GetComponent<PetStatusUI>();
        if (enableTTS) tts = gameObject.AddComponent<AndroidTTS>();

        // 运行时装配语音链路（Vosk 插件组件）
        var voice = gameObject.AddComponent<VoiceProcessor>();
        vosk = gameObject.AddComponent<VoskSpeechToText>();
        vosk.ModelPath = voskModelPath;
        vosk.VoiceProcessor = voice;
        vosk.AutoStart = true;
        vosk.OnTranscriptionResult += OnTranscriptJson;
        vosk.OnStatusUpdated += OnVoskStatus;

        if (Pet != null) Pet.OnBuildComplete += OnBuildComplete;
    }

    void Start()
    {
        if (ui != null)
        {
            ui.SetStatus("语音模型加载中…");
            ui.SetHint("加载完成后说“你好”唤醒我");
        }
    }

    void Update()
    {
        if (Pet == null) return;

        // 唤醒超时 → 回到待机
        if (Pet.State == PetState.Awake && Time.time > wakeDeadline)
        {
            Pet.SetState(PetState.Idle);
            SetGrammarMode(true);
            SetIdleUI();
        }

        // 构建完成后保持一段时间 → 回到待机球体
        if (Pet.State == PetState.Built && Time.time > builtAt + builtHoldTime)
        {
            Pet.SetState(PetState.Idle);
            SetGrammarMode(true);
            SetIdleUI();
        }
    }

    // ---------------- 语法切换（提升识别准确度） ----------------

    /// <summary>切换 Vosk 识别语法：true=待机限定语法（唤醒词极准），false=自由识别（任意物体名）</summary>
    void SetGrammarMode(bool idle)
    {
        if (vosk == null || !useWakeGrammar || grammarActive == idle) return;
        grammarActive = idle;
        try
        {
            vosk.SetKeyPhrases(idle ? new List<string>(IdleGrammar) : null);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 切换语法失败: {e.Message}");
        }
    }

    // ---------------- 语音事件 ----------------

    void OnVoskStatus(string s)
    {
        if (s == "Initialized")
        {
            SetGrammarMode(true); // 待机：限定语法
            SetIdleUI();
        }
        else if (ui != null && s != "Start Recording" && s != "Stopped")
        {
            ui.SetStatus("语音: " + s);
        }
    }

    void OnTranscriptJson(string json)
    {
        string text;
        try
        {
            var result = new RecognitionResult(json);
            text = result.Phrases != null && result.Phrases.Length > 0
                ? result.Phrases[0].Text
                : "";
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 解析识别结果失败: {e.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        string norm = Normalize(text);
        if (ui != null) ui.SetHeard(text);
        HandleText(text, norm);
    }

    void HandleText(string raw, string norm)
    {
        if (Pet == null || aiBusy) return;

        switch (Pet.State)
        {
            case PetState.Idle:
                if (IsWake(norm))
                {
                    Wake();
                    if (HasBuildVerb(norm)) _ = TryBuildAsync(raw, norm);
                }
                break;

            case PetState.Awake:
                // 构建指令优先：一句话“你好，构建立方体”也要能直接触发构建
                if (HasBuildVerb(norm)) { _ = TryBuildAsync(raw, norm); break; }
                if (IsWake(norm)) Wake();
                break;

            case PetState.Built:
                // 展示阶段可被语音打断：新的构建指令直接开新一轮；“你好”回到监听
                if (HasBuildVerb(norm)) { _ = TryBuildAsync(raw, norm); break; }
                if (IsWake(norm))
                {
                    Pet.SetState(PetState.Awake);
                    Wake();
                }
                break;
        }
    }

    // ---------------- 流程控制 ----------------

    void Wake()
    {
        Pet.SetState(PetState.Awake);
        wakeDeadline = Time.time + listenTimeout;
        SetGrammarMode(false); // 唤醒后：自由识别（可说任意物体）
        tts?.Speak("在的");
        if (ui != null)
        {
            // 粒子回应“在的”，提示聆听构建指令
            ui.SetStatus("在的");
            ui.SetHint($"请说“构建 某物”，如：构建立方体（{Mathf.CeilToInt(wakeDeadline - Time.time)} 秒）");
        }
    }

    async Task TryBuildAsync(string raw, string norm)
    {
        aiBusy = true;
        Pet.SetState(PetState.Thinking);
        if (ui != null)
        {
            ui.SetStatus("AI 思考中…");
            ui.SetHint("正在规划模型蓝图");
        }

        bool built = false;

        // 所有构建全部走 GLM 蓝图：任意物体 → 基础几何体组合（网络失败自动重试一次）
        if (string.IsNullOrEmpty(glmApiKey))
        {
            // 未配置 Key：无法构建，明确提示
            tts?.Speak("请先配置大模型密钥");
            if (ui != null)
            {
                ui.SetStatus("未配置 GLM API Key");
                ui.SetHint("请在 Inspector 的 glmApiKey 填入智谱开放平台密钥");
            }
            Pet.SetState(PetState.Awake);
            wakeDeadline = Time.time + listenTimeout;
            aiBusy = false;
            return;
        }

        {
            string reply = null;
            for (int attempt = 0; attempt < 2 && string.IsNullOrEmpty(reply); attempt++)
            {
                try
                {
                    reply = await LLMClient.AskAsync(glmApiKey, raw, glmModel);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[AIPet] GLM 调用异常: {e.Message}");
                }
                if (string.IsNullOrEmpty(reply) && attempt == 0 && ui != null)
                    ui.SetHint("GLM 连接失败，重试中…");
            }

            if (!string.IsNullOrEmpty(reply))
            {
                var parts = LLMClient.ParseBlueprint(reply);

                if (parts != null && parts.Count == 0)
                {
                    // AI 明确表示不认识该物体，直接告知
                    tts?.Speak("不知道那是什么");
                    if (ui != null)
                    {
                        ui.SetStatus($"我不认识“{raw}”");
                        ui.SetHint("换个常见的物体试试，如：构建立方体");
                    }
                    Pet.SetState(PetState.Awake);
                    wakeDeadline = Time.time + listenTimeout;
                    aiBusy = false;
                    return;
                }

                if (parts != null && parts.Count > 0)
                {
                    if (ui != null)
                    {
                        ui.SetStatus($"正在构建：{raw}");
                        ui.SetHint($"GLM 蓝图：{parts.Count} 个部件");
                    }
                    lastBuildLabel = $"{raw}（{parts.Count} 部件）";
                    Pet.BuildBlueprint(parts);
                    built = true;
                }
            }
        }

        if (!built)
        {
            tts?.Speak("没听清要构建什么，请再说一遍");
            if (ui != null)
            {
                ui.SetStatus("GLM 连接失败或返回无法解析");
                ui.SetHint("请检查网络和 API Key 后重试");
            }
            Pet.SetState(PetState.Awake);
            wakeDeadline = Time.time + listenTimeout;
        }

        aiBusy = false;
    }

    void OnBuildComplete()
    {
        builtAt = Time.time;
        tts?.Speak("构建完成");
        if (ui != null)
        {
            ui.SetStatus($"构建完成：{lastBuildLabel} ✓");
            ui.SetHint($"说“你好”进入下一轮聆听（{Mathf.CeilToInt(builtHoldTime)} 秒后自动待机）");
        }
    }

    void SetIdleUI()
    {
        if (ui == null) return;
        ui.SetStatus("待机中");
        ui.SetHeard("");
        ui.SetHint("说“你好”唤醒我");
    }

    // ---------------- 文本工具 ----------------

    static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    static bool ContainsAny(string s, string[] words)
    {
        foreach (var w in words)
            if (s.Contains(w)) return true;
        return false;
    }

    static bool IsWake(string norm) => ContainsAny(norm, WakeWords);

    static bool HasBuildVerb(string norm) => ContainsAny(norm, BuildVerbs);

    // ---------------- 调试接口 ----------------

    /// <summary>模拟一句语音输入（编辑器/联调测试用）</summary>
    public void DebugSimulate(string transcript)
    {
        if (ui != null) ui.SetHeard(transcript);
        HandleText(transcript, Normalize(transcript));
    }
}
