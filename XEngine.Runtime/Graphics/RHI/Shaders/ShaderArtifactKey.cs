// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XEngine.Runtime.RHI.Shaders;

public readonly record struct ShaderArtifactKey
{
    public const int ByteLength = 32;

    public string? Value { get; }

    private ShaderArtifactKey(string value) => Value = value;

    public static ShaderArtifactKey Create(
        ShaderCompileRequest request,
        string compilerIdentity,
        string targetProfile,
        IReadOnlyDictionary<string, string>? dependencyHashes = null,
        string matrixLayout = ShaderArtifact.DefaultMatrixLayout)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProfile);
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixLayout);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(ShaderArtifactCodec.SchemaVersion);
            writer.Write((int)request.TargetBackend);
            writer.Write((int)request.Language);
            WriteString(writer, compilerIdentity);
            WriteString(writer, targetProfile);
            WriteString(writer, matrixLayout);
            WriteString(writer, request.VertexEntryPoint);
            WriteString(writer, request.FragmentEntryPoint);
            WriteString(writer, NormalizeSource(request.VertexSource));
            WriteString(writer, NormalizeSource(request.FragmentSource));
            WriteString(writer, NormalizeSource(request.GeometrySource ?? string.Empty));
            WriteString(writer, request.ShaderName ?? string.Empty);
            WriteString(writer, request.PassName ?? string.Empty);
            WriteString(writer, request.VariantKey ?? string.Empty);
            WriteKeywords(writer, request.Keywords);
            WriteDefines(writer, request.ExtraDefines);
            WriteDependencies(writer, dependencyHashes);
        }

        return FromBytes(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    public static string ComputeSourceHash(ShaderCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write((int)request.Language);
            WriteString(writer, NormalizeSource(request.VertexSource));
            WriteString(writer, NormalizeSource(request.FragmentSource));
            WriteString(writer, NormalizeSource(request.GeometrySource ?? string.Empty));
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static string ComputeDependencyHash(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeSource(source))));
    }

    public static ShaderArtifactKey Parse(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != ByteLength * 2)
            throw new FormatException("Shader artifact keys must contain 64 hexadecimal characters.");

        byte[] bytes;
        try
        {
            bytes = Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            throw new FormatException("Shader artifact keys must contain 64 hexadecimal characters.");
        }

        return FromBytes(bytes);
    }

    internal static ShaderArtifactKey FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteLength)
            throw new ArgumentException($"Shader artifact keys must contain {ByteLength} bytes.", nameof(bytes));
        return new ShaderArtifactKey(Convert.ToHexString(bytes));
    }

    internal byte[] ToBytes()
    {
        string? value = Value;
        if (value == null)
            throw new InvalidOperationException("A default shader artifact key is not valid.");
        return Convert.FromHexString(value);
    }

    public override string ToString() => Value ?? string.Empty;

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string NormalizeSource(string source) =>
        source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static void WriteKeywords(BinaryWriter writer, IReadOnlyDictionary<string, bool>? keywords)
    {
        if (keywords == null || keywords.Count == 0)
        {
            writer.Write(0);
            return;
        }

        var entries = new KeyValuePair<string, bool>[keywords.Count];
        int index = 0;
        foreach (KeyValuePair<string, bool> entry in keywords)
            entries[index++] = entry;
        Array.Sort(entries, static (left, right) => StringComparer.Ordinal.Compare(left.Key, right.Key));

        writer.Write(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            WriteString(writer, entries[i].Key);
            writer.Write(entries[i].Value);
        }
    }

    private static void WriteDefines(BinaryWriter writer, IReadOnlyList<string>? defines)
    {
        if (defines == null || defines.Count == 0)
        {
            writer.Write(0);
            return;
        }

        var entries = new string[defines.Count];
        for (int i = 0; i < defines.Count; i++)
            entries[i] = defines[i];
        Array.Sort(entries, StringComparer.Ordinal);

        writer.Write(entries.Length);
        for (int i = 0; i < entries.Length; i++)
            WriteString(writer, entries[i]);
    }

    private static void WriteDependencies(BinaryWriter writer, IReadOnlyDictionary<string, string>? dependencies)
    {
        if (dependencies == null || dependencies.Count == 0)
        {
            writer.Write(0);
            return;
        }

        var entries = new KeyValuePair<string, string>[dependencies.Count];
        int index = 0;
        foreach (KeyValuePair<string, string> entry in dependencies)
            entries[index++] = new KeyValuePair<string, string>(entry.Key.Replace('\\', '/'), entry.Value);
        Array.Sort(entries, static (left, right) =>
        {
            int pathComparison = StringComparer.Ordinal.Compare(left.Key, right.Key);
            return pathComparison != 0
                ? pathComparison
                : StringComparer.Ordinal.Compare(left.Value, right.Value);
        });

        writer.Write(entries.Length);
        for (int i = 0; i < entries.Length; i++)
        {
            WriteString(writer, entries[i].Key);
            WriteString(writer, entries[i].Value);
        }
    }
}
