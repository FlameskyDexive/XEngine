// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Editor.AssetBundles.BuildPipeline;

using Xunit;

namespace XEngine.Editor.Test.AssetBundles;

/// <summary>
/// Tests for the programmable build-pipeline framework itself (independent of the concrete
/// AssetBundle tasks): task ordering, context-object passing, abort-on-failure, and custom-task
/// insertion. Models Unity's ScriptableBuildPipeline task-graph contract.
/// </summary>
public class AssetBundleBuildPipelineTests
{
    private sealed class TestContext : IBuildContextObject
    {
        public int Value;
        public List<string> Log = new();
    }

    private sealed class AddTenTask : IBuildTask
    {
        public int Version => 1;
        public string Name => "AddTen";
        public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
        {
            var c = context.Get<TestContext>();
            c.Value += 10;
            c.Log.Add("AddTen");
            return BuildReturnCode.Success;
        }
    }

    private sealed class FailTask : IBuildTask
    {
        public int Version => 1;
        public string Name => "Fail";
        public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
        {
            context.Get<TestContext>().Log.Add("Fail");
            return BuildReturnCode.Error;
        }
    }

    private sealed class NeverRunTask : IBuildTask
    {
        public int Version => 1;
        public string Name => "NeverRun";
        public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
        {
            context.Get<TestContext>().Log.Add("NeverRun");
            return BuildReturnCode.Success;
        }
    }

    private sealed class ThrowTask : IBuildTask
    {
        public int Version => 1;
        public string Name => "Throw";
        public BuildReturnCode Run(BuildContext context, IProgress<BuildTaskProgress>? progress = null)
            => throw new System.InvalidOperationException("boom");
    }

    [Fact]
    public void TasksRunInOrder_PassingContextThrough()
    {
        var context = new BuildContext();
        context.Set(new TestContext());

        var code = BuildPipelineRunner.Run(new List<IBuildTask> { new AddTenTask(), new AddTenTask(), new AddTenTask() }, context);

        Assert.Equal(BuildReturnCode.Success, code);
        Assert.Equal(30, context.Get<TestContext>().Value);
        Assert.Equal(new[] { "AddTen", "AddTen", "AddTen" }, context.Get<TestContext>().Log);
    }

    [Fact]
    public void FirstFailure_AbortsPipeline_NoLaterTasksRun()
    {
        var context = new BuildContext();
        context.Set(new TestContext());

        var code = BuildPipelineRunner.Run(new List<IBuildTask> { new AddTenTask(), new FailTask(), new NeverRunTask() }, context);

        Assert.Equal(BuildReturnCode.Error, code);
        Assert.Equal(10, context.Get<TestContext>().Value);
        Assert.Equal(new[] { "AddTen", "Fail" }, context.Get<TestContext>().Log);
    }

    [Fact]
    public void ThrowingTask_ConvertsToExceptionReturnCode_AbortsPipeline()
    {
        var context = new BuildContext();
        context.Set(new TestContext());

        var code = BuildPipelineRunner.Run(new List<IBuildTask> { new ThrowTask(), new NeverRunTask() }, context);

        Assert.Equal(BuildReturnCode.Exception, code);
        // NeverRun must not have executed.
        Assert.DoesNotContain("NeverRun", context.Get<TestContext>().Log);
    }

    [Fact]
    public void Context_TryGet_MissingType_ReturnsFalse()
    {
        var context = new BuildContext();
        Assert.False(context.TryGet<TestContext>(out _));
        Assert.False(context.Contains<TestContext>());
    }

    [Fact]
    public void Context_Get_MissingType_Throws()
    {
        var context = new BuildContext();
        Assert.Throws<System.InvalidOperationException>(() => context.Get<TestContext>());
    }

    [Fact]
    public void Context_Set_OverwritesPreviousValue()
    {
        var context = new BuildContext();
        context.Set(new TestContext { Value = 1 });
        context.Set(new TestContext { Value = 99 });
        Assert.Equal(99, context.Get<TestContext>().Value);
    }

    [Fact]
    public void DefaultAssetBundleTasks_Create_ProducesExpectedOrder()
    {
        var tasks = DefaultAssetBundleTasks.Create();
        var names = new List<string>();
        foreach (var t in tasks) names.Add(t.Name);
        Assert.Equal(new[] { "Collect", "ComputeDependency", "WriteBundles", "WriteManifest" }, names);
    }
}
