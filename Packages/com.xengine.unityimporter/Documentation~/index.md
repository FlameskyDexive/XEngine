# com.xengine.unityimporter — Documentation

Shared Unity-asset direct-import layer for XEngine. Extracted from `com.xengine.genshin`
(M0 of the zonezero plan: `docs/superpowers/specs/2026-08-21-zonezero-combat-demo.md`)
so every demo package (genshin, zonezero, ...) builds on the same converters instead of
copying them.

## Package layout

| Path | Contents |
|---|---|
| `Editor/UnityYaml.cs` | Parser for Unity serialized files (`%YAML 1.1` + `--- !u!CLASSID &FILEID` documents) |
| `Editor/UnityAssetSniffer.cs` | Content sniffing so Unity-YAML files can claim extensions XEngine also owns natively |
| `Editor/UnityGuidMap.cs` | Resolves Unity GUIDs via the deterministic `unity-guid-map.json` sidecar (engine tooling data; Unity .meta files never enter the project) |
| `Editor/UnityAssetResolver.cs` | Scans a source Unity project's .meta files to build guid → path tables |
| `Editor/UnityMeshImporter.cs` / `UnityMeshReader.cs` | Unity `.mesh` m_VertexData stream decode (incl. the skinning-channel format-swap voting fallback), bind poses, submeshes, dual UV |
| `Editor/UnityPrefabImporter.cs` / `UnityPrefabReader.cs` | Unity `.prefab` → engine `Model`/GameObject hierarchy incl. SkinnedMeshRenderer; FBX mesh refs resolve to named sub-assets (no first-mesh fallback on multi-mesh files); `m_Materials` guids map to copied `.mat` files |
| `Editor/UnityMatImporter.cs` | Unity `.mat` → engine Material (Standard placeholder, or Unlit when the source GUID is URP Unlit; diffuse map wired) |
| `Editor/UnityTextureMetaAligner.cs` | Unity TextureImporter `.meta` → engine TextureImporter settings (sRGB / mipmaps / wrap / filter) |

All importers register with `[ImporterFor(".mesh/.prefab/.mat", Priority = 100)]` and
delegate engine-native (Echo) files to the stock importers, so both file flavors coexist
in one project.

## Design notes

- Unity GUIDs are 32-hex; XEngine uses `System.Guid`. The sidecar written by per-game
  asset copier menus maps Unity guid → engine guid deterministically (same input file →
  same engine guid), keeping re-imports stable.
- Importers log with the `[UnityImport]` prefix.
- The package is editor-only tooling; `Runtime/` carries just the identity marker.

## Consumers

- `com.xengine.genshin` — FernNPR → GenshinToon demo (original home of this code).
- `com.xengine.zonezero` — YSA Toon combat demo (FBX sub-asset resolution, .FBX.meta
  animation events, Unity .controller parsing are layered there and contributed back).
