using System.Collections.Generic;

using XEngine.Async;

namespace XEngine.Editor.GUI.Panels;

internal sealed class GameViewCaptureRequestQueue<T>
{
    private readonly List<GameViewCaptureRequest<T>> _pending = new();

    internal GameViewCaptureRequest<T> Request()
    {
        var request = new GameViewCaptureRequest<T>();
        _pending.Add(request);
        return request;
    }

    internal void Complete(T result)
    {
        for (int i = 0; i < _pending.Count; i++)
            _pending[i].Complete(result);
        _pending.Clear();
    }

    internal bool Cancel(GameViewCaptureRequest<T> request)
    {
        for (int i = _pending.Count - 1; i >= 0; i--)
        {
            if (!ReferenceEquals(_pending[i], request))
                continue;

            _pending[i].Cancel();
            _pending.RemoveAt(i);
            return true;
        }

        return false;
    }
}

internal sealed class GameViewCaptureRequest<T>
{
    private readonly XTaskCompletionSource<T> _completion = new();

    internal XTask<T> Task => _completion.Task;

    internal void Complete(T result) => _completion.TrySetResult(result);

    internal void Cancel() => _completion.TrySetCanceled();
}
