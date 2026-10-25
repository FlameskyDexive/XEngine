using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Regression: --hub-info / --hub-command are machine-readable CLI probes and must never be
/// redirected into the §6 project-less handoff — with XHub installed, the handoff launches
/// the hub GUI and exits before the handshake JSON prints, so XHub's capability probe
/// times out and hub-installed editors fail to register.
/// </summary>
public class HubInfoCliTests
{
    [Fact]
    public void HubInfo_NeverHandsOffToHub()
    {
        Program.ReadArguments(["--hub-info"]);
        Assert.False(Program.ShouldHandOffToHub());
    }

    [Fact]
    public void HubCommand_NeverHandsOffToHub()
    {
        Program.ReadArguments(["--hub-command"]);
        Assert.False(Program.ShouldHandOffToHub());
    }
}
