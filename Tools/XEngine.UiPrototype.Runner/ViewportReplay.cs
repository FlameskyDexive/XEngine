using XEngine.Runtime.Resources;
using XEngine.Runtime;

namespace XEngine.UiPrototype;

/// <summary>Precomputed 256x256 RGBA frames uploaded at 30Hz to the same borrowed
/// texture. This models changing image content, not a scene-render benchmark.</summary>
internal sealed class ViewportReplay
{
    private readonly bool _queued;
    public string UploadMode => _queued ? "CommandBuffer.UpdateTexture + Submit" : "Texture2D.SetData";
    public const int Size = 256, BytesPerUpdate = Size * Size * 4;
    private readonly byte[][] _frames = [new byte[BytesPerUpdate], new byte[BytesPerUpdate]];
    private int _lastTick = -1;
    public int Updates { get; private set; }
    public int SourceTick => _lastTick;
    public int SkippedSourceFrames { get; private set; }
    public double LastUploadMs { get; private set; }
    public long LastUploadAllocatedBytes { get; private set; }
    public ViewportReplay(bool queued = false)
    {
        _queued = queued;
        for (int frame = 0; frame < 2; frame++)
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int at = (y * Size + x) * 4;
                    bool alternate = ((x / 64 + y / 64 + frame) & 1) != 0;
                    _frames[frame][at] = alternate ? (byte)100 : (byte)40;
                    _frames[frame][at + 1] = alternate ? (byte)40 : (byte)100;
                    _frames[frame][at + 2] = 70;
                    _frames[frame][at + 3] = 255;
                }
    }
    public bool Advance(double seconds, Texture2D texture)
    {
        int tick = checked((int)Math.Floor(seconds * 30));
        LastUploadMs = 0; LastUploadAllocatedBytes = 0;
        if (tick == _lastTick) return false;
        SkippedSourceFrames += Math.Max(0, tick - _lastTick - 1);
        _lastTick = tick;
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_queued)
        {
            // The command owns a copy of the pixels. The shared render queue records
            // this transfer before the UI draws; Vulkan retains its staging resource
            // with the submission and emits transfer-to-shader barriers.
            using var command = Graphics.GetCommandBuffer("N1 viewport upload");
            command.UpdateTexture<byte>(texture, 0, 0, Size, Size, _frames[tick & 1]);
            Graphics.Submit(command);
        }
        else texture.SetData(_frames[tick & 1].AsMemory(), 0, 0, Size, Size);
        LastUploadMs = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        LastUploadAllocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Updates++;
        return true;
    }
}
