// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace XEngine.Editor.Build.Android;

internal static class AndroidElfInspector
{
    private const uint StaticSymbolTableSection = 2;
    private const uint DynamicSegment = 2;
    private const uint DynamicSymbolTableSection = 11;
    private const uint LoadSegment = 1;
    private const uint NoteSegment = 4;
    private const uint StringTableSection = 3;
    private const long DynamicNull = 0;
    private const long DynamicNeeded = 1;
    private const long DynamicStringTable = 5;
    private const long DynamicStringTableSize = 10;
    private const long DynamicSoname = 14;
    private const long DynamicTextRelocations = 22;
    private const long DynamicFlags = 30;
    private const ulong DynamicFlagTextRelocations = 0x4;
    private const uint GnuBuildIdNoteType = 3;

    internal static void RequireLoadAlignment(string path, ulong minimumAlignment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (minimumAlignment == 0 || (minimumAlignment & (minimumAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(minimumAlignment), "ELF alignment must be a power of two.");

        using FileStream stream = File.OpenRead(path);
        RequireLoadAlignment(stream, path, minimumAlignment);
    }

    internal static void RequireLoadAlignment(Stream stream, string description, ulong minimumAlignment)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        if (minimumAlignment == 0 || (minimumAlignment & (minimumAlignment - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(minimumAlignment), "ELF alignment must be a power of two.");

        ElfMetadata metadata = ReadMetadata(stream, description);
        IReadOnlyList<ElfProgramHeader> headers = ReadProgramHeaders(stream, metadata, description);
        int loadCount = 0;
        for (int i = 0; i < headers.Count; i++)
        {
            ElfProgramHeader header = headers[i];
            if (header.Type != LoadSegment)
                continue;
            loadCount++;
            if (header.Alignment < minimumAlignment)
            {
                throw new InvalidDataException(
                    $"Android ELF load alignment is 0x{header.Alignment:X}; expected at least " +
                    $"0x{minimumAlignment:X}: {description}");
            }
        }
        if (loadCount == 0)
            throw new InvalidDataException($"Android ELF contains no PT_LOAD segments: {description}");
    }

    internal static void RequireMachine(Stream stream, string description, string abi)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(abi);

        ushort expected = abi switch
        {
            "arm64-v8a" => 183,
            "armeabi-v7a" => 40,
            "x86_64" => 62,
            _ => throw new ArgumentOutOfRangeException(nameof(abi), abi, "Unsupported Android ABI."),
        };
        ElfMetadata metadata = ReadMetadata(stream, description);
        if (metadata.Machine != expected)
        {
            throw new InvalidDataException(
                $"Android ELF machine {metadata.Machine} does not match ABI {abi} (expected {expected}): {description}");
        }
    }

    internal static AndroidElfDynamicMetadata ReadDynamicMetadata(Stream stream, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        ElfMetadata metadata = ReadMetadata(stream, description);
        IReadOnlyList<ElfProgramHeader> headers = ReadProgramHeaders(stream, metadata, description);
        ElfProgramHeader? dynamicHeader = null;
        for (int i = 0; i < headers.Count; i++)
        {
            if (headers[i].Type != DynamicSegment)
                continue;
            if (dynamicHeader != null)
                throw new InvalidDataException($"Android ELF contains multiple PT_DYNAMIC segments: {description}");
            dynamicHeader = headers[i];
        }
        if (dynamicHeader == null)
            throw new InvalidDataException($"Android ELF contains no PT_DYNAMIC segment: {description}");

        ulong entrySize = metadata.Is64Bit ? 16u : 8u;
        if (dynamicHeader.FileSize == 0 || dynamicHeader.FileSize % entrySize != 0)
            throw new InvalidDataException($"Android ELF has an invalid PT_DYNAMIC size: {description}");
        long dynamicOffset = ToStreamOffset(dynamicHeader.Offset, description, "PT_DYNAMIC offset");
        EnsureRange(stream, dynamicOffset, dynamicHeader.FileSize, description, "PT_DYNAMIC segment");

        var neededStringOffsets = new List<ulong>();
        ulong? sonameStringOffset = null;
        ulong? stringTableAddress = null;
        ulong stringTableSize = 0;
        bool hasStringTableSize = false;
        bool hasTextRelocations = false;
        bool hasNullTerminator = false;
        stream.Position = dynamicOffset;
        using (var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            for (ulong relative = 0; relative < dynamicHeader.FileSize; relative += entrySize)
            {
                long tag = metadata.Is64Bit
                    ? unchecked((long)reader.ReadUInt64())
                    : reader.ReadInt32();
                ulong value = metadata.Is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
                if (tag == DynamicNull)
                {
                    hasNullTerminator = true;
                    break;
                }
                switch (tag)
                {
                    case DynamicNeeded:
                        neededStringOffsets.Add(value);
                        break;
                    case DynamicStringTable:
                        stringTableAddress = value;
                        break;
                    case DynamicStringTableSize:
                        stringTableSize = value;
                        hasStringTableSize = true;
                        break;
                    case DynamicSoname:
                        if (sonameStringOffset.HasValue)
                            throw new InvalidDataException($"Android ELF contains multiple DT_SONAME entries: {description}");
                        sonameStringOffset = value;
                        break;
                    case DynamicTextRelocations:
                        hasTextRelocations = true;
                        break;
                    case DynamicFlags:
                        hasTextRelocations |= (value & DynamicFlagTextRelocations) != 0;
                        break;
                }
            }
        }
        if (!hasNullTerminator)
            throw new InvalidDataException($"Android ELF PT_DYNAMIC is missing DT_NULL: {description}");
        if ((neededStringOffsets.Count > 0 || sonameStringOffset.HasValue) &&
            (!stringTableAddress.HasValue || !hasStringTableSize || stringTableSize == 0))
        {
            throw new InvalidDataException(
                $"Android ELF DT_NEEDED or DT_SONAME entries have no valid DT_STRTAB/DT_STRSZ: {description}");
        }

        long stringTableOffset = 0;
        if (stringTableAddress.HasValue)
        {
            stringTableOffset = TranslateVirtualAddress(
                headers,
                stringTableAddress.Value,
                description,
                "DT_STRTAB");
            EnsureRange(stream, stringTableOffset, stringTableSize, description, "DT_STRTAB");
        }
        var neededLibraries = new List<string>(neededStringOffsets.Count);
        for (int i = 0; i < neededStringOffsets.Count; i++)
        {
            neededLibraries.Add(ReadString(
                stream,
                stringTableOffset,
                stringTableSize,
                neededStringOffsets[i],
                description,
                "DT_NEEDED"));
        }
        string? soname = sonameStringOffset.HasValue
            ? ReadString(
                stream,
                stringTableOffset,
                stringTableSize,
                sonameStringOffset.Value,
                description,
                "DT_SONAME")
            : null;
        return new AndroidElfDynamicMetadata(
            neededLibraries.AsReadOnly(),
            hasTextRelocations,
            soname,
            ReadGnuBuildId(stream, headers, description));
    }

    internal static void RequireIdentity(
        AndroidElfDynamicMetadata metadata,
        string description)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (!IsSafeLibraryName(metadata.Soname))
            throw new InvalidDataException($"Android ELF is missing a valid DT_SONAME: {description}");
        if (string.IsNullOrEmpty(metadata.GnuBuildId))
            throw new InvalidDataException($"Android ELF is missing a GNU build-id: {description}");
    }

    internal static AndroidElfDynamicSymbols ReadDynamicSymbols(Stream stream, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        ElfMetadata metadata = ReadMetadata(stream, description);
        IReadOnlyList<ElfSectionHeader> sections = ReadSectionHeaders(stream, metadata, description);
        ElfSectionHeader? dynamicSymbols = null;
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Type != DynamicSymbolTableSection)
                continue;
            if (dynamicSymbols != null)
                throw new InvalidDataException($"Android ELF contains multiple .dynsym sections: {description}");
            dynamicSymbols = sections[i];
        }
        if (dynamicSymbols == null)
            throw new InvalidDataException($"Android ELF contains no .dynsym section: {description}");
        if (dynamicSymbols.Link >= sections.Count)
            throw new InvalidDataException($"Android ELF .dynsym has an invalid string table link: {description}");

