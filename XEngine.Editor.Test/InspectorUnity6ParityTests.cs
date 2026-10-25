// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.Inspector;
using XEngine.Editor.Presets;
using XEngine.OrigamiUI;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

// ─────────────────────────────────────────────────────────────
//  Inspector Unity6-parity batch: numeric expressions, debug-mode
//  field surfacing, component reset/paste, presets, GO icon fields.
// ─────────────────────────────────────────────────────────────

public sealed class MathExpressionTests
{
    private static void Eq(string expr, double expected, double tol = 1e-9)
    {
        Assert.True(MathParser.TryEvaluate(expr, out double result), $"'{expr}' failed to parse");
        Assert.Equal(expected, result, 5);
    }

    [Fact]
    public void ArithmeticAndPrecedence()
    {
        Eq("7", 7);
        Eq("2*3+1", 7);
        Eq("2+3*4", 14);
        Eq("(2+3)*4", 20);
        Eq("2^10", 1024);
        Eq("10%3", 1);
        Eq("-3+5", 2);
        Eq("2*-3", -6);
        Eq("1+2*3-4/2", 5);
    }

    [Fact]
    public void ConstantsAndFunctions()
    {
        Eq("pi", Math.PI, 1e-6);
        Eq("tau/2", Math.PI, 1e-6);
        Eq("e", Math.E, 1e-6);
        Eq("sqrt(16)", 4);
        Eq("abs(-2)", 2);
        Eq("sign(-5)", -1);
        Eq("floor(1.7)", 1);
        Eq("ceil(1.2)", 2);
        Eq("round(2.5)", 3);
        Eq("min(3,5)", 3);
        Eq("max(3,5)", 5);
        Eq("cos(0)", 1);
        Eq("pow(2,8)", 256);
        Eq("log(e)", 1, 1e-6);
        Eq("log(8,2)", 3);
        Eq("atan2(1,1)", Math.PI / 4, 1e-6);
        Eq("max(1,min(4,2),3)", 3);
    }

    [Fact]
    public void CompoundAssignmentResolvesAgainstCurrent()
    {
        Assert.True(MathParser.TryEvaluate("+=2", 5, 0, 1, out double add));
        Assert.Equal(7, add, 5);
        Assert.True(MathParser.TryEvaluate("-=2", 5, 0, 1, out double sub));
        Assert.Equal(3, sub, 5);
        Assert.True(MathParser.TryEvaluate("*=3", 5, 0, 1, out double mul));
        Assert.Equal(15, mul, 5);
        Assert.True(MathParser.TryEvaluate("/=2", 6, 0, 1, out double div));
        Assert.Equal(3, div, 5);
        Assert.True(MathParser.TryEvaluate("%=3", 7, 0, 1, out double mod));
        Assert.Equal(1, mod, 5);
        // Compound with an expression on the right side.
        Assert.True(MathParser.TryEvaluate("+= 2*3", 4, 0, 1, out double compoundExpr));
        Assert.Equal(10, compoundExpr, 5);
    }

    [Fact]
    public void MultiObjectRampFunctions()
    {
        // L(a, b) lerps across the selection: first target gets a, last gets b.
        Assert.True(MathParser.TryEvaluate("L(0,10)", 0, 0, 5, out double first));
        Assert.Equal(0, first, 5);
        Assert.True(MathParser.TryEvaluate("L(0,10)", 0, 4, 5, out double last));
        Assert.Equal(10, last, 5);
        Assert.True(MathParser.TryEvaluate("L(0,10)", 0, 2, 5, out double mid));
        Assert.Equal(5, mid, 5);
        // A single target always gets a.
        Assert.True(MathParser.TryEvaluate("L(2,8)", 0, 0, 1, out double single));
        Assert.Equal(2, single, 5);

        // R(a, b) stays within [a, b).
        var rng = new Random(42);
        for (int i = 0; i < 50; i++)
        {
            Assert.True(MathParser.TryEvaluate("R(1,2)", 0, i, 10, rng, out double r));
            Assert.InRange(r, 1.0, 2.0 - 1e-9);
        }
    }

    [Fact]
    public void InvalidInputRejected()
    {
        Assert.False(MathParser.TryEvaluate("", out _));
        Assert.False(MathParser.TryEvaluate("2+", out _));
        Assert.False(MathParser.TryEvaluate("abc", out _));
        Assert.False(MathParser.TryEvaluate("(1", out _));
        Assert.False(MathParser.TryEvaluate("1)", out _));
        Assert.False(MathParser.TryEvaluate("1/0", out _));
        Assert.False(MathParser.TryEvaluate("sqrt(-1)", out _));
        Assert.False(MathParser.TryEvaluate("nosuchfunc(1)", out _));
    }
}

public sealed class DebugModeFieldTests
{
    private sealed class Sample : MonoBehaviour
    {
        public int PublicField = 1;
        [SerializeField] private float _serializedPrivate = 2f;
        private string _plainPrivate = "x";
        [HideInInspector] public int Hidden = 3;
        [SerializeIgnore] public int Ignored = 4;
    }

    private static bool Has(FieldInfo[] fields, string name)
    {
        foreach (var f in fields) if (f.Name == name) return true;
        return false;
    }

