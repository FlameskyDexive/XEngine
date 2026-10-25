using XEngine.Runtime;
using XEngine.Runtime.GUI;

namespace XEngine.UiPrototype;

internal interface IPrototypeAdapter : IDisposable
{
    long BuildCount { get; }
    UiRenderStats Stats { get; }
    void SetInput(in PrototypeInput input);
    bool Prepare(float width, float height, float scale, object texture);
    void Render();
    void SetTarget(GraphicsFrameBuffer? target);
    void SetGpuTiming(bool enabled);
}
