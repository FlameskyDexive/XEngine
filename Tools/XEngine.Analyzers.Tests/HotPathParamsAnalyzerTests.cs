using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

public class HotPathParamsAnalyzerTests
{
    private const string Attr = @"
namespace XEngine
{
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, Inherited = false)]
    public sealed class HotPathAttribute : System.Attribute { }
}
";

    [Fact]
    public async Task Params_On_HotPath_Method_Reports_PR0006()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    void M({|PR0006:params int[] xs|}) { }
}
";
        await VerifyAsync<HotPathParamsAnalyzer>(source);
    }

    [Fact]
    public async Task Params_Expanded_Invocation_In_HotPath_Reports_PR0006()
    {
        var source = Attr + @"
class C
{
    void Helper(params int[] xs) { }

    [XEngine.HotPath]
    void M()
    {
        {|PR0006:Helper(1, 2, 3)|};
    }
}
";
        await VerifyAsync<HotPathParamsAnalyzer>(source);
    }

    [Fact]
    public async Task Params_With_Preallocated_Array_In_HotPath_No_Diagnostic()
    {
        // Passing a preallocated array as the params argument does not allocate.
        var source = Attr + @"
class C
{
    void Helper(params int[] xs) { }

    static readonly int[] Preallocated = new int[] { 1, 2, 3 };

    [XEngine.HotPath]
    void M()
    {
        Helper(Preallocated);
    }
}
";
        await VerifyAsync<HotPathParamsAnalyzer>(source);
    }

    [Fact]
    public async Task Params_With_Inline_Array_Creation_In_HotPath_Reports_PR0006()
    {
        // Explicit array creation still allocates even in normal form.
        var source = Attr + @"
class C
{
    void Helper(params int[] xs) { }

    [XEngine.HotPath]
    void M()
    {
        {|PR0006:Helper(new int[] { 1, 2, 3 })|};
    }
}
";
        await VerifyAsync<HotPathParamsAnalyzer>(source);
    }

    [Fact]
    public async Task Params_Outside_HotPath_No_Diagnostic()
    {
        var source = Attr + @"
class C
{
    void Helper(params int[] xs) { }

    void M()
    {
        Helper(1, 2, 3);
    }
}
";
        await VerifyAsync<HotPathParamsAnalyzer>(source);
    }
}
