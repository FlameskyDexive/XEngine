using XEngine.Editor.UI;

namespace XEngine.Editor.UI.Tests;

public sealed class StretchOracleTests
{
    [Fact]
    public void WeightedConstraintsMatchIndependentBisectionOracle()
    {
        var random = new Random(874133);
        for (int trial = 0; trial < 1000; trial++)
        {
            using var store = new UiNodeStore();
            int count = random.Next(1, 25);
            float available = random.Next(0, 4000);
            var root = store.Create();
            store.SetRootBounds(root, new(0, 0, 500, available));
            var nodes = new UiNodeHandle[count];
            var minimum = new double[count]; var maximum = new double[count]; var weights = new double[count];
            double sumMin = 0, sumMax = 0, high = 0;
            for (int i = 0; i < count; i++)
            {
                nodes[i] = store.Create(root);
                minimum[i] = random.Next(0, 200);
                maximum[i] = minimum[i] + random.Next(0, 400);
                weights[i] = random.Next(1, 100);
                sumMin += minimum[i]; sumMax += maximum[i];
                high = Math.Max(high, maximum[i] / weights[i]);
                store.SetStyle(nodes[i], new() { Height = UiLength.Stretch((float)weights[i]),
                    MinHeight = (float)minimum[i], MaxHeight = (float)maximum[i] });
            }
            // Independent oracle solves sum(clamp(lambda * weight, min, max)) = available.
            // It shares no active-set/freeze logic with the implementation.
            double target = Math.Clamp(available, sumMin, sumMax), low = 0;
            for (int iteration = 0; iteration < 100; iteration++)
            {
                double middle = (low + high) * .5, total = 0;
                for (int i = 0; i < count; i++) total += Math.Clamp(middle * weights[i], minimum[i], maximum[i]);
                if (total < target) low = middle; else high = middle;
            }
            store.Layout();
            double cursor = 0;
            for (int i = 0; i < count; i++)
            {
                double expected = Math.Clamp(high * weights[i], minimum[i], maximum[i]);
                Assert.InRange(Math.Abs(store.GetBounds(nodes[i]).Height - expected), 0, .001);
                Assert.InRange(Math.Abs(store.GetBounds(nodes[i]).Y - cursor), 0, .01);
                cursor += expected;
            }
            Assert.InRange(Math.Abs(cursor - target), 0, .001);
        }
    }

    [Fact]
    public void MaximumAcceptedGeometryStaysFiniteAcrossDeepAccumulation()
    {
        using var store = new UiNodeStore(2000, 2000);
        var nodes = new UiNodeHandle[2000];
        nodes[0] = store.Create();
        float maximum = UiNodeStore.MaximumLogicalInput;
        store.SetRootBounds(nodes[0], new(maximum, maximum, maximum, maximum));
        for (int i = 1; i < nodes.Length; i++)
        {
            nodes[i] = store.Create(nodes[i - 1]);
            store.SetStyle(nodes[i], new() { Padding = maximum, Gap = maximum,
                Position = UiPosition.Absolute, Left = maximum, Top = maximum,
                Width = UiLength.Fixed(maximum), Height = UiLength.Fixed(maximum) });
        }
        store.Layout();
        for (int i = 0; i < nodes.Length; i++)
        {
            var bounds = store.GetBounds(nodes[i]); var clip = store.GetClip(nodes[i]); var size = store.GetDesiredSize(nodes[i]);
            Assert.True(float.IsFinite(bounds.X) && float.IsFinite(bounds.Y) && float.IsFinite(bounds.Width) && float.IsFinite(bounds.Height));
            Assert.True(float.IsFinite(clip.X) && float.IsFinite(clip.Y) && float.IsFinite(clip.Width) && float.IsFinite(clip.Height));
            Assert.True(float.IsFinite(size.Width) && float.IsFinite(size.Height));
        }
    }
}
