using XEngine.Runtime.GUI;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class BackdropBlurPlannerTests
{
    [Fact]
    public void ZeroRadiusDoesNotRequestABlurPyramid()
    {
        BackdropBlurPlan plan = BackdropBlurPlanner.Create(0f);

        Assert.False(plan.IsEnabled);
        Assert.Equal(0, plan.Iterations);
    }

    [Fact]
    public void LargestRequestedRadiusDeterminesTheSharedPlan()
    {
        float largest = BackdropBlurPlanner.SelectLargestRadius([4f, 22f, 12f]);
        BackdropBlurPlan plan = BackdropBlurPlanner.Create(largest);

        Assert.Equal(22f, largest);
        Assert.True(plan.IsEnabled);
        Assert.Equal(3, plan.Iterations);
        Assert.InRange(plan.Offset, 1.3f, 1.4f);
    }

    [Fact]
    public void BlurPlanCapsThePyramidDepth()
    {
        BackdropBlurPlan plan = BackdropBlurPlanner.Create(100000f);

        Assert.Equal(5, plan.Iterations);
        Assert.InRange(plan.Offset, 0.5f, 6f);
    }
}
