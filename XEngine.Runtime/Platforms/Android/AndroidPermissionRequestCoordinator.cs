// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.Platforms.Android;

/// <summary>
/// Serializes Android permission dialogs while preserving cancellation and rechecking queued
/// permissions after each platform callback.
/// </summary>
public sealed class AndroidPermissionRequestCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<PendingRequest> _pending = new();
    private readonly Func<string, PlatformPermissionStatus> _getPermissionStatus;
    private readonly Action<string> _dispatchPermissionRequest;
    private PendingRequest? _active;
    private bool _disposed;

    public AndroidPermissionRequestCoordinator(
        Func<string, PlatformPermissionStatus> getPermissionStatus,
        Action<string> dispatchPermissionRequest)
    {
        _getPermissionStatus = getPermissionStatus ??
            throw new ArgumentNullException(nameof(getPermissionStatus));
        _dispatchPermissionRequest = dispatchPermissionRequest ??
            throw new ArgumentNullException(nameof(dispatchPermissionRequest));
    }

    public ValueTask<PlatformPermissionStatus> RequestAsync(
        string permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        if (cancellationToken.IsCancellationRequested)
            return ValueTask.FromCanceled<PlatformPermissionStatus>(cancellationToken);

        PlatformPermissionStatus current = ReadStatus(permission);
        if (current != PlatformPermissionStatus.Denied)
            return ValueTask.FromResult(current);

        var request = new PendingRequest(permission);
        request.RegisterCancellation(cancellationToken);
        lock (_gate)
        {
            if (_disposed)
            {
                request.Complete(PlatformPermissionStatus.Unsupported);
                return new ValueTask<PlatformPermissionStatus>(request.Task);
            }
            if (!request.Task.IsCompleted)
                _pending.Enqueue(request);
        }

        StartNext();
        return new ValueTask<PlatformPermissionStatus>(request.Task);
    }

    /// <summary>Completes the active Android callback. A null permission accepts the active request.</summary>
    public bool Complete(string? permission, PlatformPermissionStatus status)
    {
        PendingRequest? completed;
        lock (_gate)
        {
            if (_active == null ||
                (!string.IsNullOrEmpty(permission) &&
                 !string.Equals(permission, _active.Permission, StringComparison.Ordinal)))
            {
                return false;
            }

            completed = _active;
            _active = null;
        }

        completed.Complete(status);
        StartNext();
        return true;
    }

    public void Dispose()
    {
        PendingRequest? active;
        PendingRequest[] pending;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            active = _active;
            _active = null;
            pending = _pending.ToArray();
            _pending.Clear();
        }

        active?.Complete(PlatformPermissionStatus.Unsupported);
        for (int i = 0; i < pending.Length; i++)
            pending[i].Complete(PlatformPermissionStatus.Unsupported);
    }

    private void StartNext()
    {
        while (true)
        {
            PendingRequest? next = null;
            lock (_gate)
            {
                if (_disposed || _active != null)
                    return;
                while (_pending.Count > 0)
                {
                    PendingRequest candidate = _pending.Dequeue();
                    if (candidate.Task.IsCompleted)
                    {
                        candidate.ReleaseCancellation();
                        continue;
                    }
                    _active = candidate;
                    next = candidate;
                    break;
                }
            }

            if (next == null)
                return;

            PlatformPermissionStatus current = ReadStatus(next.Permission);
            if (current != PlatformPermissionStatus.Denied)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_active, next))
                        _active = null;
                }
                next.Complete(current);
                continue;
            }

            try
            {
                _dispatchPermissionRequest(next.Permission);
            }
            catch (Exception)
            {
                _ = Complete(next.Permission, PlatformPermissionStatus.Unsupported);
            }
            return;
        }
    }

    private PlatformPermissionStatus ReadStatus(string permission)
    {
        try
        {
            return _getPermissionStatus(permission);
        }
        catch (Exception)
        {
            return PlatformPermissionStatus.Unsupported;
        }
    }

    private sealed class PendingRequest(string permission)
    {
        private readonly TaskCompletionSource<PlatformPermissionStatus> _completion = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _cancellationRegistration;
        private CancellationToken _cancellationToken;
        private int _registrationAssigned;

        public string Permission { get; } = permission;
        public Task<PlatformPermissionStatus> Task => _completion.Task;

        public void RegisterCancellation(CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
                return;

            _cancellationToken = cancellationToken;
            CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((PendingRequest)state!).Cancel(),
                this);
            _cancellationRegistration = registration;
            Volatile.Write(ref _registrationAssigned, 1);
            if (_completion.Task.IsCompleted)
                ReleaseCancellation();
        }

        public void Complete(PlatformPermissionStatus status)
        {
            _completion.TrySetResult(status);
            ReleaseCancellation();
        }

        public void ReleaseCancellation()
        {
            if (Interlocked.Exchange(ref _registrationAssigned, 0) == 1)
                _cancellationRegistration.Dispose();
        }

        private void Cancel() => _completion.TrySetCanceled(_cancellationToken);
    }
}
