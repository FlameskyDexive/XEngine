# 2D Feature

`com.xengine.feature.2d` **0.1.0** is an official optional meta-package (`installByDefault: false`).
Package Manager lists it as Available until Install copies it into `Library/PackageCache` and
pulls every first-party 2D subpackage. Each subpackage can still be installed and upgraded
independently.

Core `com.xengine.2d.common` and `com.xengine.2d.sprite` stay built-in and auto-mount. Animation,
Tilemap, Tilemap Extras, SpriteShape, Pixel Perfect, PSD/Aseprite importers, and 2D Tooling stay
unmounted until this feature package or the individual package is installed.

Core Sprite, Renderer2D, and Physics2D remain in `XEngine.Runtime`. Advanced runtime components
and editor tools live in the corresponding package Runtime/Editor assemblies.

SpriteShape is included: add a `SpriteShapeController`, assign a `SpriteShapeProfile`, and call
`Bake()` / `BakeCollider()` for a headless fill, edge strips, and polygon collider.
