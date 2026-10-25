// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// The format red line for the source generator: a [GenerateSerializer] type must produce an
// EchoObject graph deeply equal to what the reflection path produces for an identical mirror type,
// and each side must be able to read the other's output (old assets written by reflection load via
// the generated reader; generated output loads via the reflection reader).

using XEngine.Echo;

namespace Tests.SourceGeneratorTests;

public class ReflectionParityTests
{
    private static EchoObject SerializeAs<T>(T value, TypeMode mode = TypeMode.Auto)
        => Serializer.Serialize(typeof(T), value!, new SerializationContext(mode));

    /// <summary>Deep-compares two compounds, reporting the first differing path for diagnosis.</summary>
    private static void AssertDeepEqual(EchoObject expected, EchoObject actual)
    {
        if (!expected.Equals(actual))
        {
            Assert.Fail(
                $"EchoObject graphs differ.\n--- reflection ---\n{expected.WriteToString()}\n--- generated ---\n{actual.WriteToString()}");
        }
    }

    [Fact]
    public void Flat_GeneratedOutput_MatchesReflectionOutput()
    {
        var gen = new GenParityFlat();
        var reference = new RefParityFlat();

        var genEcho = SerializeAs(gen);
        var refEcho = SerializeAs(reference);

        AssertDeepEqual(refEcho, genEcho);

        // Sanity: attribute behaviors actually applied
        Assert.False(genEcho.Contains("Ignored"));
        Assert.False(genEcho.Contains("OptionalNull"));
        Assert.True(genEcho.Contains("OptionalSet"));
        Assert.False(genEcho.Contains("Conditional")); // Cond == false
        Assert.True(genEcho.Contains("privateField"));
    }

    [Fact]
    public void Flat_GeneratedOutput_MatchesReflectionOutput_AggressiveTypeMode()
    {
        var genEcho = SerializeAs(new GenParityFlat(), TypeMode.Aggressive);
        var refEcho = SerializeAs(new RefParityFlat(), TypeMode.Aggressive);

        // The root $type necessarily names the two different mirror types - the only allowed
        // difference. Everything nested must match exactly.
        Assert.Contains("GenParityFlat", genEcho.Get("$type")!.StringValue);
        Assert.Contains("RefParityFlat", refEcho.Get("$type")!.StringValue);
        genEcho.Remove("$type");
        refEcho.Remove("$type");
        AssertDeepEqual(refEcho, genEcho);
    }

    [Fact]
    public void Flat_SerializeIfTrue_MatchesReflection()
    {
        var gen = new GenParityFlat { Cond = true };
        var reference = new RefParityFlat { Cond = true };
        var genEcho = SerializeAs(gen);
        AssertDeepEqual(SerializeAs(reference), genEcho);
        Assert.True(genEcho.Contains("Conditional"));
    }

    [Fact]
    public void Flat_ReflectionWrittenData_LoadsThroughGeneratedReader()
    {
        // Old-asset simulation: data written by the reflection path, read by the generated type.
        var reference = new RefParityFlat { S = "old asset", I = 999, Cond = true };
        reference.SetPrivateField(4321);
        var refEcho = SerializeAs(reference);

        var gen = Serializer.Deserialize<GenParityFlat>(refEcho)!;

        Assert.Equal("old asset", gen.S);
        Assert.Equal(999, gen.I);
        Assert.Equal(4321, gen.PrivateFieldValue);
        Assert.Equal(reference.E, gen.E);
        Assert.Equal(reference.EUl, gen.EUl);
        Assert.Equal(reference.G, gen.G);
        Assert.Equal(reference.Dt, gen.Dt);
        Assert.Equal(reference.Ts, gen.Ts);
        Assert.Equal(reference.Dto, gen.Dto);
        Assert.Equal(reference.Ints, gen.Ints);
        Assert.Equal(reference.Arr, gen.Arr);
        Assert.Equal(reference.Map, gen.Map);
        Assert.Equal(reference.Bytes, gen.Bytes);
        Assert.Equal(reference.NullableSet, gen.NullableSet);
        Assert.Null(gen.NullableNull);
        Assert.NotNull(gen.Nested);
        Assert.Null(gen.NestedNull);
        Assert.Equal(99, gen.Boxed);
        Assert.Equal('X', gen.C);
    }