        ElfSectionHeader strings = sections[(int)dynamicSymbols.Link];
        if (strings.Type != StringTableSection || strings.Size == 0)
            throw new InvalidDataException($"Android ELF .dynsym has no valid string table: {description}");
        ulong requiredEntrySize = metadata.Is64Bit ? 24u : 16u;
        if (dynamicSymbols.EntrySize < requiredEntrySize ||
            dynamicSymbols.Size == 0 ||
            dynamicSymbols.Size % dynamicSymbols.EntrySize != 0)
        {
            throw new InvalidDataException($"Android ELF .dynsym has an invalid entry size: {description}");
        }

        long symbolOffset = ToStreamOffset(dynamicSymbols.Offset, description, ".dynsym offset");
        long stringOffset = ToStreamOffset(strings.Offset, description, ".dynstr offset");
        EnsureRange(stream, symbolOffset, dynamicSymbols.Size, description, ".dynsym section");
        EnsureRange(stream, stringOffset, strings.Size, description, ".dynstr section");

        var undefined = new HashSet<string>(StringComparer.Ordinal);
        var weakUndefined = new HashSet<string>(StringComparer.Ordinal);
        var defined = new HashSet<string>(StringComparer.Ordinal);
        ulong count = dynamicSymbols.Size / dynamicSymbols.EntrySize;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        for (ulong index = 0; index < count; index++)
        {
            long entryOffset = checked(symbolOffset + (long)(index * dynamicSymbols.EntrySize));
            stream.Position = entryOffset;
            uint nameOffset = reader.ReadUInt32();
            byte info;
            ushort sectionIndex;
            if (metadata.Is64Bit)
            {
                info = reader.ReadByte();
                _ = reader.ReadByte();
                sectionIndex = reader.ReadUInt16();
            }
            else
            {
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                info = reader.ReadByte();
                _ = reader.ReadByte();
                sectionIndex = reader.ReadUInt16();
            }

            int binding = info >> 4;
            if (nameOffset == 0 || (binding != 1 && binding != 2))
                continue;
            string name = ReadString(stream, stringOffset, strings.Size, nameOffset, description, ".dynsym");
            if (sectionIndex == 0)
            {
                undefined.Add(name);
                if (binding == 2)
                    weakUndefined.Add(name);
            }
            else
                defined.Add(name);
        }

