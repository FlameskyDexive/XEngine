# Vendored spine-csharp

- Upstream: https://github.com/EsotericSoftware/spine-runtimes/tree/3.8/spine-csharp/src
- Branch: `3.8`
- Snapshot SHA: `8b4844bd4b193ba9e54487ed397a777993cbad56` (describe: `3.8.95-254-g8b4844bd4`)
- Local source: `D:\Engine\spine-runtimes` (spine-csharp on branch 3.8)
- License: Spine Runtimes License (see `LICENSE`)

`3.8.75` is not a runtime tag. Upstream `SkeletonJson` / `SkeletonBinary` reject that export version string. XEngine package 0.1.2 includes the local reader compatibility patch described below.

Local patch: `Skin.cs` qualifies `Spine.Collections.OrderedDictionary` so net10 does not bind `System.Collections.Generic.OrderedDictionary`.

## Local patches (IceyXEngine spine38 migration)

- `SkeletonBinary.cs` / `SkeletonJson.cs`: the upstream 3.8.75 version rejection
  (`Unsupported skeleton data...`) is lifted. The Icey port consumes skeletons converted
  by the Spine 3.8.75 editor CLI (SpineConverter2To3 pipeline, QA 94/94 runtimeOk), and
  the upstream rejection exists only to guard against an editor export bug that the
  conversion pipeline's QA already screens for (empty frames / missing-texture magenta).
