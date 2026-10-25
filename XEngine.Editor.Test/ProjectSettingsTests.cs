// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Projects.Scripting;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Tests for project settings persistence (<see cref="ProjectSettingsRegistry"/> load/save).</summary>
public class ProjectSettingsTests : EditorTestHarness
{
    public ProjectSettingsTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    // Settings persist as Echo YAML: a saved value must survive a save/load round-trip.
    [Fact]
    public void SettingsSaveLoad_RoundTripsYaml()
    {
        EditorRegistries.GetSettings<GeneralSettings>().ProductName = "RoundTripped";
        EditorRegistries.SaveSettings();

        Assert.True(File.Exists(Path.Combine(Project.ProjectSettingsPath, "General.yaml")));

        EditorRegistries.GetSettings<GeneralSettings>().ProductName = "overwritten";
        EditorRegistries.OnProjectOpened();

        Assert.Equal("RoundTripped", EditorRegistries.GetSettings<GeneralSettings>().ProductName);
    }

    [Fact]
    public void GeneralSettings_DefaultProductName_IsXEngine()
    {
        Assert.Equal("XEngine", new GeneralSettings().ProductName);
    }

    [Theory]
    [InlineData("xengine.harmonyos", "XENGINE_HARMONYOS")]
    [InlineData("xengine.ios", "XENGINE_IOS")]
    [InlineData("xengine.web", "XENGINE_WEB")]
    public void EditorPlatformDefines_FollowSelectedBuildPipeline(string pipelineId, string expectedPlatformDefine)
    {
        BuildSettings settings = EditorRegistries.GetSettings<BuildSettings>();
        string originalPipelineId = settings.ActivePipelineId;
        try
        {
            settings.ActivePipelineId = pipelineId;

            string[] defines = ScriptCompiler.GetEditorPlatformDefines().Split(';');
            Assert.Contains(expectedPlatformDefine, defines);
            Assert.Contains("XENGINE_MOBILE", defines);
        }
        finally
        {
            settings.ActivePipelineId = originalPipelineId;
        }
    }
}