    [Fact]
    public void NormalModeShowsOnlyExposedFields()
    {
        var fields = PropertyGridRenderer.GetSerializableFields(typeof(Sample), includePrivate: false);
        Assert.True(Has(fields, "PublicField"));
        Assert.True(Has(fields, "_serializedPrivate"));
        Assert.False(Has(fields, "_plainPrivate"));
        Assert.False(Has(fields, "Hidden"));
        Assert.False(Has(fields, "Ignored"));
    }

    [Fact]
    public void DebugModeAlsoSurfacesPrivateFields()
    {
        var fields = PropertyGridRenderer.GetSerializableFields(typeof(Sample), includePrivate: true);
        Assert.True(Has(fields, "PublicField"));
        Assert.True(Has(fields, "_serializedPrivate"));
        Assert.True(Has(fields, "_plainPrivate"));
        // Hidden/ignored stay excluded even in debug mode.
        Assert.False(Has(fields, "Hidden"));
        Assert.False(Has(fields, "Ignored"));
    }
}

public sealed class ComponentResetAndPasteTests
{
    public sealed class DefaultsComp : MonoBehaviour
    {
        public int Value = 7;
        [SerializeField] private float _rate = 0.5f;
        public string Label = "default";

        public float Rate => _rate;
    }

    private sealed class NoCtorComp : MonoBehaviour
    {
        public NoCtorComp(int _) { }
    }

    [Fact]
    public void ResetRestoresFreshInstanceDefaults()
    {
        using var go = new GameObject("reset");
        var comp = go.AddComponent<DefaultsComp>();
        comp.Value = 99;
        SetPrivate(comp, "_rate", 13f);
        comp.Label = "changed";

        Assert.True(ComponentClipboard.Reset(comp));

        Assert.Equal(7, comp.Value);
        Assert.Equal(0.5f, comp.Rate);
        Assert.Equal("default", comp.Label);
    }

    [Fact]
    public void ResetRejectsTypesWithoutParameterlessCtor()
    {
        // Not attached to a GameObject - Reset's guard fires before any GO interaction.
        var comp = new NoCtorComp(1);
        Assert.False(ComponentClipboard.Reset(comp));
    }

    [Fact]
    public void ApplyFieldsFromCopiesAndHonorsExclusions()
    {
        using var go = new GameObject("apply");
        var target = go.AddComponent<DefaultsComp>();
        var source = new DefaultsComp { Value = 5, Label = "src" };
        SetPrivate(source, "_rate", 9f);

        var exclude = new System.Collections.Generic.HashSet<string> { "Label" };
        Assert.True(ComponentClipboard.ApplyFieldsFrom(target, source, "test", exclude));

        Assert.Equal(5, target.Value);
        Assert.Equal(9f, target.Rate);
        Assert.Equal("default", target.Label); // excluded from the copy
    }

    [Fact]
    public void CopySerializableFieldsSkipsExcluded()
    {
        var target = new DefaultsComp { Value = 1 };
        var source = new DefaultsComp { Value = 2, Label = "from-source" };
        ComponentClipboard.CopySerializableFields(source, target, new System.Collections.Generic.HashSet<string> { "Value" });
        Assert.Equal(1, target.Value);
        Assert.Equal("from-source", target.Label);
    }

    private static void SetPrivate(object obj, string name, object value)
        => obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(obj, value);
}

public sealed class PresetApplyTests
{
    public sealed class PresetComp : MonoBehaviour
    {
        public int Value = 1;
        public string Label = "a";
    }

    [Fact]
    public void ApplyToComponentCopiesValuesExceptExcluded()
    {
        using var sourceGo = new GameObject("src");
        var source = sourceGo.AddComponent<PresetComp>();
        source.Value = 42;
        source.Label = "captured";

        var echo = Serializer.Serialize(typeof(PresetComp), source);
        Assert.NotNull(echo);
        var preset = new Preset
        {
            TargetType = typeof(PresetComp).AssemblyQualifiedName,
            Data = echo.WriteToString(),
        };
        preset.Excluded.Add("Label");

        using var targetGo = new GameObject("dst");
        var target = targetGo.AddComponent<PresetComp>();
        Assert.True(PresetUtility.ApplyToComponent(preset, target));

        Assert.Equal(42, target.Value);
        Assert.Equal("a", target.Label); // excluded
    }

    [Fact]
    public void ApplyToComponentRejectsTypeMismatch()
    {
        using var go = new GameObject("mismatch");
        var comp = go.AddComponent<PresetComp>();
        var preset = new Preset { TargetType = typeof(MathExpressionTests).AssemblyQualifiedName, Data = "" };
        Assert.False(PresetUtility.ApplyToComponent(preset, comp));
    }
}

public sealed class GameObjectIconSerializationTests
{
    [Fact]
    public void IconFieldsRoundTripThroughEcho()
    {
        using var go = new GameObject("iconic");
        go.LabelIcon = 4;
        go.IconAssetGuid = "6f9619ff-8b86-d011-b42d-00cf4fc964ff";

        var echo = Serializer.Serialize(typeof(GameObject), go);
        Assert.NotNull(echo);

        using var restored = Serializer.Deserialize<GameObject>(echo);
        Assert.NotNull(restored);
        Assert.Equal(4, restored.LabelIcon);
        Assert.Equal("6f9619ff-8b86-d011-b42d-00cf4fc964ff", restored.IconAssetGuid);
    }
}
