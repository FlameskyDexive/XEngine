// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Binary serialization for <see cref="PackageManifest"/>. Compact varint layout.</summary>
public static class PackageManifestSerializer
{
    /// <summary>Serialize a manifest to a byte array.</summary>
    public static byte[] Serialize(PackageManifest manifest)
    {
        if (manifest == null) throw new ArgumentNullException(nameof(manifest));
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true);
        var w = new BundleBufferWriter(bw);

        // Magic + version.
        bw.Write(PackageManifestConsts.FileMagic);
        w.WriteVarInt32(PackageManifestConsts.FileVersion);

        // Build metadata.
        w.WriteString(manifest.PackageName);
        w.WriteString(manifest.PackageVersion);
        w.WriteString(manifest.PackageNote);
        w.WriteByte((byte)manifest.BuildTarget);
        w.WriteByte((byte)manifest.Compression);
        w.WriteString(manifest.BuildPipeline);

        // Flags.
        w.WriteBool(manifest.EnableAddressable);
        w.WriteBool(manifest.LocationToLower);
        w.WriteBool(manifest.IncludeAssetGuid);
        w.WriteByte((byte)manifest.OutputNameStyle);

        // Build a global directory interning table for asset paths (path components) to keep the
        // asset list compact. YooAsset uses a similar directory table; we keep a simple variant
        // that interns the full AssetPath's directory portion.
        // For simplicity and since asset paths are already modest in number, we store paths
        // directly; a future optimization can add interning without changing the version
        // (the version bump would gate it).

        // Bundles.
        w.WriteVarInt32(manifest.BundleList.Count);
        foreach (var b in manifest.BundleList)
        {
            w.WriteString(b.BundleName);
            w.WriteString(b.FileName);
            w.WriteBundleHash(b.BundleGuid);
            w.WriteBundleHash(b.FileHash);
            w.WriteUInt32(b.FileCrc);
            w.WriteVarInt64(b.FileSize);
            w.WriteBool(b.IsEncrypted);
            w.WriteBool(b.IsRawFile);
            w.WriteVarInt32Array(b.DependentBundleIds);
            w.WriteStringArray(b.Tags);
        }

        // Assets.
        w.WriteVarInt32(manifest.AssetList.Count);
        foreach (var a in manifest.AssetList)
        {
            w.WriteString(a.Address);
            w.WriteString(a.AssetPath);
            w.WriteGuid(a.AssetGuid);
            w.WriteVarInt32(a.BundleId);
            w.WriteVarInt32Array(a.DependentBundleIds);
            w.WriteStringArray(a.Tags);
        }

        return ms.ToArray();
    }

    /// <summary>Serialize and write to a file.</summary>
    public static void SerializeToFile(PackageManifest manifest, string path)
    {
        var bytes = Serialize(manifest);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Deserialize a manifest from bytes. Returns null if the magic/version is wrong.</summary>
    public static PackageManifest? Deserialize(byte[] bytes)
    {
        if (bytes == null || bytes.Length < PackageManifestConsts.MinFileSize) return null;
        using var ms = new MemoryStream(bytes, writable: false);
        return Deserialize(ms);
    }

    /// <summary>Deserialize a manifest from a stream.</summary>
    public static PackageManifest? Deserialize(Stream input)
    {
        var br = new BinaryReader(input, System.Text.Encoding.UTF8, leaveOpen: true);
        uint magic = br.ReadUInt32();
        if (magic != PackageManifestConsts.FileMagic) return null;

        var r = new BundleBufferReader(br);
        int version = r.ReadVarInt32();
        if (version != PackageManifestConsts.FileVersion) return null;

        var manifest = new PackageManifest
        {
            FileVersion = version,
            PackageName = r.ReadString(),
            PackageVersion = r.ReadString(),
            PackageNote = r.ReadString(),
            BuildTarget = (AssetBundlePlatform)r.ReadByte(),
            Compression = (BundleCompressionMode)r.ReadByte(),
            BuildPipeline = r.ReadString(),
            EnableAddressable = r.ReadBool(),
            LocationToLower = r.ReadBool(),
            IncludeAssetGuid = r.ReadBool(),
            OutputNameStyle = (ManifestFileNameStyle)r.ReadByte(),
        };

        int bundleCount = r.ReadVarInt32();
        manifest.BundleList.Capacity = bundleCount;
        for (int i = 0; i < bundleCount; i++)
        {
            manifest.BundleList.Add(new BundleInfo
            {
                BundleName = r.ReadString(),
                FileName = r.ReadString(),
                BundleGuid = r.ReadBundleHash(),
                FileHash = r.ReadBundleHash(),
                FileCrc = r.ReadUInt32(),
                FileSize = r.ReadVarInt64(),
                IsEncrypted = r.ReadBool(),
                IsRawFile = r.ReadBool(),
                DependentBundleIds = r.ReadVarInt32Array(),
                Tags = r.ReadStringArray(),
            });
        }

        int assetCount = r.ReadVarInt32();
        manifest.AssetList.Capacity = assetCount;
        for (int i = 0; i < assetCount; i++)
        {
            manifest.AssetList.Add(new ManifestAsset
            {
                Address = r.ReadString(),
                AssetPath = r.ReadString(),
                AssetGuid = r.ReadGuid(),
                BundleId = r.ReadVarInt32(),
                DependentBundleIds = r.ReadVarInt32Array(),
                Tags = r.ReadStringArray(),
            });
        }

        manifest.BuildIndexes();
        return manifest;
    }

    /// <summary>Deserialize a manifest from a file.</summary>
    public static PackageManifest? DeserializeFromFile(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Deserialize(fs);
    }
}
