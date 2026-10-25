using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

public class HotPathAwaitAnalyzerTests
{
    private const string Attr = @"
namespace XEngine
{
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, Inherited = false)]
    public sealed class HotPathAttribute : System.Attribute { }
}
";

    [Fact]
    public async Task Await_In_HotPath_Reports_PR0005()
    {
        var source = @"
using System.Threading.Tasks;
" + Attr + @"
class C
{
    [XEngine.HotPath]
    async Task M(Task t)
    {
        {|PR0005:await t|};
    }
}
";
        await VerifyAsync<HotPathAwaitAnalyzer>(source);
    }

    [Fact]
    public async Task Await_Outside_HotPath_No_Diagnostic()
    {
        var source = @"
using System.Threading.Tasks;
" + Attr + @"
class C
{
    async Task M(Task t)
    {
        await t;
    }
}
";
        await VerifyAsync<HotPathAwaitAnalyzer>(source);
    }
}
