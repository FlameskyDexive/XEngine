using System;
using System.Collections.Generic;
using System.Numerics;

using NSubstitute;
using Silk.NET.Input;
using Xunit;

namespace XEngine.Runtime.Test;

public class DesktopKeyboardTransitionTests
{
    [Theory]
    [InlineData(KeyCode.J)]
    [InlineData(KeyCode.Number0)]
    [InlineData(KeyCode.Escape)]
    public void TapBetweenTicks_DeliversBothEdgesExactlyOnce(KeyCode key)
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        var events = new List<(KeyCode, bool)>();
        input.OnKeyEvent += (code, down) => events.Add((code, down));
        fixture.Press(key);
        fixture.Release(key);

        input.LateUpdate();
        Assert.True(input.GetKeyDown(key));
        Assert.True(input.GetKey(key));
        Assert.False(input.GetKeyUp(key));
        input.LateUpdate();
        Assert.True(input.GetKeyUp(key));
        Assert.False(input.GetKey(key));
        Assert.False(input.GetKeyDown(key));
        input.LateUpdate();
        Assert.False(input.GetKeyUp(key));
        Assert.Equal(new[] { (key, true), (key, false) }, events);
    }

    [Fact]
    public void HeldAndRepeatedKeys_DoNotRepeatEdges_AndChordKeysAdvanceIndependently()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Press(KeyCode.ControlLeft);
        input.LateUpdate();
        Assert.True(input.GetKeyDown(KeyCode.ControlLeft));
        fixture.Press(KeyCode.ControlLeft);
        fixture.Press(KeyCode.S);
        fixture.Release(KeyCode.S);
        input.LateUpdate();
        Assert.True(input.GetKey(KeyCode.ControlLeft));
        Assert.False(input.GetKeyDown(KeyCode.ControlLeft));
        Assert.True(input.GetKeyDown(KeyCode.S));
        input.LateUpdate();
        Assert.True(input.GetKeyUp(KeyCode.S));
        fixture.Release(KeyCode.ControlLeft);
        input.LateUpdate();
        Assert.True(input.GetKeyUp(KeyCode.ControlLeft));
    }

    [Fact]
    public void ReleasingOneOfTwoKeyboards_DoesNotReleaseTheAggregateKey()
    {
        var fixture = new Fixture(2);
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Press(KeyCode.J, 0);
        input.LateUpdate();
        fixture.Press(KeyCode.J, 1);
        fixture.Release(KeyCode.J, 0);
        input.LateUpdate();
        Assert.True(input.GetKey(KeyCode.J));
        Assert.False(input.GetKeyDown(KeyCode.J));
        Assert.False(input.GetKeyUp(KeyCode.J));
        fixture.Release(KeyCode.J, 1);
        input.LateUpdate();
        Assert.True(input.GetKeyUp(KeyCode.J));
    }

    [Fact]
    public void PolledKeyboardWithoutCallbacks_PreservesNumericAliasEdges()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.SetHeld(KeyCode.Number0, true);
        input.LateUpdate();
        Assert.True(input.GetKeyDown(KeyCode.D0));
        input.LateUpdate();
        Assert.False(input.GetKeyDown(KeyCode.Number0));
        fixture.SetHeld(KeyCode.Number0, false);
        input.LateUpdate();
        Assert.True(input.GetKeyUp(KeyCode.D0));
    }

    [Fact]
    public void HotpluggedKeyboard_RetainsTaps_AndDisconnectReleasesHeldKey()
    {
        var fixture = new Fixture(2);
        fixture.SetConnected(1, false);
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.SetConnected(1, true);
        fixture.Press(KeyCode.J, 1);
        fixture.Release(KeyCode.J, 1);
        input.LateUpdate();
        Assert.True(input.GetKeyDown(KeyCode.J));
        input.LateUpdate();
        Assert.True(input.GetKeyUp(KeyCode.J));
        fixture.Press(KeyCode.J, 1);
        input.LateUpdate();
        fixture.SetConnected(1, false);
        input.LateUpdate();
        Assert.True(input.GetKeyUp(KeyCode.J));
    }

    [Fact]
    public void BurstOfTaps_DrainsWithoutStaleEdges_AndUnknownKeysAreIgnored()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Press(KeyCode.Unknown);
        fixture.Release(KeyCode.Unknown);
        for (int tap = 0; tap < 100; tap++)
        {
            fixture.Press(KeyCode.J);
            fixture.Release(KeyCode.J);
        }
        for (int tick = 0; tick < 200; tick++)
        {
            input.LateUpdate();
            Assert.Equal(tick % 2 == 0, input.GetKeyDown(KeyCode.J));
            Assert.Equal(tick % 2 == 1, input.GetKeyUp(KeyCode.J));
        }
        input.LateUpdate();
        Assert.False(input.IsAnyKeyDown);
        Assert.False(input.GetKeyDown(KeyCode.J));
        Assert.False(input.GetKeyUp(KeyCode.J));
    }

    private sealed class Fixture
    {
        public IInputContext Context { get; } = Substitute.For<IInputContext>();
        private readonly IKeyboard[] _keyboards;
        private readonly HashSet<KeyCode>[] _held;

        public Fixture(int count = 1)
        {
            _keyboards = new IKeyboard[count];
            _held = new HashSet<KeyCode>[count];
            for (int i = 0; i < count; i++)
            {
                int device = i;
                _held[i] = new HashSet<KeyCode>();
                _keyboards[i] = Substitute.For<IKeyboard>();
                _keyboards[i].IsKeyPressed(Arg.Any<Key>())
                    .Returns(call => _held[device].Contains((KeyCode)call.Arg<Key>()));
            }
            var mouse = Substitute.For<IMouse>();
            mouse.Position.Returns(Vector2.Zero);
            Context.Mice.Returns(new[] { mouse });
            Context.Keyboards.Returns(_keyboards);
            Context.Gamepads.Returns(Array.Empty<IGamepad>());
        }

        public void Press(KeyCode key, int device = 0)
        {
            _held[device].Add(key);
            _keyboards[device].KeyDown += Raise.Event<Action<IKeyboard, Key, int>>(
                _keyboards[device], (Key)key, 0);
        }

        public void Release(KeyCode key, int device = 0)
        {
            _held[device].Remove(key);
            _keyboards[device].KeyUp += Raise.Event<Action<IKeyboard, Key, int>>(
                _keyboards[device], (Key)key, 0);
        }

        public void SetHeld(KeyCode key, bool held)
        {
            if (held) _held[0].Add(key);
            else _held[0].Remove(key);
        }

        public void SetConnected(int device, bool connected)
        {
            Context.Keyboards.Returns(connected ? _keyboards : new[] { _keyboards[0] });
            Context.ConnectionChanged += Raise.Event<Action<IInputDevice, bool>>(_keyboards[device], connected);
        }
    }
}