        string[] undefinedSymbols = [.. undefined];
        string[] weakUndefinedSymbols = [.. weakUndefined];
        string[] definedSymbols = [.. defined];
        Array.Sort(undefinedSymbols, StringComparer.Ordinal);
        Array.Sort(weakUndefinedSymbols, StringComparer.Ordinal);
        Array.Sort(definedSymbols, StringComparer.Ordinal);
        return new AndroidElfDynamicSymbols(undefinedSymbols, weakUndefinedSymbols, definedSymbols);
    }

    internal static bool HasStaticSymbolTable(Stream stream, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        ElfMetadata metadata = ReadMetadata(stream, description);
        IReadOnlyList<ElfSectionHeader> sections = ReadSectionHeaders(stream, metadata, description);
        for (int i = 0; i < sections.Count; i++)
        {
            if (sections[i].Type == StaticSymbolTableSection)
                return true;
        }
        return false;
    }

    internal static string ReadBuildId(Stream stream, string description)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        ElfMetadata metadata = ReadMetadata(stream, description);
        IReadOnlyList<ElfProgramHeader> headers = ReadProgramHeaders(stream, metadata, description);
        string? buildId = ReadGnuBuildId(stream, headers, description);
        if (string.IsNullOrEmpty(buildId))
            throw new InvalidDataException($"Android ELF is missing a GNU build-id: {description}");
        return buildId;
    }

    private static ElfMetadata ReadMetadata(Stream stream, string description)
    {
        if (!stream.CanSeek)
            throw new InvalidDataException($"Android ELF stream must be seekable: {description}");
        stream.Position = 0;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        byte[] identity = reader.ReadBytes(16);
        if (identity.Length != 16 ||
            identity[0] != 0x7f || identity[1] != (byte)'E' ||
            identity[2] != (byte)'L' || identity[3] != (byte)'F')
        {
            throw new InvalidDataException($"Android native library is not ELF: {description}");
        }
        if (identity[5] != 1)
            throw new InvalidDataException($"Android native library is not little-endian ELF: {description}");

        bool is64Bit = identity[4] switch
        {
            1 => false,
            2 => true,
            _ => throw new InvalidDataException($"Android native library has an invalid ELF class: {description}"),
        };
        stream.Position = 18;
        ushort machine = reader.ReadUInt16();
        stream.Position = is64Bit ? 32 : 28;
        ulong programHeaderOffset = is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
        stream.Position = is64Bit ? 54 : 42;
        ushort entrySize = reader.ReadUInt16();
        ushort entryCount = reader.ReadUInt16();
        stream.Position = is64Bit ? 40 : 32;
        ulong sectionHeaderOffset = is64Bit ? reader.ReadUInt64() : reader.ReadUInt32();
        stream.Position = is64Bit ? 58 : 46;
        ushort sectionEntrySize = reader.ReadUInt16();
        ushort sectionCount = reader.ReadUInt16();
        int requiredEntrySize = is64Bit ? 56 : 32;
        if (entrySize < requiredEntrySize || entryCount == 0)
            throw new InvalidDataException($"Android ELF has no valid program header table: {description}");

        return new ElfMetadata(
            is64Bit,
            machine,
            programHeaderOffset,
            entrySize,
            entryCount,
            requiredEntrySize,
            sectionHeaderOffset,
            sectionEntrySize,
            sectionCount);
    }

    private static IReadOnlyList<ElfProgramHeader> ReadProgramHeaders(
        Stream stream,
        ElfMetadata metadata,
        string description)
    {
        var result = new ElfProgramHeader[metadata.EntryCount];
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < metadata.EntryCount; i++)
        {
            long entryOffset = checked((long)metadata.ProgramHeaderOffset + (long)i * metadata.EntrySize);
            EnsureRange(stream, entryOffset, (ulong)metadata.RequiredEntrySize, description, "program header");
            stream.Position = entryOffset;
            uint type = reader.ReadUInt32();
            ulong offset;
            ulong virtualAddress;
            ulong fileSize;
            ulong alignment;
            if (metadata.Is64Bit)
            {
                _ = reader.ReadUInt32();
                offset = reader.ReadUInt64();
                virtualAddress = reader.ReadUInt64();
                _ = reader.ReadUInt64();
                fileSize = reader.ReadUInt64();
                _ = reader.ReadUInt64();
                alignment = reader.ReadUInt64();
            }
            else
            {
                offset = reader.ReadUInt32();
                virtualAddress = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                fileSize = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                alignment = reader.ReadUInt32();
            }
            result[i] = new ElfProgramHeader(type, offset, virtualAddress, fileSize, alignment);
        }
        return result;
    }

    private static IReadOnlyList<ElfSectionHeader> ReadSectionHeaders(
        Stream stream,
        ElfMetadata metadata,
        string description)
    {
        int requiredEntrySize = metadata.Is64Bit ? 64 : 40;
        if (metadata.SectionHeaderOffset == 0 || metadata.SectionEntrySize < requiredEntrySize ||
            metadata.SectionCount == 0)
        {
            throw new InvalidDataException($"Android ELF has no valid section header table: {description}");
        }

        var result = new ElfSectionHeader[metadata.SectionCount];
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        for (int i = 0; i < metadata.SectionCount; i++)
        {
            long entryOffset = checked((long)metadata.SectionHeaderOffset + (long)i * metadata.SectionEntrySize);
            EnsureRange(stream, entryOffset, (ulong)requiredEntrySize, description, "section header");
            stream.Position = entryOffset;
            _ = reader.ReadUInt32();
            uint type = reader.ReadUInt32();
            ulong offset;
            ulong size;
            uint link;
            ulong entrySize;
            if (metadata.Is64Bit)
            {
                _ = reader.ReadUInt64();
                _ = reader.ReadUInt64();
                offset = reader.ReadUInt64();
                size = reader.ReadUInt64();
                link = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt64();
                entrySize = reader.ReadUInt64();
            }
            else
            {
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                offset = reader.ReadUInt32();
                size = reader.ReadUInt32();
                link = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                entrySize = reader.ReadUInt32();
            }
            result[i] = new ElfSectionHeader(type, offset, size, link, entrySize);
        }
        return result;
    }

    private static long TranslateVirtualAddress(
        IReadOnlyList<ElfProgramHeader> headers,
        ulong address,
        string description,
        string source)
    {
        for (int i = 0; i < headers.Count; i++)
        {
            ElfProgramHeader header = headers[i];
            if (header.Type != LoadSegment || address < header.VirtualAddress)
                continue;
            ulong relative = address - header.VirtualAddress;
            if (relative >= header.FileSize)
                continue;
            return ToStreamOffset(checked(header.Offset + relative), description, source + " file offset");
        }
        throw new InvalidDataException($"Android ELF {source} is outside PT_LOAD segments: {description}");
    }

    private static string ReadString(
        Stream stream,
        long tableOffset,
        ulong tableSize,
        ulong stringOffset,
        string description,
        string source)
    {
        if (stringOffset >= tableSize)
            throw new InvalidDataException($"Android ELF {source} string offset is invalid: {description}");
        if (tableOffset < 0 || stringOffset > (ulong)(long.MaxValue - tableOffset))
            throw new InvalidDataException($"Android ELF {source} string offset is out of range: {description}");
        long offset = tableOffset + (long)stringOffset;
        ulong remaining = tableSize - stringOffset;
        stream.Position = offset;
        var bytes = new List<byte>();
        for (ulong i = 0; i < remaining; i++)
        {
            int value = stream.ReadByte();
            if (value < 0)
                throw new InvalidDataException($"Android ELF DT_STRTAB is truncated: {description}");
            if (value == 0)
            {
                if (bytes.Count == 0)
                    throw new InvalidDataException($"Android ELF {source} library name is empty: {description}");
                return Encoding.UTF8.GetString(bytes.ToArray());
            }
            bytes.Add((byte)value);
        }
        throw new InvalidDataException($"Android ELF {source} string is unterminated: {description}");
    }

    private static string? ReadGnuBuildId(
        Stream stream,
        IReadOnlyList<ElfProgramHeader> headers,
        string description)
    {
        string? buildId = null;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        for (int headerIndex = 0; headerIndex < headers.Count; headerIndex++)
        {
            ElfProgramHeader header = headers[headerIndex];
            if (header.Type != NoteSegment || header.FileSize == 0)
                continue;

            long noteOffset = ToStreamOffset(header.Offset, description, "PT_NOTE offset");
            EnsureRange(stream, noteOffset, header.FileSize, description, "PT_NOTE segment");
            long noteEnd = checked(noteOffset + (long)header.FileSize);
            long cursor = noteOffset;
            while (cursor < noteEnd)
            {
                if (noteEnd - cursor < 12)
                    throw new InvalidDataException($"Android ELF PT_NOTE header is truncated: {description}");
                stream.Position = cursor;
                uint nameSize = reader.ReadUInt32();
                uint descriptorSize = reader.ReadUInt32();
                uint type = reader.ReadUInt32();
                ulong namePadded = AlignNoteField(nameSize);
                ulong descriptorPadded = AlignNoteField(descriptorSize);
                ulong payloadSize = checked(namePadded + descriptorPadded);
                if (payloadSize > (ulong)(noteEnd - cursor - 12))
                    throw new InvalidDataException($"Android ELF PT_NOTE payload is truncated: {description}");

                long nameOffset = cursor + 12;
                stream.Position = nameOffset;
                byte[] name = reader.ReadBytes(checked((int)nameSize));
                if (name.Length != nameSize)
                    throw new InvalidDataException($"Android ELF PT_NOTE name is truncated: {description}");
                long descriptorOffset = checked(nameOffset + (long)namePadded);
                stream.Position = descriptorOffset;
                byte[] descriptor = reader.ReadBytes(checked((int)descriptorSize));
                if (descriptor.Length != descriptorSize)
                    throw new InvalidDataException($"Android ELF PT_NOTE descriptor is truncated: {description}");

                if (type == GnuBuildIdNoteType && IsGnuNoteName(name))
                {
                    if (descriptor.Length == 0)
                        throw new InvalidDataException($"Android ELF GNU build-id is empty: {description}");
                    if (buildId != null)
                        throw new InvalidDataException($"Android ELF contains multiple GNU build-id notes: {description}");
                    buildId = Convert.ToHexString(descriptor);
                }
                cursor = checked(cursor + 12 + (long)payloadSize);
            }
        }
        return buildId;
    }

    private static ulong AlignNoteField(uint size) => checked(((ulong)size + 3) & ~3UL);

    private static bool IsGnuNoteName(IReadOnlyList<byte> name) =>
        name.Count == 4 && name[0] == (byte)'G' && name[1] == (byte)'N' &&
        name[2] == (byte)'U' && name[3] == 0;

    private static bool IsSafeLibraryName(string? value) =>
        !string.IsNullOrEmpty(value) &&
        value.EndsWith(".so", StringComparison.Ordinal) &&
        value.IndexOf('/') < 0 &&
        value.IndexOf('\\') < 0;

    private static long ToStreamOffset(ulong value, string description, string source)
    {
        if (value > long.MaxValue)
            throw new InvalidDataException($"Android ELF {source} is out of range: {description}");
        return (long)value;
    }

    private static void EnsureRange(Stream stream, long offset, ulong length, string description, string source)
    {
        if (offset < 0 || (ulong)offset > (ulong)stream.Length ||
            length > (ulong)stream.Length - (ulong)offset)
        {
            throw new InvalidDataException($"Android ELF {source} is truncated: {description}");
        }
    }

    private sealed record ElfMetadata(
        bool Is64Bit,
        ushort Machine,
        ulong ProgramHeaderOffset,
        ushort EntrySize,
        ushort EntryCount,
        int RequiredEntrySize,
        ulong SectionHeaderOffset,
        ushort SectionEntrySize,
        ushort SectionCount);

    private sealed record ElfProgramHeader(
        uint Type,
        ulong Offset,
        ulong VirtualAddress,
        ulong FileSize,
        ulong Alignment);

    private sealed record ElfSectionHeader(
        uint Type,
        ulong Offset,
        ulong Size,
        uint Link,
        ulong EntrySize);
}

internal sealed record AndroidElfDynamicMetadata(
    IReadOnlyList<string> NeededLibraries,
    bool HasTextRelocations,
    string? Soname,
    string? GnuBuildId);

internal sealed record AndroidElfDynamicSymbols(
    IReadOnlyList<string> UndefinedSymbols,
    IReadOnlyList<string> WeakUndefinedSymbols,
    IReadOnlyList<string> DefinedSymbols);