    [Fact]
    public void Flat_GeneratedWrittenData_LoadsThroughReflectionReader()
    {
        // Forward compatibility: data written by the generated type, read by the reflection path.
        var gen = new GenParityFlat { S = "new asset", I = 777 };
        gen.SetPrivateField(8765);
        var genEcho = SerializeAs(gen);

        var reference = Serializer.Deserialize<RefParityFlat>(genEcho)!;

        Assert.Equal("new asset", reference.S);
        Assert.Equal(777, reference.I);
        Assert.Equal(8765, reference.PrivateFieldValue);
        Assert.Equal(gen.E, reference.E);
        Assert.Equal(gen.EUl, reference.EUl);
        Assert.Equal(gen.G, reference.G);
        Assert.Equal(gen.Dt, reference.Dt);
        Assert.Equal(gen.Ts, reference.Ts);
        Assert.Equal(gen.Ints, reference.Ints);
        Assert.Equal(gen.Arr, reference.Arr);
        Assert.Equal(gen.Map, reference.Map);
        Assert.Equal(gen.Bytes, reference.Bytes);
        Assert.Equal('X', reference.C);
    }

    [Fact]
    public void Flat_TextRoundtrip_ThroughGeneratedType()
    {
        var gen = new GenParityFlat { Cond = true };
        string text = SerializeAs(gen).WriteToString();
        var back = Serializer.Deserialize<GenParityFlat>(EchoObject.ReadFromString(text))!;
        Assert.Equal(gen.I, back.I);
        Assert.Equal(gen.Ul, back.Ul);
        Assert.Equal(gen.Dec, back.Dec);
        Assert.Equal(gen.C, back.C);
        Assert.Equal(gen.EUl, back.EUl);
        Assert.Equal(gen.Conditional, back.Conditional);
    }

    [Fact]
    public void Inheritance_GeneratedOutput_MatchesReflectionOutput()
    {
        var genEcho = SerializeAs(new GenParityDerived());
        var refEcho = SerializeAs(new RefParityDerived());

        AssertDeepEqual(refEcho, genEcho);

        Assert.True(genEcho.Contains("BasePub"));
        Assert.True(genEcho.Contains("basePriv"));  // base-private via UnsafeAccessor
        Assert.True(genEcho.Contains("BaseProt"));
        Assert.True(genEcho.Contains("DerivedField"));
        Assert.True(genEcho.Contains("ReadonlyField"));
    }

    [Fact]
    public void Inheritance_BasePrivateAndReadonlyFields_Roundtrip()
    {
        var original = new GenParityDerived(ro: 4444) { BasePub = 11, DerivedField = 33 };
        original.SetBasePriv(22);

        var back = Serializer.Deserialize<GenParityDerived>(SerializeAs(original))!;

        Assert.Equal(11, back.BasePub);
        Assert.Equal(22, back.BasePrivValue);
        Assert.Equal(33, back.DerivedField);
        Assert.Equal(4444, back.ReadonlyField); // written through the UnsafeAccessor
    }

    [Fact]
    public void SubclassOfGeneratedType_FallsBackToReflection_KeepingItsOwnFields()
    {
        // PlainChildOfGenerated inherits GenParityDerived's generated ISerializable but is not
        // annotated itself; the dispatcher must reject the inherited implementation (it doesn't know
        // ChildField) and use reflection for the whole object.
        var child = new PlainChildOfGenerated { ChildField = 987, DerivedField = 654, BasePub = 321 };
        child.SetBasePriv(111);

        var echo = SerializeAs(child);
        Assert.True(echo.Contains("ChildField"));
        Assert.True(echo.Contains("DerivedField"));
        Assert.True(echo.Contains("basePriv"));

        var back = Serializer.Deserialize<PlainChildOfGenerated>(echo)!;
        Assert.Equal(987, back.ChildField);
        Assert.Equal(654, back.DerivedField);
        Assert.Equal(321, back.BasePub);
        Assert.Equal(111, back.BasePrivValue);
    }

