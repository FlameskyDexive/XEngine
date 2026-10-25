// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using XEngine.Data.Provenance;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ P1-7 — Shared fixture: a temp project root with a minimal .scene file and a wired-up
/// <see cref="SceneWriter"/>/<see cref="AssetWriter"/> backed by a real <see cref="ChangeJournal"/>.
/// Tests get isolated on-disk state each run (no cross-test contamination of the journal).
/// </summary>
public sealed class TestSceneFixture : IDisposable
{
    public string ProjectRoot { get; }
    public string ScenePath { get; }
    public string SceneAbsolutePath { get; }
    public string JournalRoot { get; }
    public ChangeJournal Journal { get; }
    public SceneWriter SceneWriter { get; }
    public AssetWriter AssetWriter { get; }
    public ScriptWriter ScriptWriter { get; }

    /// <summary>The GameObject Identifier tests can patch/delete/component-target.</summary>
    public const string PlayerIdentifier = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    public TestSceneFixture()
    {
        ProjectRoot = Path.Combine(Path.GetTempPath(), "xengine-ai-test-" + Guid.NewGuid().ToString("N"));
        JournalRoot = Path.Combine(ProjectRoot, ".xengine", "journal");
        Directory.CreateDirectory(JournalRoot);

        string sceneDir = Path.Combine(ProjectRoot, "Assets", "Scenes");
        Directory.CreateDirectory(sceneDir);
        ScenePath = "Assets/Scenes/Test.scene";
        SceneAbsolutePath = Path.Combine(sceneDir, "Test.scene");
        File.WriteAllText(SceneAbsolutePath, MakeMinimalScene());

        Journal = new ChangeJournal(JournalRoot, "test-project");
        SceneWriter = new SceneWriter(Journal);
        AssetWriter = new AssetWriter(Journal);
        ScriptWriter = new ScriptWriter(Journal);
    }

    /// <summary>Reset the scene file to its initial state (call between tests that mutate it).</summary>
    public void ResetScene() => File.WriteAllText(SceneAbsolutePath, MakeMinimalScene());

    /// <summary>Read the scene's current content hash (the value material_inspect/scene_hierarchy returns).</summary>
    public string ReadSceneHash() =>
        XEngine.Data.Provenance.ProvenancedDelta.ContentHash(
            XEngine.Echo.EchoObject.ReadFromString(File.ReadAllText(SceneAbsolutePath)));

    public void Dispose()
    {
        try { Directory.Delete(ProjectRoot, recursive: true); } catch { /* test cleanup */ }
    }

    /// <summary>A scene with a single empty Player GameObject — the minimal shape SceneWriter/EchoFacade
    /// understand (serializeObj.array[0] = Player with empty Components/Children).</summary>
    public static string MakeMinimalScene() => $@"{{
  ""$id"": 1,
  ""$type"": ""XEngine.Runtime.Resources.Scene, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"",
  ""serializeObj"": {{
    ""$id"": 2,
    ""array"": [
      {{
        ""$id"": 3,
        ""Name"": ""Player"",
        ""AssetPath"": """",
        ""Identifier"": ""{PlayerIdentifier}"",
        ""Static"": 0B,
        ""Enabled"": 1B,
        ""EnabledInHierarchy"": 1B,
        ""TagIndex"": 0,
        ""LayerIndex"": 0,
        ""HideFlags"": 0,
        ""Transform"": {{
          ""$id"": 4,
          ""$type"": ""XEngine.Vector.Transform, XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null"",
          ""_localPosition"": {{ ""X"": 0F, ""Y"": 0F, ""Z"": 0F }},
          ""_localScale"": {{ ""X"": 1F, ""Y"": 1F, ""Z"": 1F }},
          ""_localRotation"": {{ ""X"": 0F, ""Y"": 0F, ""Z"": 0F, ""W"": 1F }}
        }},
        ""Components"": [],
        ""Children"": []
      }}
    ]
  }}
}}";

    public static ChangeProvenance Provenance(string tool, string idempotencyKey, string? intent = null)
        => ChangeProvenance.ForTool(tool, intent, idempotencyKey);
}
