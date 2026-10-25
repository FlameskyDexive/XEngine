using System;
using System.IO;
using XEngine.Animation.Humanoid;
using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;

namespace XEngine.Editor.Importers;

public partial class EditorModelImporter
{
    // NUL cannot occur in a source node/file name. Keep this identity independent of the model's
    // display name and other sub-assets; existing mesh/material/clip/Humanoid IDs remain untouched.
    internal const string GenericAvatarStableId = "XEngine\0GenericAvatar\0v1";

    private static void GenerateGenericAvatar(ImportContext ctx, ModelImportResult data)
    {
        var settings = ctx.Settings;
        if (settings == null
            || (settings.TryGet("animationType", out var type) && type.IntValue != ModelRigBinding.Generic)
            || !settings.TryGet("avatarDefinition", out var definition) || definition.IntValue != 0)
            return; // NoAvatar, old/missing settings and unavailable Copy retain their old output.

        if (data.RootGO == null) throw new InvalidDataException("Generic Avatar requires a model hierarchy.");
        string rootPath = "";
        if (settings.TryGet("genericRootPath", out var root))
        {
            if (root.TagType != EchoType.String)
                throw new InvalidDataException("Generic root path must be a string.");
            rootPath = root.StringValue ?? "";
        }

        var avatar = Avatar.BuildGeneric(data.RootGO.Transform, rootPath,
            Path.GetFileNameWithoutExtension(ctx.AbsolutePath) + "Avatar");
        ctx.AddSubAsset(GenericAvatarStableId, avatar.Name, avatar);
        var animator = data.RootGO.GetComponent<Animator>();
        if (animator != null) animator.Avatar = new AssetRef<Avatar>(avatar);

        // This is Rig policy only. Clip-level overrides are not exposed by this importer yet.
        // Missing motion curves do not choose a different bone; runtime ExplicitBone fails closed.
        for (int i = 0; i < data.Animations.Count; i++)
        {
            data.Animations[i].RootMotionPolicy = rootPath.Length == 0
                ? ClipRootMotionPolicy.Disabled : ClipRootMotionPolicy.ExplicitBone;
            data.Animations[i].RootBonePath = rootPath;
        }
    }

    private static void RemoveImportedAnimation(ModelImportResult data)
    {
        // Clay attaches one component on the model root. Remove its refs before disposing the
        // unregistered clips; imported scene-less components have not received OnEnable.
        if (data.RootGO != null)
        {
            var animator = data.RootGO.GetComponent<Animator>();
            if (animator != null)
            {
                animator.Clip = default;
                data.RootGO.RemoveComponent(animator);
                animator.Dispose();
            }
            var legacy = data.RootGO.GetComponent<AnimationComponent>();
            if (legacy != null)
            {
                legacy.DefaultClip = default;
                legacy.Clips.Clear();
                data.RootGO.RemoveComponent(legacy);
                legacy.Dispose();
            }
        }
        for (int i = 0; i < data.Animations.Count; i++) data.Animations[i].Dispose();
        data.Animations.Clear();
    }
}
