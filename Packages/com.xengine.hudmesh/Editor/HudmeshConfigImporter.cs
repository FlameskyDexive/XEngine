using System;
using System.IO;

using XEngine.Editor.Importers;
using XEngine.Echo;
using XEngine.Runtime;

namespace XEngine.Hudmesh.Editor;

/// <summary>Claims <c>.hudconfig</c> files and imports them as <see cref="HudmeshConfig"/> assets.</summary>
[ImporterFor(".hudconfig", Priority = 100)]
public sealed class HudmeshConfigImporter : AssetImporter
{
    public override int Version => 1;

    public override bool CanImport(string absolutePath)
        => Path.GetExtension(absolutePath).Equals(".hudconfig", StringComparison.OrdinalIgnoreCase);

    public override bool Import(ImportContext ctx)
    {
        if (!CanImport(ctx.AbsolutePath))
            return false;

        HudmeshConfig config;
        try
        {
            EchoObject echo = EchoObject.ReadFromString(File.ReadAllText(ctx.AbsolutePath));
            config = Serializer.Deserialize<HudmeshConfig>(echo)
                ?? throw new InvalidOperationException("deserialized to null");
        }
        catch (Exception)
        {
            return false;
        }

        config.Name = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);
        ctx.SetMainAsset(config);
        return true;
    }
}
