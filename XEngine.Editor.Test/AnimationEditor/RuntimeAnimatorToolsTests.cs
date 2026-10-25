// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Linq;
using System.Reflection;

using ModelContextProtocol.Server;
using XEngine.Animation;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test.AnimationEditor;

/// <summary>★ M45 — Live animator MCP surface + preview scrub + state-name resolve.</summary>
public class RuntimeAnimatorToolsTests
{
    [Fact]
    public void McpTools_AdvertiseLiveAnimatorApi()
    {
        var names = typeof(EditorRuntimeTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n != null)
            .ToHashSet();
        Assert.Contains("runtime_animator_state", names);
        Assert.Contains("runtime_animator_set_parameter", names);
        Assert.Contains("runtime_animator_play", names);
        Assert.Contains("runtime_animation_preview", names);
    }

    [Fact]
    public void ResolveStateName_MatchesHashedState()
    {
        var c = new AnimatorController { DefaultStateName = "Idle" };
        c.States.Add(new AnimatorController.State { Name = "Idle" });
        c.States.Add(new AnimatorController.State { Name = "Walk" });
        int hash = AnimationNameHash.Hash("Walk");
        Assert.Equal("Walk", RuntimeAnimatorInspect.ResolveStateName(c, hash));
        Assert.Null(RuntimeAnimatorInspect.ResolveStateName(c, 123));
    }

    [Fact]
    public void ScrubTo_SetsPlaybackTime()
    {
        var clip = AnimationWindowBinding.CreateEmptyClip("Idle");
        clip.Duration = 1f;
        var win = new AnimationClipWindow();
        win.Bind(null, clip);
        win.ScrubTo(0.4f);
        Assert.Equal(0.4f, win.PlaybackTime, 3);
        win.ScrubTo(9f);
        Assert.Equal(1f, win.PlaybackTime, 3);
    }

    [Fact]
    public void SetParameter_FloatAndTrigger()
    {
        var author = new AnimatorController { Name = "c", DefaultStateName = "Idle" };
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Speed", Type = AnimatorParameterType.Float });
        author.Parameters.Add(new AnimatorController.Parameter { Name = "Jump", Type = AnimatorParameterType.Trigger });
        author.States.Add(new AnimatorController.State { Name = "Idle" });
        var go = new GameObject("Rig");
        var animator = go.AddComponent<Animator>();
        animator.SetControllerAsset(author);
        Assert.True(RuntimeAnimatorInspect.SetParameter(animator, "Speed", "Float", 0.5f));
        Assert.Equal(0.5f, animator.GetFloat("Speed"), 3);
        Assert.True(RuntimeAnimatorInspect.SetParameter(animator, "Jump", "Trigger", 1f));
        Assert.True(animator.GetBool("Jump"));
    }
}
