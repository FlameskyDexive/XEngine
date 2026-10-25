using System;
using System.IO;

namespace XEngine.Editor.Projects;

/// <summary>An OS-held project lock. A crashed editor releases the handle automatically;
/// the marker file may remain and must never be deleted to bypass a live owner.</summary>
internal sealed class ProjectDirectoryLease : IDisposable
{
    private readonly FileStream _handle;

    private ProjectDirectoryLease(FileStream handle) => _handle = handle;

    internal static ProjectDirectoryLease Acquire(string projectPath)
    {
        string root = Path.GetFullPath(projectPath);
        string temp = Path.Combine(root, "Temp");
        Directory.CreateDirectory(temp);
        try
        {
            return new ProjectDirectoryLease(new FileStream(Path.Combine(temp, "EditorProject.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException error)
        {
            throw new InvalidOperationException(
                $"项目目录已被另一个编辑器或构建进程占用：{root}\n请先在原编辑器中保存并关闭项目，再重新打开。", error);
        }
    }

    public void Dispose() => _handle.Dispose();
}
