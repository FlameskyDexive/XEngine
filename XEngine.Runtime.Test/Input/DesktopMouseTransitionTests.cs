using System;
using System.Collections.Generic;
using System.Numerics;

using NSubstitute;
using Silk.NET.Input;
using Xunit;
using XEngine.Vector;

namespace XEngine.Runtime.Test;

public class DesktopMouseTransitionTests
{
    [Fact]
    public void CoalescedPressAndMove_PreservesPressOriginBeforeHeldMovement()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Move(10, 20);
        // GLFW's Position getter can already reflect the next move while dispatching MouseDown.
        fixture.SetPolledPosition(80, 90);
        fixture.Press(0);
        fixture.Move(80, 90);

        input.LateUpdate();
        Assert.True(input.GetMouseButtonDown(0));
        Assert.Equal(new Int2(10, 20), input.MousePosition);
        input.LateUpdate();
        Assert.True(input.GetMouseButton(0));
        Assert.False(input.GetMouseButtonDown(0));
        Assert.Equal(new Int2(80, 90), input.MousePosition);
        Assert.Equal(new Float2(70, 70), input.MouseDelta);
    }

    [Fact]
    public void CompleteDragBetweenTicks_DeliversPressThenHeldMovementThenRelease()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Move(10, 20);
        fixture.Press(0);
        fixture.Move(80, 90);
        fixture.Release(0);

        input.LateUpdate();
        Assert.True(input.GetMouseButtonDown(0));
        Assert.Equal(new Int2(10, 20), input.MousePosition);
        input.LateUpdate();
        Assert.True(input.GetMouseButton(0));
        Assert.False(input.GetMouseButtonUp(0));
        Assert.Equal(new Int2(80, 90), input.MousePosition);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
        Assert.Equal(new Int2(80, 90), input.MousePosition);
        input.LateUpdate();
        Assert.False(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void QueuedClicksAtDifferentPositions_KeepTheirOwnCoordinates()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Move(10, 20);
        fixture.Press(0);
        fixture.Release(0);
        fixture.Move(80, 90);
        fixture.Press(0);
        fixture.Release(0);

        for (int tick = 0; tick < 4; tick++)
        {
            input.LateUpdate();
            Assert.Equal(tick % 2 == 0, input.GetMouseButtonDown(0));
            Assert.Equal(tick % 2 == 1, input.GetMouseButtonUp(0));
            Assert.Equal(tick < 2 ? new Int2(10, 20) : new Int2(80, 90), input.MousePosition);
        }
    }

    [Fact]
    public void ButtonsPressedAtDifferentPositions_AreDeliveredAtTheirOwnPointerPosition()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Move(10, 20);
        fixture.Press(0);
        fixture.Move(80, 90);
        fixture.Press(1);

        input.LateUpdate();
        Assert.True(input.GetMouseButtonDown(0));
        Assert.False(input.GetMouseButton(1));
        Assert.Equal(new Int2(10, 20), input.MousePosition);
        input.LateUpdate();
        Assert.True(input.GetMouseButton(0));
        Assert.True(input.GetMouseButtonDown(1));
        Assert.Equal(new Int2(80, 90), input.MousePosition);
    }

    [Fact]
    public void ClickBetweenTicks_DeliversPressAndReleaseOnSuccessiveTicks()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);

        fixture.Press(0);
        fixture.Release(0);
        input.LateUpdate();

        Assert.True(input.GetMouseButtonDown(0));
        Assert.True(input.GetMouseButton(0));
        Assert.False(input.GetMouseButtonUp(0));

        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
        Assert.False(input.GetMouseButton(0));
        Assert.False(input.GetMouseButtonDown(0));

        input.LateUpdate();
        Assert.False(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void DoubleClickBetweenTicks_PreservesBothClicks()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Press(0);
        fixture.Release(0);
        fixture.Press(0);
        fixture.Release(0);

        for (int tick = 0; tick < 4; tick++)
        {
            input.LateUpdate();
            Assert.Equal(tick % 2 == 0, input.GetMouseButtonDown(0));
            Assert.Equal(tick % 2 == 1, input.GetMouseButtonUp(0));
        }
        input.LateUpdate();
        Assert.False(input.GetMouseButtonDown(0));
        Assert.False(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void HeldButton_DoesNotRepeatEdges_AndOtherButtonsAdvanceIndependently()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Press(0);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonDown(0));

        fixture.Press(1);
        fixture.Release(1);
        input.LateUpdate();
        Assert.True(input.GetMouseButton(0));
        Assert.False(input.GetMouseButtonDown(0));
        Assert.True(input.GetMouseButtonDown(1));
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(1));
        Assert.True(input.GetMouseButton(0));

        fixture.Release(0);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void ReleasingOneOfTwoMice_DoesNotReleaseTheAggregateButton()
    {
        var fixture = new Fixture(2);
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.Press(0, 0);
        input.LateUpdate();
        fixture.Press(0, 1);
        fixture.Release(0, 0);
        input.LateUpdate();
        Assert.True(input.GetMouseButton(0));
        Assert.False(input.GetMouseButtonDown(0));
        Assert.False(input.GetMouseButtonUp(0));
        fixture.Release(0, 1);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void PolledDeviceWithoutCallbacks_StillDeliversButtonChanges()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.SetHeld(0, true);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonDown(0));
        input.LateUpdate();
        Assert.False(input.GetMouseButtonDown(0));
        fixture.SetHeld(0, false);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void HotpluggedMouse_RetainsShortClicks_AndDisconnectReleasesHeldButton()
    {
        var fixture = new Fixture(2);
        fixture.SetConnected(1, false);
        using var input = new DefaultInputHandler(fixture.Context);
        fixture.SetConnected(1, true);
        fixture.Press(0, 1);
        fixture.Release(0, 1);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonDown(0));
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
        fixture.Press(0, 1);
        input.LateUpdate();
        fixture.SetConnected(1, false);
        input.LateUpdate();
        Assert.True(input.GetMouseButtonUp(0));
    }

    [Fact]
    public void BurstOfClicks_HasNoQueueCapacityLimit_AndDrainsWithoutStaleEdges()
    {
        var fixture = new Fixture();
        using var input = new DefaultInputHandler(fixture.Context);
        for (int click = 0; click < 100; click++)
        {
            fixture.Press(0);
            fixture.Release(0);
        }
        for (int tick = 0; tick < 200; tick++)
        {
            input.LateUpdate();
            Assert.Equal(tick % 2 == 0, input.GetMouseButtonDown(0));
            Assert.Equal(tick % 2 == 1, input.GetMouseButtonUp(0));
        }
        input.LateUpdate();
        Assert.False(input.GetMouseButtonDown(0));
        Assert.False(input.GetMouseButtonUp(0));
    }

    private sealed class Fixture
    {
        public IInputContext Context { get; } = Substitute.For<IInputContext>();
        private readonly IMouse[] _mice;
        private readonly bool[][] _held;

        public Fixture(int mouseCount = 1)
        {
            _mice = new IMouse[mouseCount];
            _held = new bool[mouseCount][];
            for (int i = 0; i < mouseCount; i++)
            {
                int device = i;
                _held[i] = new bool[12];
                _mice[i] = Substitute.For<IMouse>();
                _mice[i].Position.Returns(Vector2.Zero);
                _mice[i].IsButtonPressed(Arg.Any<Silk.NET.Input.MouseButton>())
                    .Returns(call => _held[device][(int)call.Arg<Silk.NET.Input.MouseButton>()]);
            }
            Context.Mice.Returns(_mice);
            Context.Keyboards.Returns(Array.Empty<IKeyboard>());
            Context.Gamepads.Returns(Array.Empty<IGamepad>());
        }

        public void Press(int button, int device = 0)
        {
            _held[device][button] = true;
            _mice[device].MouseDown += Raise.Event<Action<IMouse, Silk.NET.Input.MouseButton>>(
                _mice[device], (Silk.NET.Input.MouseButton)button);
        }

        public void Release(int button, int device = 0)
        {
            _held[device][button] = false;
            _mice[device].MouseUp += Raise.Event<Action<IMouse, Silk.NET.Input.MouseButton>>(
                _mice[device], (Silk.NET.Input.MouseButton)button);
        }

        public void SetHeld(int button, bool held) => _held[0][button] = held;

        public void SetPolledPosition(float x, float y) => _mice[0].Position.Returns(new Vector2(x, y));

        public void Move(float x, float y)
        {
            SetPolledPosition(x, y);
            _mice[0].MouseMove += Raise.Event<Action<IMouse, Vector2>>(_mice[0], new Vector2(x, y));
        }

        public void SetConnected(int device, bool connected)
        {
            Context.Mice.Returns(connected ? _mice : new[] { _mice[0] });
            Context.ConnectionChanged += Raise.Event<Action<IInputDevice, bool>>(_mice[device], connected);
        }
    }
}
