using System.Text;

using Spine;

using XEngine.Spine;

using Xunit;

namespace XEngine.Runtime.Test.Spine;

public sealed class SpineMixSettingsTests : RuntimeTestBase
{
    private const string MixJson = """
        {
          "skeleton": { "hash": "mix", "spine": "4.2.37", "width": 64, "height": 64 },
          "bones": [
            { "name": "root" },
            { "name": "body", "parent": "root", "y": 20 }
          ],
          "slots": [
            { "name": "body", "bone": "body" }
          ],
          "skins": [
            { "name": "default" }
          ],
          "animations": {
            "idle": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.2, "x": 0, "y": 0 }
                  ]
                }
              }
            },
            "walk": {
              "bones": {
                "body": {
                  "translate": [
                    { "x": 0, "y": 0 },
                    { "time": 0.5, "x": 10, "y": 0 }
                  ]
                }
              }
            }
          }
        }
        """;

    [Fact]
    public void GetAnimationStateData_AppliesDefaultAndCustomMixes()
    {
        SkeletonDataAsset asset = SkeletonDataAsset.CreateRuntime(
            Encoding.UTF8.GetBytes(MixJson), atlas: null, scale: 1f);
        asset.DefaultMix = 0.15f;
        asset.CustomMixes.Add(new SpineMixEntry { From = "walk", To = "idle", Duration = 0.4f });
        // Unknown animations must be skipped, not throw (SetMix throws on unknown names).
        asset.CustomMixes.Add(new SpineMixEntry { From = "missing", To = "idle", Duration = 1f });

        AnimationStateData stateData = asset.GetAnimationStateData();
        SkeletonData skeletonData = asset.GetSkeletonData();
        global::Spine.Animation walk = skeletonData.FindAnimation("walk")!;
        global::Spine.Animation idle = skeletonData.FindAnimation("idle")!;

        Assert.Equal(0.15f, stateData.DefaultMix);
        Assert.Equal(0.4f, stateData.GetMix(walk, idle));
        // Pair without a custom entry falls back to the default mix.
        Assert.Equal(0.15f, stateData.GetMix(idle, walk));
    }

    [Fact]
    public void ClearStateCache_PicksUpEditedMixes()
    {
        SkeletonDataAsset asset = SkeletonDataAsset.CreateRuntime(
            Encoding.UTF8.GetBytes(MixJson), atlas: null, scale: 1f);
        asset.DefaultMix = 0.1f;
        Assert.Equal(0.1f, asset.GetAnimationStateData().DefaultMix);

        asset.DefaultMix = 0.3f;
        Assert.Equal(0.1f, asset.GetAnimationStateData().DefaultMix); // still cached

        asset.ClearStateCache();
        Assert.Equal(0.3f, asset.GetAnimationStateData().DefaultMix);
    }
}
