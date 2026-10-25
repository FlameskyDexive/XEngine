// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class SceneViewEditorTests
{
    [Fact]
    public void EditorCameraDisposesItsDetachedObject()
    {
        var camera = new EditorCamera();
        var owned = camera.Camera.GameObject;
        camera.Dispose();
        Assert.True(owned.IsDisposed);
        camera.Dispose();
    }

    [Fact]
    public void LookAlong_SnapsToRightTopAndFront()
    {
        using var camera = new EditorCamera();

        camera.LookAlong(new Float3(-1f, 0f, 0f));
        Assert.True(SceneViewPanel.IsLookingAlong(camera.Forward, new Float3(-1f, 0f, 0f)));

        camera.LookAlong(new Float3(0f, -1f, 0f));
        Assert.True(SceneViewPanel.IsLookingAlong(camera.Forward, new Float3(0f, -1f, 0f)));

        camera.LookAlong(new Float3(0f, 0f, -1f));
        Assert.True(SceneViewPanel.IsLookingAlong(camera.Forward, new Float3(0f, 0f, -1f)));
    }

    [Fact]
    public void PreferredSceneCamera_DefaultsToMainCameraTagWithoutSelection()
    {
        // The overlay reads the enabled-camera registry, which only an active scene maintains
        // (the editor's open scene is always active).
        using var scene = new Scene();
        scene.Enable();
        var otherGo = new GameObject("Other Camera");
        Camera other = otherGo.AddComponent<Camera>();
        scene.Add(otherGo);

        var mainGo = new GameObject("Main Camera");
        mainGo.Tag = "Main Camera";
        Camera main = mainGo.AddComponent<Camera>();
        scene.Add(mainGo);

        var hiddenGo = new GameObject("EditorCamera");
        hiddenGo.HideFlags = HideFlags.HideAndDontSave;
        hiddenGo.AddComponent<Camera>();
        scene.Add(hiddenGo);

        var disabledGo = new GameObject("Disabled Camera");
        disabledGo.AddComponent<Camera>();
        scene.Add(disabledGo);
        disabledGo.Enabled = false;

        var cameras = SceneViewPanel.CollectSceneCameras(scene);
        Assert.Equal(2, cameras.Count);
        Assert.Same(main, SceneViewPanel.PreferredSceneCamera(cameras));
        Assert.DoesNotContain(cameras, c => c.GameObject == hiddenGo);
        Assert.DoesNotContain(cameras, c => c.GameObject == disabledGo);
        Assert.Contains(other, cameras);

        // Re-enabling and removing objects keeps the registry in step without a scene walk.
        disabledGo.Enabled = true;
        scene.Remove(otherGo);
        cameras = SceneViewPanel.CollectSceneCameras(scene);
        Assert.Equal(2, cameras.Count);
        Assert.Contains(cameras, c => c.GameObject == disabledGo);
        Assert.DoesNotContain(other, cameras);
    }

    [Fact]
    public void FocusSelection_FramesOverlayCanvasUsingWorldSpaceAabb()
    {
        using var camera = new EditorCamera();
        using var scene = new Scene();
        var go = new GameObject("Canvas");
        GameCanvas canvas = go.AddComponent<GameCanvas>();
        canvas.RenderMode = RenderMode.ScreenSpaceOverlay;
        canvas.ReferenceResolution = new Float2(1280f, 720f);
        scene.Add(go);

        Selection.Select(go);
        GameCanvas.ScreenSizeOverride = new Float2(1280f, 720f);
        GameCanvas.EditorWorldSpaceOverride = false;
        try
        {
            camera.FocusSelection();
            // Overlay HUDs used to frame as a 0.5-unit point at the origin. With pixels-per-unit
            // mapping a 1280×720 canvas is ~13 units across, so the camera must stand further back.
            Assert.True(Float3.Length(camera.Position) > 8f);
        }
        finally
        {
            Selection.Clear();
            GameCanvas.ScreenSizeOverride = null;
            GameCanvas.EditorWorldSpaceOverride = false;
        }
    }

    [Fact]
    public void DollyByScroll_ZoomsOrthographicSize()
    {
        using var camera = new EditorCamera();
        camera.SetProjectionOrthographic(true);
        float before = camera.Camera.OrthographicSize;

        camera.DollyByScroll(2f);

        Assert.True(camera.Camera.OrthographicSize < before);
        Assert.True(camera.Camera.OrthographicSize >= 0.1f);
    }

    [Fact]
    public void ProcessInput_UnlocksLookWhenRightMouseReleasedWithoutMouseUp()
    {
        var input = new StubMouseInput();
        Input.PushHandler(input);
        try
        {
            using var camera = new EditorCamera();
            var origin = new Float2(10f, 10f);
            var size = new Float2(400f, 300f);

            input.HeldRight = true;
            input.DownRight = true;
            camera.ProcessInput(0.016f, isHovered: true, origin, origin, size);
            Assert.True(Input.CursorLocked);

            // Skipped GUI frame: button is up but GetMouseButtonUp never fired.
            input.HeldRight = false;
            input.DownRight = false;
            input.UpRight = false;
            camera.ProcessInput(0.016f, isHovered: false, origin, origin, size);
            Assert.False(Input.CursorLocked);
        }
        finally
        {
            Input.ClearLockContexts();
            Input.PopHandler();
        }
    }

    [Fact]
    public void DollyByScroll_MovesPerspectiveCamera()
    {
        using var camera = new EditorCamera();
        Float3 before = camera.Position;

        camera.DollyByScroll(2f);

        Assert.False(before.Equals(camera.Position));
    }

    [Fact]
    public void DollyByScroll_ClampsOrbitSoClipPlanesStayFinite()
    {
        using var camera = new EditorCamera();
        for (int i = 0; i < 80; i++)
            camera.DollyByScroll(-4f);

        Assert.InRange(camera.Camera.FarClipPlane, 1000f, EditorCamera.MaxClipExtent);
        Assert.True(float.IsFinite(camera.Camera.FarClipPlane));
        Assert.True(float.IsFinite(camera.Camera.NearClipPlane));
    }

    [Fact]
    public void OrthographicIso_UsesSymmetricClipPlanesSoGroundIsNotNearClipped()
    {
        using var camera = new EditorCamera();
        Assert.True(camera.Camera.NearClipPlane > 0f);

        camera.SetProjectionOrthographic(true);

        Assert.True(camera.Camera.NearClipPlane < 0f);
        Assert.True(camera.Camera.FarClipPlane > 0f);
        Assert.Equal(camera.Camera.FarClipPlane, MathF.Abs(camera.Camera.NearClipPlane), 3);

        camera.SetProjectionOrthographic(false);
        Assert.True(camera.Camera.NearClipPlane > 0f);
    }
}

