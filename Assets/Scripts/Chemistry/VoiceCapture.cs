using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 命令语音采集器：复用 VoiceProcessor 的麦克风帧（不另开 Microphone，避免设备独占冲突）。
/// STT 准确率优化：
/// - 开头 ~0.4 秒自动标定环境噪声，静默阈值按噪声自适应抬高（吵环境不误触发、静环境不丢音）；
/// - TTS 播报期间的麦克风帧整段丢弃（扬声器自听是云端误识别的主要来源）；
/// - 首尾保留更长缓冲（前 0.35s / 后 0.45s），词语不被截断；
/// - 编码前对过小的音量做增益归一化（峰值提升到 ~0.46 满幅），云端识别不再吃"小声"亏。
/// 检测到说话、随后静默超过阈值即判定一句话结束，裁剪静音后编码 16k/16bit 单声道 WAV。
/// </summary>
public class VoiceCapture : MonoBehaviour
{
    [Tooltip("判定说话结束的静默时长（秒）")]
    public float silenceEnd = 1.35f;
    [Tooltip("最短说话时长（秒），低于此视为误触发，静默丢弃")]
    public float minSpeech = 0.3f;
    [Tooltip("单句最长录制时长（秒）")]
    public float maxSeconds = 12f;
    [Tooltip("能量阈值基数（16-bit PCM 帧 RMS）：实际阈值 = max(基数, 环境噪声×倍数)；偏高会截掉轻声音节导致识别残缺")]
    public float rmsThreshold = 550f;
    [Tooltip("噪声自适应倍数：阈值 = 噪声底 × 此倍数")]
    public float noiseMultiplier = 2.4f;
    [Tooltip("开始采集后丢弃的时长（秒）：跳过唤醒/构建完成 TTS 的尾音，防止自听误识别")]
    public float startupDiscard = 0.8f;

    public bool Capturing { get; private set; }

    /// <summary>一句话结束，参数为 WAV 字节（主线程回调）</summary>
    public event Action<byte[]> OnWavReady;

    VoiceProcessor source;
    readonly List<short[]> frames = new List<short[]>();
    readonly List<float> frameRms = new List<float>();
    readonly List<float> calibSamples = new List<float>();
    int sampleRate = 16000;
    bool speechStarted;
    float speechTime;
    float silenceHold;
    float captureT;
    float effectiveThreshold = 700f;
    float discardT;
    bool calibrating;

    const int FrameSamples = 512;
    const int CalibFrameCount = 12; // ~0.38s 环境噪声标定
    const float MaxAdaptiveThreshold = 1200f; // 自适应静默阈值上限（防吵环境把说话也判成静默）
    const float MinAdaptiveThreshold = 350f;  // 自适应静默阈值下限（防极静环境阈值过低误触发）

    /// <summary>开始采集（须在麦克风已开启时调用；重复调用忽略）</summary>
    public void Begin(VoiceProcessor processor)
    {
        if (Capturing || processor == null) return;
        source = processor;
        source.OnFrameCaptured += OnFrame;
        frames.Clear();
        frameRms.Clear();
        calibSamples.Clear();
        speechStarted = false;
        speechTime = 0f;
        silenceHold = 0f;
        captureT = 0f;
        discardT = startupDiscard;
        calibrating = true;
        effectiveThreshold = rmsThreshold;
        Capturing = true;
    }

    /// <summary>停止采集并丢弃已录内容</summary>
    public void End()
    {
        if (!Capturing) return;
        Capturing = false;
        if (source != null) source.OnFrameCaptured -= OnFrame;
        frames.Clear();
        frameRms.Clear();
    }

