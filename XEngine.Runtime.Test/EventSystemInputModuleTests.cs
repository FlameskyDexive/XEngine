using XEngine.Runtime.UI;
using XEngine.Runtime.Platforms.Android;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class EventSystemInputModuleTests : RuntimeTestBase
{
    [Fact]
    public void EventSystem_SelectsRuntimeNeutralModuleAndProcessesIt()
    {
        using var scene = CreateScene(enable: false);
        GameObject go = CreateGameObject("EventSystem");
        EventSystem system = go.AddComponent<EventSystem>();
        var module = go.AddComponent<ProbeInputModule>();
        scene.Add(go);
        scene.Enable();

        Update(scene);

        Assert.Same(module, system.CurrentInputModule);
        Assert.Equal(1, module.ActivatedCount);
        Assert.Equal(1, module.ProcessedCount);
        Assert.Same(system, module.LastEventSystem);
    }

    [Fact]
    public void EventSystem_DeactivatesModuleWhenComponentIsDisabled()
    {
        using var scene = CreateScene(enable: false);
        GameObject go = CreateGameObject("EventSystem");
        EventSystem system = go.AddComponent<EventSystem>();
        var module = go.AddComponent<ProbeInputModule>();
        scene.Add(go);
        scene.Enable();
        Update(scene);

        module.Enabled = false;
        Update(scene);

        Assert.Null(system.CurrentInputModule);
        Assert.Equal(1, module.DeactivatedCount);
    }

    [Fact]
    public void EventSystem_GamepadNavigatesSubmitsAndCancelsIndependentlyOfKeyboard()
    {
        using var scene = CreateScene(enable: false);
        GameObject systemObject = CreateGameObject("EventSystem");
        EventSystem system = systemObject.AddComponent<EventSystem>();
        GameObject leftObject = CreateGameObject("Left");
        GameObject rightObject = CreateGameObject("Right");
        leftObject.EnsureRectTransform();
        rightObject.EnsureRectTransform();
        leftObject.RectTransform!.ComputedRect = new Rect(0f, 0f, 40f, 40f);
        rightObject.RectTransform!.ComputedRect = new Rect(60f, 0f, 100f, 40f);
        leftObject.AddComponent<Selectable>();
        rightObject.AddComponent<Selectable>();
        var probe = rightObject.AddComponent<SubmitCancelProbe>();
        scene.Add(systemObject);
        scene.Add(leftObject);
        scene.Add(rightObject);

        var input = new AndroidInputHandler();
        Scene.Load(scene);
        global::XEngine.Runtime.Input.PushHandler(input);
        try
        {
            system.SetSelected(leftObject);
            Assert.True(input.HandleGamepadKey(41, 22, pressed: true)); // Android KEYCODE_DPAD_RIGHT.
            Update(scene);
            Assert.Same(rightObject, system.Selected);

            input.EndFrame();
            Assert.True(input.HandleGamepadKey(41, 96, pressed: true)); // Android KEYCODE_BUTTON_A.
            Update(scene);
            Assert.Equal(1, probe.SubmitCount);

            input.EndFrame();
            Assert.True(input.HandleGamepadKey(41, 97, pressed: true)); // Android KEYCODE_BUTTON_B.
            Update(scene);
            Assert.Equal(1, probe.CancelCount);
            Assert.Same(rightObject, system.Selected);

            // A control without an ICancelHandler falls back to clearing focus. A handler owns its
            // cancel lifecycle, which lets modal controls restore focus outside their popup tree.
            input.EndFrame();
            Assert.True(input.HandleGamepadKey(41, 97, pressed: false));
            system.SetSelected(leftObject);
            Assert.True(input.HandleGamepadKey(41, 97, pressed: true));
            Update(scene);
            Assert.Null(system.Selected);

            input.EndFrame();
            system.SetSelected(rightObject);
            Assert.True(input.HandleGamepadMotion(41, -0.8f, 0f, 0f, 0f, 0f, 0f, 0f, 0f));
            Update(scene);
            Assert.Same(leftObject, system.Selected);

            // Holding a stick direction must not spam a new navigation event every frame. Releasing
            // it resets the edge so the same direction can intentionally be used again.
            Update(scene);
            Assert.Same(leftObject, system.Selected);
            Assert.True(input.HandleGamepadMotion(41, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f));
            Update(scene);
            Assert.True(input.HandleGamepadMotion(41, 0.8f, 0f, 0f, 0f, 0f, 0f, 0f, 0f));
            Update(scene);
            Assert.Same(rightObject, system.Selected);
        }
        finally
        {
            global::XEngine.Runtime.Input.PopHandler();
            Scene.Unload();
        }
    }

    private sealed class ProbeInputModule : MonoBehaviour, IEventSystemInputModule
    {
        public int ActivatedCount;
        public int DeactivatedCount;
        public int ProcessedCount;
        public EventSystem? LastEventSystem;

        public bool IsModuleSupported => true;
        public bool ShouldActivateModule => EnabledInHierarchy;

        public void ActivateModule(EventSystem eventSystem)
        {
            LastEventSystem = eventSystem;
            ActivatedCount++;
        }

        public void DeactivateModule(EventSystem eventSystem) => DeactivatedCount++;

        public void Process(EventSystem eventSystem)
        {
            LastEventSystem = eventSystem;
            ProcessedCount++;
        }
    }

    private sealed class SubmitCancelProbe : MonoBehaviour, ISubmitHandler, ICancelHandler
    {
        public int SubmitCount;
        public int CancelCount;

        public void OnSubmit() => SubmitCount++;
        public void OnCancel() => CancelCount++;
    }
}
