// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Reflection;

using Xunit;

using XEngine.OrigamiUI;
using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Editor.Test.GUI;

/// <summary>
/// Verifies the M10 PropertyGrid additions: Rect/AABB field drawers are registered,
/// the new inspector attributes have handlers, and [InspectorName] overrides field labels.
/// </summary>
public class PropertyGridAttributeTests
{
#pragma warning disable 0649 // fields assigned via reflection only
    private class Probe
    {
        [InspectorName("Fancy Name")] public float renamed;
        public float plainCamelCase;
        [InspectorName("")] public float emptyOverride;
    }
#pragma warning restore 0649

    [Fact]
    public void BuiltInFieldDrawers_RegisterRectAndAABB()
    {
        var registry = new FieldDrawerRegistry();
        BuiltInFieldDrawers.Register(registry);

        Assert.NotNull(registry.GetDrawer(typeof(Rect)));
        Assert.NotNull(registry.GetDrawer(typeof(AABB)));
    }

    [Fact]
    public void BuiltInAttributeHandlers_RegisterMinMultilineDelayed()
    {
        var registry = new AttributeHandlerRegistry();
        XEngine.Editor.GUI.BuiltInAttributeHandlers.Register(registry);

        Assert.NotNull(registry.GetHandler(typeof(MinAttribute)));
        Assert.NotNull(registry.GetHandler(typeof(MultilineAttribute)));
        Assert.NotNull(registry.GetHandler(typeof(DelayedAttribute)));
    }

    [Fact]
    public void ResolveFieldLabel_UsesInspectorNameOverride()
    {
        FieldInfo f = typeof(Probe).GetField("renamed")!;
        var attrs = Attribute.GetCustomAttributes(f);

        Assert.Equal("Fancy Name", PropertyGridRenderer.ResolveFieldLabel(f.Name, attrs));
    }

    [Fact]
    public void ResolveFieldLabel_FallsBackToNicifiedName()
    {
        FieldInfo f = typeof(Probe).GetField("plainCamelCase")!;
        var attrs = Attribute.GetCustomAttributes(f);

        string label = PropertyGridRenderer.ResolveFieldLabel(f.Name, attrs);
        Assert.NotEqual("plainCamelCase", label);
        Assert.Contains("Plain", label);
    }

    [Fact]
    public void ResolveFieldLabel_EmptyOverrideFallsBack()
    {
        FieldInfo f = typeof(Probe).GetField("emptyOverride")!;
        var attrs = Attribute.GetCustomAttributes(f);

        string label = PropertyGridRenderer.ResolveFieldLabel(f.Name, attrs);
        Assert.False(string.IsNullOrEmpty(label));
        Assert.NotEqual("", label);
    }
}
