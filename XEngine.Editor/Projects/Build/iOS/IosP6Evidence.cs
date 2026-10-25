// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// P6 placeholders for physical-device evidence that this export did not collect.
/// Host metadata describes the current machine; measurements remain explicitly not run.
/// </summary>
internal static class IosP6Evidence
{
    internal const string StartupFileName = "ios-startup.json";
    internal const string DeviceMatrixFileName = "ios-device-matrix.json";

    internal const string StartupSkipReason =
        "No physical-device cold-start-to-first-present samples were recorded. A signed iOS device app and connected device are required.";

    internal const string DeviceMatrixSkipReason =
        "No physical-device lifecycle acceptance cycles were recorded. Simulator runs do not satisfy the two consecutive device/release-cycle gate.";

    internal static void WriteStartupSkip(string exportRoot) =>
        Write(
            exportRoot,
            StartupFileName,
            new
            {
                schemaVersion = 1,
                hostOSDescription = RuntimeInformation.OSDescription,
                status = "not-run",
                metric = "cold-start-to-first-present",
                hostCanMeasure = false,
                sampleCount = 0,
                reason = StartupSkipReason,
                p50Ms = (double?)null,
                p95Ms = (double?)null,
            });

    internal static void WriteDeviceMatrixSkip(string exportRoot) =>
        Write(
            exportRoot,
            DeviceMatrixFileName,
            new
            {
                schemaVersion = 1,
                hostOSDescription = RuntimeInformation.OSDescription,
                status = "not-run",
                supported = false,
                hostCanRun = false,
                physicalDeviceVerified = false,
                cyclesPassed = 0,
                reason = DeviceMatrixSkipReason,
            });

    private static void Write(string exportRoot, string fileName, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        string directory = Path.Combine(exportRoot, "verification");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, fileName),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
