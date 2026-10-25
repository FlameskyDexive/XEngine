using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

public class HotPathForeachAnalyzerTests
{
    private const string Attr = @"
namespace XEngine
{
    [System.AttributeUsage(System.AttributeTargets.Method | System.AttributeTargets.Class, Inherited = false)]
    public sealed class HotPathAttribute : System.Attribute { }
}
";

    [Fact]
    public async Task Foreach_Over_IEnumerable_In_HotPath_Reports_PR0003()
    {
        var source = @"
using System.Collections.Generic;
" + Attr + @"
class C
{
    [XEngine.HotPath]
    void M(IEnumerable<int> items)
    {
        {|PR0003:foreach|} (var x in items) { _ = x; }
    }
}
";
        await VerifyAsync<HotPathForeachAnalyzer>(source);
    }

    [Fact]
    public async Task Foreach_Over_Array_In_HotPath_No_Diagnostic()
    {
        var source = Attr + @"
class C
{
    [XEngine.HotPath]
    void M(int[] items)
    {
        foreach (var x in items) { _ = x; }
    }
}
";
        await VerifyAsync<HotPathForeachAnalyzer>(source);
    }

    [Fact]
    public async Task Foreach_Over_List_In_HotPath_No_Diagnostic()
    {
        // List<T>.Enumerator is a struct
        var source = @"
using System.Collections.Generic;
" + Attr + @"
class C
{
    [XEngine.HotPath]
    void M(List<int> items)
    {
        foreach (var x in items) { _ = x; }
    }
}
";
        await VerifyAsync<HotPathForeachAnalyzer>(source);
    }

    [Fact]
    public async Task Foreach_ClassEnumerator_Outside_HotPath_No_Diagnostic()
    {
        var source = @"
using System.Collections.Generic;
" + Attr + @"
class C
{
    void M(IEnumerable<int> items)
    {
        foreach (var x in items) { _ = x; }
    }
}
";
        await VerifyAsync<HotPathForeachAnalyzer>(source);
    }

    [Fact]
    public async Task Foreach_Deconstruction_ClassEnumerator_In_HotPath_Reports_PR0003()
    {
        var source = @"
using System;
using System.Collections.Generic;
" + Attr + @"
class C
{
    [XEngine.HotPath]
    void M(IEnumerable<(int a, int b)> items)
    {
        {|PR0003:foreach|} (var (a, b) in items) { _ = a + b; }
    }
}
";
        await VerifyAsync<HotPathForeachAnalyzer>(source);
    }
}
