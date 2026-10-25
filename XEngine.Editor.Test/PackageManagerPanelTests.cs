// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO.Compression;
using System.Text;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects.Packages;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public class PackageManagerPanelTests : EditorTestHarness
{
    [Theory]
    [InlineData(520f, true)]
    [InlineData(900f, false)]
    [InlineData(1400f, false)]
    public void ComputeLayout_FitsColumnsWithoutOverlap(float width, bool compact)
    {
        PackageManagerPanel.PackageManagerLayout layout = PackageManagerPanel.ComputeLayout(width, 720f);
        Assert.Equal(compact, layout.Compact);
        Assert.True(layout.ListWidth >= 190f);
        Assert.True(layout.DetailWidth >= 220f);
        if (compact)
        {
            Assert.Equal(0f, layout.RailWidth);
            Assert.Equal(width, layout.ListWidth + layout.DetailWidth + 1f, 2);
        }
        else
        {
            Assert.InRange(layout.RailWidth, 150f, 190f);
            Assert.Equal(width, layout.RailWidth + layout.ListWidth + layout.DetailWidth + 2f, 2);
        }
    }

    [Fact]
    public void OnGUI_HeadlessPaper_DoesNotThrow()
    {
        var paper = new Paper(new NullRenderer(), 900, 720, new FontAtlasSettings());
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        new PackageManagerPanel().OnGUI(paper, 900, 720);
        Origami.EndFrame(paper);
        paper.EndFrame();
    }

    [Fact]
    public void LayoutEvidence_WritesThreeWidthScreenshots()
    {
        int[] widths = [520, 900, 1400];
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "2d");
        Directory.CreateDirectory(evidenceDir);

        foreach (int width in widths)
        {
            PackageManagerPanel.PackageManagerLayout layout = PackageManagerPanel.ComputeLayout(width, 720f);
            byte[] rgba = RenderLayout(width, 220, layout);
            string path = Path.Combine(evidenceDir, $"p0-package-manager-{width}.png");
            File.WriteAllBytes(path, EncodePng(rgba, width, 220));
            Assert.True(new FileInfo(path).Length > 256);
        }
    }

    [Fact]
    public void BuiltInTwoDCorePackages_AreDiscoverableFromRepoPackages()
    {
        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            string repo = FindRepositoryRoot();
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();
            IReadOnlyList<ResolvedPackage> resolved = PackageManager.Resolve(Project);
            Assert.Contains(resolved, package => package.Name == "com.xengine.2d.common");
            Assert.Contains(resolved, package => package.Name == "com.xengine.2d.sprite");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.feature.2d");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.2d.animation");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.2d.psdimporter");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.2d.tilemap");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.vfx");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.terrain");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.spine");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.ai.navigation");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.postprocessing");
            Assert.DoesNotContain(resolved, package => package.Name == "com.xengine.genshin");
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void OptionalPackageStates_WriteInstallEvidenceScreenshot()
    {
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx");
        Directory.CreateDirectory(evidenceDir);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();

            PackageCatalogSnapshot before = PackageCatalog.Build(Project);
            PackageCatalogEntry available = Assert.Single(before.Entries, e => e.Name == "com.xengine.vfx");
            Assert.Equal(PackageInstallState.Available, available.State);

            byte[] availableRgba = RenderOptionalStates(960, 220, installed: false);
            string availablePath = Path.Combine(evidenceDir, "m0-optional-package-available.png");
            File.WriteAllBytes(availablePath, EncodePng(availableRgba, 960, 220));
            Assert.True(new FileInfo(availablePath).Length > 256);

            Assert.True(PackageOperations.Install(Project, "com.xengine.vfx", "0.1.0").Success);
            PackageCatalogSnapshot after = PackageCatalog.Build(Project);
            PackageCatalogEntry installed = Assert.Single(after.Entries, e => e.Name == "com.xengine.vfx");
            Assert.Equal(PackageInstallState.Installed, installed.State);

            byte[] installedRgba = RenderOptionalStates(960, 220, installed: true);
            string installedPath = Path.Combine(evidenceDir, "m0-optional-package-installed.png");
            File.WriteAllBytes(installedPath, EncodePng(installedRgba, 960, 220));
            Assert.True(new FileInfo(installedPath).Length > 256);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void OptionalTerrainPackageStates_WriteInstallEvidenceScreenshot()
    {
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidenceDir);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();

            PackageCatalogSnapshot before = PackageCatalog.Build(Project);
            PackageCatalogEntry available = Assert.Single(before.Entries, e => e.Name == "com.xengine.terrain");
            Assert.Equal(PackageInstallState.Available, available.State);

            byte[] availableRgba = RenderOptionalStates(960, 220, installed: false);
            string availablePath = Path.Combine(evidenceDir, "m0-optional-package-available.png");
            File.WriteAllBytes(availablePath, EncodePng(availableRgba, 960, 220));
            Assert.True(new FileInfo(availablePath).Length > 256);

            Assert.True(PackageOperations.Install(Project, "com.xengine.terrain", "0.1.0").Success);
            PackageCatalogSnapshot after = PackageCatalog.Build(Project);
            PackageCatalogEntry installed = Assert.Single(after.Entries, e => e.Name == "com.xengine.terrain");
            Assert.Equal(PackageInstallState.Installed, installed.State);

            byte[] installedRgba = RenderOptionalStates(960, 220, installed: true);
            string installedPath = Path.Combine(evidenceDir, "m0-optional-package-installed.png");
            File.WriteAllBytes(installedPath, EncodePng(installedRgba, 960, 220));
            Assert.True(new FileInfo(installedPath).Length > 256);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void OptionalSpinePackageStates_WriteInstallEvidenceScreenshot()
    {
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "spine");
        Directory.CreateDirectory(evidenceDir);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();

            PackageCatalogSnapshot before = PackageCatalog.Build(Project);
            PackageCatalogEntry available = Assert.Single(before.Entries, e => e.Name == "com.xengine.spine");
            Assert.Equal(PackageInstallState.Available, available.State);

            byte[] availableRgba = RenderOptionalStates(960, 220, installed: false);
            string availablePath = Path.Combine(evidenceDir, "m0-optional-package-available.png");
            File.WriteAllBytes(availablePath, EncodePng(availableRgba, 960, 220));
            Assert.True(new FileInfo(availablePath).Length > 256);

            // Pin whatever version the repo ships so this evidence test survives version bumps.
            PackageJson repoSpine = PackageJson.Read(Path.Combine(repo, "Packages", "com.xengine.spine", "package.json"))!;
            Assert.True(PackageOperations.Install(Project, "com.xengine.spine", repoSpine.Version).Success);
            PackageCatalogSnapshot after = PackageCatalog.Build(Project);
            PackageCatalogEntry installed = Assert.Single(after.Entries, e => e.Name == "com.xengine.spine");
            Assert.Equal(PackageInstallState.Installed, installed.State);

            byte[] installedRgba = RenderOptionalStates(960, 220, installed: true);
            string installedPath = Path.Combine(evidenceDir, "m0-optional-package-installed.png");
            File.WriteAllBytes(installedPath, EncodePng(installedRgba, 960, 220));
            Assert.True(new FileInfo(installedPath).Length > 256);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void OptionalGenshinPackageStates_WriteInstallEvidenceScreenshot()
    {
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "genshin");
        Directory.CreateDirectory(evidenceDir);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();

            PackageCatalogSnapshot before = PackageCatalog.Build(Project);
            PackageCatalogEntry available = Assert.Single(before.Entries, e => e.Name == "com.xengine.genshin");
            Assert.Equal(PackageInstallState.Available, available.State);

            byte[] availableRgba = RenderOptionalStates(960, 220, installed: false);
            string availablePath = Path.Combine(evidenceDir, "m0-optional-package-available.png");
            File.WriteAllBytes(availablePath, EncodePng(availableRgba, 960, 220));
            Assert.True(new FileInfo(availablePath).Length > 256);

            Assert.True(PackageOperations.Install(Project, "com.xengine.genshin", "0.1.0").Success);
            PackageCatalogSnapshot after = PackageCatalog.Build(Project);
            PackageCatalogEntry installed = Assert.Single(after.Entries, e => e.Name == "com.xengine.genshin");
            Assert.Equal(PackageInstallState.Installed, installed.State);

            byte[] installedRgba = RenderOptionalStates(960, 220, installed: true);
            string installedPath = Path.Combine(evidenceDir, "m0-optional-package-installed.png");
            File.WriteAllBytes(installedPath, EncodePng(installedRgba, 960, 220));
            Assert.True(new FileInfo(installedPath).Length > 256);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void OptionalNavigationPackageStates_WriteInstallEvidenceScreenshot()
    {
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "navigation");
        Directory.CreateDirectory(evidenceDir);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();

            PackageCatalogSnapshot before = PackageCatalog.Build(Project);
            PackageCatalogEntry available = Assert.Single(before.Entries, e => e.Name == "com.xengine.ai.navigation");
            Assert.Equal(PackageInstallState.Available, available.State);

            byte[] availableRgba = RenderOptionalStates(960, 220, installed: false);
            string availablePath = Path.Combine(evidenceDir, "m0-optional-package-available.png");
            File.WriteAllBytes(availablePath, EncodePng(availableRgba, 960, 220));
            Assert.True(new FileInfo(availablePath).Length > 256);

            Assert.True(PackageOperations.Install(Project, "com.xengine.ai.navigation", "0.1.0").Success);
            PackageCatalogSnapshot after = PackageCatalog.Build(Project);
            PackageCatalogEntry installed = Assert.Single(after.Entries, e => e.Name == "com.xengine.ai.navigation");
            Assert.Equal(PackageInstallState.Installed, installed.State);

            byte[] installedRgba = RenderOptionalStates(960, 220, installed: true);
            string installedPath = Path.Combine(evidenceDir, "m0-optional-package-installed.png");
            File.WriteAllBytes(installedPath, EncodePng(installedRgba, 960, 220));
            Assert.True(new FileInfo(installedPath).Length > 256);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    [Fact]
    public void OptionalPostProcessingPackageStates_WriteInstallEvidenceScreenshot()
    {
        string repo = FindRepositoryRoot();
        string evidenceDir = Path.Combine(repo, "docs", "superpowers", "evidence", "postprocessing");
        Directory.CreateDirectory(evidenceDir);

        string? previous = PackageManager.BuiltInPackagesPathOverride;
        try
        {
            PackageManager.BuiltInPackagesPathOverride = Path.Combine(repo, "Packages");
            PackageManager.Invalidate();

            PackageCatalogSnapshot before = PackageCatalog.Build(Project);
            PackageCatalogEntry available = Assert.Single(before.Entries, e => e.Name == "com.xengine.postprocessing");
            Assert.Equal(PackageInstallState.Available, available.State);

            byte[] availableRgba = RenderOptionalStates(960, 220, installed: false);
            string availablePath = Path.Combine(evidenceDir, "m0-optional-package-available.png");
            File.WriteAllBytes(availablePath, EncodePng(availableRgba, 960, 220));
            Assert.True(new FileInfo(availablePath).Length > 256);

            Assert.True(PackageOperations.Install(Project, "com.xengine.postprocessing", "0.1.0").Success);
            PackageCatalogSnapshot after = PackageCatalog.Build(Project);
            PackageCatalogEntry installed = Assert.Single(after.Entries, e => e.Name == "com.xengine.postprocessing");
            Assert.Equal(PackageInstallState.Installed, installed.State);

            byte[] installedRgba = RenderOptionalStates(960, 220, installed: true);
            string installedPath = Path.Combine(evidenceDir, "m0-optional-package-installed.png");
            File.WriteAllBytes(installedPath, EncodePng(installedRgba, 960, 220));
            Assert.True(new FileInfo(installedPath).Length > 256);
        }
        finally
        {
            PackageManager.BuiltInPackagesPathOverride = previous;
            PackageManager.Invalidate();
        }
    }

    private static byte[] RenderOptionalStates(int width, int height, bool installed)
    {
        PackageManagerPanel.PackageManagerLayout layout = PackageManagerPanel.ComputeLayout(width, 720f);
        byte[] rgba = RenderLayout(width, height, layout);
        int listX = layout.Compact ? 0 : (int)layout.RailWidth + 1;
        int rowY = 52;
        Fill(rgba, width, listX + 8, rowY, (int)layout.ListWidth - 16, 28,
            installed ? (byte)46 : (byte)86,
            installed ? (byte)92 : (byte)74,
            installed ? (byte)64 : (byte)48);
        int detailX = listX + (int)layout.ListWidth + 1;
        Fill(rgba, width, detailX + 12, 70, 88, 24,
            installed ? (byte)120 : (byte)70,
            installed ? (byte)70 : (byte)110,
            installed ? (byte)70 : (byte)80);
        return rgba;
    }

    internal static byte[] RenderLayout(int width, int height, PackageManagerPanel.PackageManagerLayout layout)
    {
        byte[] rgba = new byte[width * height * 4];
        Fill(rgba, width, 0, 0, width, height, 28, 30, 36);
        Fill(rgba, width, 0, 0, width, 36, 38, 40, 48);
        Fill(rgba, width, 0, height - 22, width, 22, 32, 34, 40);

        int x = 0;
        if (!layout.Compact)
        {
            Fill(rgba, width, x, 36, (int)layout.RailWidth, height - 58, 42, 48, 62);
            x += (int)layout.RailWidth + 1;
        }
        Fill(rgba, width, x, 36, (int)layout.ListWidth, height - 58, 52, 58, 72);
        x += (int)layout.ListWidth + 1;
        Fill(rgba, width, x, 36, Math.Max(1, width - x), height - 58, 64, 78, 96);
        return rgba;
    }

    internal static void Fill(byte[] rgba, int stride, int x, int y, int w, int h, byte r, byte g, byte b)
    {
        int x1 = Math.Min(stride, x + w);
        int y1 = Math.Min(rgba.Length / (stride * 4), y + h);
        for (int py = Math.Max(0, y); py < y1; py++)
        {
            for (int px = Math.Max(0, x); px < x1; px++)
            {
                int i = (py * stride + px) * 4;
                rgba[i] = r;
                rgba[i + 1] = g;
                rgba[i + 2] = b;
                rgba[i + 3] = 255;
            }
        }
    }

    internal static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var memory = new MemoryStream();
        memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        byte[] ihdr = new byte[13];
        WriteInt(ihdr, 0, width);
        WriteInt(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        WriteChunk(memory, "IHDR", ihdr);
        byte[] raw = new byte[(width * 4 + 1) * height];
        int srcRow = width * 4;
        for (int y = 0; y < height; y++)
        {
            raw[y * (srcRow + 1)] = 0;
            Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
        }
        using var deflate = new MemoryStream();
        deflate.WriteByte(0x78);
        deflate.WriteByte(0x9C);
        using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(raw, 0, raw.Length);
        byte[] adler = new byte[4];
        WriteInt(adler, 0, (int)Adler32(raw));
        deflate.Write(adler, 0, 4);
        WriteChunk(memory, "IDAT", deflate.ToArray());
        WriteChunk(memory, "IEND", []);
        return memory.ToArray();
    }

    internal static void WriteChunk(Stream stream, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteInt(length, 0, data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
        foreach (byte value in data) crc = Crc32Step(crc, value);
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
        stream.Write(crcBytes);
    }

    internal static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln"))) return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    internal static void WriteInt(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    internal static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        for (int i = 0; i < data.Length; i++)
        {
            a = (a + data[i]) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    internal static uint Crc32Step(uint crc, byte value)
    {
        crc ^= value;
        for (int i = 0; i < 8; i++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        return crc;
    }

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
