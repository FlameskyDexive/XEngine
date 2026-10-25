// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace XEngine.Editor.Projects.Scripting;

/// <summary>Rejects Runtime assembly graphs that accidentally pull editor-only code into a Player.</summary>
internal static class PlayerAssemblyDependencyAudit
{
    private const string ForbiddenAssembly = "XEngine.Editor";

    internal static void ValidateRuntimeClosure(IReadOnlyList<ScriptCompiler.BuildAssembly> assemblies)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < assemblies.Count; i++)
            byName[assemblies[i].Name] = assemblies[i].DllPath;

        var state = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < assemblies.Count; i++)
        {
            string name = assemblies[i].Name;
            // GetBuildAssemblies is also used as a pure planning API by the build-settings UI and
            // tests before a first script compilation. Player pipelines call it after CompileAll,
            // so every emitted DLL exists there and is audited; planning remains side-effect free.
            if (!File.Exists(assemblies[i].DllPath)) continue;
            Visit(name, assemblies[i].DllPath, byName, state, new List<string>());
        }
    }

    private static void Visit(string name, string path, Dictionary<string, string> byName,
        Dictionary<string, byte> state, List<string> chain)
    {
        if (state.TryGetValue(name, out byte current))
        {
            if (current == 2) return;
            if (current == 1) return;
        }
        state[name] = 1;
        chain.Add(name);
        foreach (string dependency in ReadAssemblyReferences(path))
        {
            if (dependency.Equals(ForbiddenAssembly, StringComparison.OrdinalIgnoreCase))
            {
                var forbiddenChain = new List<string>(chain) { ForbiddenAssembly };
                throw new InvalidDataException(
                    "Runtime Player assembly dependency includes editor-only assembly: "
                    + string.Join(" -> ", forbiddenChain));
            }
            if (byName.TryGetValue(dependency, out string? dependencyPath))
                Visit(dependency, dependencyPath, byName, state, chain);
        }
        chain.RemoveAt(chain.Count - 1);
        state[name] = 2;
    }

    private static List<string> ReadAssemblyReferences(string path)
    {
        if (!File.Exists(path)) return new List<string>();
        var result = new List<string>();
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return result;
            MetadataReader reader = pe.GetMetadataReader();
            foreach (AssemblyReferenceHandle handle in reader.AssemblyReferences)
            {
                AssemblyReference reference = reader.GetAssemblyReference(handle);
                result.Add(reader.GetString(reference.Name));
            }
            return result;
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"Runtime Player assembly '{path}' has invalid metadata.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidDataException($"Could not inspect Runtime Player assembly '{path}'.", ex);
        }
    }
}
