/* * * * *
 * A unity voice processor
 * ------------------------------
 * 
 * A Unity script for recording and delivering frames of audio for real-time processing
 * 
 * Written by Picovoice 
 * 2021-02-19
 * 
 * Apache License
 * 
 * Copyright (c) 2021 Picovoice
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 *   you may not use this file except in compliance with the License.
 *   You may obtain a copy of the License at
 *   
 *   http://www.apache.org/licenses/LICENSE-2.0
 *   
 *   Unless required by applicable law or agreed to in writing, software
 *   distributed under the License is distributed on an "AS IS" BASIS,
 *   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 *   See the License for the specific language governing permissions and
 *   limitations under the License.
 * 
 * * * * */
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// Class that records audio and delivers frames for real-time audio processing
/// </summary>
public class VoiceProcessor : MonoBehaviour
{
    /// <summary>
    /// Indicates whether microphone is capturing or not
    /// </summary>
    public bool IsRecording
    {
        get { return _audioClip != null && Microphone.IsRecording(CurrentDeviceName); }
    }

    [SerializeField] private int MicrophoneIndex;

    /// <summary>
    /// Sample rate of recorded audio
    /// </summary>
    public int SampleRate { get; private set; }

    /// <summary>
    /// Size of audio frames that are delivered
    /// </summary>
    public int FrameLength { get; private set; }

    /// <summary>
    /// Event where frames of audio are delivered
    /// </summary>
    public event Action<short[]> OnFrameCaptured;

    /// <summary>
    /// Event when audio capture thread stops
    /// </summary>
    public event Action OnRecordingStop;

    /// <summary>
    /// Event when audio capture thread starts
    /// </summary>
    public event Action OnRecordingStart;

    /// <summary>
    /// Available audio recording devices
    /// </summary>
    public List<string> Devices { get; private set; }

    /// <summary>
    /// Index of selected audio recording device
    /// </summary>
    public int CurrentDeviceIndex { get; private set; }

    /// <summary>
    /// Name of selected audio recording device
    /// </summary>
    public string CurrentDeviceName
    {
        get
        {
            if (CurrentDeviceIndex < 0 || CurrentDeviceIndex >= Microphone.devices.Length)
                return string.Empty;
            return Devices[CurrentDeviceIndex];
        }
    }

    [Header("Voice Detection Settings")]
    [SerializeField, Tooltip("The minimum volume to detect voice input for"), Range(0.0f, 1.0f)]
    private float _minimumSpeakingSampleValue = 0.05f;

    [SerializeField, Tooltip("Time in seconds of detected silence before voice request is sent")]
    private float _silenceTimer = 1.0f;

    [SerializeField, Tooltip("Auto detect speech using the volume threshold.")]
    private bool _autoDetect;

    private float _timeAtSilenceBegan;
    private bool _audioDetected;
    private bool _didDetect;
    private bool _transmit;


    AudioClip _audioClip;
    private event Action RestartRecording;

    void Awake()
    {
        UpdateDevices();
    }
#if UNITY_EDITOR
    void Update()
    {
        if (CurrentDeviceIndex != MicrophoneIndex)
        {
            ChangeDevice(MicrophoneIndex);
        }
    }
#endif

    /// <summary>
    /// Updates list of available audio devices
    /// </summary>
    public void UpdateDevices()
    {
        Devices = new List<string>();
        foreach (var device in Microphone.devices)
            Devices.Add(device);

        if (Devices == null || Devices.Count == 0)
        {
            CurrentDeviceIndex = -1;
            Debug.LogError("There is no valid recording device connected");
            return;
        }

        CurrentDeviceIndex = MicrophoneIndex;
    }

    /// <summary>
    /// Change audio recording device
    /// </summary>
    /// <param name="deviceIndex">Index of the new audio capture device</param>
    public void ChangeDevice(int deviceIndex)
    {
        if (deviceIndex < 0 || deviceIndex >= Devices.Count)
        {
            Debug.LogError(string.Format("Specified device index {0} is not a valid recording device", deviceIndex));
            return;
        }

        if (IsRecording)
        {
            // one time event to restart recording with the new device 
            // the moment the last session has completed
            RestartRecording += () =>
            {
                CurrentDeviceIndex = deviceIndex;
                StartRecording(SampleRate, FrameLength);
                RestartRecording = null;
            };
            StopRecording();
        }
        else
        {
            CurrentDeviceIndex = deviceIndex;
        }
    }

