using Microsoft.Xna.Framework;

namespace FSO.SimAntics.Engine.Routing
{
    /// <summary>
    /// A straight path segment walked sideways through a pinch between two objects (see VMShimmyPlanner).
    /// While on this segment the avatar faces Facing, plays the standing-adjust animation for its step direction,
    /// and does not collide with ObjectA/ObjectB (or their multitile groups).
    /// </summary>
    public class VMPathShimmySegment : VMPathLineSegment
    {
        public short ObjectA;
        public short ObjectB;
        /// <summary>Heading the avatar faces while side-stepping (SimAntics radians).</summary>
        public float Facing;
        /// <summary>True if the avatar moves towards its own right (standing-adjust-e), false for left (-w).</summary>
        public bool StepRight;

        public VMPathShimmySegment(Point from, Point to, short objectA, short objectB, float facing, bool stepRight) : base(from, to)
        {
            ObjectA = objectA;
            ObjectB = objectB;
            Facing = facing;
            StepRight = stepRight;
        }
    }
}
