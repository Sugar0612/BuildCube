using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

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
    [Tooltip("智谱开放平台 API Key（open.bigmodel.cn）。留空则启动时从 Assets/StreamingAssets/glm_key.txt 读取（该文件已 gitignore，不会上传）")]
    public string glmApiKey = "";

    [Header("行为参数")]
    [Tooltip("唤醒后等待指令的时长（秒）")]
    public float listenTimeout = 12f;
    [Tooltip("构建完成后保持展示的时长（秒）；展示中说“你好”可提前打断进入下一轮聆听")]
    public float builtHoldTime = 45f;

    public ParticlePet Pet { get; private set; }

    PetStatusUI ui;
    VoskSpeechToText vosk;
    VoiceProcessor voice;
    AndroidTTS tts;
    float wakeDeadline;
    float builtAt = -999f;
    bool aiBusy;
    bool grammarActive;    // 当前是否处于唤醒词语法模式
    string lastBuildLabel = ""; // 构建完成提示用
    string apiKey;         // 运行时生效的密钥：Inspector 优先，否则从 StreamingAssets/glm_key.txt 读取
    const string KeyFileName = "glm_key.txt"; // 本地密钥文件（已 gitignore，随包发布）

    // 唤醒词“你好”（含同音容错写法）
    static readonly string[] WakeWords = { "你好", "您好", "拟好" };
    static readonly string[] BuildVerbs = { "构建", "建造", "搭建", "创建", "生成" };

    // 待机限定语法：唤醒词 + 一句话指令（动词+常见物体，供待机时直接说完整指令）
    static readonly string[] IdleGrammar =
    {
        "你好", "您好",
        // "构建立方体", "构建方块", "构建正方体",
        // "构建球体", "构建圆球", "构建球",
        // "构建圆柱", "构建圆柱体",
        // "构建圆锥", "构建圆锥体",
        // "构建圆环", "构建圆环体",
        // "构建金字塔", "构建角锥",
    };

    void Awake()
    {
        Pet = GetComponent<ParticlePet>();
        ui = GetComponent<PetStatusUI>();
        if (enableTTS) tts = gameObject.AddComponent<AndroidTTS>();

        // 运行时装配语音链路（Vosk 插件组件）
        voice = gameObject.AddComponent<VoiceProcessor>();
        vosk = gameObject.AddComponent<VoskSpeechToText>();
        vosk.ModelPath = voskModelPath;
        vosk.VoiceProcessor = voice;
        vosk.AutoStart = true;
        vosk.OnTranscriptionResult += OnTranscriptJson;
        vosk.OnStatusUpdated += OnVoskStatus;

        if (Pet != null) Pet.OnBuildComplete += OnBuildComplete;

        apiKey = glmApiKey; // Inspector 填写优先；为空则 Start 时从本地密钥文件读取
    }

    void Start()
    {
        if (ui != null)
        {
            ui.SetStatus("语音模型加载中…");
            ui.SetHint("加载完成后说“你好”唤醒我");
        }

        // 密钥分离：Inspector 未填时从 StreamingAssets/glm_key.txt 读取
        //（该文件已加入 .gitignore：本地打包正常带上 key，git 上传不会带走）
        if (string.IsNullOrEmpty(apiKey)) StartCoroutine(LoadLocalKey());
    }

    /// <summary>
    /// 读取本地密钥文件，取首个非注释行。
    /// Android 上 StreamingAssets 打包在 APK 内，必须用 UnityWebRequest 而非 File.IO。
    /// </summary>
    IEnumerator LoadLocalKey()
    {
        string path = Path.Combine(Application.streamingAssetsPath, KeyFileName);
#if UNITY_EDITOR || UNITY_STANDALONE
        if (File.Exists(path)) apiKey = FirstKeyLine(File.ReadAllText(path));
        else Debug.LogWarning($"[AIPet] 未找到本地密钥文件: {path}");
        yield break; // 编辑器/PC 同步读取：含 yield 才是迭代器方法，否则 CS0161
#else
        using (var req = UnityWebRequest.Get(path))
        {
            yield return req.SendWebRequest();
            if (req.result == UnityWebRequest.Result.Success) apiKey = FirstKeyLine(req.downloadHandler.text);
            else Debug.LogWarning($"[AIPet] 读取本地密钥文件失败: {req.error}");
        }
#endif
    }

    /// <summary>取文件中第一个非空且非 # 注释的行作为密钥</summary>
    static string FirstKeyLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        foreach (var raw in text.Split('\n'))
        {
            var s = raw.Trim();
            if (s.Length > 0 && !s.StartsWith("#")) return s;
        }
        return "";
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

    // ---------------- 麦克风开关 ----------------

    /// <summary>开/关麦克风采集。思考阶段无语音交互意义，关麦省电并避免误识别。</summary>
    void SetMic(bool on)
    {
        if (voice == null) return;
        try
        {
            if (on && !voice.IsRecording) voice.StartRecording();
            else if (!on && voice.IsRecording) voice.StopRecording();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 麦克风切换失败: {e.Message}");
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
        SetMic(false); // 思考阶段不收语音：关麦省电，结束后恢复
        if (ui != null)
        {
            ui.SetStatus("AI 思考中…");
            ui.SetHint("正在规划模型蓝图");
        }

        bool built = false;

        // 所有构建全部走 GLM 蓝图：任意物体 → 基础几何体组合（网络失败自动重试一次）
        if (string.IsNullOrEmpty(apiKey))
        {
            // 未配置 Key：无法构建，明确提示
            tts?.Speak("请先配置大模型密钥");
            if (ui != null)
            {
                ui.SetStatus("未配置 GLM API Key");
                ui.SetHint("请在 Assets/StreamingAssets/glm_key.txt 填入密钥（该文件不上传 git）");
            }
            Pet.SetState(PetState.Awake);
            wakeDeadline = Time.time + listenTimeout;
            aiBusy = false;
            SetMic(true);
            return;
        }

        {
            string reply = null;
            try
            {
                // 单次调用：超时/失败不自动重连（重试只会再等一轮超时），直接回监听。
                // 流式进度条设计：
                // - 思维链总长度无法预知，用时间渐近曲线持续爬升（前快后慢、无限逼近88%），
                //   永远不会像旧版"字符数/2400"那样长时间钉死在 90%；
                // - 正式答案一旦开始流式输出即接近完成，映射到 88%~99%；
                // - 降级重试时进度不清零：单调递增（只升不降）+ 提示"第N次尝试"，耗时跨次累计。
                float shownPct = 0f;   // 已展示过的最大进度（单调不回退）
                float timeBase = 0f;   // 前几次尝试的累计耗时（秒）
                float lastElapsed = 0f;// 本次尝试最近一次上报的耗时（秒）
                int lastAttempt = 1;
                reply = await LLMClient.AskAsync(apiKey, raw, glmModel, p =>
                {
                    if (ui == null) return;

                    // 检测到新一轮尝试：把上一次的耗时计入总时长
                    if (p.attempt != lastAttempt)
                    {
                        timeBase += lastElapsed;
                        lastAttempt = p.attempt;
                        lastElapsed = 0f;
                    }
                    lastElapsed = p.elapsed;
                    float totalT = timeBase + p.elapsed;

                    float pct;
                    if (p.answerChars > 0)
                        // 答案已开始输出：收尾段 88→99%
                        pct = 88f + 11f * Mathf.Clamp01(p.answerChars / 900f);
                    else
                        // 思维链阶段：88 * t/(t+15)，5s≈22%、15s≈44%、30s≈59%、60s≈68%
                        pct = 88f * (totalT / (totalT + 15f));
                    shownPct = Mathf.Max(shownPct, pct); // 单调递增：重试/网络抖动不让进度倒退

                    int bars = Mathf.RoundToInt(shownPct / 10f);
                    var sb = new StringBuilder(96);
                    sb.Append("思考 ").Append(totalT.ToString("0")).Append("s [")
                      .Append('#', bars).Append('-', 10 - bars).Append("] ")
                      .Append(shownPct.ToString("0")).Append('%');
                    if (lastAttempt > 1)
                        sb.Append(" 网络波动,第").Append(lastAttempt).Append("次尝试");
                    if (!string.IsNullOrEmpty(p.tail))
                        sb.Append('\n').Append(p.tail);
                    ui.SetHint(sb.ToString());
                });
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[AIPet] GLM 调用异常: {e.Message}");
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
                    SetMic(true);
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
        SetMic(true); // 思考结束（进入构建/唤醒），恢复语音监听
    }

    void OnBuildComplete()
    {
        builtAt = Time.time;
        SetMic(true); // 构建完成：确保麦克风恢复（展示阶段可语音打断）
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
