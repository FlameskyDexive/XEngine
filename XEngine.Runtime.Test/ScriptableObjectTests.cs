// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Tests for the <see cref="ScriptableObject"/> base type: lifecycle (OnEnable/OnDisable) exactly-once
/// semantics, the <see cref="ScriptableObject.CreateInstance{T}"/> factory, disposal routing, and Echo
/// round-tripping. Lifecycle-through-asset-backend is covered by the editor importer tests, since
/// it requires a live <see cref="AssetBackendBase"/>.
/// </summary>
public class ScriptableObjectTests
{
    /// <summary>A concrete ScriptableObject subclass that records every lifecycle callback it receives.</summary>
    private sealed class LifecycleTracker : ScriptableObject
    {
        // Lifecycle counters are runtime-only state - never persist them, otherwise a round-trip
        // would carry the source instance's counts into the deserialized copy.
        [SerializeIgnore] public int EnableCount;
        [SerializeIgnore] public int DisableCount;
        [SerializeIgnore] public int ValidateCount;

        [SerializeField] public int SerializedField = 7;
        [SerializeField] public string Name2 = "default";
        [SerializeIgnore] public int Ignored = 99;
        public int PublicUnmarked = 3;

        protected override void OnEnable() => EnableCount++;
        protected override void OnDisable() => DisableCount++;
        public override void OnValidate() => ValidateCount++;
    }

    // ── CreateInstance ──────────────────────────────────────────

    [Fact]
    public void CreateInstance_Generic_FiresOnEnableExactlyOnce()
    {
        var so = ScriptableObject.CreateInstance<LifecycleTracker>();

        Assert.Equal(1, so.EnableCount);
        Assert.Equal(0, so.DisableCount);
        Assert.True(so.IsEnabled);
    }

    [Fact]
    public void CreateInstance_TypeOverload_FiresOnEnable()
    {
        var so = (LifecycleTracker)ScriptableObject.CreateInstance(typeof(LifecycleTracker));

        Assert.Equal(1, so.EnableCount);
        Assert.True(so.IsEnabled);
    }

    [Fact]
    public void CreateInstance_StringOverload_FiresOnEnable()
    {
        var so = ScriptableObject.CreateInstance(typeof(LifecycleTracker).AssemblyQualifiedName!) as LifecycleTracker;

        Assert.NotNull(so);
        Assert.Equal(1, so!.EnableCount);
    }

    [Fact]
    public void CreateInstance_StringOverload_ReturnsNullForUnknownType()
    {
        Assert.Null(ScriptableObject.CreateInstance("Namespace.Does.Not.Exist"));
        Assert.Null(ScriptableObject.CreateInstance(""));
        Assert.Null(ScriptableObject.CreateInstance("   "));
    }

    [Fact]
    public void CreateInstance_TypeOverload_RejectsNonScriptableObject()
    {
        Assert.Throws<ArgumentException>(() => ScriptableObject.CreateInstance(typeof(NotAScriptableObject)));
    }

    [Fact]
    public void CreateInstance_TypeOverload_RejectsAbstractType()
    {
        Assert.Throws<ArgumentException>(() => ScriptableObject.CreateInstance(typeof(ScriptableObject)));
    }

    private sealed class NotAScriptableObject { }

    private sealed class PrivateConstructorTracker : ScriptableObject
    {
        private PrivateConstructorTracker() { }

        [SerializeIgnore] public int EnableCount;
        protected override void OnEnable() => EnableCount++;
    }

    private sealed class OpenGenericTracker<T> : ScriptableObject { }

    [Fact]
    public void CreateInstance_Generic_AllowsNonPublicParameterlessConstructor()
    {
        var so = ScriptableObject.CreateInstance<PrivateConstructorTracker>();

        Assert.Equal(1, so.EnableCount);
    }

    [Fact]
    public void CreateInstance_TypeOverload_RejectsNullAndOpenGenericType()
    {
        Assert.Throws<ArgumentNullException>(() => ScriptableObject.CreateInstance((Type)null!));
        Assert.Throws<ArgumentException>(() => ScriptableObject.CreateInstance(typeof(OpenGenericTracker<>)));
    }

    [Fact]
    public void CreateInstance_StringOverload_ReturnsNullForNonScriptableObject()
    {
        Assert.Null(ScriptableObject.CreateInstance(typeof(string).AssemblyQualifiedName!));
        Assert.Null(ScriptableObject.CreateInstance(typeof(ScriptableObject).AssemblyQualifiedName!));
    }

    // ── new does NOT fire OnEnable (matches Unity) ──────────────

    [Fact]
    public void New_DoesNotFireOnEnable()
    {
        var so = new LifecycleTracker();

        Assert.Equal(0, so.EnableCount);
        Assert.False(so.IsEnabled);
    }

    // ── OnDisable routing through Dispose ───────────────────────

    [Fact]
    public void Dispose_FiresOnDisableExactlyOnce_WhenEnabled()
    {
        var so = ScriptableObject.CreateInstance<LifecycleTracker>();

        so.Dispose();
        so.Dispose(); // idempotent

        Assert.Equal(1, so.DisableCount);
        Assert.False(so.IsEnabled);
    }

    [Fact]
    public void Dispose_DoesNotFireOnDisable_WhenNeverEnabled()
    {
        // Constructed via new, never enabled - OnDisable must not fire (no OnEnable to pair with).
        var so = new LifecycleTracker();

        so.Dispose();

        Assert.Equal(0, so.DisableCount);
    }

