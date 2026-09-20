using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// 命令语音采集器：复用 VoiceProcessor 的麦克风帧（不另开 Microphone，避免设备独占冲突），
/// 做能量检测——检测到说话、随后静默超过阈值即判定一句话结束，裁剪静音后编码 16k/16bit
/// 单声道 WAV 交给云端识别（GLMASR）。
/// </summary>
public class VoiceCapture : MonoBehaviour
{
    [Tooltip("判定说话结束的静默时长（秒）")]
    public float silenceEnd = 1.1f;
    [Tooltip("最短说话时长（秒），低于此视为误触发，静默丢弃")]
    public float minSpeech = 0.25f;
    [Tooltip("单句最长录制时长（秒）")]
    public float maxSeconds = 9f;
    [Tooltip("能量阈值（16-bit PCM 的帧 RMS），低于视为静默")]
    public float rmsThreshold = 700f;

    public bool Capturing { get; private set; }

    /// <summary>一句话结束，参数为 WAV 字节（主线程回调）</summary>
    public event Action<byte[]> OnWavReady;

    VoiceProcessor source;
    readonly List<short[]> frames = new List<short[]>();
    readonly List<float> frameRms = new List<float>();
    int sampleRate = 16000;
    bool speechStarted;
    float speechTime;
    float silenceHold;
    float captureT;

    const int FrameSamples = 512;

    /// <summary>开始采集（须在麦克风已开启时调用；重复调用忽略）</summary>
    public void Begin(VoiceProcessor processor)
    {
        if (Capturing || processor == null) return;
        source = processor;
        source.OnFrameCaptured += OnFrame;
        frames.Clear();
        frameRms.Clear();
        speechStarted = false;
        speechTime = 0f;
        silenceHold = 0f;
        captureT = 0f;
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

        double sum = 0;
        for (int i = 0; i < frame.Length; i++) sum += (double)frame[i] * frame[i];
        float rms = Mathf.Sqrt((float)(sum / frame.Length));

        frames.Add(frame);
        frameRms.Add(rms);
        captureT += (float)frame.Length / sampleRate;

        if (rms > rmsThreshold)
        {
            if (!speechStarted) speechStarted = true;
            speechTime += (float)frame.Length / sampleRate;
            silenceHold = 0f;
        }
        else if (speechStarted)
        {
            silenceHold += (float)frame.Length / sampleRate;
        }

        bool sentenceDone = speechStarted && silenceHold >= silenceEnd;
        bool capped = captureT >= maxSeconds;
        bool tooShortNoise = speechStarted && silenceHold >= silenceEnd && speechTime < minSpeech;
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

    /// <summary>裁剪首尾静音后编码 WAV（16kHz 单声道 16-bit）</summary>
    byte[] EncodeWav()
    {
        if (frames.Count == 0) return null;

        int first = -1, last = -1;
        for (int i = 0; i < frameRms.Count; i++)
            if (frameRms[i] > rmsThreshold) { if (first < 0) first = i; last = i; }
        if (first < 0) return null;

        int from = Mathf.Max(0, first - 8);   // 前留 ~0.25s
        int to = Mathf.Min(frames.Count - 1, last + 6); // 后留 ~0.2s
        int total = 0;
        for (int i = from; i <= to; i++) total += frames[i].Length;
        if (total == 0) return null;

        using (var ms = new MemoryStream(44 + total * 2))
        using (var w = new BinaryWriter(ms))
        {
            w.Write(0x46464952);              // RIFF
            w.Write(36 + total * 2);
            w.Write(0x45564157);              // WAVE
            w.Write(0x20746D66);              // fmt
            w.Write(16);
            w.Write((short)1);                // PCM
            w.Write((short)1);                // mono
            w.Write(sampleRate);
            w.Write(sampleRate * 2);          // byte rate
            w.Write((short)2);                // block align
            w.Write((short)16);               // bits
            w.Write(0x61746164);              // data
            w.Write(total * 2);
            for (int i = from; i <= to; i++)
            {
                var f = frames[i];
                for (int j = 0; j < f.Length; j++)
                    w.Write(f[j]);
            }
            w.Flush();
            return ms.ToArray();
        }
    }
}
