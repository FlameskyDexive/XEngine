# 2D Aseprite Importer

AsepriteDotNet importer for layers, cels, tags, slices, tilesets, and tilemaps.
The engine `AsepriteImporter` (Editor) also emits a packed `Texture2D` and one `Sprite` per opaque frame.

This is an official optional package (`installByDefault: false`). Package Manager lists it as
Available until Install copies it into `Library/PackageCache`. It is part of
`com.xengine.feature.2d`. Install the feature package to get every 2D subpackage, or install this
package independently when you only need 2D Aseprite Importer.

Runtime Sprite, Renderer2D, PixelPerfectCamera, and Physics2D stay in `XEngine.Runtime`. This
package adds the optional workflow layer on top of that core.