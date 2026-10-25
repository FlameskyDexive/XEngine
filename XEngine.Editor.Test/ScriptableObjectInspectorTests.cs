// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;

using XEngine.Echo;
using XEngine.OrigamiUI;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public class ScriptableObjectInspectorTests
{
    private sealed class InspectorAsset : ScriptableObject
    {
        public int PublicField = 0;

        [SerializeField] private int _serializedField = 0;
        [ShowInInspector] private int _shownField = 0;
        [HideInInspector] public int HiddenField = 0;
        [SerializeIgnore] public int IgnoredField = 0;

        [ShowInInspector] public int Computed => PublicField + _serializedField + _shownField;
        public int UnmarkedProperty { get; set; }
    }

    [Fact]
    public void ShowInInspector_FieldVisibilityDoesNotChangeEchoSerializationRules()
    {
        FieldInfo[] inspectorFields = PropertyGridRenderer.GetSerializableFields(typeof(InspectorAsset));
        string[] inspectorNames = Array.ConvertAll(inspectorFields, static field => field.Name);

        Assert.Contains(nameof(InspectorAsset.PublicField), inspectorNames);
        Assert.Contains("_serializedField", inspectorNames);
        Assert.Contains("_shownField", inspectorNames);
        Assert.DoesNotContain(nameof(InspectorAsset.HiddenField), inspectorNames);
        Assert.DoesNotContain(nameof(InspectorAsset.IgnoredField), inspectorNames);

        var asset = new InspectorAsset();
        var echo = Serializer.Serialize(typeof(object), asset);
        Assert.True(echo.Contains(nameof(InspectorAsset.PublicField)));
        Assert.True(echo.Contains("_serializedField"));
        Assert.False(echo.Contains("_shownField"));
        Assert.False(echo.Contains(nameof(InspectorAsset.Computed)));
    }

    [Fact]
    public void ShowInInspector_OnlyExplicitPropertiesAreDiscovered()
    {
        MethodInfo method = typeof(PropertyGridRenderer).GetMethod(
            "GetShownProperties", BindingFlags.NonPublic | BindingFlags.Static)!;

        var properties = (PropertyInfo[])method.Invoke(null, new object[] { typeof(InspectorAsset) })!;

        Assert.Contains(properties, property => property.Name == nameof(InspectorAsset.Computed));
        Assert.DoesNotContain(properties, property => property.Name == nameof(InspectorAsset.UnmarkedProperty));
    }

    [Fact]
    public void ReadOnlyScope_IsConditionalNestedAndIdempotent()
    {
        while (Origami.IsReadOnly)
            Origami.EndReadOnly();

        var disabled = Origami.BeginReadOnlyScope(enabled: false);
        Assert.False(Origami.IsReadOnly);
        disabled.Dispose();

        var outer = Origami.BeginReadOnlyScope();
        var inner = Origami.BeginReadOnlyScope();
        Assert.True(Origami.IsReadOnly);

        inner.Dispose();
        Assert.True(Origami.IsReadOnly);

        outer.Dispose();
        outer.Dispose();
        Assert.False(Origami.IsReadOnly);
    }
}
