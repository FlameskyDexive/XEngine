// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.InputSystem;
using XEngine.Runtime;

namespace XEngine.InputSystem.Editor;

/// <summary>
/// Imports .inputactions files (zonezero M4). Unity's JSON schema (InputSystem 1.x — the
/// format the ZZZ reference project ships) becomes an <see cref="InputActionAsset"/> with one
/// engine InputActionMap sub-asset per Unity map; files serialized in the engine's own Echo
/// format still import as plain InputActionMap main assets. This importer outranks the
/// engine's Echo-only InputActionMapImporter for the extension and delegates that case
/// (importer selection is winner-takes-all per extension, no fallback chain).
/// </summary>
[ImporterFor(".inputactions", Priority = 100)]
public sealed class UnityInputActionsImporter : AssetImporter
{
    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        try
        {
            string text = File.ReadAllText(ctx.AbsolutePath);
            if (UnityInputActionsJson.IsUnityJson(text))
            {
                var warnings = new List<string>();
                InputActionAsset parsed = UnityInputActionsJson.Parse(text, warnings);
                foreach (string warning in warnings)
                    Debug.LogWarning($"[InputActions] '{Path.GetFileName(ctx.AbsolutePath)}': {warning}");

                // Register maps as sub-assets FIRST (AddSubAsset assigns their deterministic
                // GUIDs immediately), then rebuild the asset's references so they serialize
                // as guid-backed AssetRefs that survive save/reload.
                var maps = new List<Runtime.InputActionMap>();
                foreach (AssetRef<Runtime.InputActionMap> reference in parsed.Maps)
                {
                    Runtime.InputActionMap? map = reference.Res;
                    if (map == null) continue;
                    ctx.AddSubAsset(map.Name, map);
                    maps.Add(map);
                }
                var asset = new InputActionAsset { Name = parsed.Name };
                foreach (string scheme in parsed.ControlSchemes)
                    asset.AddControlScheme(scheme);
                foreach (Runtime.InputActionMap map in maps)
                    asset.AddMap(map);
                ctx.SetMainAsset(asset);
                return true;
            }

            // Engine Echo format: legacy single-map asset, same as the previous importer.
            var echo = EchoObject.ReadFromString(text);
            var echoMap = Serializer.Deserialize<InputActionMap>(echo);
            if (echoMap != null)
            {
                ctx.SetMainAsset(echoMap);
                return true;
            }
            Debug.LogError($"Failed to import input actions (neither Unity JSON nor Echo): {ctx.AbsolutePath}");
            return false;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to import input actions: {ctx.AbsolutePath}\n{ex.Message}");
            return false;
        }
    }
}
