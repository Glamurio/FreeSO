using FSO.SimAntics.Engine.Routing;

namespace FSO.SimAntics
{
    /// <summary>
    /// Switches for Simitone's behaviour changes to the simulation. Every change that departs from the legacy
    /// FreeSO/TS1 behaviour is guarded by one of these, so it can be turned off from the settings dialog and compared
    /// against the original.
    ///
    /// These are per-process statics, set by the client from its settings at start-up and whenever a setting changes.
    /// They are not saved with lots. Netplay clients must use the same values, as most of them change simulation results.
    /// </summary>
    public static class VMFeatures
    {
        /// <summary>Sims and pets side-step through narrow gaps between objects (decisions/0001-shimmy).</summary>
        public static bool Shimmy
        {
            get { return VMShimmyPlanner.Enabled; }
            set { VMShimmyPlanner.Enabled = value; }
        }

        /// <summary>
        /// Routing bug fixes (roadmap 1a):
        /// - the wait timeout and collision retries are given to each goal, instead of once per route request;
        /// - a timed-out wait no longer discards every remaining goal on consecutive ticks;
        /// - moving on to the next goal restarts goal handling (doors, chairs) instead of walking straight at it;
        /// - "wall in the way" can be reported, and route failure blames the object actually in the way, not the Sim;
        /// - the RouteResult person data is cleared when a route succeeds.
        /// </summary>
        public static bool RoutingFixes = true;

        /// <summary>
        /// Action queue bug fixes:
        /// - TS1 interactions that allow dogs can be cancelled while queued (their TTAB flag shares a bit with TSO's
        ///   "must run", which made cancelling them cancel the running action instead);
        /// - queue skipping after a higher priority action no longer loops forever on items it can't remove.
        /// </summary>
        public static bool QueueFixes = true;

        /// <summary>
        /// Dynamic obstacle handling (roadmap 1b):
        /// - Sims standing still are planned around instead of walked into (falls back to the old behaviour, walking up
        ///   and asking them to move, when there is no way around);
        /// - a route is planned again when an object in the room is placed, moved or removed while walking;
        /// - a door that failed is tried once more after a short wait when there is no other way.
        /// </summary>
        public static bool DynamicObstacles = true;

        /// <summary>
        /// A Sim whose position overlaps an object (after build mode changes, or an animation that ended inside its
        /// clearance) steps out to the nearest free spot instead of failing every route. (roadmap 1c)
        /// </summary>
        public static bool Unstick = true;

        /// <summary>
        /// Record route events and the reason every queued action ended (see Diagnostics.VMDiagnostics).
        /// Does not change behaviour.
        /// </summary>
        public static bool Diagnostics = true;
    }
}
