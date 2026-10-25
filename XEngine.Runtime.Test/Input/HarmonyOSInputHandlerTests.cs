// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test.Input;

public sealed class HarmonyOSInputHandlerTests
{
    [Fact]
    public void FocusLoss_CancelsActiveTouchesUntilTheFrameBoundary()
    {
        var input = new HarmonyOSInputHandler();
        input.TouchBegan(7, 10, 20, 1);

        input.FocusLost();

        Assert.Equal(1, input.TouchCount);
        Assert.Equal(TouchPhase.Canceled, input.GetTouch(0).Phase);
        input.EndFrame();
        Assert.Equal(0, input.TouchCount);
    }

    [Fact]
    public void TouchEnd_PreservesEndedStateForOneFrame()
    {
        var input = new HarmonyOSInputHandler();
        input.TouchBegan(1, 0, 0, 1);
        input.EndFrame();
        input.TouchEnded(1, 2, 3, 0.5f);

        Assert.Equal(TouchPhase.Ended, input.GetTouch(0).Phase);
        input.EndFrame();
        Assert.Equal(0, input.TouchCount);
    }

    [Fact]
    public void BatchedPressAndMoves_PreservePressOriginThenLatestMove()
    {
        var input = new HarmonyOSInputHandler();
        input.TouchBegan(1, 100, 200, 1);
        input.TouchMoved(1, 110, 180, 1);
        input.TouchMoved(1, 120, 160, 1);
        Assert.Equal(TouchPhase.Began, input.GetTouch(0).Phase);
        Assert.Equal(new XEngine.Vector.Float2(100, 200), input.GetTouch(0).Position);
        input.EndFrame();
        Assert.Equal(TouchPhase.Moved, input.GetTouch(0).Phase);
        Assert.Equal(new XEngine.Vector.Float2(120, 160), input.GetTouch(0).Position);
        input.TouchEnded(1, 120, 160, 0);
        Assert.Equal(TouchPhase.Ended, input.GetTouch(0).Phase);
        input.EndFrame();
        Assert.Equal(0, input.TouchCount);
    }

    [Fact]
    public void RepeatedBatchedTaps_DeliverBothEdgesWithoutLeavingStuckPointers()
    {
        var input = new HarmonyOSInputHandler();
        for (int i = 0; i < 50; i++)
        {
            input.TouchBegan(1, 100, 200, 1);
            input.TouchEnded(1, 100, 200, 0);
            Assert.Equal(TouchPhase.Began, input.GetTouch(0).Phase);
            input.EndFrame();
            Assert.Equal(TouchPhase.Ended, input.GetTouch(0).Phase);
            input.EndFrame();
            Assert.Equal(0, input.TouchCount);
        }
    }

    [Fact]
    public void PhysicalKeyCodes_MapToNormalizedKeysAndFocusLossReleasesThem()
    {
        var input = new HarmonyOSInputHandler();

        Assert.True(input.HandleKey(2017, true));
        Assert.True(input.HandleKey(2054, true));
        Assert.True(input.HandleKey(2043, true));
        Assert.True(input.HandleKey(2056, true));
        Assert.True(input.HandleKey(2074, true));
        Assert.True(input.HandleKey(2079, true));
        Assert.True(input.HandleKey(2102, true));
        Assert.True(input.HandleKey(2113, true));
        Assert.True(input.HandleKey(2119, true));
        Assert.True(input.HandleKey(2816, true));
        Assert.True(input.GetKey(KeyCode.A));
        Assert.True(input.GetKey(KeyCode.Enter));
        Assert.True(input.GetKey(KeyCode.Comma));
        Assert.True(input.GetKey(KeyCode.GraveAccent));
        Assert.True(input.GetKey(KeyCode.CapsLock));
        Assert.True(input.GetKey(KeyCode.PrintScreen));
        Assert.True(input.GetKey(KeyCode.NumLock));
        Assert.True(input.GetKey(KeyCode.KeypadDivide));
        Assert.True(input.GetKey(KeyCode.KeypadEnter));
        Assert.True(input.GetKey(KeyCode.F13));

        input.FocusLost();

        Assert.False(input.GetKey(KeyCode.A));
        Assert.False(input.GetKey(KeyCode.Enter));
        Assert.False(input.GetKey(KeyCode.KeypadDivide));
        Assert.False(input.GetKey(KeyCode.F13));
        Assert.True(input.GetKeyUp(KeyCode.A));
        Assert.True(input.GetKeyUp(KeyCode.Enter));
        Assert.True(input.GetKeyUp(KeyCode.KeypadDivide));
        Assert.True(input.GetKeyUp(KeyCode.F13));
    }

