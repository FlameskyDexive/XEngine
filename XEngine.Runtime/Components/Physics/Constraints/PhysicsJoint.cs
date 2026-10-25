// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Jitter2.Dynamics.Constraints;

namespace XEngine.Runtime;

/// <summary>
/// Base class for composite joints that are made up of multiple constraints.
/// </summary>
public abstract class PhysicsJoint : PhysicsConstraint
{
    protected Joint joint;
    private ConstraintBag _bag;

    protected override Constraint GetConstraint()
    {
        // Joints are composed of multiple constraints, return null for base implementation
        return null;
    }

    /// <summary>
    /// Gets the underlying Jitter2 joint.
    /// </summary>
    public Joint GetJoint() => joint;

    /// <summary>
    /// Registers an atomic constraint as part of this joint so <see cref="Joint.Remove"/>
    /// tears the whole set down together.
    /// </summary>
    protected void AddPart(Constraint constraint)
    {
        _bag ??= new ConstraintBag();
        _bag.Add(constraint);
        joint = _bag;
    }

    protected override void DestroyConstraint()
    {
        if (joint != null)
        {
            joint.Remove();
            joint = null;
        }
        _bag = null;
    }

    public override void DrawGizmos()
    {
        // TODO DrawGizmos
    }

    private sealed class ConstraintBag : Joint
    {
        public void Add(Constraint constraint) => Register(constraint);
    }
}
