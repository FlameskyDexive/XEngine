using XEngine.NodePlay;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class NPProbeBindingComponent : MonoBehaviour
{
    public float Heat = 40f;

    public override string ToString() => "NPProbeBindingComponent";
}

/// <summary>M3: scoped blackboard resolution, converters, tri-state parameters, scene data binding.</summary>
public sealed class NodePlayBlackboardTests
{
    [Fact]
    public void ParentChain_SlashScopes_AndDynamicPromotion()
    {
        var globals = NPGlobalBlackboards.Ensure("Camp");
        globals.Add("morale", 0.75f);
        var objectBb = new NPBlackboardSource { Identifier = "Soldier" };
        var graphBb = new NPBlackboardSource { Identifier = "Graph", Parent = objectBb };
        objectBb.Parent = globals;
        objectBb.Add("hp", 100);

        // parent walk
        Assert.Equal(0.75f, graphBb.GetValue("morale", 0f));
        Assert.Equal(100, graphBb.GetValue("hp", -1));

        // named scope syntax routes to a registry scope regardless of chain position
        Assert.Equal(0.75f, graphBb.GetValue("Camp/morale", 0f));
        graphBb.SetValue("Camp/morale", 0.9f);
        Assert.Equal(0.9f, globals.GetValue("morale", 0f));

        // dynamic "_" names self-register on write, readable unqualified afterwards
        graphBb.SetValue("_tempTarget", "crate");
        Assert.Equal("crate", graphBb.GetValue("_tempTarget", ""));

        // unbound non-dynamic writes are no-ops (missing variable), reads fall back via GetValue default
        graphBb.SetValue("neverDefined", 1);   // silently ignored: not dynamic, not present, no parent hit
        Assert.Equal(-1, graphBb.GetValue("neverDefined", -1));

        NPGlobalBlackboards.ResetAll();
    }

    [Fact]
    public void Converters_NumericStringBoolAndCustomBridge()
    {
        var bb = new NPBlackboardSource { Identifier = "T" };

        bb.Add("count", 3);
        Assert.Equal(3f, bb.GetValue<float>("count", 0f));
        Assert.Equal("3", bb.GetValue<string>("count", ""));
        Assert.True(bb.GetValue<bool>("count=true?", false) == false);

        bb.Add("flag", true);
        Assert.Equal(1f, bb.GetValue<float>("flag", 0f));
        Assert.Equal("true", bb.GetValue<string>("flag", ""));

        bb.Add("label", "7");
        Assert.Equal(7, bb.GetValue<int>("label", 0));

        NPTypeConverter.Register(typeof(NPProbeBindingComponent), typeof(float), c => 42f);
        float bridged = NPTypeConverter.Convert(new NPProbeBindingComponent(), typeof(float)) as float? ?? 0f;
        Assert.Equal(42f, bridged);
        NPTypeConverter.ClearCustom();

        // Unknown route keeps the caller's fallback.
        Assert.Equal(-1, new NPVariable { Value = "x" }.GetValue(-1));
    }

    [Fact]
    public void Parameter_InlineBoundDynamicAndFallback()
    {
        var bb = new NPBlackboardSource { Identifier = "P" };
        bb.Add("speed", 2.5f);

        var inline = new NPParameter { InlineValue = 10 };
        Assert.Equal(10, inline.GetValue<int>(bb, 0));
        Assert.False(inline.IsBound);

        var bound = new NPParameter { VariableName = "speed" };
        Assert.Equal(2.5f, bound.GetValue(bb, 0f));
        bound.SetValue(bb, 5f);
        Assert.Equal(5f, bb.GetValue<float>("speed", 0f));

        // missing plain name falls back inline; dynamic name creates on demand then persists
        var ghost = new NPParameter { VariableName = "ghost", InlineValue = 99 };
        Assert.Equal(99, ghost.GetValue<int>(bb, 0));

        var dyn = new NPParameter { VariableName = "_score" };
        dyn.SetValue(bb, 12);
        Assert.Equal(12, bb.GetValue<int>("_score", 0));

        // converter fallback when the bound value's type differs from the requested one
        bb.Add("text", "8");
        Assert.Equal(8, new NPParameter { VariableName = "text" }.GetValue<int>(bb, 0));
    }

    [Fact]
    public void DataBinding_SeedsFromSceneAndWritesBack()
    {
        var go = new GameObject("BindingProbe");
        var component = go.AddComponent<NPProbeBindingComponent>();
        component.Heat = 61f;

        var bb = new NPBlackboardSource { Identifier = "S" };
        var heat = bb.Add("heat", 0f);
        heat.PropertyPath = "Heat";
        Assert.Null(heat.InitializeBinding(component, pushCurrentIntoScene: false));
        Assert.Equal(61f, bb.GetValue<float>("heat", 0f));

        // runtime write flows into the scene member
        bb.SetValue("heat", 90f);
        Assert.Equal(90f, component.Heat);

        // reverse direction: seeding into the scene instead of from it
        var cold = bb.Add("cold", 5f);
        cold.PropertyPath = "Heat";
        Assert.Null(cold.InitializeBinding(component, pushCurrentIntoScene: true));
        Assert.Equal(5f, component.Heat);

        heat.DeinitBinding();
        bb.SetValue("heat", 120f);
        Assert.Equal(5f, component.Heat);    // heat channel detached; last write on the field was cold's seed

        // cold's channel is still live after heat detached
        bb.SetValue("cold", 7f);
        Assert.Equal(7f, component.Heat);

        // unknown path reports an error without throwing
        var bad = bb.Add("bad", 0f);
        bad.PropertyPath = "NoSuchThing";
        string? error = bad.InitializeBinding(component, false);
        Assert.NotNull(error);
        Assert.Contains("no bindable member", error!);
    }
}
