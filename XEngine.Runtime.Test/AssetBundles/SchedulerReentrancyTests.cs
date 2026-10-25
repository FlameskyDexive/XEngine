using System;
using XEngine.Runtime.AssetBundles;
using Xunit;

namespace XEngine.Runtime.Test.AssetBundles;

public sealed class SchedulerReentrancyTests : IDisposable
{
    public SchedulerReentrancyTests() => AsyncOperationSystem.Clear();
    public void Dispose() => AsyncOperationSystem.Clear();

    [Fact]
    public void NestedSynchronousLoad_DoesNotReenterOuterOperationOrLoseSnapshot()
    {
        var outer = new NestedLoad();
        var sibling = new CompleteOnce();
        AsyncOperationSystem.StartOperation(outer);
        AsyncOperationSystem.StartOperation(sibling);
        AsyncOperationSystem.Update();
        Assert.Equal(EOperationStatus.Succeeded, outer.Status);
        Assert.Equal(1, outer.Updates);
        Assert.Equal(1, sibling.Updates);
        Assert.True(sibling.IsDone);
    }

    private sealed class CompleteOnce : AsyncOperationBase
    {
        public int Updates;
        protected override void InternalStart() { }
        protected override void InternalUpdate() { Updates++; SetResult(); }
    }

    private sealed class NestedLoad : AsyncOperationBase
    {
        public int Updates;
        protected override void InternalStart() { }
        protected override void InternalUpdate()
        {
            if (++Updates != 1) { SetError("Outer load was reentered"); return; }
            var dependency = new CompleteOnce();
            AsyncOperationSystem.StartOperation(dependency);
            dependency.WaitForCompletion();
            if (Status == EOperationStatus.Failed) return;
            if (dependency.Status == EOperationStatus.Succeeded) SetResult();
            else SetError(dependency.Error!);
        }
    }
}
