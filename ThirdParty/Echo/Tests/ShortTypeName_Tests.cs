// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// $type short-form tests: new data writes "Namespace.Type, AssemblyName" (no Version/Culture/
// PublicKeyToken tail); readers must resolve BOTH the short form and the legacy full AQN so old
// and new data stay mutually loadable.

using XEngine.Echo;

namespace XEngine.Echo.Test;

public class ShortTypeName_Tests
{
    private class ShortNameBase { public int A; }
    private class ShortNameDerived : ShortNameBase { public int B; }

    [Fact]
    public void GetFullTypeName_PlainType_IsShortAssemblyQualified()
    {
        string name = TypeNameRegistry.GetFullTypeName(typeof(ShortNameBase));

        Assert.DoesNotContain("Version=", name);
        Assert.DoesNotContain("PublicKeyToken=", name);
        Assert.EndsWith(", Echo.Tests", name);
        Assert.StartsWith(typeof(ShortNameBase).FullName!, name);
    }

    [Fact]
    public void GetFullTypeName_ConstructedGeneric_KeepsFullAqn()
    {
        // Constructed generics embed fully-qualified argument names in FullName; keep the
        // unambiguous full AQN there.
        string name = TypeNameRegistry.GetFullTypeName(typeof(List<int>));
        Assert.Equal(typeof(List<int>).AssemblyQualifiedName, name);
    }

    [Fact]
    public void GetFullTypeName_ArrayOfPlainType_IsShortForm()
    {
        string name = TypeNameRegistry.GetFullTypeName(typeof(ShortNameBase[]));
        Assert.DoesNotContain("Version=", name);
        Assert.EndsWith(", Echo.Tests", name);
    }

    [Fact]
    public void ResolveFullTypeName_AcceptsBothShortAndLegacyForms()
    {
        Type t = typeof(ShortNameDerived);

        Assert.Same(t, TypeNameRegistry.ResolveFullTypeName(TypeNameRegistry.GetFullTypeName(t)));
        Assert.Same(t, TypeNameRegistry.ResolveFullTypeName(t.AssemblyQualifiedName!));
        Assert.Same(t, TypeNameRegistry.ResolveFullTypeName($"{t.FullName}, {t.Assembly.GetName().Name}"));
    }

    [Fact]
    public void PolymorphicRoundtrip_WritesShortForm_AndResolves()
    {
        var value = new ShortNameDerived { A = 1, B = 2 };

        // typeof(object) target forces a $type envelope, like the editor's asset writer.
        var echo = Serializer.Serialize(typeof(object), value);
        string typeName = echo.Get("$type")!.StringValue;
        Assert.DoesNotContain("Version=", typeName);

        var back = Serializer.Deserialize(echo, typeof(object));
        var typed = Assert.IsType<ShortNameDerived>(back);
        Assert.Equal(1, typed.A);
        Assert.Equal(2, typed.B);
    }

    [Fact]
    public void LegacyFullAqnData_StillDeserializes()
    {
        // Old asset simulation: $type carries the legacy full AQN.
        var value = new ShortNameDerived { A = 7, B = 9 };
        var echo = Serializer.Serialize(typeof(object), value);
        echo.Get("$type")!.Value = typeof(ShortNameDerived).AssemblyQualifiedName;

        var back = Serializer.Deserialize(echo, typeof(object));
        var typed = Assert.IsType<ShortNameDerived>(back);
        Assert.Equal(7, typed.A);
        Assert.Equal(9, typed.B);
    }
}
