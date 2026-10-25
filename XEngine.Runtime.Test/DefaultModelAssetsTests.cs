// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Guards the embedded default-model OBJ sources under
/// <c>XEngine.Runtime/Assets/Defaults/</c>. They are parsed from a stream with no source
/// path (see <c>BuiltInAssets</c>), so an <c>mtllib</c> line can never resolve and Clay
/// logs "OBJ references mtllib ... but no source path is set" on first use of each
/// primitive. The shipped files must not reference .mtl files that don't sit next to them.
/// </summary>
public sealed class DefaultModelAssetsTests
{
    [Theory]
    [InlineData("Cube.obj")]
    [InlineData("Plane.obj")]
    [InlineData("Sphere.obj")]
    [InlineData("Cylinder.obj")]
    [InlineData("Capsule.obj")]
    [InlineData("SkyDome.obj")]
    public void DefaultObj_HasNoUnresolvableMtllibReference(string fileName)
    {
        string path = Path.Combine(FindRepositoryRoot(), "XEngine.Runtime", "Assets", "Defaults", fileName);
        Assert.True(File.Exists(path), path);

        foreach (string line in File.ReadLines(path))
        {
            Assert.False(
                line.TrimStart().StartsWith("mtllib", System.StringComparison.OrdinalIgnoreCase),
                $"{fileName} references an mtllib that cannot resolve from an embedded stream: '{line.Trim()}'");
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
