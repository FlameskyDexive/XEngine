using System.Collections.Generic;
using XEngine.ILInterpreter.VM;
using XEngine.Runtime;

namespace XEngine.ILInterpreter.Integration;

/// <summary>
/// AOT-compiled proxy component (plan §5.4): holds the module/type identity and
/// the serialized interpreted-field pairs, and forwards engine lifecycle calls
/// into the interpreted instance. The interpreted type is never registered as a
/// native CLR component; after dispose, forwarded calls report a released
/// handle instead of touching freed state.
/// </summary>
public sealed class ILBehaviourHost : MonoBehaviour
{
    /// <summary>Module (assembly) name under the interpreter module root.</summary>
    public string ModuleName = "";

    /// <summary>Full interpreted type name, e.g. "Game.Hotfix.HotBehaviour".</summary>
    public string TypeName = "";

    /// <summary>Serialized interpreted fields as "name=invariant value" pairs.</summary>
    public List<string> FieldPairs = new();

    private ILObject? _instance;
    private bool _started;

    public ILObject? Instance => _instance;
    public bool IsReleased => _instance is null;

    /// <summary>Creates the interpreted instance and applies serialized fields. Called by the engine before Start.</summary>
    public override void OnAddedToScene()
    {
        base.OnAddedToScene();
        var service = ILInterpreterRuntimeService.Shared;
        _instance = service.CreateBehaviour(ModuleName, TypeName, FieldPairs);
        service.Forward(_instance, "OnAddedToScene");
    }

    public override void OnEnable()
    {
        base.OnEnable();
        Forward("OnEnable");
    }

    public override void Start()
    {
        base.Start();
        if (_started) return;
        _started = true;
        Forward("Start");
    }

    public override void Update() => Forward("Update");

    public override void LateUpdate() => Forward("LateUpdate");

    public override void OnDisable() => Forward("OnDisable");

    public override void OnRemovedFromScene()
    {
        Forward("OnRemovedFromScene");
        base.OnRemovedFromScene();
    }

    public override void OnDispose()
    {
        Forward("OnDestroy");
        if (_instance is not null)
        {
            ILInterpreterRuntimeService.Shared.ReleaseBehaviour(_instance);
        }
        _instance = null;
        base.OnDispose();
    }

    private void Forward(string method)
    {
        if (_instance is null) return; // released handle: no-op, never touch freed state
        try
        {
            ILInterpreterRuntimeService.Shared.Forward(_instance, method);
        }
        catch (ILError e)
        {
            XEngine.Runtime.Debug.LogError($"[ILInterpreter] {method} on {TypeName} failed: {e.Message}");
        }
    }
}