    /// <summary>
    /// Start recording audio
    /// </summary>
    /// <param name="sampleRate">Sample rate to record at</param>
    /// <param name="frameSize">Size of audio frames to be delivered</param>
    /// <param name="autoDetect">Should the audio continuously record based on the volume</param>
    public void StartRecording(int sampleRate = 16000, int frameSize = 512, bool ?autoDetect = null)
    {
        if (autoDetect != null)
        {
            _autoDetect = (bool) autoDetect;
        }

        if (IsRecording)
        {
            // if sample rate or frame size have changed, restart recording
            if (sampleRate != SampleRate || frameSize != FrameLength)
            {
                RestartRecording += () =>
                {
                    StartRecording(SampleRate, FrameLength, autoDetect);
                    RestartRecording = null;
                };
                StopRecording();
            }

            return;
        }

        // 僵尸会话清理：失焦/系统键盘遮挡时安卓可能静默终止录音，旧 clip 残留但不再产出数据。
        // 不先显式 End 的话，安卓可能拒绝再次开启麦克风（表现为再也识别不到语音）。
        if (_audioClip != null)
        {
            Microphone.End(CurrentDeviceName);
            Destroy(_audioClip);
            _audioClip = null;
        }

        SampleRate = sampleRate;
        FrameLength = frameSize;

        _audioClip = Microphone.Start(CurrentDeviceName, true, 1, sampleRate);
        if (_audioClip == null)
        {
            Debug.LogError("[VoiceProcessor] Microphone.Start 失败（权限被拒或设备被占用），稍后由看门狗重试");
            return;
        }

        StartCoroutine(RecordData());
    }

    /// <summary>
    /// Stops recording audio
    /// </summary>
    public void StopRecording()
    {
        bool wasRecording = IsRecording;
        if (!wasRecording && _audioClip == null) return;

        // 无论是否系统判定为录音中，只要 clip 存在就显式 End + Destroy，
        // 确保失焦/系统杀死后设备被释放，且 RestartRecording 能触发
        Microphone.End(CurrentDeviceName);
        Destroy(_audioClip);
        _audioClip = null;
        _didDetect = false;

        StopCoroutine(RecordData());

        // 必须触发：让等待换设备/换参数的 RestartRecording 回调执行
        if (wasRecording && OnRecordingStop != null)
            OnRecordingStop.Invoke();
        if (RestartRecording != null)
        {
            RestartRecording.Invoke();
            RestartRecording = null;
        }
    }

    /// <summary>
    /// Loop for buffering incoming audio data and delivering frames
    /// </summary>
    IEnumerator RecordData()
    {
        float[] sampleBuffer = new float[FrameLength];
        int startReadPos = 0;

        if (OnRecordingStart != null)
            OnRecordingStart.Invoke();

        while (IsRecording)
        {
            int curClipPos = Microphone.GetPosition(CurrentDeviceName);
            if (curClipPos < startReadPos)
                curClipPos += _audioClip.samples;

            int samplesAvailable = curClipPos - startReadPos;
            if (samplesAvailable < FrameLength)
            {
                yield return null;
                continue;
            }

            int endReadPos = startReadPos + FrameLength;
            if (endReadPos > _audioClip.samples)
            {
                // fragmented read (wraps around to beginning of clip)
                // read bit at end of clip
                int numSamplesClipEnd = _audioClip.samples - startReadPos;
                float[] endClipSamples = new float[numSamplesClipEnd];
                _audioClip.GetData(endClipSamples, startReadPos);

                // read bit at start of clip
                int numSamplesClipStart = endReadPos - _audioClip.samples;
                float[] startClipSamples = new float[numSamplesClipStart];
                _audioClip.GetData(startClipSamples, 0);

                // combine to form full frame
                Buffer.BlockCopy(endClipSamples, 0, sampleBuffer, 0, numSamplesClipEnd);
                Buffer.BlockCopy(startClipSamples, 0, sampleBuffer, numSamplesClipEnd, numSamplesClipStart);
            }
            else
            {
                _audioClip.GetData(sampleBuffer, startReadPos);
            }

            startReadPos = endReadPos % _audioClip.samples;
            if (_autoDetect == false)
            {
                _transmit =_audioDetected = true;
            }
            else
            {
                float maxVolume = 0.0f;

                for (int i = 0; i < sampleBuffer.Length; i++)
                {
                    if (sampleBuffer[i] > maxVolume)
                    {
                        maxVolume = sampleBuffer[i];
                    }
                }

                if (maxVolume >= _minimumSpeakingSampleValue)
                {
                    _transmit= _audioDetected = true;
                    _timeAtSilenceBegan = Time.time;
                }
                else
                {
                    _transmit = false;

                    if (_audioDetected && Time.time - _timeAtSilenceBegan > _silenceTimer)
                    {
                        _audioDetected = false;
                    }
                }
            }

            if (_audioDetected)
            {
                _didDetect = true;
                // converts to 16-bit int samples
                short[] pcmBuffer = new short[sampleBuffer.Length];
                for (int i = 0; i < FrameLength; i++)
                {
                    pcmBuffer[i] = (short) Math.Floor(sampleBuffer[i] * short.MaxValue);
                }

                // raise buffer event
                if (OnFrameCaptured != null && _transmit)
                    OnFrameCaptured.Invoke(pcmBuffer);
            }
            else
            {
                if (_didDetect)
                {
                    if (OnRecordingStop != null)
                        OnRecordingStop.Invoke();
                    _didDetect = false;
                }
            }
        }


        if (OnRecordingStop != null)
            OnRecordingStop.Invoke();
        if (RestartRecording != null)
            RestartRecording.Invoke();
    }
}
