// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading.Tasks;

using XEngine.Async;
using XEngine.Async.Internal;
using Xunit;

namespace XEngine.Runtime.Test.XTaskTests;

/// <summary>
/// Hot-reload hygiene: pending frame-loop operators awaiting code from a collectible (hot-reloadable)
/// assembly must be dropped by <see cref="XTaskLoopHelper.ClearForReload"/> so they cannot pin the
/// script AssemblyLoadContext, while engine-bound operators keep running.
/// </summary>
/// <remarks>
/// Collectible-bound delegates come from a collectible copy of Echo.dll (never the test assembly —
/// a duplicate-identity copy of the assembly xunit lives in crashes the test host during test-case
/// serialization). The full ALC-unload weak-reference check is not meaningful under testhost: that
/// process pins every loaded assembly for its lifetime, unlike the engine's editor process, which
/// is what ScriptAssemblyManager's unload verification observes.
/// </remarks>
public class XTaskReloadCleanupTests
{
    public XTaskReloadCleanupTests()
    {
        XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    private static Assembly LoadCollectibleEchoCopy()
    {
        string echoPath = Path.Combine(AppContext.BaseDirectory, "Echo.dll");
        byte[] bytes = File.ReadAllBytes(echoPath);
        var alc = new AssemblyLoadContext("XTaskReloadCleanupTests-copy", isCollectible: true);
        Assembly copy = alc.LoadFromStream(new MemoryStream(bytes));
        Assert.True(copy.IsCollectible, "Echo copy did not load into a collectible context");
        return copy;
    }

    /// <summary>A Func&lt;bool&gt; whose method is declared in the collectible copy.</summary>
    private static Func<bool> CollectiblePredicate(Assembly copy)
    {
        MethodInfo getter = copy.GetType("XEngine.Echo.TypeNameRegistry", throwOnError: true)!
            .GetProperty("TypeResolverIsAuthoritative", BindingFlags.Public | BindingFlags.Static)!
            .GetGetMethod()!;
        return getter.CreateDelegate<Func<bool>>();
    }

    /// <summary>An Action whose method is declared in the collectible copy (never invoked).</summary>
    private static Action CollectibleAction(Assembly copy)
        => copy.GetType("XEngine.Echo.Serializer", throwOnError: true)!
            .GetMethod("ClearCache", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Action>();

    [Fact]
    public async Task ClearForReload_DropsCollectibleBoundOperators_KeepsEngineBoundOnes()
    {
        // Engine-bound control: survives ClearForReload and completes afterwards.
        bool gate = false;
        XTask engineBound = XTask.WaitUntil(() => gate);
        int baseline = XTaskLoopHelper.PendingItemCount;
        Assert.True(baseline >= 1);

        // Pending WaitUntil polled by a collectible predicate — the classic user-coroutine pin.
        Assembly copy = LoadCollectibleEchoCopy();
        XTask collectible = XTask.WaitUntil(CollectiblePredicate(copy));
        Assert.Equal(baseline + 1, XTaskLoopHelper.PendingItemCount);

        XTaskLoopHelper.ClearForReload();

        Assert.Equal(baseline, XTaskLoopHelper.PendingItemCount);
        Assert.False(collectible.IsCompleted); // abandoned, never resumed

        // The engine-bound operator must still be pending and finish normally.
        Assert.False(engineBound.IsCompleted);
        gate = true;
        XTaskLoopHelper.Tick(FrameTiming.Update);
        Assert.True(engineBound.IsCompleted);
        await engineBound;
    }

    [Fact]
    public void ClearForReload_DropsCollectibleBoundContinuations_InQueue()
    {
        Assembly copy = LoadCollectibleEchoCopy();
        XTask.Post(CollectibleAction(copy), FrameTiming.Update);
        int baseline = XTaskLoopHelper.PendingItemCount;

        XTaskLoopHelper.ClearForReload();

        Assert.Equal(baseline - 1, XTaskLoopHelper.PendingItemCount);
    }

    [Fact]
    public void CollectibleBinding_DetectsDelegatesAndStates_FromCollectibleAssemblies()
    {
        Assembly copy = LoadCollectibleEchoCopy();
        Func<bool> collectible = CollectiblePredicate(copy);
        bool engineGate = false;
        Func<bool> engine = () => engineGate;
        Action<object> engineContinuation = static _ => { };

        Assert.True(CollectibleBinding.IsBound(collectible));
        Assert.False(CollectibleBinding.IsBound(engine));

        // A pending awaiter is collectible-bound through EITHER its continuation delegate or its
        // state object (e.g. an async state machine box or array from the collectible assembly).
        var core = new XTaskCompletionSourceCore<AsyncUnit>();
        Type echoObjectType = copy.GetType("XEngine.Echo.EchoObject", throwOnError: true)!;
        object collectibleState = Array.CreateInstance(echoObjectType, 0);
        core.OnCompleted(engineContinuation, collectibleState, core.Version);
        Assert.True(core.IsAwaiterCollectible);

        var clean = new XTaskCompletionSourceCore<AsyncUnit>();
        clean.OnCompleted(engineContinuation, engineGate, clean.Version);
        Assert.False(clean.IsAwaiterCollectible);
    }

    /// <summary>Stand-in for the pooled promise classes: a lambda declared in a generic class, whose
    /// compiler-generated host type reports the open generic definition as its DeclaringType.</summary>
    private static class GenericHost<T>
    {
        public static readonly Func<int> SizeGetter = () => typeof(T).Name.Length;
        public static readonly Func<int> NestedGetter = Nested.Getter;
        private static class Nested { public static readonly Func<int> Getter = () => 1; }
    }

    [Fact]
    public void IsBound_LambdaDeclaredInGenericClass_TerminatesAndIsEngineBound()
    {
        // Regression: DeclaringType of the lambda's <>c host is GenericHost<T> (the OPEN definition);
        // its generic argument T has DeclaringType GenericHost<T> again, and IsTypeBound recursed until
        // the stack overflowed - which killed the live editor in TaskPool.RemoveCollectibleSizeGetters
        // during the hot-reload that follows a Play session (AsyncXTask<TStateMachine> size getters).
        Assert.False(CollectibleBinding.IsBound(GenericHost<int>.SizeGetter));
        Assert.False(CollectibleBinding.IsBound(GenericHost<string>.NestedGetter));
        Assert.False(CollectibleBinding.IsBound(GenericHost<AsyncUnit>.SizeGetter));

        // A collectible closed type as the argument is still detected through the generic arguments.
        Assembly copy = LoadCollectibleEchoCopy();
        Type echoObjectType = copy.GetType("XEngine.Echo.EchoObject", throwOnError: true)!;
        object collectibleList = Activator.CreateInstance(typeof(System.Collections.Generic.List<>).MakeGenericType(echoObjectType))!;
        Assert.True(CollectibleBinding.IsBound(collectibleList));
        Assert.False(CollectibleBinding.IsBound(new System.Collections.Generic.List<int>()));
    }

    [Fact]
    public void ClearForReload_WithoutCollectibleWork_DoesNotDisturbEngineOperators()
    {
        int resumed = 0;
        CountingCoroutine(() => resumed++).Forget();

        XTaskLoopHelper.ClearForReload();

        XTaskLoopHelper.Tick(FrameTiming.Update); // NextFrame resumes
        XTaskLoopHelper.Tick(FrameTiming.Update); // DelayFrame(1) completes
        Assert.Equal(1, resumed);
    }

    private static async XTaskVoid CountingCoroutine(Action onDone)
    {
        await XTask.NextFrame(FrameTiming.Update);
        await XTask.DelayFrame(1, FrameTiming.Update);
        onDone();
    }
}