    [Fact]
    public void PhysicalMouseEvents_MapButtonsMoveAndFocusLossReleasesThem()
    {
        var input = new HarmonyOSInputHandler();

        Assert.True(input.HandleMouse(HarmonyOSMouseAction.Press, 2, 11f, 12f));
        Assert.True(input.GetMouseButton((int)MouseButton.Right));
        Assert.True(input.GetMouseButtonDown((int)MouseButton.Right));
        Assert.Equal(new XEngine.Vector.Int2(11, 12), input.MousePosition);

        Assert.True(input.HandleMouse(HarmonyOSMouseAction.Move, 0, 21f, 22f));
        Assert.Equal(new XEngine.Vector.Int2(21, 22), input.MousePosition);
        Assert.Equal(new XEngine.Vector.Float2(10f, 10f), input.MouseDelta);

        Assert.True(input.HandleMouse(HarmonyOSMouseAction.Release, 2, 21f, 22f));
        Assert.False(input.GetMouseButton((int)MouseButton.Right));
        Assert.True(input.GetMouseButtonUp((int)MouseButton.Right));

        Assert.True(input.HandleMouse(HarmonyOSMouseAction.Press, 16, 21f, 22f));
        input.FocusLost();
        Assert.False(input.GetMouseButton((int)MouseButton.Button5));
        Assert.True(input.GetMouseButtonUp((int)MouseButton.Button5));

        input.EndFrame();
        input.TouchBegan(7, 30f, 31f, 1f);
        Assert.True(input.HandleMouse(HarmonyOSMouseAction.Press, 1, 30f, 31f));
        input.TouchEnded(7, 30f, 31f, 1f);
        Assert.True(input.GetMouseButton((int)MouseButton.Left));

        Assert.True(input.HandleMouse(HarmonyOSMouseAction.Release, 1, 30f, 31f));
        Assert.False(input.GetMouseButton((int)MouseButton.Left));
    }

    [Fact]
    public void MouseWheel_AccumulatesFiniteDeltasForOneFrame()
    {
        var input = new HarmonyOSInputHandler();

        Assert.True(input.HandleMouseWheel(1.25f));
        Assert.True(input.HandleMouseWheel(-0.5f));
        Assert.False(input.HandleMouseWheel(float.NaN));
        Assert.Equal(0.75f, input.MouseWheelDelta);

        input.EndFrame();

        Assert.Equal(0f, input.MouseWheelDelta);
    }

    [Fact]
    public void NonFinitePointerSamples_AreIgnoredWithoutMutatingInputState()
    {
        var input = new HarmonyOSInputHandler();
        input.TouchBegan(7, 10f, 20f, 0.5f);
        int mouseEvents = 0;
        input.OnMouseEvent += (_, _, _, _, _) => mouseEvents++;

        input.TouchBegan(8, float.NaN, 30f, 0.5f);
        input.TouchMoved(7, float.PositiveInfinity, 21f, 0.5f);
        input.TouchEnded(7, 11f, 21f, float.NegativeInfinity);

        Assert.Equal(1, input.TouchCount);
        Assert.Equal(new XEngine.Vector.Float2(10f, 20f), input.GetTouch(0).Position);
        Assert.Equal(TouchPhase.Began, input.GetTouch(0).Phase);
        Assert.False(input.HandleMouse(HarmonyOSMouseAction.Move, 0, float.NaN, 12f));
        Assert.False(input.HandleMouse(HarmonyOSMouseAction.Press, 1, 12f, float.NegativeInfinity));
        Assert.Equal(new XEngine.Vector.Int2(10, 20), input.MousePosition);
        Assert.True(input.GetMouseButton((int)MouseButton.Left));
        Assert.Equal(0, mouseEvents);
    }
}
