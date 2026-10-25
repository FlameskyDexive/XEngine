using System.IO;
using XHub.Services;
using Xunit;

namespace XHub.Test;

public class InstallationInspectorTests
{
    [Fact]
    public void FindEditorExe_ShippedReleasePayload_FindsUnderXEngineFolder()
    {
        string root = Path.Combine(Path.GetTempPath(), "xhub-tests",
            "inspector-" + Path.GetRandomFileName());
        try
        {
            string dir = Path.Combine(root, "XEngine");
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, "XEngine.Editor.exe");
            File.WriteAllBytes(exe, [0x4D, 0x5A]); // "MZ"

            Assert.Equal(exe, InstallationInspector.FindEditorExe(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void FindEditorExe_MissingEditor_ReturnsEmpty()
    {
        string root = Path.Combine(Path.GetTempPath(), "xhub-tests",
            "inspector-" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(root);
            Assert.Equal("", InstallationInspector.FindEditorExe(root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
