using Xunit;

using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

public sealed class HudStackingTests
{
    private sealed class FakeWidget : HudWidget
    {
        public override void Fill(System.Collections.Generic.List<HudMeshData?> targets, in HudFillContext context) { }
    }

    private static HudWidget Slot(float height, float gap)
        => new FakeWidget { Size = new Float2(10f, height), ItemLineGap = gap, IsValid = true };

    [Fact]
    public void StackHeight_AccumulatesLowerValidSlots()
    {
        HudWidget?[] slots =
        {
            Slot(30f, 12f), // Blood
            Slot(22f, 12f), // Name
            null,           // Title (hidden)
            Slot(22f, 12f), // GuildName
            null,           // GuildIcon
        };

        Assert.Equal(0f, HudStacking.StackHeight(slots, 0));
        Assert.Equal(42f, HudStacking.StackHeight(slots, 1));      // blood 30 + gap 12
        Assert.Equal(76f, HudStacking.StackHeight(slots, 3));      // 42 + name 22 + gap 12
        Assert.Equal(110f, HudStacking.TotalHeight(slots));        // 76 + guild 22 + gap 12
    }

    [Fact]
    public void StackHeight_IgnoresInvalidSlots()
    {
        HudWidget?[] slots =
        {
            Slot(30f, 12f),
            Slot(22f, 12f),
        };
        slots[0].IsValid = false;

        Assert.Equal(0f, HudStacking.StackHeight(slots, 1));
    }

    [Fact]
    public void Reflow_MovesUpperSlotsWhenMiddleSlotHides()
    {
        HudObject obj = new();
        obj.InitHeadless(Float2.Zero);

        obj.SetSlicedSprite(HudSlot.Blood, new HudSpriteInfo { Name = "bg" }, new Float2(180f, 30f), HudAlignment.Middle);
        obj.SetSprite(HudSlot.Name, new HudSpriteInfo { Name = "name", Width = 100f, Height = 22f }, HudAlignment.Middle);
        obj.SetSprite(HudSlot.Title, new HudSpriteInfo { Name = "title", Width = 100f, Height = 22f }, HudAlignment.Middle);

        HudSpriteWidget? blood = obj.GetSpriteWidget(HudSlot.Blood);
        HudSpriteWidget? name = obj.GetSpriteWidget(HudSlot.Name);
        HudSpriteWidget? title = obj.GetSpriteWidget(HudSlot.Title);
        Assert.NotNull(blood);
        Assert.NotNull(name);
        Assert.NotNull(title);

        // Name sits above blood: 30 + 12.
        Assert.Equal(42f, name.Offset.Y, 5);
        // Title sits above name: 42 + 22 + 12.
        Assert.Equal(76f, title.Offset.Y, 5);

        // Hide the name: blood stays, title drops onto the blood.
        obj.SetSlotVisible(HudSlot.Name, false);
        Assert.Equal(0f, blood.Offset.Y, 5);
        Assert.Equal(42f, title.Offset.Y, 5);
        Assert.False(obj.IsSlotVisible(HudSlot.Name));

        // Re-show: everything back.
        obj.SetSlotVisible(HudSlot.Name, true);
        Assert.Equal(42f, name.Offset.Y, 5);
        Assert.Equal(76f, title.Offset.Y, 5);
    }
}
