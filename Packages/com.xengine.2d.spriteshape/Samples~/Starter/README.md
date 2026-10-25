# SpriteShape Starter

Headless bake of a closed square (fill + collider) and an open ledge (edges only).

```csharp
var profile = new SpriteShapeProfile();
profile.AngleRanges.Add(new SpriteShapeAngleRange
{
    Start = -45f,
    End = 45f,
    EdgeSpriteId = "ground",
});

var closed = go.AddComponent<SpriteShapeController>();
closed.Closed = true;
closed.Profile = profile;
closed.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 0f) });
closed.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 0f) });
closed.Spline.Add(new SpriteShapePoint { Position = new Float2(1f, 1f) });
closed.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 1f) });
closed.Bake();
closed.BakeCollider();
// closed.FillIndices.Length == 6; PolygonCollider2D has four points.

var open = ledge.AddComponent<SpriteShapeController>();
open.Closed = false;
open.EdgeWidth = 0.2f;
open.Profile = profile;
open.Spline.Add(new SpriteShapePoint { Position = new Float2(0f, 0f) });
open.Spline.Add(new SpriteShapePoint { Position = new Float2(4f, 0f) });
open.Bake();
// open.FillIndices is empty; open.Edges has one ground strip.
```
