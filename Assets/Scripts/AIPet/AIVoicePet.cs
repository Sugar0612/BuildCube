using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// 化学分子 MR 助手编排器（v2：全息舞台 + 反应分析）：
/// 1. 唤醒词"你好"由 Vosk 离线限定语法负责；命令语音走智谱 GLM-ASR 云端识别（化学热词加成）；
/// 2. 说出物质 → GLM 解析候选 + PubChem 预校验 → 确认卡片点选 → 全息舞台飞入缩小版球棍模型
///    （统一真实比例、各自独立旋转、框下显示名字）；舞台渐进生长：一个分子填入后
///    才出现加号和下一个相框（最多 4 个反应物）；
/// 3. 集齐 ≥2 个反应物后可点「开始反应」或说"开始反应"：GLM 分析 →「=」出现 →
///    产物逐个解析上台；方程式/条件/可行性显示在右侧面板；
/// 4. 手柄/手掌可拨动全息粒子（斥力避让）。
/// </summary>
public class AIVoicePet : MonoBehaviour
{
    [Header("语音识别")]
    [Tooltip("StreamingAssets 下的 Vosk 模型文件名（负责唤醒词）")]
    public string voskModelPath = "vosk-model-small-cn-0.22.zip";
    [Tooltip("待机时是否启用限定语法（只听唤醒词，识别更准）")]
    public bool useWakeGrammar = true;
    [Tooltip("命令语音走智谱云端识别（GLM-ASR，化学术语更准）；关闭则回退 Vosk 自由识别")]
    public bool useCloudSTT = true;

    [Header("语音播报（TTS）")]
    [Tooltip("是否启用语音播报（真机 Android 系统 TTS）")]
    public bool enableTTS = true;

    [Header("GLM 大模型")]
    [Tooltip("GLM 模型名")]
    public string glmModel = "glm-5.3-flash";
    [Tooltip("智谱开放平台 API Key（open.bigmodel.cn）。留空则启动时从 Assets/StreamingAssets/glm_key.txt 读取（该文件已 gitignore，不会上传）")]
    public string glmApiKey = "";

    [Header("行为参数")]
    [Tooltip("唤醒后等待指令的时长（秒）；上台后同样是连续添加的自由聆听窗口时长")]
    public float listenTimeout = 12f;
    [Tooltip("确认卡片等待操作的超时（秒），超时自动收起")]
    public float confirmTimeout = 120f;

    public ParticlePet Pet { get; private set; }

    ChemUIController chemUI;
    MoleculeTray tray;
    HoloTray holo;
    EquationBench bench;
    VoiceCapture capture;
    PetStatusUI ui;
    VoskSpeechToText vosk;
    VoiceProcessor voice;
    AndroidTTS tts;
    float wakeDeadline;
    float listenDeadline = -999f;  // 自由聆听窗口截止时间
    float confirmDeadline;
    bool aiBusy;           // 解析/确认流程进行中：抑制语音与超时
    bool awaitingConfirm;  // 确认卡片展示中
    bool userAbort;        // 用户按了「停止思考」：中止在途请求后静默退出，不报失败
    bool listenWindow;     // 自由聆听窗口（连续添加分子无需反复唤醒）
    bool grammarActive;    // 当前是否处于唤醒词语法模式
    bool asrBusy;          // 云端识别进行中（避免重复提交）
    bool micWanted = true; // 期望麦克风开启（仅思考期间关麦；看门狗据此自愈）
    float micRetryAt;      // 看门狗下次重试时刻
    string apiKey;         // 运行时生效的密钥：Inspector 优先，否则从 StreamingAssets/glm_key.txt 读取
    const string KeyFileName = "glm_key.txt"; // 本地密钥文件（已 gitignore，随包发布）

    // 思考进度条（单调不回退，多个 GLM 请求间共用）
    float shownPct, timeBase, lastElapsed;
    int lastAttempt = 1;

    // 反应产物（按产物槽位；反应物变化即失效重算）
    readonly Molecule[] productSlots = new Molecule[EquationBench.ProductSlots];

    // 唤醒词“你好”（含同音容错写法）
    static readonly string[] WakeWords = { "你好", "您好", "拟好" };
    static readonly string[] BuildVerbs = { "构建", "建造", "搭建", "创建", "生成" };

    // 待机限定语法：唤醒词（唤醒后云端识别命令，或本地模式切自由识别）
    static readonly string[] IdleGrammar = { "你好", "您好" };

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

