using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

public class LinqForbiddenAnalyzerTests
{
    [Fact]
    public async Task Using_System_Linq_Without_Invocation_No_Diagnostic()
    {
        const string source = @"
using System.Linq;
class C { void M() { } }
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task No_Linq_No_Diagnostic()
    {
        const string source = @"
class C
{
    void M()
    {
        var a = new[] { 1, 2, 3 };
        for (int i = 0; i < a.Length; i++) { _ = a[i]; }
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Linq_Invocation_Reports_PR0001()
    {
        const string source = @"
using System.Linq;
class C
{
    void M(int[] xs)
    {
        var f = {|PR0001:xs.First()|};
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Query_Expression_Reports_PR0001()
    {
        const string source = @"
using System.Linq;
class C
{
    void M(int[] xs)
    {
        var q = {|PR0001:from x in xs where x > 0 select x|};
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Benchmark_Allowlisted_Array_And_List_Predicate_Operators_No_Diagnostic()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class C
{
    void M(int[] array, List<int> list)
    {
        _ = array.Any(static x => x > 0);
        _ = list.All(static x => x > 0);
        _ = list.Count(static x => x > 0);
        _ = array.FirstOrDefault(static x => x > 0);
        _ = list.FirstOrDefault(static x => x > 0);
        _ = list.Any(static x => x > 0);
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Parameterless_Count_On_Array_Still_Reports()
    {
        const string source = @"
using System.Linq;
class C
{
    int M(int[] xs) => {|PR0001:xs.Count()|};
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Array_Count_And_All_Still_Report()
    {
        const string source = @"
using System.Linq;
class C
{
    void M(int[] array)
    {
        _ = {|PR0001:array.Count(static x => x > 0)|};
        _ = {|PR0001:array.All(static x => x > 0)|};
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task ToList_On_Array_And_List_Is_Allowlisted_Outside_HotPath()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class C
{
    void M(int[] array, List<int> list)
    {
        _ = array.ToList();
        _ = list.ToList();
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task ToList_Inside_HotPath_Still_Reports()
    {
        const string source = @"
using System;
using System.Linq;
namespace XEngine { sealed class HotPathAttribute : Attribute { } }
class C
{
    [XEngine.HotPath]
    void M(int[] array) => _ = {|PR0001:array.ToList()|};
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Capturing_Predicate_Still_Reports()
    {
        const string source = @"
using System.Linq;
class C
{
    bool M(int[] items, int threshold) => {|PR0001:items.Any(x => x > threshold)|};
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Where_ToArray_And_Concat_Still_Report()
    {
        const string source = @"
using System.Linq;
class C
{
    void M(int[] a, int[] b)
    {
        _ = {|PR0001:{|PR0001:a.Where(static x => x > 0)|}.ToArray()|};
        _ = {|PR0001:a.Concat(b)|};
    }
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }

    [Fact]
    public async Task Allowlisted_Operator_On_IEnumerable_Still_Reports()
    {
        const string source = @"
using System.Collections.Generic;
using System.Linq;
class C
{
    bool M(IEnumerable<int> items) => {|PR0001:items.Any(static x => x > 0)|};
}
";
        await VerifyAsync<LinqForbiddenAnalyzer>(source);
    }
}
