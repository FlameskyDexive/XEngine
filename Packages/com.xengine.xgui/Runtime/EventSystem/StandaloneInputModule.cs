using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Runtime.UI;

/// <summary>
/// Default desktop/mobile module. Pointer and keyboard stages can be independently disabled for
/// split-input setups; platform-specific modules can derive from <see cref="PointerInputModule"/>.
/// </summary>
[AddComponentMenu("UI/Standalone Input Module")]
public sealed class StandaloneInputModule : PointerInputModule
{
    [SerializeField] private bool _processPointer = true;
    [SerializeField] private bool _processKeyboard = true;

    public bool ProcessPointer { get => _processPointer; set => _processPointer = value; }
    public bool ProcessKeyboard { get => _processKeyboard; set => _processKeyboard = value; }

    public override void Process(EventSystem eventSystem)
    {
        EventSystemInputMode mode = EventSystemInputMode.None;
        if (_processPointer) mode |= EventSystemInputMode.Pointer;
        if (_processKeyboard) mode |= EventSystemInputMode.Keyboard;
        eventSystem.ProcessInput(mode, Time.TimeSinceStartup);
    }
}
