using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public sealed class CommandBufferSubmissionStateTests
{
    [Fact]
    public void CaptureSubmissionStateKeepsGlobalUniformsImmutablePerSubmission()
    {
        Float4x4 first = Float4x4.CreateTranslation(new Float3(1f, 2f, 3f));
        Float4x4 second = Float4x4.CreateTranslation(new Float3(4f, 5f, 6f));

        GlobalUniforms.Clear();
        try
        {
            GlobalUniforms.SetMatrixVP(first);
            using CommandBuffer firstSubmission = Graphics.GetCommandBuffer("first-global-snapshot");
            firstSubmission.CaptureSubmissionState();

            GlobalUniforms.SetMatrixVP(second);
            using CommandBuffer secondSubmission = Graphics.GetCommandBuffer("second-global-snapshot");
            secondSubmission.CaptureSubmissionState();

            Assert.True(firstSubmission._hasGlobalUniformsSnapshot);
            Assert.True(secondSubmission._hasGlobalUniformsSnapshot);
            Assert.Equal(first, firstSubmission._globalUniformsSnapshot.xengine_MatVP);
            Assert.Equal(second, secondSubmission._globalUniformsSnapshot.xengine_MatVP);
        }
        finally
        {
            GlobalUniforms.Clear();
        }
    }
}
