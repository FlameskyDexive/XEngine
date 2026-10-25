# Changelog

All notable changes to this package are documented here. Format based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2026-09-11

### Added

- Optional package skeleton (Runtime + Editor asmdefs, docs, samples, tests).
- Interpreted-assembly classification: `interpretedAssembly` flag on assembly
  definitions; interpreted units compile to `Library/ILInterpreter/Modules/`.
- Dependency-direction validation (host assemblies referencing interpreted
  assemblies fail the compile plan).
- CLR-loading, auto-reference and player/AOT-collection exclusion for
  interpreted assemblies; flag is inert while the package is not installed.