    /// <summary>VoiceProcessor 在主线程逐帧回调（512 样本 @16kHz）</summary>
    void OnFrame(short[] frame)
    {
        if (!Capturing || frame == null || frame.Length == 0) return;

        float frameSec = (float)frame.Length / sampleRate;

        // TTS 尾音保护：采集刚开始的短窗口内音频直接丢弃（宠物播报"在的/构建完成"的自听）
        if (discardT > 0f)
        {
            discardT -= frameSec;
            return;
        }

        // TTS 播报期间的帧整段丢弃：扬声器自听会污染命令语音，云端把播报声也识别进去
        if (AndroidTTS.Instance != null && AndroidTTS.Instance.IsPlaying) return;

        double sum = 0;
        for (int i = 0; i < frame.Length; i++) sum += (double)frame[i] * frame[i];
        float rms = Mathf.Sqrt((float)(sum / frame.Length));

        // 环境噪声标定：开头几帧（无人说话时）估计噪声底
        if (calibrating)
        {
            if (rms > rmsThreshold * 1.8f) calibrating = false; // 标定期已有说话：直接用基数
            else
            {
                calibSamples.Add(rms);
                if (calibSamples.Count >= CalibFrameCount)
                {
                    float noise = 0f;
                    foreach (var s in calibSamples) noise += s;
                    noise /= calibSamples.Count;
                    // 上下限钳制：噪声底 × 倍数在吵环境会抬得过高，正常说话也过不了阈值 → 彻底识别不到；
                    // 极静环境又可能压得过低，导致呼吸声/电流声也触发说话判定
                    effectiveThreshold = Mathf.Clamp(
                        Mathf.Max(rmsThreshold, noise * noiseMultiplier),
                        MinAdaptiveThreshold, MaxAdaptiveThreshold);
                    calibrating = false;
                    Debug.Log($"[VoiceCapture] 噪声底 {noise:F0} → 静默阈值 {effectiveThreshold:F0}");
                }
            }
        }

        frames.Add(frame);
        frameRms.Add(rms);
        captureT += frameSec;

        if (rms > effectiveThreshold)
        {
            if (!speechStarted) speechStarted = true;
            speechTime += frameSec;
            silenceHold = 0f;
        }
        else if (speechStarted)
        {
            silenceHold += frameSec;
        }

        bool sentenceDone = speechStarted && silenceHold >= silenceEnd;
        bool capped = captureT >= maxSeconds;
        if (capped && !speechStarted)
        {
            // 整段都没检测到说话（误开窗/纯环境噪声）：丢弃重采，不产生空上传
            frames.Clear();
            frameRms.Clear();
            speechTime = 0f;
            silenceHold = 0f;
            captureT = 0f;
            return;
        }
        bool tooShortNoise = sentenceDone && speechTime < minSpeech;
        if (sentenceDone || capped)
        {
            if (tooShortNoise)
            {
                // 误触发：丢弃，继续采
                frames.Clear();
                frameRms.Clear();
                speechStarted = false;
                speechTime = 0f;
                silenceHold = 0f;
                captureT = 0f;
                return;
            }
            Finish();
        }
    }

    void Finish()
    {
        var wav = EncodeWav();
        End();
        if (wav != null) OnWavReady?.Invoke(wav);
    }

    /// <summary>裁剪首尾静音 → 增益归一化 → 编码 WAV（16kHz 单声道 16-bit）</summary>
    byte[] EncodeWav()
    {
        if (frames.Count == 0) return null;

        int first = -1, last = -1;
        for (int i = 0; i < frameRms.Count; i++)
            if (frameRms[i] > effectiveThreshold) { if (first < 0) first = i; last = i; }
        if (first < 0) return null;

        int from = Mathf.Max(0, first - 11);   // 前留 ~0.35s（词语开头不截断）
        int to = Mathf.Min(frames.Count - 1, last + 14); // 后留 ~0.45s（词尾轻声母不被截断）

        // 拼接样本
        int total = 0;
        for (int i = from; i <= to; i++) total += frames[i].Length;
        if (total == 0) return null;
        var samples = new short[total];
        int w = 0;
        for (int i = from; i <= to; i++)
        {
            var f = frames[i];
            for (int j = 0; j < f.Length; j++) samples[w++] = f[j];
        }

        // 增益归一化：过小的音量提升到峰值 ~15000（仅放大，不衰减；防削顶钳制）
        short peak = 1;
        for (int i = 0; i < samples.Length; i++)
        {
            int a = Math.Abs(samples[i]);
            if (a > peak) peak = (short)a;
        }
        if (peak < 10000)
        {
            float gain = Mathf.Min(15000f / peak, 8f);
            for (int i = 0; i < samples.Length; i++)
                samples[i] = (short)Mathf.Clamp(samples[i] * gain, short.MinValue, short.MaxValue);
        }

        using (var ms = new MemoryStream(44 + total * 2))
        using (var bw = new BinaryWriter(ms))
        {
            bw.Write(0x46464952);              // RIFF
            bw.Write(36 + total * 2);
            bw.Write(0x45564157);              // WAVE
            bw.Write(0x20746D66);              // fmt
            bw.Write(16);
            bw.Write((short)1);                // PCM
            bw.Write((short)1);                // mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);          // byte rate
            bw.Write((short)2);                // block align
            bw.Write((short)16);               // bits
            bw.Write(0x61746164);              // data
            bw.Write(total * 2);
            for (int i = 0; i < samples.Length; i++) bw.Write(samples[i]);
            bw.Flush();
            return ms.ToArray();
        }
    }
}
