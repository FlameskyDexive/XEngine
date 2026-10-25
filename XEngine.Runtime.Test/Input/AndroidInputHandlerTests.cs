// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.Android;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Input;

public sealed class AndroidInputHandlerTests
{
    [Fact]
    public void KeyMappingPreservesEdgesUntilFrameEnd()
    {
        var input = new AndroidInputHandler();

        Assert.True(input.HandleKey(29, true));
        Assert.True(input.GetKey(KeyCode.A));
        Assert.True(input.GetKeyDown(KeyCode.A));
        Assert.False(input.GetKeyUp(KeyCode.A));

        input.EndFrame();
        Assert.True(input.GetKey(KeyCode.A));
        Assert.False(input.GetKeyDown(KeyCode.A));

        Assert.True(input.HandleKey(29, false));
        Assert.False(input.GetKey(KeyCode.A));
        Assert.True(input.GetKeyUp(KeyCode.A));
        input.EndFrame();
        Assert.False(input.GetKeyUp(KeyCode.A));
    }

    [Fact]
    public void FocusLossClearsKeysTouchesAndMouse()
    {
        var input = new AndroidInputHandler();
        input.HandleKey(4, true);
        input.TouchBegan(11, 12.5f, 24.5f, 0.75f);
        Assert.True(input.GetMouseButton(0));
        Assert.Equal(1, input.TouchCount);

        input.FocusLost();

        Assert.False(input.GetKey(KeyCode.Escape));
        Assert.True(input.GetKeyUp(KeyCode.Escape));
        Assert.False(input.GetMouseButton(0));
        Assert.True(input.GetMouseButtonUp(0));
        Assert.Equal(0, input.TouchCount);
    }

    [Fact]
    public void TouchPhasesAndMouseBridgeAreStableAcrossFrameBoundary()
    {
        var input = new AndroidInputHandler();
        input.TouchBegan(1, 10, 20, 0.5f);
        input.TouchBegan(2, 30, 40, 1f);
        Assert.Equal(2, input.TouchCount);
        Assert.Equal(TouchPhase.Began, input.GetTouch(0).Phase);
        Assert.True(input.GetMouseButtonDown(0));

        input.EndFrame();
        input.TouchMoved(1, 15, 25, 0.6f);
        Assert.Equal(TouchPhase.Moved, input.GetTouch(0).Phase);
        Assert.Equal(new Int2(15, 25), input.MousePosition);

        input.TouchEnded(1, 15, 25, 0.6f);
        Assert.Equal(TouchPhase.Ended, input.GetTouch(0).Phase);
        Assert.True(input.GetMouseButtonUp(0));
        input.EndFrame();
        Assert.Equal(1, input.TouchCount);
        Assert.Equal(2, input.GetTouch(0).PointerId);
        input.EndFrame();
        Assert.Equal(1, input.TouchCount);
        Assert.Equal(TouchPhase.Stationary, input.GetTouch(0).Phase);
    }

    [Fact]
    public void TouchMotionHistoryPreservesNewestSamplesUntilFrameEnd()
    {
        var input = new AndroidInputHandler();
        input.TouchBegan(7, 10, 20, 0.5f);
        for (int sample = 0; sample < 10; sample++)
            input.TouchHistory(7, sample, sample + 1, sample / 10f, 1000 + sample);
        input.TouchMoved(7, 20, 30, 1f);

        Assert.Equal(8, input.GetTouchHistoryCount(0));
        Assert.Equal(new TouchHistorySample(7, new Float2(2, 3), 0.2f, 1002), input.GetTouchHistory(0, 0));
        Assert.Equal(new TouchHistorySample(7, new Float2(9, 10), 0.9f, 1009), input.GetTouchHistory(0, 7));
        Assert.Equal(new Float2(20, 30), input.GetTouch(0).Position);

        input.EndFrame();

        Assert.Equal(0, input.GetTouchHistoryCount(0));
    }

    [Fact]
    public void TouchMotionHistoryIsClearedAtFrameBoundaryWhenOtherPointerEnds()
    {
        var input = new AndroidInputHandler();
        input.TouchBegan(1, 0, 0, 1f);
        input.TouchBegan(2, 0, 0, 1f);
        input.TouchHistory(2, 4, 5, 0.6f, 42);
        input.TouchEnded(1, 0, 0, 1f);

        input.EndFrame();

        Assert.Equal(1, input.TouchCount);
        Assert.Equal(2, input.GetTouch(0).PointerId);
        Assert.Equal(0, input.GetTouchHistoryCount(0));
    }

    [Fact]
    public void AndroidTouchCoordinatesAndSafeInsetsMapToFramebufferPixels()
    {
        AndroidInputCoordinateTransform transform = AndroidInputCoordinateTransform.Create(540, 1200, 1080, 2400);

        Assert.Equal(new Float2(540, 1200), transform.Map(270, 600));
        WindowSafeAreaInsets insets = transform.MapInsets(new WindowSafeAreaInsets(15, 50, 20, 30));
        Assert.Equal(new WindowSafeAreaInsets(30, 100, 40, 60), insets);
        Assert.Equal(new WindowSafeArea(30, 100, 1010, 2240), insets.ToSafeArea(1080, 2400));
    }

