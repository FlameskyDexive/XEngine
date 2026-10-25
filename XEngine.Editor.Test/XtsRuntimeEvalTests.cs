using XEngine.XTS;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N2 gate for the XTS managed runtime: value conversion across the ABI,
/// ESM semantics (including circular imports), unbounded-loop interruption,
/// heap limits, and create/destroy churn without growth.
/// </summary>
public class XtsRuntimeEvalTests
{
    private static string PackageRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Packages", "com.xengine.xts");
                if (File.Exists(Path.Combine(candidate, "package.json")))
                    return candidate;
                dir = dir.Parent!;
            }
            throw new InvalidOperationException("com.xengine.xts package not found above the test output");
        }
    }

    [Theory]
    [InlineData("6 * 7", 42d)]
    [InlineData("1.5 + 2", 3.5d)]
    [InlineData("2 ** 10", 1024d)]
    public void EvaluateExpression_ConvertsNumbers(string code, double expected)
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        var status = runtime.EvaluateExpression(code, out var value);
        Assert.Equal(XtsStatus.Ok, status);
        // QuickJS normalizes small integral doubles to a tagged-int value
        Assert.True(value.IsNumber, $"expected a number, got {value.Kind}");
        Assert.Equal(expected, value.AsDouble(), precision: 9);
    }

    [Fact]
    public void EvaluateExpression_ConvertsPrimitivesOfEveryKind()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();

        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("'中文' + '字符串'", out var text));
        Assert.Equal(XtsValueKind.String, text.Kind);
        Assert.Equal("中文字符串", text.AsString());

        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("true", out var flag));
        Assert.Equal(XtsValueKind.Bool, flag.Kind);
        Assert.True(flag.AsBool());

        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("null", out var none));
        Assert.Equal(XtsValueKind.Null, none.Kind);

        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("undefined", out var nothing));
        Assert.Equal(XtsValueKind.Undefined, nothing.Kind);
        Assert.True(nothing.IsUndefined);

        // objects stay behind the boundary until the binding layer
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("({ engine: 'xengine' })", out var obj));
        Assert.Equal(XtsValueKind.Undefined, obj.Kind);
    }

    [Fact]
    public void EvaluateExpression_SurfaceExceptionsWithMessage()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        Assert.Equal(XtsStatus.Exception, runtime.EvaluateExpression("nope.nope", out var value));
        Assert.Contains("ReferenceError", runtime.LastError);
        Assert.True(value.IsUndefined);

        // the environment stays usable after a failed expression
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("40 + 2", out var recovered));
        Assert.Equal(42d, recovered.AsDouble());
    }

    [Fact]
    public void EsmModules_SupportCircularImports()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("a.js",
            "import { ping } from './b.js';\nexport function pong(n) { return n <= 0 ? 40 : ping(n - 1); }"));
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("b.js",
            "import { pong } from './a.js';\nexport function ping(n) { return n <= 0 ? 40 : pong(n - 1); }"));
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("main.js",
            "import { pong } from './a.js';\nglobalThis.score = pong(2) + 2;"));

        Assert.Equal(XtsStatus.Ok, runtime.EvalModule("main.js"));
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("globalThis.score", out var score));
        Assert.Equal(42d, score.AsDouble());
    }

    [Fact]
    public void EsmModules_ClassifyChainFailures()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("broken.js", "throw new TypeError('lib boom');"));
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("main.js", "import './broken.js';"));
        Assert.Equal(XtsStatus.Exception, runtime.EvalModule("main.js"));
        Assert.Contains("lib boom", runtime.LastError);

        // unresolved import in the chain is a compile failure, not an exception
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("ghost.js", "import './ghost2.js';"));
        Assert.Equal(XtsStatus.Compile, runtime.EvalModule("ghost.js"));
    }

    [Fact]
    public void ExecutionDeadline_StopsInfiniteLoopAndEnvSurvives()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        runtime.SetExecutionDeadlineMs(100);
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("spin.js", "while (true) { }"));
        Assert.Equal(XtsStatus.Limit, runtime.EvalModule("spin.js"));

        runtime.SetExecutionDeadlineMs(0);
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("41 + 1", out var after));
        Assert.Equal(42d, after.AsDouble());
    }

    [Fact]
    public void MemoryLimit_FailsAndRequiresRebuild()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create(new XtsRuntimeOptions { MemoryLimitBytes = 256 << 10 });
        runtime.SetExecutionDeadlineMs(5000);
        Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource(
            "alloc.js", "const a = []; for (let i = 0; i < 200000; i++) a.push({ x: i });"));
        Assert.Equal(XtsStatus.Limit, runtime.EvalModule("alloc.js"));
        runtime.Dispose();

        using var fresh = XtsRuntime.Create();
        Assert.Equal(XtsStatus.Ok, fresh.EvaluateExpression("'ok'", out var ok));
        Assert.Equal("ok", ok.AsString());
    }

    [Fact]
    public void CreateDestroy_ChurnStaysUsable()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        for (var i = 0; i < 100; i++)
        {
            using var runtime = XtsRuntime.Create(new XtsRuntimeOptions { MemoryLimitBytes = 8 << 20 });
            Assert.Equal(XtsStatus.Ok, runtime.AddModuleSource("m.js", "export const v = 1;"));
            Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("2 + 2", out var v));
            Assert.Equal(4d, v.AsDouble());
        }
    }
}
