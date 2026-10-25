// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using XEngine.Async;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>Tests for the unmanaged-core struct that backs every single-use XTask source.</summary>
public class XTaskCompletionSourceCoreTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ConcurrentRegistrationAndCompletion_InvokesExactlyOnce(int completionKind)
    {
        const int iterations = 100_000;
        using var barrier = new Barrier(2);
        using var workerDone = new ManualResetEventSlim();
        var race = new CompletionRace();
        Exception? registrationFailure = null;
        Exception? completionFailure = null;
        int missingCallbacks = 0;
        var failure = new FormatException("race marker");

        // Dedicated test-harness work, independent of the XTask core under test.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                for (int i = 0; i < iterations; i++)
                {
                    barrier.SignalAndWait();
                    try
                    {
                        race.Core.OnCompleted(static state =>
                            Interlocked.Increment(ref ((CompletionRace)state).Callbacks), race, 0);
                    }
                    catch (Exception ex)
                    {
                        registrationFailure ??= ex;
                    }
                    barrier.SignalAndWait();
                }
            }
            finally
            {
                workerDone.Set();
            }
        });

        for (int i = 0; i < iterations; i++)
        {
            race.Core = default;
            race.Callbacks = 0;
            barrier.SignalAndWait();
            try
            {
                switch (completionKind)
                {
                    case 0: race.Core.TrySetResult(42); break;
                    case 1: race.Core.TrySetException(failure); break;
                    case 2: race.Core.TrySetCanceled(CancellationToken.None); break;
                }
            }
            catch (Exception ex)
            {
                completionFailure ??= ex;
            }
            barrier.SignalAndWait();
            if (race.Callbacks != 1) missingCallbacks++;
            // Observe exception/cancellation before replacing the core.
            try { race.Core.GetResult(0); }
            catch (Exception ex) when (ex is FormatException or OperationCanceledException) { }
        }

        Assert.True(workerDone.Wait(TimeSpan.FromSeconds(10)), "registration worker did not finish");
        Assert.Null(registrationFailure);
        Assert.Null(completionFailure);
        Assert.Equal(0, missingCallbacks);
    }

    private sealed class CompletionRace
    {
        public XTaskCompletionSourceCore<int> Core;
        public int Callbacks;
    }

    [Fact]
    public void GetResult_BeforeCompletion_Throws()
    {
        var core = new XTaskCompletionSourceCore<int>();
        Assert.Throws<InvalidOperationException>(() => core.GetResult(0));
    }

    [Fact]
    public void TrySetResult_Completes_And_GetStatus_IsSucceeded()
    {
        var core = new XTaskCompletionSourceCore<int>();
        Assert.Equal(XTaskStatus.Pending, core.GetStatus(0));

        bool ok = core.TrySetResult(42);

        Assert.True(ok);
        Assert.Equal(XTaskStatus.Succeeded, core.GetStatus(0));
        Assert.Equal(42, core.GetResult(0));
    }

    [Fact]
    public void TrySetResult_Twice_SecondReturnsFalse()
    {
        var core = new XTaskCompletionSourceCore<int>();
        Assert.True(core.TrySetResult(1));
        Assert.False(core.TrySetResult(2));
    }

    [Fact]
    public void TrySetException_Faults_And_GetResult_Throws()
    {
        var core = new XTaskCompletionSourceCore<int>();
        var ex = new FormatException("boom");
        Assert.True(core.TrySetException(ex));

        Assert.Equal(XTaskStatus.Faulted, core.GetStatus(0));
        var thrown = Assert.Throws<FormatException>(() => core.GetResult(0));
        Assert.Same(ex, thrown);
    }

    [Fact]
    public void TrySetCanceled_Cancels_And_GetResult_ThrowsOCE()
    {
        var core = new XTaskCompletionSourceCore<int>();
        using var cts = new CancellationTokenSource();
        Assert.True(core.TrySetCanceled(cts.Token));

        Assert.Equal(XTaskStatus.Canceled, core.GetStatus(0));
        Assert.Throws<OperationCanceledException>(() => core.GetResult(0));
    }

    [Fact]
    public void Reset_BumpsVersion_OldTokenInvalid()
    {
        var core = new XTaskCompletionSourceCore<int>();
        short v0 = core.Version;
        core.TrySetResult(1);

        core.Reset();

        short v1 = core.Version;
        Assert.NotEqual(v0, v1);
        Assert.Equal(XTaskStatus.Pending, core.GetStatus(v1));
        Assert.Throws<InvalidOperationException>(() => core.GetStatus(v0));
    }

    [Fact]
    public void OnCompleted_BeforeComplete_FiresOnComplete()
    {
        var core = new XTaskCompletionSourceCore<int>();
        bool fired = false;
        object? captured = null;

        core.OnCompleted(s => { fired = true; captured = s; }, "marker", 0);
        Assert.False(fired);

        core.TrySetResult(7);

        Assert.True(fired);
        Assert.Equal("marker", captured);
    }

    [Fact]
    public void OnCompleted_AfterComplete_FiresInline()
    {
        var core = new XTaskCompletionSourceCore<int>();
        core.TrySetResult(7);

        bool fired = false;
        core.OnCompleted(_ => fired = true, null, 0);

        Assert.True(fired);
    }

    [Fact]
    public void OnCompleted_Twice_SecondThrows()
    {
        var core = new XTaskCompletionSourceCore<int>();
        core.OnCompleted(_ => { }, null, 0);
        Assert.Throws<InvalidOperationException>(() => core.OnCompleted(_ => { }, null, 0));
    }

    [Fact]
    public void OnCompleted_WithStaleToken_Throws()
    {
        var core = new XTaskCompletionSourceCore<int>();
        short v0 = core.Version;
        core.Reset();

        Assert.Throws<InvalidOperationException>(() =>
            core.OnCompleted(_ => { }, null, v0));
    }

    [Fact]
    public void UnsafeGetStatus_DoesNotValidateToken()
    {
        var core = new XTaskCompletionSourceCore<int>();
        core.TrySetResult(1);

        // UnsafeGetStatus does not throw for any token.
        Assert.Equal(XTaskStatus.Succeeded, core.UnsafeGetStatus());
    }
}
