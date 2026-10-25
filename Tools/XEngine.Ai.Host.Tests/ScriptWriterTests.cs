// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>★ Proves script_create / script_delete / script_apply_edits.</summary>
public class ScriptWriterTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public ScriptWriterTests(TestSceneFixture fx) => _fx = fx;

    private string ScriptsDir => Path.Combine(_fx.ProjectRoot, "Assets", "Scripts");
    private string ScriptPath(string name) => Path.Combine(ScriptsDir, name);

    [Fact]
    public async Task Create_WritesCsAndMeta_WithScriptImporter()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("Created.cs");
        var result = await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Created.cs",
            "class Created { }", TestSceneFixture.Provenance("test.script", "create-1"));

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".meta"));
        Assert.False(string.IsNullOrEmpty(result.ContentSha));
        // .meta has the ScriptImporter stamp.
        string meta = File.ReadAllText(path + ".meta");
        Assert.Contains("ScriptImporter", meta);
    }

    [Fact]
    public async Task Create_Duplicate_RejectsWithScriptExists()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("Dup.cs");
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Dup.cs", "class Dup { }",
            TestSceneFixture.Provenance("test.script", "dup-create-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Dup.cs", "class Dup { }",
                TestSceneFixture.Provenance("test.script", "dup-create-2")));
    }

    [Fact]
    public async Task ApplyEdits_UniqueMatch_Replaces()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("Editable.cs");
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Editable.cs",
            "class Editable { public int Value = 1; }",
            TestSceneFixture.Provenance("test.script", "edit-create-1"));

        var result = await _fx.ScriptWriter.ApplyEditsAsync(path, "Assets/Scripts/Editable.cs",
            new[] { new XEngine.Ai.Protocol.ScriptTextEdit("public int Value = 1;", "public int Value = 2;") },
            TestSceneFixture.Provenance("test.script", "edit-apply-1"));

        Assert.Equal(1, result.EditsApplied);
        string content = File.ReadAllText(path);
        Assert.Contains("Value = 2", content);
        Assert.DoesNotContain("Value = 1", content);
    }

    [Fact]
    public async Task ApplyEdits_AmbiguousMatch_Rejects()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("Amb.cs");
        // "x" appears 3 times — ambiguous for occurrence=first.
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Amb.cs",
            "class Amb { int x; int x; int x; }",
            TestSceneFixture.Provenance("test.script", "amb-create-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.ScriptWriter.ApplyEditsAsync(path, "Assets/Scripts/Amb.cs",
                new[] { new XEngine.Ai.Protocol.ScriptTextEdit("x", "y") },
                TestSceneFixture.Provenance("test.script", "amb-apply-1")));
    }

    [Fact]
    public async Task ApplyEdits_OccurrenceAll_ReplacesEveryMatch()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("All.cs");
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/All.cs",
            "class All { int a; int a; }",
            TestSceneFixture.Provenance("test.script", "all-create-1"));

        await _fx.ScriptWriter.ApplyEditsAsync(path, "Assets/Scripts/All.cs",
            new[] { new XEngine.Ai.Protocol.ScriptTextEdit("int a;", "int b;", Occurrence: "all") },
            TestSceneFixture.Provenance("test.script", "all-apply-1"));

        string content = File.ReadAllText(path);
        Assert.Contains("int b;", content);
        Assert.DoesNotContain("int a;", content);
    }

    [Fact]
    public async Task Delete_RemovesCsAndMeta()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("Del.cs");
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Del.cs", "class Del { }",
            TestSceneFixture.Provenance("test.script", "del-create-1"));
        Assert.True(File.Exists(path + ".meta"));

        await _fx.ScriptWriter.DeleteAsync(path, "Assets/Scripts/Del.cs",
            TestSceneFixture.Provenance("test.script", "del-1"));

        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".meta"));
    }

    [Fact]
    public async Task Create_IdempotentReplay_DoesNotOverwrite()
    {
        Directory.CreateDirectory(ScriptsDir);
        string path = ScriptPath("Idem.cs");
        var prov = TestSceneFixture.Provenance("test.script", "idem-1");
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Idem.cs", "class Idem { }", prov);

        // Replay with different content — must not overwrite.
        await _fx.ScriptWriter.CreateAsync(path, "Assets/Scripts/Idem.cs", "class Overwrite { }", prov);

        string content = File.ReadAllText(path);
        Assert.Contains("class Idem", content);
        Assert.DoesNotContain("Overwrite", content);
    }
}
