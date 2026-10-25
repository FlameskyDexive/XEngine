// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Build;

/// <summary>
/// Desktop specific build profile. This is used for builds targeting Windows, MacOSX and Linux.
/// Other platforms should inherit from PlatformBuildProfile and implement their own profile details.
/// </summary>
public class DesktopBuildProfile : PlatformBuildProfile
{
    public Type pipelineType => typeof(DesktopBuildPipeline);

    public override Type GetPipelineType()
    {
        return pipelineType;
    }

    // New profiles default to the host platform so a first build on a Mac/Linux workstation
    // does not silently cross-compile a Windows apphost that cannot run locally.
    public BuildTarget Platform = OperatingSystem.IsMacOS() ? BuildTarget.MacOS
        : OperatingSystem.IsLinux() ? BuildTarget.Linux
        : BuildTarget.Windows;
    public bool SelfContained = false;
    public bool PublishTrimmed = false;
    public int WindowWidth = 1280;
    public int WindowHeight = 720;

    /// <summary>macOS CPU architecture (Apple Silicon is the current dev host; Intel persists).</summary>
    public MacOSArchitecture MacOSArch = MacOSArchitecture.Arm64;

    public enum MacOSArchitecture
    {
        /// <summary>Apple Silicon (osx-arm64) — M1 and later.</summary>
        Arm64 = 0,
        /// <summary>Intel Macs (osx-x64).</summary>
        X64 = 1,
    }

    public string RuntimeIdentifier
    {
        get => Platform switch
        {
            BuildTarget.Linux => "linux-x64",
            BuildTarget.MacOS => MacOSArch == MacOSArchitecture.X64 ? "osx-x64" : "osx-arm64",
            _ => "win-x64",
        };
    }

    public override void OnGUI(Paper paper)
    {
        EditorGUI.SettingsRow(paper, "bld_platform", "Platform", () =>
            Origami.EnumDropdown(paper, "bld_platform_v", Platform,
                v =>
                {
                    Platform = v;
                    EditorRegistries.SaveSettings();
                    ScriptAssemblyManager.RequestRecompile();
                }).Show(), separator: false);

        if (Platform == BuildTarget.MacOS)
        {
            EditorGUI.SettingsRow(paper, "bld_macos_arch", "Architecture", () =>
                Origami.EnumDropdown(paper, "bld_macos_arch_v", MacOSArch,
                    v => { MacOSArch = v; EditorRegistries.SaveSettings(); }).Show(), separator: false);
        }

        EditorGUI.SettingsToggle(paper, "bld_selfcontained", "Self-Contained", SelfContained,
            v => { SelfContained = v; EditorRegistries.SaveSettings(); }, separator: false);

        EditorGUI.SettingsToggle(paper, "bld_trimmed", "Publish Trimmed", PublishTrimmed,
            v => { PublishTrimmed = v; EditorRegistries.SaveSettings(); }, separator: false);

        EditorGUI.SectionHeader(paper, "bld_window_h", "Window");

        EditorGUI.SettingsRow(paper, "bld_width", "Width", () =>
            Origami.NumericField<int>(paper, "bld_width_v", WindowWidth,
                v => { WindowWidth = Math.Max(320, v); EditorRegistries.SaveSettings(); })
                .Min(320).Show(), separator: false);

        EditorGUI.SettingsRow(paper, "bld_height", "Height", () =>
            Origami.NumericField<int>(paper, "bld_height_v", WindowHeight,
                v => { WindowHeight = Math.Max(240, v); EditorRegistries.SaveSettings(); })
                .Min(240).Show(), separator: false);
    }

    public override void ModifyDefines(List<string> defines)
    {
        defines.Add("XENGINE_DESKTOP");
        defines.Add(Platform switch
        {
            BuildTarget.Windows => "XENGINE_WINDOWS",
            BuildTarget.Linux => "XENGINE_LINUX",
            BuildTarget.MacOS => "XENGINE_MACOS",
            _ => "XENGINE_WINDOWS",
        });
    }

    public override void ToDefault()
    {
        Platform = OperatingSystem.IsMacOS() ? BuildTarget.MacOS
            : OperatingSystem.IsLinux() ? BuildTarget.Linux
            : BuildTarget.Windows;
        MacOSArch = MacOSArchitecture.Arm64;
        SelfContained = false;
        PublishTrimmed = false;
        WindowWidth = 1280;
        WindowHeight = 720;
    }
}
