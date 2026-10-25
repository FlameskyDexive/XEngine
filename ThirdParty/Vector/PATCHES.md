# XEngine patches to Vector (math)

Baseline for diffing: Anthology tag `v2.8.6` (commit `b3ab895d`), path `Vector/` — see `UPSTREAM.txt`.
Retrieve any baseline file with `git show v2.8.6:Vector/<path>`.

## Status

- Was vendored at **v2.8.6**; **synced to v3.3.0 on 2026-08-31** (M1.3 of the prowl-sync plan).
- Upstream v2.8.6→v3.3.0 added `AnimationCurve.cs` / `Gradient.cs` / `Keyframe.cs` (the engine-side
  curve/gradient types upstream moved INTO Vector), CSG additions (`CSGBrushValidity`,
  `SurfaceTexSpace`, `GeometryGenerator.Planes`), and `GeometryGenerator`/`CSGScene` changes.
- Upstream did **not** modify any file XEngine had locally patched → the sync was purely additive.

## Local patches (all preserved; 9 files are XEngine-only additions)

### 1. `AABB.cs` — TransformAABB Arvo fast path

- **Motive**: upstream's 8-corner affine transform loop allocates and does 8 transforms; Arvo's
  method is allocation-free and branch-light — hot path for collider/particle bounds.
- **Shape of change**: replaces the body of `TransformAABB` with the Arvo method (same results,
  numerically equivalent for the affine cases the engine uses).

### 2. XEngine-only SIMD / math extension files (upstream-MISSING, keep on every sync)

`Vector/BatchMath.cs`, `Vector/FastMaths.cs`, `Vector/Internal/Float3LanePacks.cs`,
`Vector/Maths.VectorUtilities.cs`, `Vector/Matrices/FloatNonSquareMatrices.cs`,
`Vector/Transforms.cs`, `Vector/Vectors/BoolVectors.cs`, `Vector/Vectors/HalfVectors.cs`,
`Vector/Vectors/UnsignedVectors.cs`.
Benchmark evidence: `docs/benchmarks/2026-08-31-batchmath/` (Intrinsic route 0.07–0.11× scalar
at Count≥256, 0 B alloc); correctness: `Tests/Vector.Tests/MathAndBatchTests.cs` (XEngine-only).

### 3. Minor

- `Vector/Vector.csproj` (+2 local lines), `Tests/Vector.Tests/Vector.Tests.csproj` (+1),
  `OpenTKSample/GameWindow.cs` (1 line) — vendored-side tweaks, keep.

## Engine-side note (temporary aliases)

Vector's new `AnimationCurve`/`Gradient`/`GradientColorKey`/`GradientAlphaKey` collide with the
engine's own `XEngine.Runtime` copies (inherited from the fork). Until M3 deletes the engine
copies and adopts Vector's (upstream's end state), the affected files carry explicit
`using X = XEngine.Runtime.X;` aliases: `Animation/GenericCurveBinder.cs`, Editor
`AnimationRecording.cs` / `AnimationClipWindow*.cs` / `BuiltInPropertyEditors.cs` / `CurveField.cs`
/ `GradientField.cs`, `Packages/com.xengine.terrain/Editor/TerrainEditor.cs`,
`Samples/PhysicsCubes/Program.cs`, and two `XEngine.Editor.Test` animation test files.

## Verification (2026-08-31 sync)

- `Vector.Tests` (net10.0): **289/289 pass** (now includes upstream `AnimationCurveTests`,
  `GradientTests`, `CSGTests`).
- Full `XEngine.sln` build: **0 errors**.