    [Fact]
    public void ImeTextIsSeparateFromPhysicalKeys()
    {
        bool keyboardVisible = false;
        var input = new AndroidInputHandler(softKeyboard: visible => keyboardVisible = visible);
        TextInputState observed = default;
        input.OnTextInput += state => observed = state;

        input.SetTextInputEnabled(true);
        input.SetComposingText("世", 1);
        input.CommitText("界");

        Assert.True(keyboardVisible);
        Assert.Equal("界", input.InputString);
        Assert.Equal("界", input.TextInputState.Text);
        Assert.Equal(string.Empty, input.TextInputState.Composition);
        Assert.Equal(input.TextInputState, observed);
        Assert.False(input.GetKey(KeyCode.C));

        input.EndFrame();
        Assert.Equal(string.Empty, input.InputString);
    }

    [Fact]
    public void ImeCommitAndDeleteRespectSelection()
    {
        var input = new AndroidInputHandler();
        input.CommitText("hello");
        input.SetSelection(1, 3);

        input.CommitText("i");
        Assert.Equal("hio", input.TextInputState.Text);
        Assert.Equal(2, input.TextInputState.SelectionStart);

        input.SetSelection(2, -1);
        input.DeleteSurroundingText(1, 1);
        Assert.Equal(string.Empty, input.TextInputState.Text);
        Assert.Equal(0, input.TextInputState.SelectionStart);
    }

    [Fact]
    public void PrimaryPointerCanBeReassignedAfterItEnds()
    {
        var input = new AndroidInputHandler();
        input.TouchBegan(1, 10, 20, 1f);
        input.TouchEnded(1, 10, 20, 1f);
        input.EndFrame();

        input.TouchBegan(2, 30, 40, 1f);

        Assert.True(input.GetMouseButton(0));
        Assert.True(input.GetMouseButtonDown(0));
        Assert.Equal(new Int2(30, 40), input.MousePosition);
    }

    [Fact]
    public void GamepadButtonsAxesAndTriggersPreserveFrameEdges()
    {
        var input = new AndroidInputHandler();

        Assert.True(input.GamepadConnected(41));
        Assert.Equal(1, input.GetGamepadCount());
        Assert.True(input.HandleGamepadKey(41, 96, true));
        Assert.True(input.GetGamepadButton(0, GamepadButton.A));
        Assert.True(input.GetGamepadButtonDown(0, GamepadButton.A));
        Assert.True(input.HandleGamepadMotion(41, 0.5f, -0.75f, 0.25f, 0.5f, 0.7f, 0.8f, -1f, 1f));
        Assert.Equal(new Float2(0.5f, 0.75f), input.GetGamepadAxis(0, 0));
        Assert.Equal(new Float2(0.25f, -0.5f), input.GetGamepadAxis(0, 1));
        Assert.Equal(0.7f, input.GetGamepadTrigger(0, 0));
        Assert.Equal(0.8f, input.GetGamepadTrigger(0, 1));
        Assert.True(input.GetGamepadButton(0, GamepadButton.DPadLeft));
        Assert.True(input.GetGamepadButton(0, GamepadButton.DPadDown));

        input.EndFrame();

        Assert.False(input.GetGamepadButtonDown(0, GamepadButton.A));
        Assert.True(input.HandleGamepadKey(41, 96, false));
        Assert.True(input.GetGamepadButtonUp(0, GamepadButton.A));
    }

    [Fact]
    public void GamepadHotUnplugCompactsSlotsAndClearsState()
    {
        var input = new AndroidInputHandler();

        Assert.True(input.GamepadConnected(10));
        Assert.True(input.GamepadConnected(20));
        input.HandleGamepadKey(20, 97, true);
        input.GamepadDisconnected(10);

        Assert.Equal(1, input.GetGamepadCount());
        Assert.True(input.IsGamepadConnected(0));
        Assert.True(input.GetGamepadButton(0, GamepadButton.B));
        input.GamepadDisconnected(20);
        Assert.Equal(0, input.GetGamepadCount());
        Assert.False(input.GetGamepadButton(0, GamepadButton.B));
    }

    [Fact]
    public void FocusLossClearsGamepadButtonsAndAxes()
    {
        var input = new AndroidInputHandler();

        input.HandleGamepadKey(5, 102, true);
        input.HandleGamepadMotion(5, 0.8f, 0.2f, 0f, 0f, 0.4f, 0f, 0f, 0f);
        input.FocusLost();

        Assert.True(input.IsGamepadConnected(0));
        Assert.False(input.GetGamepadButton(0, GamepadButton.LeftBumper));
        Assert.True(input.GetGamepadButtonUp(0, GamepadButton.LeftBumper));
        Assert.Equal(Float2.Zero, input.GetGamepadAxis(0, 0));
        Assert.Equal(0f, input.GetGamepadTrigger(0, 0));
    }
}
