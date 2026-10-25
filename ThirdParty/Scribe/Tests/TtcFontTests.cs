// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Scribe;

namespace Tests;

/// <summary>
/// TrueType Collection support: Windows ships its CJK fonts (msyh.ttc / simsun.ttc) as 'ttcf'
/// containers, and without face-offset resolution every constructor failed outright — the direct
/// cause of the editor rendering Chinese as tofu. The offset helper is a pure function; the
/// container smoke test only runs where the fixture exists.
/// </summary>
public class TtcFontTests
{
    [Fact]
    public void GetFontOffsetForIndex_BareFont_FaceZeroOnly()
    {
        byte[] bare = new byte[16]; // content irrelevant: no 'ttcf' magic means a bare font
        Assert.Equal(0, FontFile.GetFontOffsetForIndex(bare, 0));
        Assert.Equal(-1, FontFile.GetFontOffsetForIndex(bare, 1));
        Assert.Equal(-1, FontFile.GetFontOffsetForIndex(null!, 0));
    }

    [Fact]
    public void GetFontOffsetForIndex_Collection_ResolvesFaceOffsets()
    {
        byte[] ttc = new byte[24];
        ttc[0] = (byte)'t';
        ttc[1] = (byte)'t';
        ttc[2] = (byte)'c';
        ttc[3] = (byte)'f';
        WriteU32(ttc, 8, 2);   // numFonts
        WriteU32(ttc, 12, 12); // face 0 offset
        WriteU32(ttc, 16, 340); // face 1 offset
        Assert.Equal(12, FontFile.GetFontOffsetForIndex(ttc, 0));
        Assert.Equal(340, FontFile.GetFontOffsetForIndex(ttc, 1));
        Assert.Equal(-1, FontFile.GetFontOffsetForIndex(ttc, 2));
        Assert.Equal(-1, FontFile.GetFontOffsetForIndex(ttc, -1));
    }

    [Fact]
    public void TtcContainer_FirstFace_LoadsAndCoversChinese()
    {
        string fontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        string ttc = Path.Combine(fontsDir, "msyh.ttc"); // Microsoft YaHei
        if (!OperatingSystem.IsWindows() || !File.Exists(ttc))
            return; // fixture only exists on Windows machines with the YaHei collection

        var font = new FontFile(ttc);
        Assert.False(string.IsNullOrEmpty(font.FamilyName));
        Assert.NotEqual(0, font.FindGlyphIndex(0x4E2D)); // 中
        Assert.NotEqual(0, font.FindGlyphIndex(0x56FD)); // 国
    }

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }
}
