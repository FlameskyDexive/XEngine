using System.Reflection;

using XEngine.Editor.Importers;
using XEngine.Editor.GUI.Registries;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VfxImporterTests
{
    [Fact]
    public void ScriptableObjectImporter_RegistersVfxExtension()
    {
        var attribute = typeof(ScriptableObjectImporter).GetCustomAttribute<ImporterForAttribute>();
        Assert.NotNull(attribute);
        Assert.Contains(".vfx", attribute!.Extensions);
        Assert.Contains(".shadergraph", attribute.Extensions);
    }

    [Fact]
    public void AssetTypeStyles_MapsVfxExtension()
    {
        AssetTypeStyle style = AssetTypeStyles.For(".vfx");
        Assert.NotNull(style.Icon);
        Assert.NotSame(AssetTypeStyles.For(".unknown_ext_zzz").Icon, style.Icon);
    }
}
