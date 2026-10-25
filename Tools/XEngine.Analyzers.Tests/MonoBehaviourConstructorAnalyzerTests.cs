using System.Threading.Tasks;
using Xunit;
using static XEngine.Analyzers.Tests.AnalyzerTestHelper;

namespace XEngine.Analyzers.Tests;

/// <summary>
/// Tests for PR0010 (prowl-sync M7.1, ported from upstream 8300a5ad): constructors on MonoBehaviours
/// run before the component is attached, and a component without a public parameterless constructor
/// can never be constructed by the engine at all.
/// </summary>
public class MonoBehaviourConstructorAnalyzerTests
{
    private const string Stubs = @"
namespace XEngine.Runtime
{
    public class GameObject
    {
        public string Name = string.Empty;
    }
    public class MonoBehaviour
    {
        public GameObject GameObject = new GameObject();
    }
}
";

    [Fact]
    public async Task NoPublicParameterlessConstructor_Reports_PR0010_And_PR0011()
    {
        // Two diagnostics: the parameterized constructor is discouraged (PR0010, warning) AND the
        // type can never be constructed by the engine (PR0011, error).
        const string source = Stubs + @"
namespace XEngine.Runtime.Testing
{
    public class C : XEngine.Runtime.MonoBehaviour
    {
        public C(int x) { }
    }
}
";
        var results = new[]
        {
            new Microsoft.CodeAnalysis.Testing.DiagnosticResult(MonoBehaviourConstructorAnalyzer.NoParameterlessConstructor)
                .WithSpan(16, 18, 16, 19).WithArguments("C"),
            new Microsoft.CodeAnalysis.Testing.DiagnosticResult(MonoBehaviourConstructorAnalyzer.DeclaredConstructor)
                .WithSpan(18, 9, 18, 28).WithArguments("C"),
        };
        await VerifyAsync<MonoBehaviourConstructorAnalyzer>(source, results);
    }

    [Fact]
    public void NoParameterlessConstructor_HasErrorSeverity()
    {
        // The engine can never construct such a component, so this one stays an error (unlike the
        // declared-constructor warning).
        Assert.Equal(
            Microsoft.CodeAnalysis.DiagnosticSeverity.Error,
            MonoBehaviourConstructorAnalyzer.NoParameterlessConstructor.DefaultSeverity);
    }

    [Fact]
    public void DeclaredConstructor_HasWarningSeverity()
    {
        Assert.Equal(
            Microsoft.CodeAnalysis.DiagnosticSeverity.Warning,
            MonoBehaviourConstructorAnalyzer.DeclaredConstructor.DefaultSeverity);
    }
}
