using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

public class HotPathNewAnalyzerTests
{
    private const string Attr = @"
namespace XEngine
{
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, Inherited = false)]
    public sealed class HotPathAttribute : System.Attribute { }

    [System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
    public sealed class PoolAttribute : System.Attribute
    {
        public int InitialCapacity { get; set; } = 16;
    }
}
";

    [Fact]
    public async Task New_Class_In_HotPath_Reports_PR0007()
    {
        var source = Attr + @"
class Box { }
class C
{
    [XEngine.HotPath]
    object M() => {|PR0007:new Box()|};
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }

    [Fact]
    public async Task New_Struct_In_HotPath_No_Diagnostic()
    {
        var source = Attr + @"
struct Point { public int X; }
class C
{
    [XEngine.HotPath]
    Point M() => new Point();
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }

    [Fact]
    public async Task New_Pooled_Class_In_HotPath_No_Diagnostic()
    {
        var source = Attr + @"
[XEngine.Pool]
class PooledBox { }
class C
{
    [XEngine.HotPath]
    object M() => new PooledBox();
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }

    [Fact]
    public async Task New_Class_Outside_HotPath_No_Diagnostic()
    {
        var source = Attr + @"
class Box { }
class C
{
    object M() => new Box();
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }

    [Fact]
    public async Task New_Array_In_HotPath_Reports_PR0007()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    int[] M() => {|PR0007:new int[4]|};
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }

    [Fact]
    public async Task Collection_Expression_Array_In_HotPath_Reports_PR0007()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    int[] M() => {|PR0007:[1, 2, 3]|};
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }

    [Fact]
    public async Task New_String_In_HotPath_Reports_PR0007()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    string M(char[] chars) => {|PR0007:new string(chars)|};
}
";
        await VerifyAsync<HotPathNewAnalyzer>(source);
    }
}
