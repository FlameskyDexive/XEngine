---
name: edit-volume
description: Patch a Universal VolumeProfile (.volumeprofile) via volume_patch, or a scene Volume component via scene_component_patch. Journaled, Echo text.
---

# edit-volume

Use when an AI needs to change blended volume settings. Profiles are `.volumeprofile` Echo assets; per-instance knobs (global vs local box, weight, blend distance) are on the scene `Volume` component.

## Patch a profile
```bash
xengine-cli volume-inspect --project <root> --volume Assets/Settings/Default.volumeprofile
# note contentHash

xengine-cli volume-patch --project <root> --volume Assets/Settings/Default.volumeprofile \
  --patches '[{"path":"Components/$values/0/GlobalDensity","value":"0.02"},{"path":"Components/$values/0/OverrideDensity","value":"true"}]' \
  --expected-before-hash <hash> \
  --idempotency-key <key> --intent "raise blended volumetric fog density"
```

Paths match [inspect-volume](../inspect-volume/SKILL.md). Same drift protection / idempotency as `asset_data_patch`.

## Patch a scene Volume
```bash
xengine-cli scene-component-patch --project <root> --scene <path> \
  --object <GameObject-identifier> --type Volume \
  --patches '[{"path":"IsGlobal","value":"false"},{"path":"Weight","value":"0.5"},{"path":"BlendDistance","value":"2"},{"path":"Priority","value":"1"}]' \
  --idempotency-key <key> --intent "local half-weight volume"
```

Bind a profile with `Profile/AssetID` = the `.volumeprofile` GUID from `asset_inspect`. Local volumes use `Transform` scale as AABB half-extents.

After patching, the running editor must reload the asset (or `runtime_refresh`) before Play mode picks up values. Call `VolumeManager.Instance.Update(cameraPosition)` (or render) to refresh the blended stack.

Or MCP: `volume_patch` / `scene_component_patch`.
