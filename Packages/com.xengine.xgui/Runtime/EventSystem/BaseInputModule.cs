using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Runtime.UI;

/// <summary>
/// Base lifecycle and arbitration contract for package-owned event-system input modules.
/// The host <see cref="EventSystem"/> selects one module on the same GameObject and invokes it once
/// per frame. Concrete modules stay in XGUI so Runtime can run without the package.
/// </summary>
[RequireComponent(typeof(EventSystem))]
public abstract class BaseInputModule : MonoBehaviour, IEventSystemInputModule
{
    [SerializeField] private bool _forceModuleActive;
    [SerializeIgnore] private EventSystem? _eventSystem;

    /// <summary>The event system this module is attached to.</summary>
    public EventSystem? EventSystem => _eventSystem;

    /// <summary>Allows a device-specific module to report that its input backend is unavailable.</summary>
    public virtual bool IsModuleSupported => true;

    /// <summary>Whether the module should win arbitration while enabled.</summary>
    public virtual bool ShouldActivateModule
        => EnabledInHierarchy && IsModuleSupported && (_forceModuleActive || Application.IsPlaying);

    public override void OnEnable()
    {
        _eventSystem = GetComponent<EventSystem>();
    }

    public override void OnDisable()
    {
        _eventSystem = null;
    }

    public virtual void ActivateModule(EventSystem eventSystem)
    {
        _eventSystem = eventSystem;
    }

    public virtual void DeactivateModule(EventSystem eventSystem)
    {
        if (ReferenceEquals(_eventSystem, eventSystem)) _eventSystem = null;
    }

    /// <summary>Processes the module's input for the current frame.</summary>
    public abstract void Process(EventSystem eventSystem);

}