    [Fact]
    public void DisposedInstance_CannotBeEnabledAgain()
    {
        var so = ScriptableObject.CreateInstance<LifecycleTracker>();

        so.Dispose();
        so.NotifyEnabled();

        Assert.Equal(1, so.EnableCount);
        Assert.Equal(1, so.DisableCount);
        Assert.False(so.IsEnabled);
    }

    // ── Idempotency of NotifyEnabled / NotifyDisabled ───────────

    [Fact]
    public void NotifyEnabled_IsIdempotent()
    {
        var so = new LifecycleTracker();

        so.NotifyEnabled();
        so.NotifyEnabled();
        so.NotifyEnabled();

        Assert.Equal(1, so.EnableCount);
    }

    [Fact]
    public void EnableDisableEnable_FiresOnEnableTwice()
    {
        var so = new LifecycleTracker();

        so.NotifyEnabled();
        so.NotifyDisabled();
        so.NotifyEnabled();

        Assert.Equal(2, so.EnableCount);
        Assert.Equal(1, so.DisableCount);
    }

    // ── OnValidate ──────────────────────────────────────────────

    [Fact]
    public void OnValidate_IsOverridableAndCallable()
    {
        var so = new LifecycleTracker();

        so.OnValidate();
        so.OnValidate();

        Assert.Equal(2, so.ValidateCount);
    }

    // ── Exception isolation ─────────────────────────────────────

    private sealed class ThrowingOnEnable : ScriptableObject
    {
        protected override void OnEnable() => throw new InvalidOperationException("boom");
    }

    private sealed class ThrowingOnDisable : ScriptableObject
    {
        protected override void OnDisable() => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void OnEnable_Throwing_DoesNotPropagate()
    {
        // An exception in OnEnable must not escape CreateInstance - the instance is still returned
        // (the asset is resident regardless). This keeps a bad ScriptableObject from breaking the load path.
        var so = ScriptableObject.CreateInstance<ThrowingOnEnable>();

        Assert.NotNull(so);
        // IsEnabled stays true even though OnEnable threw, so a later Dispose still attempts OnDisable.
        Assert.True(so.IsEnabled);
    }

    [Fact]
    public void OnDisable_Throwing_DoesNotPropagate()
    {
        var so = ScriptableObject.CreateInstance<ThrowingOnDisable>();

        Exception? exception = Record.Exception(so.Dispose);

        Assert.Null(exception);
        Assert.True(so.IsDisposed);
        Assert.False(so.IsEnabled);
    }

    [CreateAssetMenu("Inherited Menu Test")]
    private class MenuBase : ScriptableObject { }

    private sealed class MenuDerived : MenuBase { }

    [Fact]
    public void CreateAssetMenu_IsNotInheritedByDerivedTypes()
    {
        Assert.NotNull(Attribute.GetCustomAttribute(typeof(MenuBase), typeof(CreateAssetMenuAttribute)));
        Assert.Null(Attribute.GetCustomAttribute(typeof(MenuDerived), typeof(CreateAssetMenuAttribute)));
    }

    // ── Echo round-trip ─────────────────────────────────────────

    [Fact]
    public void Serialize_RoundTrip_PreservesFields()
    {
        var original = new LifecycleTracker { SerializedField = 42, Name2 = "hello", PublicUnmarked = 5 };

        // typeof(object) forces a $type envelope so a typed deserialize recovers the subtype,
        // matching how AssetCreateMenu writes .asset files.
        var echo = Serializer.Serialize(typeof(object), original);
        Assert.NotNull(echo);

        var deserialized = Serializer.Deserialize<LifecycleTracker>(echo);

        Assert.NotNull(deserialized);
        Assert.Equal(42, deserialized!.SerializedField);
        Assert.Equal("hello", deserialized.Name2);
        Assert.Equal(5, deserialized.PublicUnmarked);
    }

    [Fact]
    public void Serialize_RoundTrip_RespectsSerializeIgnore()
    {
        var original = new LifecycleTracker { Ignored = 12345 };

        var echo = Serializer.Serialize(typeof(object), original);
        var deserialized = Serializer.Deserialize<LifecycleTracker>(echo);

        // Ignored is reset to its default because it was never written.
        Assert.Equal(99, deserialized!.Ignored);
    }

    [Fact]
    public void Deserialize_DoesNotFireOnEnable()
    {
        // Deserialization constructs the object directly; OnEnable only fires when the asset backend
        // makes it resident (covered in the editor tests). A bare deserialize must not enable it.
        var original = ScriptableObject.CreateInstance<LifecycleTracker>();
        var echo = Serializer.Serialize(typeof(object), original);

        var deserialized = Serializer.Deserialize<LifecycleTracker>(echo);

        Assert.Equal(0, deserialized!.EnableCount);
    }

    // ── EngineObject identity still works ───────────────────────

    [Fact]
    public void ScriptableObject_InheritsEngineObjectIdentity()
    {
        var a = ScriptableObject.CreateInstance<LifecycleTracker>();
        var b = ScriptableObject.CreateInstance<LifecycleTracker>();

        Assert.NotEqual(a.InstanceID, b.InstanceID);
        Assert.True(a.IsValid());
        Assert.False(a.IsNotValid());

        a.Dispose();
        Assert.True(a.IsDisposed);
        Assert.True(a.IsNotValid());
    }
}
