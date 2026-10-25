// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Reads the required GNU build-id note from one 64-bit little-endian OpenHarmony ELF.</summary>
internal static class HarmonyOSElfBuildIdReader
{
    private const uint ProgramNoteType = 4;
    private const uint GnuBuildIdNoteType = 3;

    internal static string ReadRequired(byte[] bytes, string description)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.Length < 64 || bytes[0] != 0x7f || bytes[1] != (byte)'E' || bytes[2] != (byte)'L' || bytes[3] != (byte)'F')
            throw new InvalidDataException($"HarmonyOS ELF is invalid: {description}");
        if (bytes[4] != 2 || bytes[5] != 1)
            throw new InvalidDataException($"HarmonyOS ELF must be 64-bit little-endian: {description}");

        ulong programOffset = ReadUInt64(bytes, 32, description);
        ushort programEntrySize = ReadUInt16(bytes, 54, description);
        ushort programCount = ReadUInt16(bytes, 56, description);
        if (programEntrySize < 56 || !Fits(bytes, programOffset, (ulong)programEntrySize * programCount))
            throw new InvalidDataException($"HarmonyOS ELF program headers are invalid: {description}");

        string? buildId = null;
        for (uint index = 0; index < programCount; index++)
        {
            ulong header = programOffset + index * programEntrySize;
            uint type = ReadUInt32(bytes, header, description);
            if (type != ProgramNoteType)
                continue;
            ulong noteOffset = ReadUInt64(bytes, header + 8, description);
            ulong noteLength = ReadUInt64(bytes, header + 32, description);
            if (!Fits(bytes, noteOffset, noteLength))
                throw new InvalidDataException($"HarmonyOS ELF PT_NOTE is truncated: {description}");

            ulong cursor = noteOffset;
            ulong end = noteOffset + noteLength;
            while (cursor < end)
            {
                if (!Fits(bytes, cursor, 12))
                    throw new InvalidDataException($"HarmonyOS ELF note header is truncated: {description}");
                uint nameLength = ReadUInt32(bytes, cursor, description);
                uint descriptorLength = ReadUInt32(bytes, cursor + 4, description);
                uint noteType = ReadUInt32(bytes, cursor + 8, description);
                cursor += 12;
                ulong nameOffset = cursor;
                ulong namePaddedLength = Align4(nameLength, description);
                if (!Fits(bytes, nameOffset, namePaddedLength))
                    throw new InvalidDataException($"HarmonyOS ELF note name is truncated: {description}");
                cursor += namePaddedLength;
                ulong descriptorOffset = cursor;
                ulong descriptorPaddedLength = Align4(descriptorLength, description);
                if (!Fits(bytes, descriptorOffset, descriptorPaddedLength))
                    throw new InvalidDataException($"HarmonyOS ELF note descriptor is truncated: {description}");
                if (noteType == GnuBuildIdNoteType && IsGnuName(bytes, nameOffset, nameLength, description))
                {
                    if (descriptorLength == 0)
                        throw new InvalidDataException($"HarmonyOS ELF GNU build-id is empty: {description}");
                    if (buildId != null)
                        throw new InvalidDataException($"HarmonyOS ELF contains multiple GNU build-id notes: {description}");
                    buildId = Convert.ToHexString(bytes.AsSpan(checked((int)descriptorOffset), checked((int)descriptorLength))).ToLowerInvariant();
                }
                cursor += descriptorPaddedLength;
            }
        }

        return buildId ?? throw new InvalidDataException($"HarmonyOS ELF is missing a GNU build-id: {description}");
    }

    private static bool IsGnuName(byte[] bytes, ulong offset, uint length, string description)
    {
        if (length != 4 || bytes[checked((int)offset)] != (byte)'G' || bytes[checked((int)offset + 1)] != (byte)'N' ||
            bytes[checked((int)offset + 2)] != (byte)'U' || bytes[checked((int)offset + 3)] != 0)
        {
            return false;
        }
        return true;
    }

    private static ulong Align4(uint value, string description)
    {
        try
        {
            return checked(((ulong)value + 3) & ~3UL);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"HarmonyOS ELF note size is invalid: {description}", exception);
        }
    }

    private static bool Fits(byte[] bytes, ulong offset, ulong length) =>
        offset <= (ulong)bytes.Length && length <= (ulong)bytes.Length - offset;

    private static ushort ReadUInt16(byte[] bytes, ulong offset, string description)
    {
        if (!Fits(bytes, offset, 2))
            throw new InvalidDataException($"HarmonyOS ELF header is truncated: {description}");
        return BitConverter.ToUInt16(bytes, checked((int)offset));
    }

    private static uint ReadUInt32(byte[] bytes, ulong offset, string description)
    {
        if (!Fits(bytes, offset, 4))
            throw new InvalidDataException($"HarmonyOS ELF header is truncated: {description}");
        return BitConverter.ToUInt32(bytes, checked((int)offset));
    }

    private static ulong ReadUInt64(byte[] bytes, ulong offset, string description)
    {
        if (!Fits(bytes, offset, 8))
            throw new InvalidDataException($"HarmonyOS ELF header is truncated: {description}");
        return BitConverter.ToUInt64(bytes, checked((int)offset));
    }
}
