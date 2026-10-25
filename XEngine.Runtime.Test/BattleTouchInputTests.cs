using XEngine.Vector;
using XEngine.Zonezero.UI;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class BattleTouchInputTests
{
    [Theory]
    [InlineData(0, 1, KeyCode.W)]
    [InlineData(-1, 0, KeyCode.A)]
    [InlineData(0, -1, KeyCode.S)]
    [InlineData(1, 0, KeyCode.D)]
    public void Stick_MapsCardinalDirectionsAndReleasesOpposite(float x, float y, KeyCode expected)
    {
        BattleTouchInputBridge.ReleaseAll();
        try
        {
            BattleTouchInputBridge.SetMove(new Float2(-x, -y));
            BattleTouchInputBridge.SetMove(new Float2(x, y));
            foreach (var key in new[] { KeyCode.W, KeyCode.A, KeyCode.S, KeyCode.D })
                Assert.Equal(key == expected, InputInjector.IsHeld(key));
        }
        finally { BattleTouchInputBridge.ReleaseAll(); }
    }

    [Theory]
    [InlineData(0, KeyCode.J)]
    [InlineData(1, KeyCode.K)]
    [InlineData(2, KeyCode.L)]
    [InlineData(3, KeyCode.I)]
    public void ButtonTap_SurvivesUntilNextInputSample(int slot, KeyCode key)
    {
        BattleTouchInputBridge.ReleaseAll();
        try
        {
            BattleTouchInputBridge.TapSkill(slot);
            InputInjector.NewFrame();
            Assert.True(InputInjector.IsHeld(key));
            Assert.True(InputInjector.ConsumePressedEdge(key));
            InputInjector.NewFrame();
            Assert.False(InputInjector.IsHeld(key));
            Assert.True(InputInjector.ConsumeReleasedEdge(key));
        }
        finally { BattleTouchInputBridge.ReleaseAll(); }
    }
}
