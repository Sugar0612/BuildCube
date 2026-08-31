using System.Collections;
using UnityEngine;

/// <summary>
/// 宠物语音播报：
/// - 固定短语（在的/构建完成等）播放本地 WAV（Resources/AIPetVoice），任何设备都保证有声音；
/// - 动态文本回退 Android 系统 TTS（引擎可用时才生效）。
/// PICO 设备多数没有系统 TTS 引擎，所以固定短语绝不依赖系统 TTS。
/// </summary>
public class AndroidTTS : MonoBehaviour
{
    public static AndroidTTS Instance { get; private set; }

    [Range(0f, 1f)] public float volume = 1f;
    [Tooltip("语音是否按 3D 空间音效衰减（宠物在面前时更有方位感）")]
    public bool spatial = true;

    AudioSource audioSource;

    // 固定短语 → 音频剪辑（Resources/AIPetVoice/）
    static readonly (string key, string clip)[] Phrases =
    {
        ("在的", "wake"),
        ("构建完成", "built"),
        ("没听清", "notclear"),
        ("不知道", "unknown"),
    };

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;

        audioSource = gameObject.AddComponent<AudioSource>();
        audioSource.playOnAwake = false;
        audioSource.volume = volume;
        if (spatial)
        {
            audioSource.spatialBlend = 1f;       // 3D
            audioSource.rolloffMode = AudioRolloffMode.Linear;
            audioSource.minDistance = 1f;
            audioSource.maxDistance = 6f;
        }
    }

    /// <summary>
    /// 播报：已知短语播本地 WAV（保证有声）；其他文本尝试系统 TTS（可能无声）。
    /// </summary>
    public void Speak(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var (key, clip) in Phrases)
        {
            if (!text.Contains(key)) continue;
            var ac = Resources.Load<AudioClip>("AIPetVoice/" + clip);
            if (ac != null)
            {
                audioSource.PlayOneShot(ac, 1f);
                return;
            }
            Debug.LogWarning($"[PetVoice] 缺少语音剪辑: {clip}");
        }

        // 动态文本：回退系统 TTS（仅真机、引擎可用时）
        SpeakText(text);
    }

    /// <summary>Android 系统 TTS 播动态文本（编辑器无效果）。若设备无 TTS 引擎则静默。</summary>
    public void SpeakText(string text)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            using (var activity = new AndroidJavaClass("com.unity3d.player.UnityPlayer")
                   .GetStatic<AndroidJavaObject>("currentActivity"))
            using (var tts = new AndroidJavaObject("android.speech.tts.TextToSpeech", activity, null))
            {
                using (var locale = new AndroidJavaClass("java/util/Locale"))
                    tts.Call<AndroidJavaObject>("setLanguage", locale.GetStatic<AndroidJavaObject>("CHINA"));
                tts.Call<int>("speak", text, 0, null, "aipet_" + Time.frameCount);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[PetVoice] 系统 TTS 不可用: {e.Message}");
        }
#else
        Debug.Log($"[PetVoice] (编辑器跳过语音) {text}");
#endif
    }

    void Update()
    {
        if (audioSource != null && !Mathf.Approximately(audioSource.volume, volume))
            audioSource.volume = volume;
    }
}
