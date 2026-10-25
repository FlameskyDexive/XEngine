using System;
using System.Text;

namespace XEngine.XTS;

/// <summary>
/// A JS function the host received from a script (a "delegate" on the TS
/// side). Functions cross the boundary as indices into the environment's
/// <c>__xts_handles</c> array — the runtime keeps no native references, so
/// environment teardown can never race a live handle. Invoke runs the
/// function through an expression evaluation; Release drops the array slot.
/// </summary>
public sealed class XtsFunctionHandle
{
    private XtsRuntime? _runtime;

    internal XtsFunctionHandle(XtsRuntime runtime, int index)
    {
        _runtime = runtime;
        Index = index;
    }

    public int Index { get; private set; }

    public XtsValue Invoke(params XtsValue[] args)
    {
        var runtime = _runtime ?? throw new ObjectDisposedException(nameof(XtsFunctionHandle));
        return runtime.InvokeFunction(Index, args);
    }

    public void Release()
    {
        if (_runtime is not { } runtime)
            return;
        runtime.EvaluateExpression($"__xts_handles[{Index}] = null;", out _);
        _runtime = null;
    }
}
