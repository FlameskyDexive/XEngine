using System;
using System.IO;

using XEngine.XUI.Core;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// The documents the package actually ships. A sample that does not parse is worse than no sample:
/// it is the first thing anyone copies, and it teaches the language wrong.
/// </summary>
public sealed class XuiPackageAssetTests
{
    private const string PackageRoot = "Packages/com.xengine.xui";

    [Fact]
    public void ShippedLoginSampleParsesExpandsAndDeclaresItsScreen()
    {
        string path = Path.Combine(RepoRoot(), PackageRoot, "Samples~", "LoginScreen", "Login.xui");
        Assert.True(File.Exists(path), "Missing sample document: " + path);

        XuiDocument document = XuiDocumentParser.ParseFile(path, "Samples~/LoginScreen/Login.xui");

        ScreenDef screen = Assert.Single(document.Screens);
        Assert.Equal("Login", screen.Name);
        Assert.Equal(XuiLayerKind.Normal, screen.Layer);
        Assert.NotNull(screen.Reference);

        var templates = new XuiTemplateTable();
        templates.Add(document);
        ElementNode root = XuiTemplateExpander.Expand(screen.Root, templates, path);

        // The template is used twice; expansion must leave two distinctly-named input fields, which is
        // the whole reason its ids are parameterised.
        Assert.NotNull(FindById(root, "XInputAccount"));
        Assert.NotNull(FindById(root, "XInputPassword"));
        Assert.NotNull(FindById(root, "XBtnLogin"));
    }

    [Fact]
    public void EverySampleDocumentUsesOnlyBuiltinOrDeclaredTags()
    {
        string samples = Path.Combine(RepoRoot(), PackageRoot, "Samples~");
        Assert.True(Directory.Exists(samples), "Missing Samples~ folder: " + samples);

        foreach (string path in Directory.EnumerateFiles(samples, "*.xui", SearchOption.AllDirectories))
        {
            XuiDocument document = XuiDocumentParser.ParseFile(path);
            var templates = new XuiTemplateTable();
            templates.Add(document);

            for (int i = 0; i < document.Screens.Count; i++)
            {
                ElementNode root = XuiTemplateExpander.Expand(document.Screens[i].Root, templates, path);
                AssertTagsAreBuiltin(root, path);
            }
        }
    }

    private static void AssertTagsAreBuiltin(ElementNode node, string path)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            ElementNode child = node.Children[i];
            Assert.True(
                XuiTagLibrary.IsBuiltin(child.Tag),
                path + " uses <" + child.Tag + ">, which is neither a built-in tag nor a declared template.");
            AssertTagsAreBuiltin(child, path);
        }
    }

    private static ElementNode? FindById(ElementNode node, string id)
    {
        if (string.Equals(node.Id, id, StringComparison.Ordinal)) return node;
        for (int i = 0; i < node.Children.Count; i++)
        {
            ElementNode? found = FindById(node.Children[i], id);
            if (found != null) return found;
        }
        return null;
    }

    [Fact]
    public void EveryShippedPixelDocumentParsesAndRasterizes()
    {
        string samples = Path.Combine(RepoRoot(), PackageRoot, "Samples~");
        Assert.True(Directory.Exists(samples), "Missing Samples~ folder: " + samples);

        int checkedFiles = 0;
        foreach (string path in Directory.EnumerateFiles(samples, "*.pxl", SearchOption.AllDirectories))
        {
            PxlDocument document = PxlParser.ParseFile(path);
            Assert.NotEmpty(document.Sections);

            foreach (PxlSection section in document.Sections)
            {
                byte[] rgba = PxlRasterizer.Rasterize(document, section, scale: 1);
                Assert.Equal(section.Width * section.Height * 4, rgba.Length);

                // Every character the grid uses must be declared, or part of the art is silently gone.
                foreach (string row in section.Rows)
                {
                    foreach (char c in row)
                    {
                        Assert.True(
                            c == PxlDocument.TransparentChar || document.Chars.ContainsKey(c),
                            path + " uses undeclared character '" + c + "'.");
                    }
                }
            }
            checkedFiles++;
        }

        Assert.True(checkedFiles > 0, "No .pxl samples found to check.");
    }

    private static string RepoRoot() => RepoRootLocator.Find();
}
