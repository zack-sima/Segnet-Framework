using UnityEditor;
using UnityEditor.Build.Reporting;

public static class BuildScript
{
    public static void PerformBuild()
    {
        var options = new BuildPlayerOptions
        {
            scenes = new[]
            {
                "Assets/Scenes/Sample Menu Scene.unity",
                "Assets/Scenes/Sample Game Scene.unity",
            },
            locationPathName = "Builds/macOS/Segnet-Test.app",
            target = BuildTarget.StandaloneOSX,
            options = BuildOptions.None
        };

        BuildReport report = BuildPipeline.BuildPlayer(options);

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new System.Exception("macOS build failed");
        }
    }
}
