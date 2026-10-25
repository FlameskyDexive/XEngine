using XEngine.Editor.Build;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class StandardDirectionalVariantCollectorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsBothLightingPathsIncludingShadowPassForRuntimeSwitches(bool collectedDirectionalOnly)
    {
        Shader shader = Shader.LoadDefault(DefaultShader.Standard);
        var collection = VulkanShaderVariantCollector.Collect(
        [
            new VulkanShaderVariantRoot(BuiltInAssets.GuidFor(DefaultShader.Standard), shader,
            [
                new VulkanShaderVariantUsage("runtime-mesh", new Dictionary<string, bool>
                {
                    ["HAS_TANGENTS"] = true, ["DIRECTIONAL_ONLY"] = collectedDirectionalOnly,
                }),
            ]),
        ]);
        foreach (string pass in new[] { "Standard", "StandardShadow" })
        {
            Assert.Contains(collection.Descriptors, x => x.PassStableId == pass && x.CanonicalKeywordSet == "HAS_TANGENTS;");
            Assert.Contains(collection.Descriptors, x => x.PassStableId == pass && x.CanonicalKeywordSet == "DIRECTIONAL_ONLY;HAS_TANGENTS;");
        }
    }
}