        // 化学 UI：确认卡片 + 分子列表面板 + 方程式舞台 + 全息粒子 + 命令录音器
        chemUI = gameObject.AddComponent<ChemUIController>();
        tray = gameObject.AddComponent<MoleculeTray>();
        bench = gameObject.AddComponent<EquationBench>();
        holo = gameObject.AddComponent<HoloTray>();
        capture = gameObject.AddComponent<VoiceCapture>();
        capture.OnWavReady += OnCaptureWav;
        holo.SetSlots(bench.Anchors);
        chemUI.CandidatePicked += OnCandidatePicked;
        chemUI.Respeak += OnRespeak;
        chemUI.Cancelled += OnCancelCard;
        chemUI.ManualSubmitted += OnManualSubmitted;
        chemUI.KeyboardClosed += OnKeyboardClosed;
        chemUI.StopRequested += OnStopThinking;
        tray.AddRequested += OnTrayAddRequested;
        tray.KeyboardInputRequested += OnTrayKeyboardRequested;
        tray.ReactRequested += OnBenchReactRequested;
        tray.TrayChanged += OnTrayChanged;

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

        // 麦克风看门狗：系统键盘唤起/失焦时安卓可能静默杀掉录音（表现为再也识别不到任何语音）。
        // 期望开麦但实际不在录 → 周期自动重开。须在 aiBusy 早退之前；且等 Vosk 初始化完成，
        // 否则会抢在 ToggleRecording 之前开麦，导致其反向关麦并终止识别线程。
        if (micWanted && vosk != null && vosk.IsInitialized && voice != null
            && !voice.IsRecording && Time.time >= micRetryAt)
        {
            micRetryAt = Time.time + 1.5f;
            Debug.Log("[AIPet] 看门狗：麦克风未在录，尝试重开");
            SetMic(true);
        }

        // 确认卡片超时：自动收起（全息分子不受影响）
        if (awaitingConfirm && Time.time > confirmDeadline)
        {
            chemUI.Hide();
            GoIdle();
            return;
        }

        // 「停止思考」按钮：仅在思考中显示（必须在 aiBusy 早退之前，否则思考期间永远执行不到）
        chemUI.ShowStop(aiBusy && Pet.State == PetState.Thinking);

        if (aiBusy) return; // 流程进行中不处理超时（内部自行管理状态）

        // 唤醒超时 → 回到待机
        if (Pet.State == PetState.Awake && Time.time > wakeDeadline)
            GoIdle();

