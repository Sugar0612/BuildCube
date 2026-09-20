using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// 命令行构建入口（Editor 菜单 + 批处理模式）：
/// Unity.exe -batchmode -quit -projectPath &lt;项目&gt; -executeMethod BuildScript.BuildAndroidApk -logFile build_log.txt
/// </summary>
public static class BuildScript
{
    const string OutputPath = "Build/BuildCube.apk";

    [MenuItem("Tools/构建 Android APK")]
    public static void MenuBuild() => BuildAndroidApk();

    public static void BuildAndroidApk()
    {
        // 输出路径可用命令行 -outputPath Build/xxx.apk 覆盖（版本化产物用）
        string output = OutputPath;
        var args = System.Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "-outputPath") output = args[i + 1];

        var scenes = EditorBuildSettings.scenes
            .Where(s => s.enabled && !string.IsNullOrEmpty(s.path))
            .Select(s => s.path)
            .ToArray();
        if (scenes.Length == 0) scenes = new[] { "Assets/Scenes/Main.unity" };

        Debug.Log($"[BuildScript] 开始构建 APK：scenes=[{string.Join(", ", scenes)}] → {output}");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = scenes,
            locationPathName = output,
            target = BuildTarget.Android,
            options = BuildOptions.None,
        });

        if (report.summary.result != BuildResult.Succeeded)
        {
            Debug.LogError($"[BuildScript] 构建失败: {report.summary.result}，错误 {report.summary.totalErrors} 个");
            EditorApplication.Exit(1);
            return;
        }

        Debug.Log($"[BuildScript] 构建成功: {report.summary.outputPath} ({report.summary.totalSize / 1048576} MB)");
        EditorApplication.Exit(0);
    }
}