    [Fact]
    public void ShadowedFieldName_QualifiedKey_MatchesReflection()
    {
        var gen = new GenParityShadow { Shadow = 5 };
        ((ParityShadowBase)gen).Shadow = 6;
        var reference = new RefParityShadow { Shadow = 5 };
        ((ParityShadowBase)reference).Shadow = 6;

        var genEcho = SerializeAs(gen);
        var refEcho = SerializeAs(reference);

        AssertDeepEqual(refEcho, genEcho);
        Assert.True(genEcho.Contains("Shadow"));
        Assert.True(genEcho.Contains("Shadow@Tests.SourceGeneratorTests.ParityShadowBase"));

        var back = Serializer.Deserialize<GenParityShadow>(genEcho)!;
        Assert.Equal(5, back.Shadow);
        Assert.Equal(6, ((ParityShadowBase)back).Shadow);
    }

    [Fact]
    public void SharedReferences_IdAssignment_MatchesReflection()
    {
        var nested = new ParityNested();
        var list = new List<int> { 9, 8 };
        var gen = new GenParityRefs { A = nested, B = nested, SharedList1 = list, SharedList2 = list };

        var nested2 = new ParityNested();
        var list2 = new List<int> { 9, 8 };
        var reference = new RefParityRefs { A = nested2, B = nested2, SharedList1 = list2, SharedList2 = list2 };

        var genEcho = SerializeAs(gen);
        var refEcho = SerializeAs(reference);

        // $id numbering must line up field-for-field, or reference graphs written by one path could
        // not be read as the same shape by the other.
        AssertDeepEqual(refEcho, genEcho);

        var back = Serializer.Deserialize<GenParityRefs>(genEcho)!;
        Assert.Same(back.A, back.B);
        Assert.Same(back.SharedList1, back.SharedList2);
        Assert.Equal(new List<int> { 9, 8 }, back.SharedList1);
    }

    [Fact]
    public void NullTag_AssignsDefault_LikeSetValueNull()
    {
        // Old data with an explicit Null for a value-type field: reflection's SetValue(null) writes
        // default(T); the generated reader must do the same.
        var compound = EchoObject.NewCompound();
        compound.Add("I", new EchoObject(EchoType.Null, null));
        compound.Add("E", new EchoObject(EchoType.Null, null));
        compound.Add("Bo", new EchoObject(EchoType.Null, null));

        var gen = Serializer.Deserialize<GenParityFlat>(compound)!;
        Assert.Equal(0, gen.I);
        Assert.Equal(ParityEnum.Zero, gen.E);
        Assert.False(gen.Bo);
    }

    [Fact]
    public void WidenedNumericTag_ReadsThroughConvert_LikeReflection()
    {
        // A float field stored as Int/Double (hand-edited or legacy data) must still read.
        var compound = EchoObject.NewCompound();
        compound.Add("F", new EchoObject(12));            // int tag into float field
        compound.Add("I", new EchoObject((short)34));     // short tag into int field
        compound.Add("L", new EchoObject(56));            // int tag into long field

        var gen = Serializer.Deserialize<GenParityFlat>(compound)!;
        Assert.Equal(12f, gen.F);
        Assert.Equal(34, gen.I);
        Assert.Equal(56L, gen.L);
    }

    [Fact]
    public void FormerlySerializedAs_StillHonored_ByGeneratedReader()
    {
        // Uses the pre-existing generated type from AttributeTestTypes to double-check the rewrite
        // kept former-name lookup intact when combined with the new lookup chain.
        var tag = EchoObject.NewCompound();
        tag.Add("OldName", new EchoObject(EchoType.Int, 777));
        var result = Serializer.Deserialize<ObjectWithFormerlySerializedAs>(tag)!;
        Assert.Equal(777, result.NewName);
    }
}
