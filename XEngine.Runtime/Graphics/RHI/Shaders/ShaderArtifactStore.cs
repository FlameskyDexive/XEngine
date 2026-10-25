// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace XEngine.Runtime.RHI.Shaders;

public readonly record struct ShaderArtifactStoreStats(
    long MemoryHits,
    long DiskHits,
    long Misses,
    long Writes,
    long Corruptions,
    long BytesWritten);

public sealed class ShaderArtifactStore
{
    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private readonly int _memoryCapacity;
    private readonly Dictionary<ShaderArtifactKey, ShaderArtifact> _memory = [];
    private readonly Queue<ShaderArtifactKey> _memoryOrder = [];
    private ShaderArtifactStoreStats _stats;

    public ShaderArtifactStore(string rootDirectory, int memoryCapacity = 256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(memoryCapacity, 1);
        _rootDirectory = Path.GetFullPath(rootDirectory);
        _memoryCapacity = memoryCapacity;
    }

    public string RootDirectory => _rootDirectory;

    public ShaderArtifactStoreStats GetStats()
    {
        lock (_gate)
            return _stats;
    }

    public bool TryGet(ShaderArtifactKey key, out ShaderArtifact artifact)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue(key, out artifact!))
            {
                _stats = _stats with { MemoryHits = _stats.MemoryHits + 1 };
                return true;
            }

            string path = GetArtifactPath(key);
            if (!File.Exists(path))
            {
                _stats = _stats with { Misses = _stats.Misses + 1 };
                artifact = null!;
                return false;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                artifact = ShaderArtifactCodec.Read(stream, key);
                Cache(key, artifact);
                _stats = _stats with { DiskHits = _stats.DiskHits + 1 };
                return true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                _stats = _stats with { Misses = _stats.Misses + 1 };
                artifact = null!;
                return false;
            }
            catch (Exception exception) when (IsCorruptArtifact(exception))
            {
                TryDelete(path);
                _stats = _stats with
                {
                    Misses = _stats.Misses + 1,
                    Corruptions = _stats.Corruptions + 1,
                };
                artifact = null!;
                return false;
            }
        }
    }

    public bool Contains(ShaderArtifactKey key)
    {
        lock (_gate)
        {
            if (_memory.ContainsKey(key))
            {
                _stats = _stats with { MemoryHits = _stats.MemoryHits + 1 };
                return true;
            }

            string path = GetArtifactPath(key);
            if (!File.Exists(path))
            {
                _stats = _stats with { Misses = _stats.Misses + 1 };
                return false;
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                ShaderArtifactCodec.Validate(stream, key);
                _stats = _stats with { DiskHits = _stats.DiskHits + 1 };
                return true;
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                _stats = _stats with { Misses = _stats.Misses + 1 };
                return false;
            }
            catch (Exception exception) when (IsCorruptArtifact(exception))
            {
                TryDelete(path);
                _stats = _stats with
                {
                    Misses = _stats.Misses + 1,
                    Corruptions = _stats.Corruptions + 1,
                };
                return false;
            }
        }
    }

    public void Put(ShaderArtifactKey key, ShaderArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        lock (_gate)
        {
            string path = GetArtifactPath(key);
            Directory.CreateDirectory(_rootDirectory);
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 64 * 1024,
                    FileOptions.WriteThrough))
                {
                    ShaderArtifactCodec.Write(stream, key, artifact);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(temporaryPath, path, overwrite: true);
                long bytes = new FileInfo(path).Length;
                Cache(key, artifact);
                _stats = _stats with
                {
                    Writes = _stats.Writes + 1,
                    BytesWritten = _stats.BytesWritten + bytes,
                };
            }
            finally
            {
                TryDelete(temporaryPath);
            }
        }
    }

    public void ClearMemory()
    {
        lock (_gate)
        {
            _memory.Clear();
            _memoryOrder.Clear();
        }
    }

    public string GetArtifactPath(ShaderArtifactKey key)
    {
        string? value = key.Value;
        if (value == null)
            throw new ArgumentException("A default shader artifact key is not valid.", nameof(key));
        return Path.Combine(_rootDirectory, value + ".xshader");
    }

    private void Cache(ShaderArtifactKey key, ShaderArtifact artifact)
    {
        if (_memory.ContainsKey(key))
        {
            _memory[key] = artifact;
            return;
        }

        while (_memory.Count >= _memoryCapacity)
            _memory.Remove(_memoryOrder.Dequeue());

        _memory.Add(key, artifact);
        _memoryOrder.Enqueue(key);
    }

    private static bool IsCorruptArtifact(Exception exception) =>
        exception is InvalidDataException or EndOfStreamException;

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}

