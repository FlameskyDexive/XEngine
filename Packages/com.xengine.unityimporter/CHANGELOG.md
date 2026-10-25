# Changelog

All notable changes to this package are documented here.

## [0.1.0] - 2026-08-21

### Added

- Package scaffold extracted from `com.xengine.genshin` (zonezero plan M0): UnityYaml
  parser, UnityAssetSniffer, UnityGuidMap sidecar resolution, UnityAssetResolver, and
  the `.mesh`/`.prefab`/`.mat` Unity-YAML importers now live here; `com.xengine.genshin`
  depends on this package instead of carrying its own copy.
- M1: `UnityPrefabImporter` resolves FBX mesh sub-assets by skinned-node name (multi-mesh
  FBXs no longer collapse onto the first mesh); `UnityMatImporter` maps URP Unlit shader GUID
  `650dd9526735d5b46b79224bc6e94025` to Default/Unlit (everything else stays Standard
  placeholder) and wires `_BaseMap`/`_MainTex` onto Default/Standard; `UnityTextureMetaAligner`
  is the shared Unity-meta → engine TextureImporter settings helper used by genshin/zonezero
  copiers. Prefab SkinnedMeshRenderers add typed Material dependencies so copied PNG `.mat`
  files (not FBX-embedded mats) are what the character actually draws.

### Changed

- `UnityPrefabImporter` v7: the short-lived Clay-axis rotation replacement on FBX-backed
  skinned nodes (v6) is reverted — TRS is a straight pass-through again. Zonezero switched
  to an FBX-first pipeline (native prefabs generated from the engine FBX Model instantiate)
  and no longer routes character prefabs through this importer; genshin, the remaining
  consumer, expects Unity rotations untouched.
