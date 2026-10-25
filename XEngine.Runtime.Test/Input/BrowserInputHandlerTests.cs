// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.Browser;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Input;

public sealed class BrowserInputHandlerTests : IDisposable
{
    public BrowserInputHandlerTests() => BrowserInputRuntime.ResetForTests();

    public void Dispose() => BrowserInputRuntime.ResetForTests();

    [Fact]
    public void CssPixelsMapToFramebuffer()
    {
        BrowserInputCoordinateTransform transform = BrowserInputCoordinateTransform.Create(200, 100, 400, 200);
        Assert.Equal(new Float2(200, 100), transform.Map(100, 50));
    }

    [Fact]
    public void PointerMapsCssToFramebufferAndBridgesMouse()
    {
        var input = new BrowserInputHandler();
        input.SetFramebufferSize(400, 200);
        input.HandlePointer(BrowserInputHandler.PointerDown, 1, 100, 50, 200, 100, buttons: 1, pressure: 1f, isTouch: false);

        Assert.Equal(new Float2(200, 100), input.LastMappedPointer);
        Assert.Equal(new Int2(200, 100), input.MousePosition);
        Assert.True(input.GetMouseButton(0));
        Assert.True(input.GetMouseButtonDown(0));
        input.EndFrame();
        Assert.False(input.GetMouseButtonDown(0));
        Assert.True(input.GetMouseButton(0));

        input.HandlePointer(BrowserInputHandler.PointerUp, 1, 100, 50, 200, 100, buttons: 0, pressure: 0f, isTouch: false);
        Assert.True(input.GetMouseButtonUp(0));
        Assert.False(input.GetMouseButton(0));
    }

    [Fact]
    public void TouchPhasesBridgePrimaryMouse()
    {
        var input = new BrowserInputHandler();
        input.SetFramebufferSize(100, 100);
        input.HandlePointer(BrowserInputHandler.PointerDown, 7, 10, 20, 100, 100, 1, 0.5f, isTouch: true);
        Assert.Equal(1, input.TouchCount);
        Assert.Equal(TouchPhase.Began, input.GetTouch(0).Phase);
        Assert.True(input.GetMouseButtonDown(0));

        input.EndFrame();
        input.HandlePointer(BrowserInputHandler.PointerMove, 7, 15, 25, 100, 100, 1, 0.6f, isTouch: true);
        Assert.Equal(TouchPhase.Moved, input.GetTouch(0).Phase);
        Assert.Equal(new Int2(15, 25), input.MousePosition);

        input.HandlePointer(BrowserInputHandler.PointerUp, 7, 15, 25, 100, 100, 0, 0.6f, isTouch: true);
        Assert.Equal(TouchPhase.Ended, input.GetTouch(0).Phase);
        Assert.True(input.GetMouseButtonUp(0));
        input.EndFrame();
        Assert.Equal(0, input.TouchCount);
    }

    [Fact]
    public void KeyboardCodeMapsUntilFrameEnd()
    {
        var input = new BrowserInputHandler();
        Assert.True(input.HandleKey("KeyA", true, "a"));
        Assert.True(input.GetKey(KeyCode.A));
        Assert.True(input.GetKeyDown(KeyCode.A));
        Assert.Equal("a", input.InputString);
        input.EndFrame();
        Assert.True(input.GetKey(KeyCode.A));
        Assert.False(input.GetKeyDown(KeyCode.A));
        Assert.True(input.HandleKey("Escape", true));
        Assert.True(input.GetKey(KeyCode.Escape));
        input.HandleKey("KeyA", false);
        Assert.True(input.GetKeyUp(KeyCode.A));
    }

    [Fact]
    public void GamepadStandardMaskAndFocusLoss()
    {
        var input = new BrowserInputHandler();
        input.HandleGamepad(0, buttonMask: 1 | 4096, 0.8f, 0f, 0f, 0f, 0.2f, 0f);
        Assert.True(input.IsGamepadConnected(0));
        Assert.True(input.GetGamepadButton(0, GamepadButton.A));
        Assert.True(input.GetGamepadButtonDown(0, GamepadButton.A));
        Assert.True(input.GetGamepadButton(0, GamepadButton.DPadUp));
        Assert.Equal(0.8f, input.GetGamepadAxis(0, 0).X, 3);

        input.HandleKey("KeyA", true);
        input.FocusLost();
        Assert.False(input.GetKey(KeyCode.A));
        Assert.True(input.GetKeyUp(KeyCode.A));
        Assert.Equal(0, input.TouchCount);
    }

    [Fact]
    public void WheelIsConsumedAtFrameBoundary()
    {
        var input = new BrowserInputHandler();
        input.SetFramebufferSize(100, 100);
        input.HandleWheel(10, 10, 100, 100, 120);
        Assert.Equal(-1f, input.MouseWheelDelta);
        input.EndFrame();
        Assert.Equal(0f, input.MouseWheelDelta);
    }

    [Fact]
    public void PlatformServicesOpenUrlAndClipboardRequireGesture()
    {
        BrowserInputRuntime.Configure("zh-CN", online: true);
        Assert.Equal("zh-CN", BrowserInputRuntime.Services.LocaleName);
        Assert.Equal(
            PlatformNetworkReachability.ReachableViaLocalAreaNetwork,
            BrowserInputRuntime.Services.NetworkReachability);
        Assert.False(BrowserInputRuntime.Services.TryOpenUrl("https://example.com"));
        BrowserInputRuntime.NoteUserGesture();
        Assert.True(BrowserInputRuntime.Services.TryOpenUrl("https://example.com/docs"));
        Assert.Equal("https://example.com/docs", BrowserInputRuntime.TakePendingOpenUrl());
        Assert.Null(BrowserInputRuntime.TakePendingOpenUrl());
        Assert.True(BrowserInputRuntime.Services.TrySetClipboard("copied"));
        Assert.Equal("copied", BrowserInputRuntime.TakePendingClipboardWrite());
    }
}
