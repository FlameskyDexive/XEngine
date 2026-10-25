using System;
using System.Collections.Generic;

using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;

namespace XEngine.Ai.Host;

internal static partial class AnimationAssetOps
{
    public static OverrideControllerInspectResult InspectOverrideController(string text, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        var overrides = new List<ClipOverrideInfo>();
        foreach (EchoObject row in Items(root, "Overrides"))
        {
            overrides.Add(new ClipOverrideInfo(
                GetAssetId(RefNode(row, "From")),
                GetAssetId(RefNode(row, "To"))));
        }

        return new OverrideControllerInspectResult(
            targetAsset,
            Str(root, "Name"),
            GetAssetId(RefNode(root, "Base")),
            overrides,
            ProvenancedDelta.ContentHash(root));
    }

    public static void PatchOverrideController(EchoObject root, IReadOnlyList<SceneFieldPatch> patches)
    {
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(root, p.Path, p.Value);
    }

    public static AvatarMaskInspectResult InspectAvatarMask(string text, string targetAsset)
    {
        EchoObject root = EchoObject.ReadFromString(text);
        var entries = new List<AvatarMaskEntryInfo>();
        foreach (EchoObject e in Items(root, "Entries"))
            entries.Add(new AvatarMaskEntryInfo(Str(e, "Path"), Num(e, "Weight", 1f)));

        var humanoid = new List<AvatarMaskHumanoidPartInfo>();
        IReadOnlyList<EchoObject> parts = Items(root, "HumanoidParts");
        string[] names = { "Root", "Body", "Head", "LeftArm", "RightArm", "LeftLeg", "RightLeg", "LeftFingers", "RightFingers" };
        for (int i = 0; i < names.Length; i++)
        {
            bool on = i >= parts.Count || BoolScalar(parts[i], def: true);
            humanoid.Add(new AvatarMaskHumanoidPartInfo(names[i], on));
        }

        return new AvatarMaskInspectResult(
            targetAsset,
            Str(root, "Name"),
            entries,
            humanoid,
            ProvenancedDelta.ContentHash(root));
    }

    public static void PatchAvatarMask(EchoObject root, IReadOnlyList<SceneFieldPatch> patches)
    {
        foreach (SceneFieldPatch p in patches)
            EchoFacade.SetPathValue(root, p.Path, p.Value);
    }

    static EchoObject RefNode(EchoObject parent, string field)
        => parent.TryGet(field, out EchoObject? n) && n is not null ? n : new EchoObject();

    static bool BoolScalar(EchoObject v, bool def)
        => v.TagType switch
        {
            EchoType.Bool => v.BoolValue,
            EchoType.Byte => v.ByteValue != 0,
            EchoType.Int => v.IntValue != 0,
            _ => def,
        };
}
