# DotRecast (vendored)

C# port of Recast & Detour. Source: https://github.com/ikpil/DotRecast  
License: zlib (`LICENSE.txt`). See `NOTICE` and repo-root `THIRD_PARTY_NOTICES.md`.

Vendored modules:

- `DotRecast.Core`
- `DotRecast.Recast`
- `DotRecast.Detour`
- `DotRecast.Detour.TileCache`
- `DotRecast.Detour.Crowd`
- `DotRecast.Detour.Dynamic`

Not vendored: Recast.Toolset, Recast.Demo, Detour.Extras (A* importer pulls Newtonsoft.Json).

Refresh: `powershell -File ThirdParty/DotRecast/UpdateSources.ps1 <DotRecast-checkout>`
