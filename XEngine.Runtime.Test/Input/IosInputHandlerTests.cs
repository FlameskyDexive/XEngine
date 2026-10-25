// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.iOS;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Input;

public sealed class IosInputHandlerTests
{
    [Fact]
    public void HidKeyMappingPreservesEdgesUntilFrameEnd()
    {
        var input = new IosInputHandler();

        Assert.True(input.HandleKey(4, true));
        Assert.True(input.GetKey(KeyCode.A));
        Assert.True(input.GetKeyDown(KeyCode.A));
        Assert.False(input.GetKeyUp(KeyCode.A));

        input.EndFrame();
        Assert.True(input.GetKey(KeyCode.A));
        Assert.False(input.GetKeyDown(KeyCode.A));

        Assert.True(input.HandleKey(4, false));
        Assert.False(input.GetKey(KeyCode.A));
        Assert.True(input.GetKeyUp(KeyCode.A));
        input.EndFrame();
        Assert.False(input.GetKeyUp(KeyCode.A));
    }

    [Fact]
    public void HidBackspaceMapsToKeyCodeBackspace()
    {
        var input = new IosInputHandler();
        Assert.True(input.HandleKey(42, true));
        Assert.True(input.GetKey(KeyCode.Backspace));
    }

    [Fact]
    public void FocusLossClearsKeysTouchesAndMouse()
    {
        var input = new IosInputHandler();
        input.HandleKey(41, true);
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
        var input = new IosInputHandler();
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
    public void IosTouchCoordinatesAndSafeInsetsMapToFramebufferPixels()
    {
        IosInputCoordinateTransform transform = IosInputCoordinateTransform.Create(540, 1200, 1080, 2400);

        Assert.Equal(new Float2(540, 1200), transform.Map(270, 600));
        WindowSafeAreaInsets insets = transform.MapInsets(new WindowSafeAreaInsets(15, 50, 20, 30));
        Assert.Equal(new WindowSafeAreaInsets(30, 100, 40, 60), insets);
        Assert.Equal(new WindowSafeArea(30, 100, 1010, 2240), insets.ToSafeArea(1080, 2400));
    }

    [Fact]
    public void ImeTextIsSeparateFromPhysicalKeys()
    {
        bool keyboardVisible = false;
        var input = new IosInputHandler(softKeyboard: visible => keyboardVisible = visible);
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
        var input = new IosInputHandler();
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
    public void GamepadButtonsAxesAndTriggersPreserveFrameEdges()
    {
        var input = new IosInputHandler();

        Assert.True(input.GamepadConnected(41));
        Assert.Equal(1, input.GetGamepadCount());
        Assert.True(input.HandleGamepadButton(41, GamepadButton.A, true));
        Assert.True(input.GetGamepadButton(0, GamepadButton.A));
        Assert.True(input.GetGamepadButtonDown(0, GamepadButton.A));
        Assert.True(input.HandleGamepadMotion(41, 0.5f, -0.75f, 0.25f, 0.5f, 0.7f, 0.8f, -1f, 1f));
        Assert.Equal(new Float2(0.5f, -0.75f), input.GetGamepadAxis(0, 0));
        Assert.Equal(new Float2(0.25f, 0.5f), input.GetGamepadAxis(0, 1));
        Assert.Equal(0.7f, input.GetGamepadTrigger(0, 0));
        Assert.Equal(0.8f, input.GetGamepadTrigger(0, 1));
        Assert.True(input.GetGamepadButton(0, GamepadButton.DPadLeft));
        Assert.True(input.GetGamepadButton(0, GamepadButton.DPadUp));

        input.EndFrame();

        Assert.False(input.GetGamepadButtonDown(0, GamepadButton.A));
        Assert.True(input.GetGamepadButton(0, GamepadButton.A));

        input.GamepadDisconnected(41);
        Assert.Equal(0, input.GetGamepadCount());
        Assert.False(input.IsGamepadConnected(0));
    }

    [Fact]
    public void GamepadSlotsAreFixedCapacity()
    {
        var input = new IosInputHandler();
        Assert.True(input.GamepadConnected(1));
        Assert.True(input.GamepadConnected(2));
        Assert.True(input.GamepadConnected(3));
        Assert.True(input.GamepadConnected(4));
        Assert.False(input.GamepadConnected(5));
        Assert.Equal(4, input.GetGamepadCount());
    }
}
