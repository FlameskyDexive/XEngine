using System.Text.Json;
using XEngine.XTS;
using XEngine.XTS.Binding;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// N3 gate for the XTS binding layer: registry validation with rejection
/// diagnostics (open generics, byref, pointers, duplicates, missing
/// handlers), declaration + glue generation, capability manifest
/// verification, and live two-way calls across the ABI — including bigint,
/// JSON-structured values and script-provided delegates held as handles.
/// </summary>
public class XtsBindingTests
{
    private static XtsFunctionSpec Spec(string id, string global, XtsBindingHandler handler,
        params (string Name, XtsBindingType Type, Type? Clr)[] parameters)
    {
        return new XtsFunctionSpec
        {
            BindingId = id,
            GlobalName = global,
            Handler = handler,
            Parameters = parameters.Select(p => new XtsBindingParameter(p.Name, p.Type, p.Clr)).ToArray(),
        };
    }

    // ------------------------------------------------------- validation

    [Fact]
    public void Registry_AcceptsClosedGenericsAndPrimitives()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var registry = new XtsBindingRegistry();
        registry.Add(Spec("engine.time.deltaTime", "engine_time_deltaTime",
            _ => XtsValue.From(0.016)));
        registry.Add(Spec("engine.stats.topScores", "engine_stats_topScores",
            _ => XtsValue.From("[1,2,3]"),
            ("limit", XtsBindingType.Int32, typeof(int))));
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "engine.stats.scoresList",
            GlobalName = "engine_stats_scoresList",
            Handler = _ => XtsValue.From("[]"),
            Parameters = new[] { new XtsBindingParameter("scores", XtsBindingType.Json, typeof(List<int>)) },
        });

        Assert.Empty(registry.Validate());
    }

    [Fact]
    public void Registry_RejectsOpenGenericsByRefPointersDuplicatesAndMissingHandlers()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var registry = new XtsBindingRegistry();
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "a.open",
            GlobalName = "a_open",
            Handler = _ => XtsValue.Undefined,
            Parameters = new[] { new XtsBindingParameter("bad", XtsBindingType.Json, typeof(List<>)) },
        });
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "a.byref",
            GlobalName = "a_byref",
            Handler = _ => XtsValue.Undefined,
            Parameters = new[] { new XtsBindingParameter("ref", XtsBindingType.Int32, typeof(int).MakeByRefType()) },
        });
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "a.dup",
            GlobalName = "a_shared",
            Handler = _ => XtsValue.Undefined,
        });
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "a.dup2",
            GlobalName = "a_shared", // collides with a.dup
            Handler = _ => XtsValue.Undefined,
        });
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "a.nohandler",
            GlobalName = "a_nohandler",
        });

        var errors = registry.Validate();
        Assert.Contains(errors, e => e.Contains("open generic"));
        Assert.Contains(errors, e => e.Contains("ref/out"));
        Assert.Contains(errors, e => e.Contains("collides"));
        Assert.Contains(errors, e => e.Contains("no handler"));
    }

    // ------------------------------------------------------ generation

    [Fact]
    public void Generator_EmitsDeclarationsGlueAndCapabilityManifest()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        var registry = new XtsBindingRegistry();
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "engine.time.deltaTime",
            GlobalName = "engine_time_deltaTime",
            Handler = _ => XtsValue.From(0.016),
            ReturnType = XtsBindingType.Float64,
        });
        registry.Add(new XtsFunctionSpec
        {
            BindingId = "engine.mover.addPath",
            GlobalName = "engine_mover_addPath",
            Handler = _ => XtsValue.Undefined,
            ReturnType = XtsBindingType.Json,
            Parameters = new[]
            {
                new XtsBindingParameter("points", XtsBindingType.Json, typeof(IReadOnlyList<double>)),
                new XtsBindingParameter("speed", XtsBindingType.Float64, typeof(double)),
            },
        });

        var declarations = XtsBindingGenerator.GenerateDeclarations(registry);
        Assert.Contains("declare function engine_time_deltaTime(): number;", declarations);
        Assert.Contains("points: number[]", declarations);
        Assert.Contains("speed: number", declarations);

        var glue = XtsBindingGenerator.GenerateGlueModule(registry);
        Assert.Contains("export function deltaTime() {", glue);
        Assert.Contains("JSON.parse(engine_mover_addPath(JSON.stringify(points), speed))", glue);

        var capabilities = XtsBindingGenerator.GenerateCapabilityManifest(registry, "0.1.0");
        var errors = XtsBindingGenerator.VerifyCapabilityRequirements(capabilities,
            new[] { "engine.time.deltaTime", "engine.mover.addPath" });
        Assert.Empty(errors);

        var missing = XtsBindingGenerator.VerifyCapabilityRequirements(capabilities,
            new[] { "engine.missing.binding" });
        Assert.Contains(missing, e => e.Contains("engine.missing.binding"));
    }

    // ----------------------------------------------- live two-way calls

    [Fact]
    public void Runtime_TwoWayCalls_PrimitivesBigintAndJson()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        runtime.RegisterCallback("engine_add", args =>
        {
            var sum = args[0].AsInt32() + args[1].AsInt32();
            return XtsValue.From(sum);
        });
        runtime.RegisterCallback("engine_scale", args => XtsValue.From(args[0].AsDouble() * 2.0));
        runtime.RegisterCallback("engine_bump_bigint", args => XtsValue.From(args[0].AsBigInt() + 1));
        runtime.RegisterCallback("engine_merge", args =>
        {
            // JSON channel: the script stringifies structured values; the host
            // parses, computes, and hands a JSON document back (by value)
            var doc = JsonDocument.Parse(args[0].AsString());
            var x = doc.RootElement.GetProperty("x").GetDouble();
            var y = doc.RootElement.GetProperty("y").GetDouble();
            return XtsValue.From($"{{\"len\":{x * x + y * y}}}");
        });

        Assert.True(runtime.EvaluateExpression("engine_add(2, 40)", out var sum) == XtsStatus.Ok,
            $"engine_add failed: {runtime.LastError}");
        Assert.Equal(42d, sum.AsDouble());

        Assert.True(runtime.EvaluateExpression("engine_scale(10.5)", out var scaled) == XtsStatus.Ok,
            $"engine_scale failed: {runtime.LastError}");
        Assert.Equal(21d, scaled.AsDouble());

        Assert.True(runtime.EvaluateExpression("engine_bump_bigint(9007199254740993n)", out var big) == XtsStatus.Ok,
            $"engine_bump_bigint failed: {runtime.LastError}");
        Assert.Equal(XtsValueKind.BigInt, big.Kind);
        Assert.Equal(9007199254740994L, big.AsBigInt());

        Assert.True(runtime.EvaluateExpression(
            "JSON.parse(engine_merge(JSON.stringify({x: 3.0, y: 4.0}))).len", out var len) == XtsStatus.Ok,
            $"engine_merge failed: {runtime.LastError}");
        Assert.Equal(25d, len.AsDouble());
    }

    [Fact]
    public void Runtime_Delegates_ScriptFunctionsInvokeAndRelease()
    {
        // XTS hot-update native (xengine_xts) is Windows/HarmonyOS-only; osx build is
        // tracked macOS follow-up work.
        if (XtsNativeAvailability.ShouldSkip)
            return;
        using var runtime = XtsRuntime.Create();
        XtsFunctionHandle? received = null;
        runtime.RegisterCallback("engine_on_tick", args =>
        {
            // the script (or generated glue) passes __xts_hold(fn): the raw
            // argument is the handle index into __xts_handles
            received = runtime.CreateFunctionHandle(args[0].AsInt32());
            return XtsValue.From(received.Index);
        });

        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression(
            "globalThis.__seen = 0; engine_on_tick(__xts_hold(function (v) { globalThis.__seen = v; }));",
            out var handleValue));
        Assert.Equal(XtsValueKind.Int32, handleValue.Kind);
        Assert.NotNull(received);

        var invoked = received!.Invoke(XtsValue.From(42));
        Assert.Equal(XtsStatus.Ok, runtime.EvaluateExpression("globalThis.__seen", out var seen));
        Assert.Equal(42d, seen.AsDouble());

        received.Release();
        Assert.ThrowsAny<Exception>(() => received.Invoke(XtsValue.From(1)));
    }
}