// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Animation;

namespace XEngine.Runtime.Test.Animation;

public sealed class AnimatorControllerAssetLifetimeTests : RuntimeTestBase
{
    private sealed class SeededAssetBackend : AssetBackendBase
    {
        public void Seed(Guid id, EngineObject asset) => SetLoaded(id, asset);

        protected override EngineObject? LoadFresh(Guid assetId) => null;
    }

    [Fact]
    public void ControllerClips_RemainPlayableAfterIdleSweep_UntilOwningSceneDisposes()
    {
        AssetBackendBase? previousBackend = AssetDatabase.Current;
        Guid idleId = Guid.NewGuid();
        Guid attackId = Guid.NewGuid();
        var idle = AnimationTestHelpers.MakeConstantPosClip("idle", "Hips", 0f);
        var attack = AnimationTestHelpers.MakeConstantPosClip("attack", "Hips", 10f);
        idle.AssetID = idleId;
        attack.AssetID = attackId;
        var backend = new SeededAssetBackend();
        backend.Seed(idleId, idle);
        backend.Seed(attackId, attack);
        AssetDatabase.Current = backend;

        var scene = CreateScene(enable: true);
        try
        {
            var root = CreateGameObject("Armature");
            var hips = CreateGameObject("Hips");
            hips.SetParent(root);
            scene.Add(root);

            var controller = new AnimatorController { DefaultStateName = "Idle" };
            controller.States.Add(new AnimatorController.State
            {
                Name = "Idle",
                Motion = new AssetRef<AnimationClip>(idle),
            });
            controller.States.Add(new AnimatorController.State
            {
                Name = "Attack",
                Motion = new AssetRef<AnimationClip>(attack),
            });

            var animator = new Animator { PlayAutomatically = false };
            root.AddComponent(animator);
            Assert.True(animator.TryActivateController(controller));
            Update(scene, frames: 1);

            Assert.True(AssetDatabase.IsLocked(idleId));
            Assert.True(AssetDatabase.IsLocked(attackId));
            AssetDatabase.ForceIdle(idleId);
            AssetDatabase.ForceIdle(attackId);
            backend.ForceIdleSweep();
            Assert.False(idle.IsDisposed);
            Assert.False(attack.IsDisposed);

            animator.CrossFade("Attack", 0.25f);
            Update(scene, frames: 2);
            Assert.True(animator.GetCurrentAnimatorStateInfo().IsName("Attack"));

            scene.Disable();
            scene.Dispose();
            Assert.False(AssetDatabase.IsLocked(idleId));
            Assert.False(AssetDatabase.IsLocked(attackId));
        }
        finally
        {
            if (!scene.IsDisposed)
            {
                if (scene.IsActive)
                    scene.Disable();
                scene.Dispose();
            }
            if (!idle.IsDisposed)
                idle.Dispose();
            if (!attack.IsDisposed)
                attack.Dispose();
            AssetDatabase.Forget(idleId);
            AssetDatabase.Forget(attackId);
            AssetDatabase.Current = previousBackend;
        }
    }
}