file sealed class StubMouseInput : IInputHandler
{
    public bool HeldRight;
    public bool DownRight;
    public bool UpRight;

    public string Clipboard { get => ""; set { } }
    public bool IsAnyKeyDown => false;
    public Float2 MouseDelta => Float2.Zero;
    public Int2 MousePosition { get => Int2.Zero; set { } }
    public float MouseWheelDelta => 0f;
    public Int2 PrevMousePosition => Int2.Zero;

    public event Action<KeyCode, bool> OnKeyEvent { add { } remove { } }
    public event Action<MouseButton, float, float, bool, bool> OnMouseEvent { add { } remove { } }

    public char? GetPressedChar() => null;
    public string InputString => string.Empty;
    public bool GetKey(KeyCode key) => false;
    public bool GetKeyDown(KeyCode key) => false;
    public bool GetKeyUp(KeyCode key) => false;
    public bool GetMouseButton(int button) => button == 1 && HeldRight;
    public bool GetMouseButtonDown(int button) => button == 1 && DownRight;
    public bool GetMouseButtonUp(int button) => button == 1 && UpRight;
    public void SetCursorVisible(bool visible, int miceIndex = 0) { }
    public void SetCursorShape(PaperCursor shape, int miceIndex = 0) { }
    public int GetGamepadCount() => 0;
    public bool IsGamepadConnected(int gamepadIndex) => false;
    public bool GetGamepadButton(int gamepadIndex, GamepadButton button) => false;
    public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button) => false;
    public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button) => false;
    public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex) => Float2.Zero;
    public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) => 0f;
    public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }
}
