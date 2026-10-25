using System.Collections.Generic;

using XEngine.InputSystem;
using XEngine.InputSystem.Editor;
using XEngine.Runtime;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// zonezero M4 — com.xengine.inputsystem: Unity .inputactions JSON parse + translation onto
/// the engine InputManagement layer, .triggered/IsPressed/ReadValue polling through a mock
/// input handler, and rebinding override round-trips. The fixture mirrors the ZZZ reference
/// project's Player map (same actions and binding shapes, trimmed to the demo-relevant set).
/// </summary>
public sealed class ZonezeroInputSystemTests : EditorTestHarness
{
    private const string UnityJson = """
        {
          "name": "InputSystem",
          "maps": [
            {
              "name": "Player",
              "id": "b8d38b2b-0000-0000-0000-000000000001",
              "actions": [
                { "name": "Move", "type": "Value", "id": "a1", "expectedControlType": "Vector2" },
                { "name": "Fire", "type": "Button", "id": "a2", "expectedControlType": "Button" },
                { "name": "Evade", "type": "Button", "id": "a3", "expectedControlType": "Button" },
                { "name": "BigSkill", "type": "Button", "id": "a4", "expectedControlType": "Button" },
                { "name": "SwitchModel", "type": "Button", "id": "a5", "expectedControlType": "Button" }
              ],
              "bindings": [
                { "name": "WASD", "id": "b1", "path": "2DVector", "isComposite": true, "action": "Move" },
                { "name": "up", "id": "b2", "path": "<Keyboard>/w", "isPartOfComposite": true, "action": "Move" },
                { "name": "down", "id": "b3", "path": "<Keyboard>/s", "isPartOfComposite": true, "action": "Move" },
                { "name": "left", "id": "b4", "path": "<Keyboard>/a", "isPartOfComposite": true, "action": "Move" },
                { "name": "right", "id": "b5", "path": "<Keyboard>/d", "isPartOfComposite": true, "action": "Move" },
                { "name": "", "id": "b6", "path": "<Gamepad>/leftStick", "action": "Move", "groups": "Gamepad" },
                { "name": "", "id": "b7", "path": "<Gamepad>/rightTrigger", "action": "Fire", "groups": "Gamepad" },
                { "name": "", "id": "b8", "path": "<Mouse>/leftButton", "action": "Fire", "groups": "Keyboard&Mouse" },
                { "name": "", "id": "b9", "path": "<Keyboard>/leftShift", "action": "Evade" },
                { "name": "", "id": "b10", "path": "<Keyboard>/e", "action": "Evade" },
                { "name": "", "id": "b11", "path": "<Keyboard>/q", "action": "BigSkill" },
                { "name": "", "id": "b12", "path": "<Keyboard>/space", "action": "SwitchModel" },
                { "name": "", "id": "b13", "path": "<Joystick>/trigger", "action": "Fire", "groups": "Joystick" }
              ]
            },
            {
              "name": "UI",
              "id": "92e26955-0000-0000-0000-000000000002",
              "actions": [
                { "name": "Submit", "type": "Button", "id": "u1", "expectedControlType": "Button" }
              ],
              "bindings": [
                { "name": "", "id": "ub1", "path": "*/{Submit}", "action": "Submit" }
              ]
            }
          ],
          "controlSchemes": [
            { "name": "Keyboard&Mouse" },
            { "name": "Gamepad" },
            { "name": "Joystick" }
          ]
        }
        """;

    [Fact]
    public void UnityJson_ParsesMapsActionsSchemes()
    {
        var warnings = new List<string>();
        InputActionAsset asset = UnityInputActionsJson.Parse(UnityJson, warnings);

        Assert.Equal("InputSystem", asset.Name);
        Assert.Equal(2, asset.Maps.Count);
        Assert.Contains("Keyboard&Mouse", asset.ControlSchemes);
        Assert.Contains("Gamepad", asset.ControlSchemes);

        InputActionMap? player = asset.FindMap("Player");
        Assert.NotNull(player);
        Assert.Equal(5, player!.Actions.Count);

        InputAction? move = player.FindAction("Move");
        Assert.NotNull(move);
        Assert.Equal(InputActionType.Value, move!.ActionType);
        Assert.Equal(typeof(Float2), move.ExpectedValueType);

        // Unsupported device paths warn but never abort the import.
        Assert.Contains(warnings, w => w.Contains("Joystick"));
    }

