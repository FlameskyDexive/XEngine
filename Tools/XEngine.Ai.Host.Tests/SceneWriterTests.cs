// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Echo;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// ★ P1-7 — Proves the P0 safety guarantees and write-surface behaviors of <see cref="SceneWriter"/>
/// as permanent, repeatable tests (the manual smoke scripts from P0-1..P0-6 become regression guards).
/// </summary>
public class SceneWriterTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public SceneWriterTests(TestSceneFixture fx) => _fx = fx;

    // ---- P0-1 idempotency ----

    [Fact]
    public async Task Patch_SameIdempotencyKey_Twice_DoesNotRewriteFile()
    {
        _fx.ResetScene();
        var patches = new[] { new SceneFieldPatch("Transform/_localPosition/X", "5") };
        var prov = TestSceneFixture.Provenance("test.patch", "idem-1");

        var first = await _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, patches, prov, dryRun: false);
        string contentAfterFirst = File.ReadAllText(_fx.SceneAbsolutePath);

        // Replay with a different patch value but the SAME idempotency key — must dedup.
        var replayPatches = new[] { new SceneFieldPatch("Transform/_localPosition/X", "999") };
        var replay = await _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, replayPatches, prov, dryRun: false);

        string contentAfterReplay = File.ReadAllText(_fx.SceneAbsolutePath);

        Assert.Equal(first.ChangeId, replay.ChangeId);          // same change id returned
        Assert.Equal(first.AfterHash, replay.AfterHash);        // same after-hash
        Assert.Equal(contentAfterFirst, contentAfterReplay);    // file untouched by the replay
        Assert.Contains("\"X\": 5", contentAfterReplay);        // the first patch's value stands
        Assert.DoesNotContain("\"X\": 999", contentAfterReplay);
    }

    // ---- P0-2 drift protection ----

    [Fact]
    public async Task Patch_StaleExpectedBeforeHash_IsRejected()
    {
        _fx.ResetScene();
        string hash = _fx.ReadSceneHash();

        // First patch changes the file, so the original hash is now stale.
        await _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier,
            new[] { new SceneFieldPatch("Transform/_localPosition/Y", "7") },
            TestSceneFixture.Provenance("test.patch", "first-1"), dryRun: false);

        // Second patch with the STALE hash must refuse.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
                TestSceneFixture.PlayerIdentifier,
                new[] { new SceneFieldPatch("Transform/_localPosition/Z", "9") },
                TestSceneFixture.Provenance("test.patch", "stale-1"), dryRun: false,
                expectedBeforeHash: hash));

        Assert.Contains("stale_state", ex.Message);
    }

    [Fact]
    public async Task Patch_MatchingExpectedBeforeHash_Succeeds()
    {
        _fx.ResetScene();
        string hash = _fx.ReadSceneHash();

        var result = await _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier,
            new[] { new SceneFieldPatch("Transform/_localPosition/X", "3") },
            TestSceneFixture.Provenance("test.patch", "match-1"), dryRun: false,
            expectedBeforeHash: hash);

        Assert.NotEqual(result.BeforeHash, result.AfterHash);
    }

    // ---- P0-4 component add/remove ----

    [Fact]
    public async Task AddComponent_Camera_IsReadableBack()
    {
        _fx.ResetScene();
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera",
            initialFields: new[] { new SceneFieldPatch("FieldOfView", "75") },
            TestSceneFixture.Provenance("test.comp", "add-cam-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        var player = Assert.Single(hierarchy.RootObjects);
        var camera = Assert.Single(player.Components);
        Assert.Equal("Camera", camera.TypeName);
        // The initialFields patch landed.
        Assert.Equal("75", camera.Fields["FieldOfView"]);
        // A fresh _identifier was minted and hoisted to the top level.
        Assert.False(string.IsNullOrEmpty(camera.Identifier));
    }

    [Fact]
    public async Task AddComponent_UnknownType_IsRejectedWithKnownNames()
    {
        _fx.ResetScene();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
                TestSceneFixture.PlayerIdentifier, "Frobnicator",
                initialFields: null,
                TestSceneFixture.Provenance("test.comp", "add-unknown-1"), dryRun: false));

        Assert.Contains("Camera", ex.Message);  // known-names hint includes Camera
    }

    [Fact]
    public async Task RemoveComponent_ByType_RemovesFirstMatch()
    {
        _fx.ResetScene();
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "AudioSource", null,
            TestSceneFixture.Provenance("test.comp", "add-audio-1"), dryRun: false);

        await _fx.SceneWriter.RemoveComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, componentIdentifier: null, componentType: "AudioSource",
            TestSceneFixture.Provenance("test.comp", "rm-audio-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        var player = Assert.Single(hierarchy.RootObjects);
        Assert.Empty(player.Components);
    }

    [Fact]
    public async Task RemoveComponent_ByIdentifier_RemovesExactMatch()
    {
        _fx.ResetScene();
        var addResult = await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera", null,
            TestSceneFixture.Provenance("test.comp", "add-cam-byid-1"), dryRun: false);
        // Extract the new component's identifier from the summary ("id xxxxxxxx…").
        var hierarchyAfterAdd = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        var camId = hierarchyAfterAdd.RootObjects[0].Components[0].Identifier;

        await _fx.SceneWriter.RemoveComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, componentIdentifier: camId, componentType: null,
            TestSceneFixture.Provenance("test.comp", "rm-cam-byid-1"), dryRun: false);

        var hierarchyAfterRemove = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        Assert.Empty(hierarchyAfterRemove.RootObjects[0].Components);
    }

    // ---- scene_component_patch (the bug fix: edit existing component fields by identifier/type) ----

    [Fact]
    public async Task PatchComponent_ByType_EditsField()
    {
        _fx.ResetScene();
        // Add a Camera with a known FieldOfView so we can verify the patch changes it.
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera",
            new[] { new SceneFieldPatch("FieldOfView", "55") },
            TestSceneFixture.Provenance("test.comp", "patch-add-1"), dryRun: false);

        // Patch FieldOfView via type match.
        await _fx.SceneWriter.PatchComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, componentIdentifier: null, componentType: "Camera",
            new[] { new SceneFieldPatch("FieldOfView", "75") }, enable: null,
            TestSceneFixture.Provenance("test.comp", "patch-fov-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        var cam = Assert.Single(hierarchy.RootObjects[0].Components);
        Assert.Equal("75", cam.Fields["FieldOfView"]);
    }

    [Fact]
    public async Task PatchComponent_ByIdentifier_EditsField()
    {
        _fx.ResetScene();
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera", null,
            TestSceneFixture.Provenance("test.comp", "patch-id-add-1"), dryRun: false);
        var camId = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath).RootObjects[0].Components[0].Identifier;

        await _fx.SceneWriter.PatchComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, componentIdentifier: camId, componentType: null,
            new[] { new SceneFieldPatch("NearClipPlane", "0.5") }, enable: null,
            TestSceneFixture.Provenance("test.comp", "patch-id-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        Assert.Equal("0.5", hierarchy.RootObjects[0].Components[0].Fields["NearClipPlane"]);
    }

    [Fact]
    public async Task PatchComponent_Enable_TogglesEnabledFlag()
    {
        _fx.ResetScene();
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera", null,
            TestSceneFixture.Provenance("test.comp", "enable-add-1"), dryRun: false);

        // Disable.
        await _fx.SceneWriter.PatchComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, componentIdentifier: null, componentType: "Camera",
            patches: Array.Empty<SceneFieldPatch>(), enable: false,
            TestSceneFixture.Provenance("test.comp", "disable-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        Assert.Equal("False", hierarchy.RootObjects[0].Components[0].Fields["_enabled"]);

        // Re-enable.
        await _fx.SceneWriter.PatchComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, componentIdentifier: null, componentType: "Camera",
            patches: Array.Empty<SceneFieldPatch>(), enable: true,
            TestSceneFixture.Provenance("test.comp", "enable-1"), dryRun: false);

        hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        Assert.Equal("True", hierarchy.RootObjects[0].Components[0].Fields["_enabled"]);
    }

    [Fact]
    public async Task PatchComponent_NotFound_Rejects()
    {
        _fx.ResetScene();
        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(() =>
            _fx.SceneWriter.PatchComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
                TestSceneFixture.PlayerIdentifier, componentIdentifier: null, componentType: "NonExistent",
                new[] { new SceneFieldPatch("X", "1") }, enable: null,
                TestSceneFixture.Provenance("test.comp", "patch-nf-1"), dryRun: false));
    }

    [Fact]
    public async Task SceneObjectPatch_ComponentField_ByIndex_Works()
    {
        // ★ The root-cause regression test: scene_object_patch with a Components/i/Field path.
        // Before the SetPathValue fix, this threw "Cannot get tag from non-compound tag".
        _fx.ResetScene();
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Camera",
            new[] { new SceneFieldPatch("FieldOfView", "55") },
            TestSceneFixture.Provenance("test.comp", "idx-add-1"), dryRun: false);

        // Patch via Components/0/FieldOfView (the path that used to crash).
        await _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier,
            new[] { new SceneFieldPatch("Components/0/FieldOfView", "60") },
            TestSceneFixture.Provenance("test.scene", "idx-patch-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        Assert.Equal("60", hierarchy.RootObjects[0].Components[0].Fields["FieldOfView"]);
    }

    [Fact]
    public async Task Patch_IntegerLiteral_OnFloatLeaf_KeepsFloatType()
    {
        // ★ Regression: an integer literal like "-10" on a float-typed leaf (Float3 transform
        // channels) used to serialize as a Long ("-10L"), which float deserialization silently
        // reads back as 0. The literal must keep the leaf's numeric kind.
        _fx.ResetScene();
        await _fx.SceneWriter.PatchAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier,
            new[] { new SceneFieldPatch("Transform/_localPosition/Z", "-10") },
            TestSceneFixture.Provenance("test.scene", "float-kind-1"), dryRun: false);

        string content = File.ReadAllText(_fx.SceneAbsolutePath);
        Assert.Contains("\"Z\": -10F", content);
        Assert.DoesNotContain("\"Z\": -10L", content);
    }

    [Fact]
    public async Task AddComponent_Animator_IsReadableBack()
    {
        _fx.ResetScene();
        await _fx.SceneWriter.AddComponentAsync(_fx.SceneAbsolutePath, _fx.ScenePath,
            TestSceneFixture.PlayerIdentifier, "Animator",
            initialFields: null,
            TestSceneFixture.Provenance("test.comp", "add-anim-1"), dryRun: false);

        var hierarchy = SceneReader.ReadHierarchy(_fx.SceneAbsolutePath);
        var player = Assert.Single(hierarchy.RootObjects);
        var animator = Assert.Single(player.Components);
        Assert.Equal("Animator", animator.TypeName);
    }
}
