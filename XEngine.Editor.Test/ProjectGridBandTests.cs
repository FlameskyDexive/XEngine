using System.Collections.Generic;
using XEngine.Editor.GUI.Panels;
using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Pure math behind the Project panel's banded (virtualized) thumbnail grid: the row
/// table measured from one full flow layout must give back a band that covers the viewport with
/// one padding row each side, spacers that reproduce the full extent exactly for rows of unequal
/// height, and a verification that drops the table when a band frame disagrees with it.</summary>
public sealed class ProjectGridBandTests
{
    // 8 rows of unequal height (one-line and two-line labels mixed) with a 6px gap between lines:
    // tops 0,90,208,298,416,506,596,714; the last row is 112 tall and has no trailing gap (total 826).
    private static readonly float[] MixedRows = { 0f, 90f, 208f, 298f, 416f, 506f, 596f, 714f, 826f };
    private const float Gap = 6f;
    private static readonly ProjectGridRowTable.Key KeyA = new(1, 72f, 500f, 1.5f, false);

    private static ProjectGridRowTable Table(float[] rows, int columns = 5)
    {
        var table = new ProjectGridRowTable();
        table.Set(KeyA, columns, rows, Gap);
        return table;
    }

    [Theory]
    [InlineData(0f, 0)]      // above the grid clamps to the first row
    [InlineData(89.9f, 0)]
    [InlineData(90f, 1)]
    [InlineData(300f, 3)]
    [InlineData(825f, 7)]
    [InlineData(5000f, 7)]   // below the grid clamps to the last row
    public void RowAtFindsTheRowContainingY(float y, int expected)
        => Assert.Equal(expected, ProjectGridRowTable.RowAt(MixedRows, y));

    [Fact]
    public void BandCoversViewportPlusOnePaddingRowEachSide()
    {
        // Viewport 200 tall scrolled to 9 + 300 shows 300..500: rows 3 (298..416) and 4 (416..506)
        // intersect; one padding row each side gives 2..5.
        var (first, last) = Table(MixedRows).Band(309f, 200f, ProjectPanel.GridContentOffset);
        Assert.Equal(2, first);
        Assert.Equal(5, last);
        // 10px more viewport reaches row 5 (top 506) and the band grows by one row.
        Assert.Equal(6, Table(MixedRows).Band(309f, 210f, ProjectPanel.GridContentOffset).last);
    }

    [Fact]
    public void BandClampsAtBothEnds()
    {
        var table = Table(MixedRows);
        var top = table.Band(0f, 200f, ProjectPanel.GridContentOffset);
        Assert.Equal(0, top.first);
        var bottom = table.Band(10_000f, 200f, ProjectPanel.GridContentOffset);
        Assert.Equal(7, bottom.last);
        Assert.True(bottom.first <= 7);
    }

    [Fact]
    public void EmptyTableHasNoBand()
    {
        var table = new ProjectGridRowTable();
        Assert.False(table.IsValidFor(KeyA));
        var (first, last) = ProjectGridRowTable.Band(new List<float> { 0f }, 0f, 200f, 9f);
        Assert.True(last < first);
    }

    [Fact]
    public void BandHeightDropsTheTrailingGapUnlessTheBandEndsTheGrid()
    {
        var table = Table(MixedRows);
        // Rows 2..4: line heights (208..298 = 90-6=84, 298..416 = 118-6=112, 416..506 = 90-6=84) + two gaps = 292.
        Assert.Equal(292f, table.BandHeight(2, 4), 3);
        // Rows 6..7 end the grid: 596..714 minus one gap (112) + last row 112 = 224 + one inner gap = 230.
        Assert.Equal(230f, table.BandHeight(6, 7), 3);
        Assert.Equal(826f, table.BandHeight(0, 7), 3);
    }

    [Fact]
    public void SpacersPlusBandReproduceTheFullExtentForUnequalRows()
    {
        var table = Table(MixedRows);
        for (float scroll = 0; scroll < 900f; scroll += 37f)
        {
            var (first, last) = table.Band(scroll, 200f, ProjectPanel.GridContentOffset);
            var (top, bottom) = table.Spacers(first, last);
            Assert.Equal(826f, top + table.BandHeight(first, last) + bottom, 3);
            Assert.Equal(MixedRows[first], top, 3);
            // The row after the band starts exactly where the full layout puts it.
            if (last + 1 < table.Rows)
                Assert.Equal(MixedRows[last + 1], top + table.BandHeight(first, last) + Gap, 3);
        }
    }

    [Fact]
    public void VerifyAcceptsRowsMatchingTheTable()
    {
        var table = Table(MixedRows);
        // Band rows 2..4 built: tops relative to row 2 are 0, 90, 208; wrap height 292 (no trailing gap).
        Assert.True(table.Verify(KeyA, 5, 2, new[] { 0f, 90f, 208f }, 292f));
        Assert.True(table.IsValidFor(KeyA));
        Assert.Equal(0, table.Mismatches);
        // The band that ends the grid keeps the table's full distance.
        Assert.True(table.Verify(KeyA, 5, 6, new[] { 0f, 118f }, 230f));
    }

    [Fact]
    public void VerifyDropsTheTableOnAnyDisagreementAndDisablesAfterRepeats()
    {
        var table = Table(MixedRows);
        Assert.False(table.Verify(KeyA, 5, 2, new[] { 0f, 90f, 200f }, 292f)); // second row 8px off
        Assert.False(table.IsValidFor(KeyA));
        Assert.False(table.IsDisabled(KeyA));
        table.Set(KeyA, 5, MixedRows, Gap);
        Assert.False(table.Verify(KeyA, 4, 2, new[] { 0f, 90f, 208f }, 292f)); // column count changed
        table.Set(KeyA, 5, MixedRows, Gap);
        Assert.False(table.Verify(KeyA, 5, 2, new[] { 0f, 90f, 208f }, 298f)); // band height keeps a trailing gap
        Assert.True(table.IsDisabled(KeyA));
        Assert.Contains("band height", table.LastMismatch);
        // Another key is unaffected, and a fresh measurement for it clears the mismatch run.
        var keyB = KeyA with { CellSize = 104f };
        Assert.False(table.IsDisabled(keyB));
        table.Set(keyB, 3, new[] { 0f, 120f, 240f });
        Assert.Equal(0, table.Mismatches);
        Assert.True(table.IsValidFor(keyB));
    }

    [Fact]
    public void KeyChangesInvalidateTheTable()
    {
        var table = Table(MixedRows);
        Assert.True(table.IsValidFor(KeyA));
        Assert.False(table.IsValidFor(KeyA with { Width = 501f }));
        Assert.False(table.IsValidFor(KeyA with { ShowExtensions = true }));
        Assert.False(table.IsValidFor(KeyA with { EntriesStamp = 2 }));
        Assert.False(table.IsValidFor(KeyA with { Scale = 1f }));
    }
}