    [Fact]
    public void UnityJson_TranslatesBindingsToEngineTypes()
    {
        var warnings = new List<string>();
        InputActionAsset asset = UnityInputActionsJson.Parse(UnityJson, warnings);
        InputActionMap player = asset.FindMap("Player")!;

        // Move: WASD composite + gamepad left stick.
        InputAction move = player.FindAction("Move")!;
        Assert.Single(move.CompositeBindings);           // 2DVector → Vector2CompositeBinding
        Assert.Single(move.Bindings);                    // leftStick → gamepad axis 0

        // Fire: right trigger + mouse left + skipped Joystick.
        InputAction fire = player.FindAction("Fire")!;
        Assert.Equal(2, fire.Bindings.Count);

        // Evade: two keyboard bindings (leftShift + e).
        InputAction evade = player.FindAction("Evade")!;
        Assert.Equal(2, evade.Bindings.Count);
        Assert.Equal(KeyCode.ShiftLeft, evade.Bindings[0].Key);
        Assert.Equal(KeyCode.E, evade.Bindings[1].Key);

        Assert.Equal(KeyCode.Q, player.FindAction("BigSkill")!.Bindings[0].Key);
        Assert.Equal(KeyCode.Space, player.FindAction("SwitchModel")!.Bindings[0].Key);
    }

    [Fact]
    public void UnityJson_KeyTranslation_MapsUnityNames()
    {
        Assert.Equal(KeyCode.W, UnityInputActionsJson.TranslateKey("w"));
        Assert.Equal(KeyCode.Up, UnityInputActionsJson.TranslateKey("upArrow"));
        Assert.Equal(KeyCode.ShiftLeft, UnityInputActionsJson.TranslateKey("leftShift"));
        Assert.Equal(KeyCode.Space, UnityInputActionsJson.TranslateKey("space"));
        Assert.Equal(KeyCode.Number5, UnityInputActionsJson.TranslateKey("5"));
    }

    [Fact]
    public void Sniff_DistinguishesUnityJsonFromEcho()
    {
        Assert.True(UnityInputActionsJson.IsUnityJson("{ \"maps\": [] }"));
        Assert.False(UnityInputActionsJson.IsUnityJson("{\"Name\":\"Map\"}"));
    }

    [Fact]
    public void Polling_MoveValueAndButtonTriggeredThroughMockHandler()
    {
        var handler = new MockInputHandler();
        InputActionAsset asset = UnityInputActionsJson.Parse(UnityJson, new List<string>());
        InputActionMap player = asset.FindMap("Player")!;
        Input.RegisterActionMap(player);
        try
        {
            player.Enable();

            // Frame 1: hold W → Move reads +Y on the press frame and after.
            handler.Press(KeyCode.W);
            player.UpdateActions(handler, 0.016f);
            InputAction move = player.FindAction("Move")!;
            Assert.Equal(0f, move.ReadValue<Float2>().X, 3);
            Assert.Equal(1f, move.ReadValue<Float2>().Y, 3);

            InputAction evade = player.FindAction("Evade")!;
            handler.Press(KeyCode.ShiftLeft);
            player.UpdateActions(handler, 0.016f);
            Assert.True(evade.Triggered());       // Unity .triggered on the press frame
            Assert.True(evade.IsPressed());       // Unity .IsPressed()

            // Frame 2: still held → triggered goes false, IsPressed stays true.
            player.UpdateActions(handler, 0.016f);
            Assert.False(evade.Triggered());
            Assert.True(evade.IsPressed());

            // Frame 3: release → pressed goes false.
            handler.Release(KeyCode.ShiftLeft);
            player.UpdateActions(handler, 0.016f);
            Assert.False(evade.IsPressed());
        }
        finally
        {
            player.Disable();
            Input.UnregisterActionMap(player);
        }
    }

