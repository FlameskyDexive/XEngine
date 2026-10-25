using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

/// <summary>
/// Tests for PR0008/PR0009 (prowl-sync M7.1, ported from upstream 9edc7a4f): null-conditional and
/// null-coalescing operators on EngineObject-derived values bypass destruction-aware validity.
/// </summary>
public class EngineObjectNullAnalyzerTests
{
    private const string Stubs = @"
namespace XEngine.Runtime
{
    public class EngineObject
    {
        public string Name = string.Empty;
        public bool IsValid() => true;
        public bool IsNotValid() => false;
    }
    public class GameObject : EngineObject { }
}
namespace XEngine.Runtime.Testing
{
    public class Plain
    {
        public string Name = string.Empty;
    }
}
";

    [Fact]
    public async Task NullConditional_OnEngineObject_Reports_PR0008()
    {
        const string source = Stubs + @"
namespace XEngine.Runtime.Testing
{
    public class C
    {
        void M(GameObject go)
        {
            var n = ({|PR0008:go|}?.Name).ToString();
        }
    }
}
";
        await VerifyAsync<EngineObjectNullAnalyzer>(source);
    }

    [Fact]
    public async Task NullCoalescing_OnEngineObject_Reports_PR0009()
    {
        const string source = Stubs + @"
namespace XEngine.Runtime.Testing
{
    public class C
    {
        void M(GameObject go)
        {
            var name = ({|PR0009:go|} ?? new GameObject()).Name;
        }
    }
}
";
        await VerifyAsync<EngineObjectNullAnalyzer>(source);
    }

    [Fact]
    public async Task NullConditional_OnPlainClass_NoDiagnostic()
    {
        const string source = Stubs + @"
namespace XEngine.Runtime.Testing
{
    public class C
    {
        void M(Plain p)
        {
            var n = p?.Name;
        }
    }
}
";
        await VerifyAsync<EngineObjectNullAnalyzer>(source);
    }

    [Fact]
    public async Task IsValidGuard_NoDiagnostic()
    {
        const string source = Stubs + @"
namespace XEngine.Runtime.Testing
{
    public class C
    {
        void M(GameObject go)
        {
            if (go.IsNotValid()) return;
            var n = go.Name;
        }
    }
}
";
        await VerifyAsync<EngineObjectNullAnalyzer>(source);
    }
}
