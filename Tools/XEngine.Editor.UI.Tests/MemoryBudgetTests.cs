using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class MemoryBudgetTests
{
    [Fact]
    public void PeakIncludesOverlappingLeasesAndSurvivesReleaseAndRejection()
    {
        var budget = new UiMemoryBudget(100);
        using var old = budget.Reserve(30);
        using (budget.Reserve(50)) Assert.Equal(80, budget.PeakReservedBytes);
        Assert.Equal(30, budget.ReservedBytes);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(71));
        Assert.Equal(80, budget.PeakReservedBytes);
        old.Dispose();
        Assert.Equal(0, budget.ReservedBytes);
        Assert.Equal(80, budget.PeakReservedBytes);
    }
    [Fact]
    public void RefusedReservationDoesNotConsumeBudgetAndDisposalReturnsItOnce()
    {
        var budget = new UiMemoryBudget(100);
        using var first = budget.Reserve(60);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(41));
        Assert.Equal(60, budget.ReservedBytes);
        var second = budget.Reserve(40); Assert.Equal(0, budget.AvailableBytes);
        second.Dispose(); second.Dispose(); Assert.Equal(40, budget.AvailableBytes);
        first.Dispose(); Assert.Equal(0, budget.ReservedBytes);
    }

    [Fact]
    public void AdmissionCannotOverflowAndNegativeChargesAreRejected()
    {
        var budget = new UiMemoryBudget(long.MaxValue);
        using var lease = budget.Reserve(long.MaxValue);
        Assert.Throws<InvalidOperationException>(() => budget.Reserve(1));
        Assert.Equal(long.MaxValue, budget.ReservedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => budget.Reserve(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new UiMemoryBudget(-1));
    }
}
