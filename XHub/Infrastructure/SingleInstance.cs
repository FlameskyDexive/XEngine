using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace XHub.Infrastructure;

/// <summary>
/// Per-user single instance: the first XHub process owns a named <see cref="Mutex"/> and a
/// named-pipe server; later launches forward their CLI requests over the pipe and exit
/// (planning doc §4.4). Names are hashed per user name so two Windows accounts never collide.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstanceGuard(Mutex mutex) => _mutex = mutex;

    public bool Owned => _mutex != null;

    public static string MutexName => $"Global\\XEngine.XHub.{NameSuffix}";
    public static string PipeName => $"XEngine.XHub.IPC.{NameSuffix}";

    private static string NameSuffix
    {
        get
        {
            string user = Environment.UserName ?? "user";
            return StableHash(user.ToLowerInvariant()).ToString("x8");
        }
    }

    /// <summary>Stable 32-bit hash (FNV-1a) used to derive per-user pipe/mutex names.</summary>
    private static uint StableHash(string value)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash;
        }
    }

    /// <summary>Tries to become the primary instance. Null when another instance is running.</summary>
    public static SingleInstanceGuard? TryAcquire()
    {
        Mutex mutex = new(initiallyOwned: false, MutexName);
        try
        {
            if (!mutex.WaitOne(TimeSpan.Zero, exitContext: false))
            {
                mutex.Dispose();
                return null;
            }
            return new SingleInstanceGuard(mutex);
        }
        catch (AbandonedMutexException)
        {
            // A previous holder crashed while holding the mutex — the OS handed it to us, so we
            // now own it and can safely become primary.
            return new SingleInstanceGuard(mutex);
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }

    /// <summary>Sends <paramref name="args"/> to the running instance over the named pipe.</summary>
    public static bool TryForward(string[] args, int timeoutMs = 2000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMs);
            string payload = JsonSerializer.Serialize(new IpcMessage(args));
            byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            HubLog.Warning($"IPC forward failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        try { _mutex.ReleaseMutex(); } catch { }
        _mutex.Dispose();
    }
}

/// <summary>Wire format for the single-instance pipe: one JSON object per request.</summary>
public sealed record IpcMessage(string[] Args)
{
    [System.Text.Json.Serialization.JsonConstructor]
    public IpcMessage() : this(Array.Empty<string>()) { }
}

/// <summary>
/// Named-pipe server loop for the primary instance. Deserialized requests are handed to
/// <paramref name="onMessage"/> — the caller is responsible for marshalling onto the UI thread.
/// </summary>
public sealed class SingleInstanceServer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;
    private readonly Action<IpcMessage> _onMessage;

    public SingleInstanceServer(Action<IpcMessage> onMessage)
    {
        _onMessage = onMessage;
        _thread = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "XHubIpcServer",
        };
    }

    public void Start() => _thread.Start();

    private async void RunLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    SingleInstanceGuard.PipeName, PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token);

                var buffer = new MemoryStream();
                byte[] chunk = new byte[4096];
                int read;
                while ((read = await server.ReadAsync(chunk, _cts.Token)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > 64 * 1024) break; // enough for any arg payload
                    if (chunk.AsSpan(0, read).Contains((byte)'\n')) break;
                }

                string payload = Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\n', '\r');
                if (payload.Length == 0) continue;
                var message = JsonSerializer.Deserialize<IpcMessage>(payload);
                if (message == null || message.Args.Length == 0) continue;
                _onMessage(message);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                HubLog.Warning($"IPC server loop error: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _thread.Join(500); } catch { }
        _cts.Dispose();
    }
}
