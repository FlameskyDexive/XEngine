// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Data.Provenance;
using XEngine.Echo;
using Xunit;

namespace XEngine.Data.Provenance.Tests;

/// <summary>
/// Proves A0 (Layer 0): the dormant Echo Delta primitive is wired in as the operation-recording
/// primitive, provenance round-trips, and delta replay reconstructs the target. These are the
/// guarantees the on-disk journal (A2) and the AI scene-write tools (B2) rely on.
/// </summary>
public class ProvenancedDeltaTests
{
    [Fact]
    public void Create_CapturesFieldLevelDiffAndAttachesProvenance()
    {
        EchoObject before = MakeGameObjectEcho("Main Camera", x: 0f, y: 1f, z: -5f);
        EchoObject after = MakeGameObjectEcho("Main Camera", x: 0f, y: 4f, z: -5f); // only Y changed

        EchoObject delta = ProvenancedDelta.Create(before, after,
            ChangeProvenance.ForTool("xengine-cli.scene-object-patch",
                "raise the camera above the character", "key-1"));

        // The provenance is readable back off the delta.
        ChangeProvenance? prov = ProvenancedDelta.GetProvenance(delta);
        Assert.NotNull(prov);
        Assert.Equal("xengine-cli.scene-object-patch", prov!.Author);
        Assert.Equal("raise the camera above the character", prov.Intent);
        Assert.Equal("key-1", prov.IdempotencyKey);

        // And a stable change id was attached.
        Assert.NotNull(ProvenancedDelta.GetChangeId(delta));
    }

    [Fact]
    public void ApplyDelta_ReplaysTheChangeToTheBaseline()
    {
        // The core replay guarantee: ApplyDelta(baseline, delta) must reconstruct "after".
        EchoObject before = MakeGameObjectEcho("Cube", x: 0f, y: 0f, z: 0f);
        EchoObject after = MakeGameObjectEcho("Cube", x: 10f, y: 0f, z: 0f);
        EchoObject delta = EchoObject.CreateDelta(before, after);

        EchoObject replayed = EchoObject.ApplyDelta(before, delta);

        Assert.Equal(after.WriteToString(), replayed.WriteToString());
    }

    [Fact]
    public void Provenance_SurvivesSerializeRoundTrip()
    {
        // The journal persists deltas as text; provenance must survive the round-trip.
        EchoObject before = MakeGameObjectEcho("Light", x: 0f, y: 5f, z: 0f);
        EchoObject after = MakeGameObjectEcho("Light", x: 0f, y: 8f, z: 0f);
        EchoObject delta = ProvenancedDelta.Create(before, after,
            ChangeProvenance.ForEditor(intent: "darken scene to dusk"));

        string serialized = delta.WriteToString();
        EchoObject restored = EchoObject.ReadFromString(serialized);

        ChangeProvenance? prov = ProvenancedDelta.GetProvenance(restored);
        Assert.NotNull(prov);
        Assert.Equal("editor", prov!.Author);
        Assert.Equal("darken scene to dusk", prov.Intent);
        Assert.Equal(ProvenancedDelta.GetProvenance(delta)!.TimestampUtc, prov.TimestampUtc);

        // And replay still works off the restored delta (ApplyDelta reads Operations from the compound).
        EchoObject replayed = EchoObject.ApplyDelta(before, restored);
        Assert.Equal(after.WriteToString(), replayed.WriteToString());
    }

    [Fact]
    public void ApplyDelta_IgnoresProvenanceTags_OnlyReplaysOperations()
    {
        // Provenance is additive: ApplyDelta must not choke on the extra ChangeId/Provenance tags.
        EchoObject before = MakeGameObjectEcho("Spawn", x: 1f, y: 2f, z: 3f);
        EchoObject after = MakeGameObjectEcho("Spawn", x: 9f, y: 2f, z: 3f);
        EchoObject delta = ProvenancedDelta.Create(before, after,
            ChangeProvenance.ForEditor("move spawn point"));

        // ApplyDelta reads only "Operations"; the Provenance/ChangeId tags are inert to replay.
        EchoObject replayed = EchoObject.ApplyDelta(before, delta);
        Assert.Equal(after.WriteToString(), replayed.WriteToString());
    }

    [Fact]
    public void ContentHash_IsStableForSameContent_AndDiffersForChangedContent()
    {
        EchoObject a = MakeGameObjectEcho("Obj", 0f, 0f, 0f);
        EchoObject b = MakeGameObjectEcho("Obj", 1f, 0f, 0f);

        Assert.Equal(ProvenancedDelta.ContentHash(a), ProvenancedDelta.ContentHash(a.Clone()));
        Assert.NotEqual(ProvenancedDelta.ContentHash(a), ProvenancedDelta.ContentHash(b));
    }

    /// <summary>Builds a minimal GameObject-shaped Echo tree (Name + Transform.Position) for diffing.</summary>
    private static EchoObject MakeGameObjectEcho(string name, float x, float y, float z)
    {
        EchoObject root = EchoObject.NewCompound();
        root["Name"] = new EchoObject(name);

        EchoObject transform = EchoObject.NewCompound();
        EchoObject position = EchoObject.NewCompound();
        position["X"] = new EchoObject(x);
        position["Y"] = new EchoObject(y);
        position["Z"] = new EchoObject(z);
        transform["Position"] = position;
        root["Transform"] = transform;
        return root;
    }
}
