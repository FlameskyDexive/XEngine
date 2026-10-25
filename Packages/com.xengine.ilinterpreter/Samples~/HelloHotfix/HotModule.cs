using XEngine.Async;

namespace Game.Hotfix;

/// <summary>生命周期计数器：ILBehaviourHost 每帧转发 Update。</summary>
public class HelloBehaviour
{
    public int Ticks;

    public void Start() { Ticks = 0; }

    public void Update() => Ticks++;

    public async XTask<int> WaitForHostValue()
    {
        int v = await HostApi.GetValue();
        return v * 2;
    }
}

/// <summary>宿主必须在 ILBindings.txt / 运行时绑定中注册的 API 面。</summary>
public static class HostApi
{
    // 运行时由宿主注册：XEngine.ILInterpreter.Hosting.XTaskAsyncSupport + 值绑定
    public static XTask<int> GetValue() => default;
}
