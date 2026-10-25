// THROWAWAY diagnostic — verify HUD sprite sub-asset GUID derivation matches the importer.
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using Xunit;
using Xunit.Abstractions;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

public class HudSpriteGuidDiagnostics(ITestOutputHelper output)
{
    [Fact]
    public void DumpHudSpriteSubAssetGuids()
    {
        string hudDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "ZonezeroTestProject/Assets/ZZZ/Arts/UI/HUD").Replace('\\', '/');
        Assert.True(Directory.Exists(hudDir), $"missing {hudDir}");

        foreach (string metaPath in Directory.GetFiles(hudDir, "*.png.meta"))
        {
            string pngName = Path.GetFileNameWithoutExtension(metaPath); // "btn_attack"
            string metaText = File.ReadAllText(metaPath);
            // Extract guid from either meta format: guid: "..." or "guid": "..."
            int idx = metaText.IndexOf("guid", StringComparison.Ordinal);
            Assert.True(idx >= 0, $"no guid in {metaPath}");
            int colon = metaText.IndexOf(':', idx);
            int q1 = metaText.IndexOf('"', colon) + 1;
            int q2 = metaText.IndexOf('"', q1);
            Guid texGuid = Guid.Parse(metaText[q1..q2]);

            // My derivation (same as BattleHUD.SpriteGuid)
            byte[] parentBytes = texGuid.ToByteArray();
            byte[] nameBytes = Encoding.UTF8.GetBytes(pngName);
            byte[] combined = new byte[parentBytes.Length + nameBytes.Length];
            parentBytes.CopyTo(combined, 0);
            nameBytes.CopyTo(combined, parentBytes.Length);
            byte[] hash = SHA256.HashData(combined);
            Guid derived = new(hash.AsSpan(0, 16));

            // What the asset database actually has: try to load the derived sprite.
            Sprite? sprite = AssetDatabase.Get(derived) as Sprite;
            output.WriteLine($"{pngName}: texGuid={texGuid} derivedSprite={derived} " +
                             $"loaded={(sprite != null ? "YES" : "no")}");
        }
    }
}
