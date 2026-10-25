using XEngine.Runtime;

namespace XEngine.Runtime.UI;

/// <summary>Base module for pointer, touch and drag input.</summary>
public abstract class PointerInputModule : BaseInputModule
{
    /// <summary>Runs the Runtime-neutral pointer dispatcher for the active event system.</summary>
    protected void ProcessPointerEvents(EventSystem eventSystem)
        => eventSystem.ProcessInput(EventSystemInputMode.Pointer, Time.TimeSinceStartup);

    public override void Process(EventSystem eventSystem)
        => ProcessPointerEvents(eventSystem);
}