internal static class ShaderArtifactCodec
{
    internal const int SchemaVersion = ShaderArtifact.CurrentSchemaVersion;
    private const uint Magic = 0x44485358;
    private const int DigestLength = 32;
    private const int MaximumPayloadLength = 256 * 1024 * 1024;
    private const int MaximumCollectionCount = 65_536;

    internal static void Write(Stream destination, ShaderArtifactKey key, ShaderArtifact artifact)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
            WriteArtifact(writer, artifact);

        ReadOnlySpan<byte> payloadBytes = payload.GetBuffer().AsSpan(0, checked((int)payload.Length));
        byte[] digest = SHA256.HashData(payloadBytes);
        using var envelope = new BinaryWriter(destination, Encoding.UTF8, leaveOpen: true);
        envelope.Write(Magic);
        envelope.Write(SchemaVersion);
        envelope.Write(key.ToBytes());
        envelope.Write(payloadBytes.Length);
        envelope.Write(payloadBytes);
        envelope.Write(digest);
    }

    internal static ShaderArtifact Read(Stream source, ShaderArtifactKey expectedKey)
    {
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Shader artifact magic does not match.");
        if (reader.ReadInt32() != SchemaVersion)
            throw new InvalidDataException("Shader artifact schema version is unsupported.");

        byte[] storedKey = ReadExact(reader, ShaderArtifactKey.ByteLength);
        if (!CryptographicOperations.FixedTimeEquals(storedKey, expectedKey.ToBytes()))
            throw new InvalidDataException("Shader artifact key does not match its content address.");

        int payloadLength = reader.ReadInt32();
        if (payloadLength < 0 || payloadLength > MaximumPayloadLength)
            throw new InvalidDataException("Shader artifact payload length is invalid.");
        byte[] payload = ReadExact(reader, payloadLength);
        byte[] storedDigest = ReadExact(reader, DigestLength);
        if (source.Position != source.Length)
            throw new InvalidDataException("Shader artifact contains trailing data.");

        byte[] actualDigest = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(storedDigest, actualDigest))
            throw new InvalidDataException("Shader artifact checksum does not match.");

        using var payloadStream = new MemoryStream(payload, writable: false);
        using var payloadReader = new BinaryReader(payloadStream, Encoding.UTF8, leaveOpen: false);
        ShaderArtifact artifact;
        try
        {
            artifact = ReadArtifact(payloadReader);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Shader artifact payload contains invalid values.", exception);
        }
        if (payloadStream.Position != payloadStream.Length)
            throw new InvalidDataException("Shader artifact payload contains trailing data.");
        return artifact;
    }

    internal static void Validate(Stream source, ShaderArtifactKey expectedKey)
    {
        using var reader = new BinaryReader(source, Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Shader artifact magic does not match.");
        if (reader.ReadInt32() != SchemaVersion)
            throw new InvalidDataException("Shader artifact schema version is unsupported.");

        byte[] storedKey = ReadExact(reader, ShaderArtifactKey.ByteLength);
        if (!CryptographicOperations.FixedTimeEquals(storedKey, expectedKey.ToBytes()))
            throw new InvalidDataException("Shader artifact key does not match its content address.");

        int payloadLength = reader.ReadInt32();
        if (payloadLength < 0 || payloadLength > MaximumPayloadLength)
            throw new InvalidDataException("Shader artifact payload length is invalid.");

        long payloadStart = source.Position;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> buffer = stackalloc byte[16 * 1024];
        int remaining = payloadLength;
        while (remaining > 0)
        {
            int read = source.Read(buffer[..Math.Min(buffer.Length, remaining)]);
            if (read == 0)
                throw new EndOfStreamException();
            hash.AppendData(buffer[..read]);
            remaining -= read;
        }

        Span<byte> actualDigest = stackalloc byte[DigestLength];
        if (!hash.TryGetHashAndReset(actualDigest, out int written) || written != DigestLength)
            throw new InvalidDataException("Shader artifact checksum could not be computed.");

        byte[] storedDigest = ReadExact(reader, DigestLength);
        if (source.Position != source.Length)
            throw new InvalidDataException("Shader artifact contains trailing data.");
        if (!CryptographicOperations.FixedTimeEquals(storedDigest, actualDigest))
            throw new InvalidDataException("Shader artifact checksum does not match.");

        source.Position = payloadStart;
        try
        {
            ValidateArtifact(reader, payloadStart + payloadLength);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
        {
            throw new InvalidDataException("Shader artifact payload contains invalid values.", exception);
        }
        if (source.Position != payloadStart + payloadLength)
            throw new InvalidDataException("Shader artifact payload contains trailing data.");
    }

    private static void WriteArtifact(BinaryWriter writer, ShaderArtifact artifact)
    {
        writer.Write((int)artifact.Backend);
        writer.Write((int)artifact.CompilerKind);
        WriteString(writer, artifact.CompilerIdentity);
        WriteString(writer, artifact.SourceHash);
        WriteString(writer, artifact.TargetProfile);
        WriteString(writer, artifact.MatrixLayout);
        WriteString(writer, artifact.ShaderName);
        WriteString(writer, artifact.PassName);
        WriteString(writer, artifact.VariantKey);
        WriteString(writer, artifact.VertexEntryPoint);
        WriteString(writer, artifact.FragmentEntryPoint);
        WriteStrings(writer, artifact.Defines);
        WriteString(writer, artifact.Diagnostics);
        writer.Write((int)artifact.Bytecode.Language);
        writer.Write((int)artifact.Bytecode.Format);
        WriteBytes(writer, artifact.Bytecode.VertexBytecode);
        WriteBytes(writer, artifact.Bytecode.FragmentBytecode);
        WriteBindingLayout(writer, artifact.Bytecode.BindingLayout);
        WriteVertexInputLayout(writer, artifact.Bytecode.VertexInputLayout);
        WriteBytes(writer, artifact.Bytecode.ComputeBytecode);
    }

    private static ShaderArtifact ReadArtifact(BinaryReader reader)
    {
        GraphicsBackend backend = (GraphicsBackend)reader.ReadInt32();
        ShaderCompilerKind compilerKind = (ShaderCompilerKind)reader.ReadInt32();
        string compilerIdentity = ReadString(reader);
        string sourceHash = ReadString(reader);
        string targetProfile = ReadString(reader);
        string matrixLayout = ReadString(reader);
        string shaderName = ReadString(reader);
        string passName = ReadString(reader);
        string variantKey = ReadString(reader);
        string vertexEntryPoint = ReadString(reader);
        string fragmentEntryPoint = ReadString(reader);
        string[] defines = ReadStrings(reader);
        string diagnostics = ReadString(reader);
        ShaderLanguage language = (ShaderLanguage)reader.ReadInt32();
        ShaderBytecodeFormat format = (ShaderBytecodeFormat)reader.ReadInt32();
        byte[] vertexBytecode = ReadBytes(reader);
        byte[] fragmentBytecode = ReadBytes(reader);
        ShaderBindingLayout? bindingLayout = ReadBindingLayout(reader);
        ShaderVertexInputLayout? vertexInputLayout = ReadVertexInputLayout(reader);
        byte[] computeBytecode = reader.BaseStream.Position < reader.BaseStream.Length
            ? ReadBytes(reader)
            : [];
        var bytecode = new CompiledShaderBytecode(
            language,
            format,
            vertexBytecode,
            fragmentBytecode,
            bindingLayout,
            vertexInputLayout,
            computeBytecode);
        return new ShaderArtifact(
            backend,
            compilerKind,
            compilerIdentity,
            sourceHash,
            targetProfile,
            matrixLayout,
            shaderName,
            passName,
            variantKey,
            vertexEntryPoint,
            fragmentEntryPoint,
            defines,
            diagnostics,
            bytecode);
    }

    private static void ValidateArtifact(BinaryReader reader, long payloadEnd)
    {
        GraphicsBackend backend = (GraphicsBackend)reader.ReadInt32();
        if (!ShaderBytecodeFormats.IsArtifactBackend(backend))
            throw new InvalidDataException("Shader artifact backend is invalid.");

        ShaderCompilerKind compilerKind = (ShaderCompilerKind)reader.ReadInt32();
        if (compilerKind == ShaderCompilerKind.Unknown)
            throw new InvalidDataException("Shader artifact compiler kind is invalid.");

        EnsureNotWhiteSpace(ReadString(reader), "compiler identity");
        ShaderArtifactKey.Parse(ReadString(reader));
        EnsureNotWhiteSpace(ReadString(reader), "target profile");
        EnsureNotWhiteSpace(ReadString(reader), "matrix layout");
        ReadString(reader);
        ReadString(reader);
        ReadString(reader);
        EnsureNotWhiteSpace(ReadString(reader), "vertex entry point");
        EnsureNotWhiteSpace(ReadString(reader), "fragment entry point");

        int defineCount = ReadCount(reader);
        for (int i = 0; i < defineCount; i++)
            ReadString(reader);
        ReadString(reader);

        ShaderLanguage language = (ShaderLanguage)reader.ReadInt32();
        if (!Enum.IsDefined(language))
            throw new InvalidDataException("Shader artifact language is invalid.");

        ShaderBytecodeFormat format = (ShaderBytecodeFormat)reader.ReadInt32();
        ShaderBytecodeFormat expectedFormat = ShaderBytecodeFormats.GetExpected(backend);
        if (format != expectedFormat)
            throw new InvalidDataException("Shader artifact bytecode format does not match its backend.");

        SkipBytes(reader);
        SkipBytes(reader);
        ValidateBindingLayout(reader);
        ValidateVertexInputLayout(reader);
        if (reader.BaseStream.Position < payloadEnd)
            SkipBytes(reader);
    }

    private static void ValidateBindingLayout(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return;
        ValidateSlots(reader);
        ValidateSlots(reader);
        ValidateSlots(reader);
    }

    private static void ValidateSlots(BinaryReader reader)
    {
        int count = ReadCount(reader);
        for (int i = 0; i < count; i++)
        {
            reader.ReadInt32();
            reader.ReadInt32();
            ReadString(reader);
            reader.ReadInt32();
            reader.ReadInt32();
            reader.ReadUInt32();
            reader.ReadInt32();
            reader.ReadInt32();
            int physicalCount = ReadCount(reader);
            for (int physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
            {
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadUInt32();
            }
            int fieldCount = ReadCount(reader);
            for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
            {
                ReadString(reader);
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadByte();
                reader.ReadByte();
                reader.ReadInt32();
                reader.ReadInt32();
                reader.ReadInt32();
            }
        }
    }

    private static void ValidateVertexInputLayout(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return;

        int count = ReadCount(reader);
        for (int i = 0; i < count; i++)
        {
            reader.ReadUInt32();
            reader.ReadUInt32();
            ReadString(reader);
            reader.ReadUInt32();
            reader.ReadInt32();
            reader.ReadByte();
        }
    }

    private static void SkipBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadLength)
            throw new InvalidDataException("Shader bytecode length is invalid.");
        reader.BaseStream.Seek(length, SeekOrigin.Current);
    }

    private static void EnsureNotWhiteSpace(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Shader artifact {fieldName} is invalid.");
    }

    private static void WriteBindingLayout(BinaryWriter writer, ShaderBindingLayout? layout)
    {
        writer.Write(layout != null);
        if (layout == null)
            return;
        WriteSlots(writer, layout.Textures);
        WriteSlots(writer, layout.Buffers);
        WriteSlots(writer, layout.Samplers);
    }

    private static ShaderBindingLayout? ReadBindingLayout(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        return new ShaderBindingLayout
        {
            Textures = ReadSlots(reader),
            Buffers = ReadSlots(reader),
            Samplers = ReadSlots(reader),
        };
    }

    private static void WriteSlots(BinaryWriter writer, ShaderBindingSlot[] slots)
    {
        writer.Write(slots.Length);
        for (int i = 0; i < slots.Length; i++)
        {
            ShaderBindingSlot slot = slots[i];
            writer.Write((int)slot.Kind);
            writer.Write(slot.Slot);
            WriteString(writer, slot.Name);
            writer.Write(slot.Space);
            writer.Write(slot.Count);
            writer.Write((uint)slot.Stages);
            writer.Write((int)slot.ResourceShape);
            writer.Write(slot.ByteSize);
            writer.Write(slot.PhysicalBindings.Length);
            for (int physicalIndex = 0; physicalIndex < slot.PhysicalBindings.Length; physicalIndex++)
            {
                ShaderPhysicalBinding physical = slot.PhysicalBindings[physicalIndex];
                writer.Write(physical.Binding);
                writer.Write(physical.Space);
                writer.Write((uint)physical.Stages);
            }
            writer.Write(slot.UniformFields.Length);
            for (int fieldIndex = 0; fieldIndex < slot.UniformFields.Length; fieldIndex++)
                WriteUniformField(writer, slot.UniformFields[fieldIndex]);
        }
    }

    private static ShaderBindingSlot[] ReadSlots(BinaryReader reader)
    {
        int count = ReadCount(reader);
        var slots = new ShaderBindingSlot[count];
        for (int i = 0; i < count; i++)
        {
            ShaderBindingKind kind = (ShaderBindingKind)reader.ReadInt32();
            int slot = reader.ReadInt32();
            string name = ReadString(reader);
            int space = reader.ReadInt32();
            int bindingCount = reader.ReadInt32();
            ShaderStageMask stages = (ShaderStageMask)reader.ReadUInt32();
            ShaderResourceShape shape = (ShaderResourceShape)reader.ReadInt32();
            int byteSize = reader.ReadInt32();
            int physicalCount = ReadCount(reader);
            var physicalBindings = new ShaderPhysicalBinding[physicalCount];
            for (int physicalIndex = 0; physicalIndex < physicalCount; physicalIndex++)
            {
                physicalBindings[physicalIndex] = new ShaderPhysicalBinding(
                    reader.ReadInt32(),
                    reader.ReadInt32(),
                    (ShaderStageMask)reader.ReadUInt32());
            }
            int fieldCount = ReadCount(reader);
            var fields = new ShaderUniformField[fieldCount];
            for (int fieldIndex = 0; fieldIndex < fieldCount; fieldIndex++)
                fields[fieldIndex] = ReadUniformField(reader);
            slots[i] = new ShaderBindingSlot(
                kind,
                slot,
                name,
                space,
                bindingCount,
                stages,
                shape,
                byteSize,
                fields,
                physicalBindings: physicalBindings);
        }
        return slots;
    }

    private static void WriteUniformField(BinaryWriter writer, ShaderUniformField field)
    {
        WriteString(writer, field.Name);
        writer.Write(field.Offset);
        writer.Write(field.Size);
        writer.Write((int)field.ScalarType);
        writer.Write(field.Rows);
        writer.Write(field.Columns);
        writer.Write(field.ElementCount);
        writer.Write(field.ArrayStride);
        writer.Write(field.MatrixStride);
    }

    private static ShaderUniformField ReadUniformField(BinaryReader reader) => new(
        ReadString(reader),
        reader.ReadInt32(),
        reader.ReadInt32(),
        (ShaderScalarType)reader.ReadInt32(),
        reader.ReadByte(),
        reader.ReadByte(),
        reader.ReadInt32(),
        reader.ReadInt32(),
        reader.ReadInt32());

    private static void WriteVertexInputLayout(BinaryWriter writer, ShaderVertexInputLayout? layout)
    {
        writer.Write(layout != null);
        if (layout == null)
            return;
        writer.Write(layout.Inputs.Length);
        for (int i = 0; i < layout.Inputs.Length; i++)
        {
            ShaderVertexInputSlot input = layout.Inputs[i];
            writer.Write(input.Semantic);
            writer.Write(input.Location);
            WriteString(writer, input.SemanticName);
            writer.Write(input.SemanticIndex);
            writer.Write((int)input.Format.ScalarType);
            writer.Write(input.Format.ComponentCount);
        }
    }

    private static ShaderVertexInputLayout? ReadVertexInputLayout(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        int count = ReadCount(reader);
        var inputs = new ShaderVertexInputSlot[count];
        for (int i = 0; i < count; i++)
        {
            inputs[i] = new ShaderVertexInputSlot(
                reader.ReadUInt32(),
                reader.ReadUInt32(),
                ReadString(reader),
                reader.ReadUInt32(),
                new ShaderVertexInputFormat((ShaderScalarType)reader.ReadInt32(), reader.ReadByte()));
        }
        return new ShaderVertexInputLayout { Inputs = inputs };
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadLength)
            throw new InvalidDataException("Shader bytecode length is invalid.");
        return ReadExact(reader, length);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static void WriteStrings(BinaryWriter writer, IReadOnlyList<string> values)
    {
        writer.Write(values.Count);
        for (int i = 0; i < values.Count; i++)
            WriteString(writer, values[i]);
    }

    private static string[] ReadStrings(BinaryReader reader)
    {
        int count = ReadCount(reader);
        var values = new string[count];
        for (int i = 0; i < count; i++)
            values[i] = ReadString(reader);
        return values;
    }

    private static string ReadString(BinaryReader reader)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > MaximumPayloadLength)
            throw new InvalidDataException("Shader artifact string length is invalid.");
        return Encoding.UTF8.GetString(ReadExact(reader, length));
    }

    private static int ReadCount(BinaryReader reader)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumCollectionCount)
            throw new InvalidDataException("Shader artifact collection count is invalid.");
        return count;
    }

    private static byte[] ReadExact(BinaryReader reader, int length)
    {
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException();
        return bytes;
    }
}