    [Fact]
    public void Rebinding_RebindAndOverridesRoundTrip()
    {
        var warnings = new List<string>();
        InputActionAsset asset = UnityInputActionsJson.Parse(UnityJson, warnings);
        InputActionMap player = asset.FindMap("Player")!;
        InputAction evade = player.FindAction("Evade")!;

        // Direct rebind: Evade[0] leftShift → Z.
        Assert.True(evade.RebindToKey(0, KeyCode.Z));
        Assert.Equal(KeyCode.Z, evade.Bindings[0].Key);

        // Mouse-button rebind on Fire[1].
        InputAction fire = player.FindAction("Fire")!;
        Assert.True(fire.RebindToMouseButton(1, MouseButton.Right));
        Assert.Equal(MouseButton.Right, fire.Bindings[1].MouseButton);

        // Override table round-trip.
        string json = asset.SaveBindingOverridesAsJson();
        InputActionAsset reloaded = UnityInputActionsJson.Parse(UnityJson, warnings);
        reloaded.LoadBindingOverridesFromJson(json);
        InputAction? evadeReloaded = reloaded.FindMap("Player")!.FindAction("Evade");
        Assert.Equal(KeyCode.Z, evadeReloaded!.Bindings[0].Key);
        Assert.Equal(MouseButton.Right, reloaded.FindMap("Player")!.FindAction("Fire")!.Bindings[1].MouseButton);
    }

    /// <summary>Minimal IInputHandler driving the action layer — keys only.</summary>
    private sealed class MockInputHandler : IInputHandler
    {
        private readonly HashSet<KeyCode> _keys = new();
        private readonly HashSet<KeyCode> _keysDown = new();
        private readonly HashSet<KeyCode> _keysUp = new();

        public void Press(KeyCode key) { if (_keys.Add(key)) _keysDown.Add(key); }
        public void Release(KeyCode key) { if (_keys.Remove(key)) _keysUp.Add(key); }
        public void EndFrame() { _keysDown.Clear(); _keysUp.Clear(); }

        public string Clipboard { get; set; } = "";
        public bool IsAnyKeyDown => _keys.Count > 0;
        public Float2 MouseDelta => Float2.Zero;
        public Int2 MousePosition { get; set; }
        public float MouseWheelDelta => 0f;
        public Int2 PrevMousePosition => Int2.Zero;
        public string InputString => "";
        public char? GetPressedChar() => null;
        public bool GetKey(KeyCode key) => _keys.Contains(key);
        public bool GetKeyDown(KeyCode key) => _keysDown.Contains(key);
        public bool GetKeyUp(KeyCode key) => _keysUp.Contains(key);
        public bool GetMouseButton(int button) => false;
        public bool GetMouseButtonDown(int button) => false;
        public bool GetMouseButtonUp(int button) => false;
        public void SetCursorVisible(bool visible, int miceIndex = 0) { }
        public void SetCursorShape(XEngine.PaperUI.PaperCursor shape, int miceIndex = 0) { }
        public int GetGamepadCount() => 0;
        public bool IsGamepadConnected(int gamepadIndex) => false;
        public bool GetGamepadButton(int gamepadIndex, GamepadButton button) => false;
        public bool GetGamepadButtonDown(int gamepadIndex, GamepadButton button) => false;
        public bool GetGamepadButtonUp(int gamepadIndex, GamepadButton button) => false;
        public Float2 GetGamepadAxis(int gamepadIndex, int axisIndex) => Float2.Zero;
        public float GetGamepadTrigger(int gamepadIndex, int triggerIndex) => 0f;
        public void SetGamepadVibration(int gamepadIndex, float leftMotor, float rightMotor) { }

#pragma warning disable CS0067 // unused on purpose — part of the interface surface
        public event System.Action<KeyCode, bool>? OnKeyEvent;
        public event System.Action<MouseButton, float, float, bool, bool>? OnMouseEvent;
#pragma warning restore CS0067
    }
}
