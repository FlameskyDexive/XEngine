using System.Xml.Linq;
using XEngine.Editor.Build;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class DesktopMacBundleMetadataTests
{
    private static Dictionary<string, string> Read(string product, string company = "DefaultCompany")
    {
        var document = XDocument.Parse(DesktopBuildPipeline.CreateMacInfoPlist(product, product, company, "0.1.0"));
        var result = new Dictionary<string, string>();
        foreach (var key in document.Descendants("key"))
            if (key.NextNode is XElement value && value.Name == "string")
                result[key.Value] = value.Value;
        return result;
    }

    [Fact]
    public void AsciiNames_KeepExistingIdentifier()
    {
        Assert.Equal("com.DefaultCompany.MyGame", Read("My Game")["CFBundleIdentifier"]);
    }

    [Theory]
    [InlineData("最小 音频工程")]
    [InlineData("Game 中文")]
    [InlineData("Jeu café 🎮")]
    public void UnicodeNames_KeepDisplayAndExecutableButProduceStableAsciiId(string name)
    {
        var values = Read(name, "公司");
        Assert.Equal(name, values["CFBundleDisplayName"]);
        Assert.Equal(name, values["CFBundleExecutable"]);
        string id = values["CFBundleIdentifier"];
        Assert.Equal(id, Read(name, "公司")["CFBundleIdentifier"]);
        Assert.All(id, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.'));
        Assert.NotEqual(id, Read(name + "甲", "公司")["CFBundleIdentifier"]);
    }

    [Fact]
    public void XmlCharacters_AreEscapedAndRoundTrip()
    {
        string name = "A & B <测试> \"game\"";
        var values = Read(name);
        Assert.Equal(name, values["CFBundleName"]);
        Assert.Equal(name, values["CFBundleExecutable"]);
    }
}