        // 自由聆听窗口结束 → 收回唤醒词语法、停止录音（全息分子保留）
        // 录音或云端识别进行中时顺延，避免说完一句被窗口掐断
        if (listenWindow && Time.time > listenDeadline && !capture.Capturing && !asrBusy)
            CloseListenWindow();
    }

    // ---------------- 麦克风开关 ----------------

    /// <summary>开/关麦克风采集。思考阶段无语音交互意义，关麦省电并避免误识别。</summary>
    void SetMic(bool on)
    {
        micWanted = on;
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

    /// <summary>恢复焦点（关掉系统键盘/切回应用）：失焦期间被安卓杀掉的麦克风立即按需重开</summary>
    void OnApplicationFocus(bool hasFocus)
    {
        if (hasFocus && micWanted && vosk != null && vosk.IsInitialized
            && voice != null && !voice.IsRecording)
        {
            Debug.Log("[AIPet] 失焦恢复，重开麦克风");
            SetMic(true);
        }
    }

    // ---------------- 自由聆听窗口 ----------------

    /// <summary>开启自由聆听窗口：连续说指令无需反复唤醒；云端模式下同时开始命令录音</summary>
    void StartListenWindow()
    {
        listenWindow = true;
        listenDeadline = Time.time + listenTimeout;
        if (useCloudSTT)
        {
            SetGrammarMode(true);  // Vosk 只负责唤醒词，避免与云端识别重复
            if (voice != null && voice.IsRecording) capture.Begin(voice);
        }
        else
        {
            SetGrammarMode(false); // 本地模式：Vosk 自由识别
        }
    }

    /// <summary>结束自由聆听窗口</summary>
    void EndListenWindow()
    {
        listenWindow = false;
        capture.End();
    }

    /// <summary>窗口结束：收回唤醒词语法，全息分子保留</summary>
    void CloseListenWindow()
    {
        EndListenWindow();
        SetGrammarMode(true);
        if (ui != null)
        {
            ui.SetStatus($"展示台 {tray.Count}/{MoleculeTray.MaxSlots}（待机中）");
            ui.SetHeard("");
            ui.SetHint("说「你好」继续添加，或点面板按钮操作");
        }
    }

    // ---------------- 语法切换（提升识别准确度） ----------------

    /// <summary>切换 Vosk 识别语法：true=待机限定语法（唤醒词极准），false=自由识别（本地模式用）</summary>
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
        float confidence = 0f;
        string text;
        try
        {
            var result = new RecognitionResult(json);
            if (result.Phrases == null || result.Phrases.Length == 0) return;
            text = result.Phrases[0].Text;
            confidence = result.Phrases[0].Confidence;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 解析识别结果失败: {e.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(text)) return;

        // 云端模式下 Vosk 只跑唤醒词语法，不会产出命令文本
        string norm = Normalize(text);
        if (ui != null) ui.SetHeard(text);
        HandleText(text, norm, confidence);
    }

    /// <summary>一句话录完 → 云端识别 → 走统一文本处理</summary>
    async Task OnCaptureWavAsync(byte[] wav)
    {
        if (asrBusy) return;
        asrBusy = true;
        if (ui != null) ui.SetStatus("识别中…");
        string text = null;
        try
        {
            text = await GLMASR.TranscribeAsync(apiKey, wav);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 云端识别异常: {e.Message}");
        }
        asrBusy = false;

        // 识别期间窗口可能已关闭/进入流程
        if (!listenWindow || aiBusy) return;

        if (string.IsNullOrWhiteSpace(text))
        {
            if (ui != null) ui.SetStatus("没听清，请再说一遍");
            if (voice != null && voice.IsRecording) capture.Begin(voice); // 继续录
            return;
        }

        text = text.Trim();
        if (ui != null) ui.SetHeard(text);
        HandleText(text, Normalize(text), 1f);

        // 处理后若仍在窗口（如只是唤醒词刷新）且录音器空闲 → 继续录
        if (listenWindow && !aiBusy && !capture.Capturing && voice != null && voice.IsRecording)
            capture.Begin(voice);
    }

    void OnCaptureWav(byte[] wav) => _ = OnCaptureWavAsync(wav);

    void HandleText(string raw, string norm, float confidence)
    {
        if (Pet == null || aiBusy) return;

        switch (Pet.State)
        {
            case PetState.Idle:
                if (IsWake(norm))
                {
                    Wake();
                    RouteCommand(raw, norm, confidence);
                }
                else if (listenWindow)
                {
                    // 自由聆听窗口：连续报下一个分子无需再喊“你好”
                    if (!RouteCommand(raw, norm, confidence))
                        _ = BeginConfirmAsync(raw, confidence);
                }
                break;

            case PetState.Awake:
                // 唤醒后自由聆听：指令优先；非唤醒词的普通话语直接当作物质名解析
                if (!RouteCommand(raw, norm, confidence) && !IsWake(norm))
                    _ = BeginConfirmAsync(raw, confidence);
                break;

            case PetState.Built:
            case PetState.Building:
                if (RouteCommand(raw, norm, confidence)) break;
                if (IsWake(norm)) Wake();
                else _ = BeginConfirmAsync(raw, confidence);
                break;
        }
    }

    /// <summary>路由显式指令。返回 true 表示已命中（清空/开始反应/构建动词）。</summary>
    bool RouteCommand(string raw, string norm, float confidence)
    {
        if (norm.Contains("清空"))
        {
            if (tray.Count > 0)
            {
                tray.Clear(); // TrayChanged 事件负责全息台与面板同步
                tts?.SpeakText("展示台已清空");
            }
            else if (ui != null) ui.SetHint("展示台本来就是空的");
            return true;
        }
        if (norm.Contains("开始反应") || norm.Contains("合成") || norm.Contains("反应"))
        {
            _ = BenchReactAsync();
            return true;
        }
        if (HasBuildVerb(norm))
        {
            _ = BeginConfirmAsync(raw, confidence);
            return true;
        }
        return false;
    }

    // ---------------- 流程控制 ----------------

    void Wake()
    {
        Pet.SetState(PetState.Awake); // 全息分子独立于智能球，不受状态切换影响
        wakeDeadline = Time.time + listenTimeout;
        StartListenWindow();
        tts?.Speak("在的");
        if (ui != null)
        {
            ui.SetStatus("在的");
            ui.SetHint(tray.Count > 0
                ? $"说“构建 某分子”继续上台（{Mathf.CeilToInt(listenTimeout)} 秒）；说「清空」重置"
                : "说“构建 水分子”或直接说物质名");
        }
    }

    /// <summary>解析用户输入为候选物质并弹出确认卡片（解决 STT 不准：由用户点选/输入纠错）</summary>
    async Task BeginConfirmAsync(string raw, float sttConfidence)
    {
        aiBusy = true;
        userAbort = false;
        EndListenWindow();
        if (tray.IsFull)
        {
            tts?.SpeakText("展示台已满，请先清空或移除一个分子");
            if (ui != null) ui.SetHint($"展示台已满 {tray.Count}/{MoleculeTray.MaxSlots}；点面板「清空展示台」或行尾 × 移除");
            GoIdle();
            return;
        }

        SetMic(false);
        chemUI.Hide();
        Pet.SetState(PetState.Thinking); // 思考态宇宙场景（全息分子独立展示，不受影响）
        ResetProgress();
        if (ui != null)
        {
            ui.SetStatus("正在解析化学物质…");
            ui.SetHint($"「{raw}」");
        }

        if (string.IsNullOrEmpty(apiKey))
        {
            tts?.Speak("请先配置大模型密钥");
            BackToListen("未配置 GLM API Key", "请在 Assets/StreamingAssets/glm_key.txt 填入密钥");
            return;
        }

        List<ChemCandidate> cands = null;
        try
        {
            cands = await ChemistryLLM.InterpretAsync(apiKey, glmModel, raw, sttConfidence, HintProgress);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 化学解析异常: {e.Message}");
        }

        if (userAbort) { AbortToListen(); return; } // 用户中止：静默退出

        Pet.SetState(PetState.Awake);
        if (cands == null) cands = new List<ChemCandidate>();

        awaitingConfirm = true;
        confirmDeadline = Time.time + confirmTimeout;
        chemUI.Show(raw, cands);
        if (ui != null)
        {
            ui.SetStatus(cands.Count > 0 ? "请点选候选物质" : "未识别出化学物质");
            ui.SetHint("选中后放上展示台；也可手动输入或重说");
        }
        // aiBusy 保持 true：卡片期间抑制语音（操作走手柄/键盘）
    }

    /// <summary>候选选中 → 解析结构 → 全息台飞入缩小版分子，智能球回归待机</summary>
    async Task ResolveAndAddToTrayAsync(ChemCandidate cand)
    {
        awaitingConfirm = false;
        chemUI.Hide();
        SetMic(false);
        Pet.SetState(PetState.Thinking);
        userAbort = false;
        ResetProgress();
        if (ui != null)
        {
            ui.SetStatus("正在获取分子结构…");
            ui.SetHint(cand.DisplayName());
        }

        var mol = await ResolveMoleculeAsync(cand);
        if (mol == null)
        {
            if (userAbort) { AbortToListen(); return; } // 用户中止：静默退出
            tts?.SpeakText("没有找到该物质的结构");
            BackToListen("结构获取失败", $"展示台保留 {tray.Count}/{MoleculeTray.MaxSlots} 个分子；请换个名称重试");
            return;
        }

        if (!tray.TryAdd(mol))
        {
            tts?.SpeakText("展示台已满，请先清空或移除一个分子");
            GoIdle();
            return;
        }
        // TryAdd 触发 TrayChanged → 全息台/舞台已同步；智能球回归待机球体
        Pet.SetState(PetState.Idle);

        string tag = mol.fromCache ? "（本地缓存）"
            : mol.source == MoleculeSource.GLM ? "（AI 估算，未经校验）" : "（PubChem 校验）";
        // 生成完成即回待机：不再连续监听，下一句需重新说「你好」唤醒
        aiBusy = false;
        GoIdle();
        if (ui != null)
        {
            ui.SetStatus($"已上台：{mol.DisplayName()} {mol.formula}{tag} ✓");
            ui.SetHint($"展示台 {tray.Count}/{MoleculeTray.MaxSlots}；已回待机，说「你好」继续添加");
        }
    }

    /// <summary>三级解析：本地缓存 → PubChem → GLM 兜底（统一入口，带 UI 过程提示）</summary>
    async Task<Molecule> ResolveMoleculeAsync(ChemCandidate cand)
    {
        try
        {
            return await ChemistryLLM.ResolveAsync(cand, apiKey, glmModel, s => { if (ui != null) ui.SetHint(s); });
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 结构解析异常: {e.Message}");
            return null;
        }
    }

    // ---------------- 展示台事件 ----------------

    void OnTrayAddRequested()
    {
        if (aiBusy) return;
        Wake();
        if (ui != null) ui.SetHint("请说分子名，或再点“手动输入”用键盘");
    }

    /// <summary>面板「开始反应」按钮 → 反应流程</summary>
    void OnBenchReactRequested() => _ = BenchReactAsync();

    /// <summary>台上分子被移除/添加/清空：反应物变化使旧反应结果失效，舞台回到渐进模式</summary>
    void OnTrayChanged()
    {
        System.Array.Clear(productSlots, 0, productSlots.Length); // 产物失效
        SyncStage(Mathf.Clamp(tray.Count + 1, 1, EquationBench.ReactantSlots), 0, false);

        if (tray.Count > 0)
        {
            if (listenWindow) listenDeadline = Time.time + listenTimeout;
            if (!aiBusy && ui != null)
                ui.SetStatus($"展示台更新：{tray.Count}/{MoleculeTray.MaxSlots} 个分子");
        }
        else
        {
            EndListenWindow();
            if (!aiBusy)
            {
                SetGrammarMode(true);
                SetMic(true);
                SetIdleUI();
                if (ui != null) ui.SetHint("展示台已清空，说「你好」重新开始");
            }
        }
    }

    /// <summary>组合 8 槽数组（前 4 反应物 + 后 4 产物）并同步全息台与舞台</summary>
    Molecule[] CombinedSlots()
    {
        var arr = new Molecule[EquationBench.TotalSlots];
        var r = tray.MoleculesBySlot();
        for (int i = 0; i < EquationBench.ReactantSlots && i < r.Length; i++) arr[i] = r[i];
        for (int j = 0; j < EquationBench.ProductSlots; j++)
            arr[EquationBench.ReactantSlots + j] = productSlots[j];
        return arr;
    }

    /// <summary>同步全息分子、相框占据/名字与渐进形态</summary>
    void SyncStage(int reactantFrames, int productFrames, bool equals)
    {
        var combined = CombinedSlots();
        holo.SetMolecules(combined);
        bench.SetFilled(combined);
        bench.SetStage(reactantFrames, productFrames, equals);
    }

    /// <summary>开始反应：GLM 分析 → 「=」出现 → 产物逐个解析上舞台</summary>
    async Task BenchReactAsync()
    {
        if (aiBusy) return;
        if (tray.Count < 2)
        {
            tts?.SpeakText("反应至少需要两种反应物");
            if (ui != null) ui.SetHint($"当前 {tray.Count} 种反应物，至少需要 2 种");
            return;
        }

        aiBusy = true;
        EndListenWindow();
        SetMic(false);
        Pet.SetState(PetState.Thinking);
        userAbort = false;
        ResetProgress();
        if (ui != null)
        {
            ui.SetStatus("正在分析反应…");
            ui.SetHint("GLM 推断方程式与产物");
        }

        ReactionResult r = null;
        try
        {
            r = await ChemistryLLM.ReactAsync(apiKey, glmModel, tray.Candidates(), HintProgress);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[AIPet] 反应分析异常: {e.Message}");
        }

        if (r == null)
        {
            if (userAbort) { AbortToListen(); return; } // 用户中止：静默退出
            tts?.SpeakText("反应分析失败");
            BackToListen("反应分析失败", "请检查网络后重试");
            return;
        }

        tray.ShowResult(r);
        System.Array.Clear(productSlots, 0, productSlots.Length);

        if (!r.feasible)
        {
            tts?.SpeakText("该反应难以进行");
            BackToListen("该反应难以进行", "详见右侧面板说明");
            return;
        }

        // 「=」先出现，然后产物解析成功一个上台一个
        int nR = tray.Count; // 反应完成后反应物区不再显示待填空框
        SyncStage(nR, 0, true);
        if (ui != null) ui.SetStatus("正在获取产物结构…");

        int shown = 0;
        foreach (var pc in r.products)
        {
            if (shown >= EquationBench.ProductSlots) break;
            Molecule pm = null;
            try { pm = await ResolveMoleculeAsync(pc); }
            catch (Exception) { /* 解析失败保留 null 占位 */ }
            if (userAbort) break; // 用户中止：保留已上台的产物即止
            productSlots[shown++] = pm;
            SyncStage(nR, shown, true);
        }

        tts?.Speak("构建完成");
        // 反应完成即回待机
        aiBusy = false;
        GoIdle();
        if (ui != null)
        {
            ui.SetStatus($"反应完成：{r.equation}");
            ui.SetHint("产物已上台；已回待机，说「你好」继续，说「清空」重置展示台");
        }
    }

    /// <summary>右侧面板「键盘输入」：直接打开系统键盘（不依赖语音，首次使用也能添加分子）</summary>
    void OnTrayKeyboardRequested()
    {
        if (aiBusy) return;
        capture.End(); // 键盘输入期间停录音
        chemUI.OpenManualInput();
        if (ui != null) ui.SetHint("请在键盘输入物质名或分子式");
    }

    /// <summary>键盘关闭（取消/失焦且仍在聆听窗口时恢复录音）</summary>
    void OnKeyboardClosed(bool submitted)
    {
        if (submitted || aiBusy) return;
        // 键盘/失焦期间麦克风可能被系统杀掉：先确保在录，再恢复命令采集
        SetMic(true);
        if (listenWindow && !capture.Capturing && voice != null && voice.IsRecording)
            capture.Begin(voice);
    }

    // ---------------- 确认卡片回调 ----------------

    void OnCandidatePicked(ChemCandidate c)
    {
        if (c == null || !awaitingConfirm) return;
        _ = ResolveAndAddToTrayAsync(c);
    }

    void OnRespeak()
    {
        if (!awaitingConfirm) return;
        awaitingConfirm = false;
        chemUI.Hide();
        BackToListen("好的，请说物质名", "可直接说，如：水分子、甲烷");
    }

    void OnCancelCard()
    {
        if (!awaitingConfirm) return;
        chemUI.Hide();
        GoIdle();
    }

    void OnManualSubmitted(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // 确认卡片展示中（aiBusy 恒 true，不能一并拦截）：手动输入 = 换个名字重新解析
        if (awaitingConfirm)
        {
            awaitingConfirm = false;
            chemUI.Hide();
        }
        else if (aiBusy) return;
        _ = BeginConfirmAsync(text.Trim(), 1f);
    }

    /// <summary>「停止思考」按钮：中止在途 GLM 请求，流程静默退出回聆听</summary>
    void OnStopThinking()
    {
        if (!aiBusy || Pet == null || Pet.State != PetState.Thinking) return;
        userAbort = true;
        LLMClient.AbortAll();
        tts?.SpeakText("已停止");
        if (ui != null) ui.SetStatus("已停止思考");
    }

    /// <summary>用户中止后的统一退出（不报失败）</summary>
    void AbortToListen()
    {
        userAbort = false;
        BackToListen("已停止思考", "说「构建 某分子」重新开始");
    }

    // ---------------- 状态回退 ----------------

    /// <summary>回到唤醒聆听态（麦克风开、计时重置；全息分子保留）</summary>
    void BackToListen(string status, string hint)
    {
        Pet.SetState(PetState.Awake);
        wakeDeadline = Time.time + listenTimeout;
        aiBusy = false;
        awaitingConfirm = false;
        SetMic(true);
        StartListenWindow();
        if (ui != null)
        {
            ui.SetStatus(status);
            ui.SetHint(hint);
        }
    }

    /// <summary>回到待机球体（全息分子独立保留）</summary>
    void GoIdle()
    {
        EndListenWindow();
        SetGrammarMode(true);
        Pet.SetState(PetState.Idle);
        aiBusy = false;
        awaitingConfirm = false;
        SetMic(true);
        SetIdleUI();
    }

    void SetIdleUI()
    {
        if (ui == null) return;
        ui.SetStatus("待机中");
        ui.SetHeard("");
        ui.SetHint(tray.Count > 0
            ? $"展示台保留 {tray.Count}/{MoleculeTray.MaxSlots} 个分子；说「你好」继续添加"
            : "说“你好”唤醒我");
    }

    // ---------------- 思考进度（单调不回退） ----------------

    void ResetProgress()
    {
        shownPct = 0f;
        timeBase = 0f;
        lastElapsed = 0f;
        lastAttempt = 1;
    }

    /// <summary>流式思考进度 → UI 提示行（时间渐近爬升，答案期收尾；跨重试累计不倒退）</summary>
    void HintProgress(LLMClient.ThinkProgress p)
    {
        if (ui == null) return;

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
            pct = 88f + 11f * Mathf.Clamp01(p.answerChars / 900f);
        else
            pct = 88f * (totalT / (totalT + 15f));
        shownPct = Mathf.Max(shownPct, pct);

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

    /// <summary>模拟一句语音输入（编辑器/联调测试用，走完整化学确认流程）</summary>
    public void DebugSimulate(string transcript)
    {
        if (ui != null) ui.SetHeard(transcript);
        HandleText(transcript, Normalize(transcript), 1f);
    }
}
