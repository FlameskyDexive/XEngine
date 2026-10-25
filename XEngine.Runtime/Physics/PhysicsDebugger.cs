// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Jitter2.Collision.Shapes;
using Jitter2.Dynamics;
using Jitter2.LinearMath;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// CPU snapshot of the current 3D physics world for the Rendering Debugger overlay.
/// </summary>
public readonly struct PhysicsDebugSnapshot
{
    public readonly int Bodies;
    public readonly int ActiveBodies;
    public readonly int SleepingBodies;
    public readonly int Shapes;
    public readonly int Triggers;
    public readonly int SensorOverlaps;
    public readonly int Contacts;

    public PhysicsDebugSnapshot(
        int bodies, int activeBodies, int sleepingBodies,
        int shapes, int triggers, int sensorOverlaps, int contacts)
    {
        Bodies = bodies;
        ActiveBodies = activeBodies;
        SleepingBodies = sleepingBodies;
        Shapes = shapes;
        Triggers = triggers;
        SensorOverlaps = sensorOverlaps;
        Contacts = contacts;
    }

    public static PhysicsDebugSnapshot Empty => new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Scene-view overlay and snapshot for 3D physics. Toggles live on
/// <c>Window / Analysis / Rendering Debugger</c>; drawing is recorded into
/// <see cref="Debug"/> the same way component gizmos are.
/// </summary>
public static class PhysicsDebugger
{
    /// <summary>Draw every shape AABB. Triggers are cyan, solid colliders green.</summary>
    public static bool DrawColliders { get; set; }

    /// <summary>Draw contact points and normals in magenta.</summary>
    public static bool DrawContacts { get; set; }

    /// <summary>Draw sleeping body AABBs in gray (and recolour collider overlay when both are on).</summary>
    public static bool DrawSleeping { get; set; }

    /// <summary>True when the editor should record extra physics gizmos this frame.</summary>
    public static bool AnyOverlayEnabled => DrawColliders || DrawContacts || DrawSleeping;

    /// <summary>
    /// Counts bodies, shapes, triggers, sensor overlaps and unique contact arbiters.
    /// The world's pinned <c>NullBody</c> is omitted.
    /// </summary>
    public static PhysicsDebugSnapshot Capture(PhysicsWorld? physics = null)
    {
        physics ??= Scene.Current?.Physics;
        if (physics?.World == null)
            return PhysicsDebugSnapshot.Empty;

        Jitter2.World world = physics.World;
        RigidBody nullBody = world.NullBody;
        int bodies = 0, active = 0, sleeping = 0, shapes = 0, triggers = 0, contacts = 0;

        ReadOnlySpan<RigidBody> all = world.RigidBodies.Elements;
        for (int i = 0; i < all.Length; i++)
        {
            RigidBody body = all[i];
            if (body == null || body == nullBody)
                continue;

            bodies++;
            if (body.IsActive) active++;
            else sleeping++;

            var bodyShapes = body.Shapes;
            for (int s = 0; s < bodyShapes.Count; s++)
            {
                RigidBodyShape shape = bodyShapes[s];
                shapes++;
                if (shape.IsSensor) triggers++;
            }

            foreach (Arbiter arbiter in body.Contacts)
            {
                if (arbiter.Body1 == body)
                    contacts++;
            }
        }

        return new PhysicsDebugSnapshot(
            bodies, active, sleeping, shapes, triggers, world.SensorOverlaps.Count, contacts);
    }

    /// <summary>
    /// Records overlay gizmos for <paramref name="physics"/> into <see cref="Debug"/>.
    /// No-op when every toggle is off.
    /// </summary>
    public static void Draw(PhysicsWorld? physics = null)
    {
        if (!AnyOverlayEnabled)
            return;

        physics ??= Scene.Current?.Physics;
        if (physics?.World == null)
            return;

        Jitter2.World world = physics.World;
        RigidBody nullBody = world.NullBody;
        ReadOnlySpan<RigidBody> all = world.RigidBodies.Elements;
        for (int i = 0; i < all.Length; i++)
        {
            RigidBody body = all[i];
            if (body == null || body == nullBody)
                continue;

            bool sleeping = !body.IsActive;
            if (DrawColliders || (DrawSleeping && sleeping))
                DrawBodyAabbs(body, sleeping);

            if (DrawContacts)
                DrawBodyContacts(body);
        }
    }

    internal static void ResetForTests()
    {
        DrawColliders = false;
        DrawContacts = false;
        DrawSleeping = false;
    }

    private static void DrawBodyAabbs(RigidBody body, bool sleeping)
    {
        var bodyShapes = body.Shapes;
        for (int s = 0; s < bodyShapes.Count; s++)
        {
            RigidBodyShape shape = bodyShapes[s];
            Color color;
            if (sleeping && DrawSleeping)
                color = Color.Gray;
            else if (shape.IsSensor)
                color = Color.Cyan;
            else
                color = Color.Green;

            DrawAabb(shape.WorldBoundingBox, color);
        }
    }

    private static void DrawBodyContacts(RigidBody body)
    {
        foreach (Arbiter arbiter in body.Contacts)
        {
            if (arbiter.Body1 != body || arbiter.Handle.IsZero)
                continue;

            ref ContactData data = ref arbiter.Handle.Data;
            DrawContact(body, in data.Contact0, (data.UsageMask & ContactData.MaskContact0) != 0);
            DrawContact(body, in data.Contact1, (data.UsageMask & ContactData.MaskContact1) != 0);
            DrawContact(body, in data.Contact2, (data.UsageMask & ContactData.MaskContact2) != 0);
            DrawContact(body, in data.Contact3, (data.UsageMask & ContactData.MaskContact3) != 0);
        }
    }

    private static void DrawContact(RigidBody body, in ContactData.Contact contact, bool active)
    {
        if (!active)
            return;

        JVector p = body.Position + contact.RelativePosition1;
        JVector n = contact.Normal;
        var from = new Float3(p.X, p.Y, p.Z);
        var to = new Float3(p.X + n.X * 0.25f, p.Y + n.Y * 0.25f, p.Z + n.Z * 0.25f);
        Debug.DrawLine(from, to, Color.Magenta);
    }

    private static void DrawAabb(in JBoundingBox box, Color color)
    {
        JVector min = box.Min;
        JVector max = box.Max;
        var center = new Float3(
            (min.X + max.X) * 0.5f,
            (min.Y + max.Y) * 0.5f,
            (min.Z + max.Z) * 0.5f);
        var half = new Float3(
            (max.X - min.X) * 0.5f,
            (max.Y - min.Y) * 0.5f,
            (max.Z - min.Z) * 0.5f);
        Debug.DrawWireCube(center, half, color);
    }
}
