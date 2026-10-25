# npm preview and takedown

Wire name: `@inxep/voxelworld`. Engine name stays `com.xengine.voxelworld`.

## Publish a preview

1. Bump `package.json` version and `VoxelWorldRuntime.Version` together.
2. Update `CHANGELOG.md`.
3. `xengine-cli package-publish --package Packages/com.xengine.voxelworld --dry-run`
4. Confirm the tarball is `@inxep/voxelworld`, includes `THIRD_PARTY_ASSETS.md`, and excludes Dungeon Mason.
5. Large art sets: set `XENGINE_NPM_TIMEOUT_SECONDS` (for example `900`) before the real publish.
6. `xengine-cli package-publish --package Packages/com.xengine.voxelworld`
7. Verify with `xengine-cli package-info --name com.xengine.voxelworld`.

## 2026-09-15 dry-run

`xengine-cli --project <engine> --edition engine-dev package-publish --package Packages/com.xengine.voxelworld --dry-run`

```
registryName: @inxep/voxelworld
version: 0.1.0
tarball: inxep-voxelworld-0.1.0.tgz
tarballBytes: 36506
dryRun: true
uploaded: false
```

This workspace had no `XENGINE_NPM_TOKEN`. After a granular `@inxep` publish token is available, drop `--dry-run` and verify with `package-info --name com.xengine.voxelworld`. N-art cleared the last third-party texture; 0.1.0 was never live-published, so the next PUT can ship this clean tree without a version bump.

## Dry-run (2026-09-15)

```
xengine-cli --project <engine-repo> --edition engine-dev package-publish --package Packages/com.xengine.voxelworld --dry-run
```

Result: wire `@inxep/voxelworld` **0.1.0**, tarball `inxep-voxelworld-0.1.0.tgz` (36506 bytes). Live npmjs PUT waits for `XENGINE_NPM_TOKEN`.

## If rights holders object

npm versions are immutable. Do not rewrite a published tarball.

1. `npm deprecate @inxep/voxelworld@<ver> "preview withdrawn; use a later version without temporary art"`
2. If still inside the registry unpublish window (typically 72 hours): `npm unpublish @inxep/voxelworld@<ver>`
3. Ship a new version that removes every path in `THIRD_PARTY_ASSETS.md` (placeholders or N-art replacements)
4. Point `latest` at the clean version
