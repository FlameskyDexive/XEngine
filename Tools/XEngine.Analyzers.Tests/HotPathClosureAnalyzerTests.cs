using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

public class HotPathClosureAnalyzerTests
{
    private const string Attr = @"
namespace XEngine
{
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, Inherited = false)]
    public sealed class HotPathAttribute : System.Attribute { }
}
";

    [Fact]
    public async Task Capturing_Lambda_In_HotPath_Reports_PR0002()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    void M(int x, System.Action<System.Func<int>> sink)
    {
        sink({|PR0002:() => x|});
    }
}
";
        await VerifyAsync<HotPathClosureAnalyzer>(source);
    }

    [Fact]
    public async Task NonCapturing_Lambda_No_Diagnostic()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    void M(System.Action<System.Func<int>> sink)
    {
        sink(() => 42);
    }
}
";
        await VerifyAsync<HotPathClosureAnalyzer>(source);
    }

    [Fact]
    public async Task Capturing_Outside_HotPath_No_Diagnostic()
    {
        var source = Attr + @"
class C
{
    void M(int x, System.Action<System.Func<int>> sink)
    {
        sink(() => x);
    }
}
";
        await VerifyAsync<HotPathClosureAnalyzer>(source);
    }
}
