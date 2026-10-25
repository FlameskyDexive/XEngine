# 2D SpriteShape

Spline profiles, fill/edge/corner sprites, and collider generation for 2D shapes.

This is an official optional package (`installByDefault: false`). Package Manager lists it as
Available until Install copies it into `Library/PackageCache`. It is part of
`com.xengine.feature.2d`. Install the feature package to get every 2D subpackage, or install this
package independently when you only need 2D SpriteShape.

Runtime types live in `XEngine.Runtime`:

- `SpriteShapeController` holds an open or closed spline of `SpriteShapePoint`s.
- `SpriteShapeProfile` maps tangent angles to fill/edge/corner sprite ids (strings, so bake is headless).
- `Bake()` tessellates a closed spline with LibTessDotNet, caches the fill by content hash, and
  emits CPU edge quads plus optional corners from the profile.
- `BakeCollider()` copies the spline onto a `PolygonCollider2D` on the same GameObject.

GPU sprite meshes from those ids are a later slice. Open splines skip fill and still bake edges.
