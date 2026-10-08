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
        /// Approach positions (roadmap 2): a chair someone is sitting in is not chosen as a destination
        /// (DestChairOccupied), and when a destination fails, the remaining ones are checked again before walking to the
        /// next (dropped if an object or a sitting Sim now takes them, tried last if a Sim stands there or is heading
        /// there). Only the content-provided positions are filtered and reordered; none are invented.
        /// </summary>
        public static bool ApproachPositions = true;

        /// <summary>
        /// Object selection (roadmap 4): "find best object for function" prefers objects in rooms the Sim can reach and
        /// objects the Sim has not just failed to route to. When no object passes, the original choice is made, so
        /// content always gets the same answer as before when there is no better one. Player commands are unchanged.
        /// </summary>
        public static bool ObjectSelection = true;

        /// <summary>
        /// Queue recovery (roadmap 5): when the object the running action uses is deleted, only that action (and its
        /// sub-actions) is dropped; the player's other queued actions are kept. Previously the Sim was reset with an empty
        /// queue.
        /// </summary>
        public static bool QueueRecovery = true;

        /// <summary>
        /// Free will fixes (roadmap 6): objects whose lockout count is running are skipped (as "find best object" already
        /// does), and objects in rooms the Sim can't reach or that it just failed to route to score much lower.
        /// </summary>
        public static bool AutonomyFixes = true;

        /// <summary>
        /// Free will balance fix (roadmap 6): an action with several pie menu entries was added to the candidate list
        /// once per entry (always as its first entry), multiplying its chance of being picked. Each action now counts
        /// once. Changes how often such actions are chosen, so it has its own switch.
        /// </summary>
        public static bool AutonomyCountOnce = true;

        /// <summary>
        /// Conversations (roadmap 10, partial): a Sim acting on free will waits for socialising Sims instead of shooing
        /// them out of the way (player-directed actions still shoo), and a Sim walking up to another Sim who moves away
        /// re-targets them (up to 3 times per route) instead of walking to where they were.
        /// </summary>
        public static bool Conversations = true;

        /// <summary>
        /// Record route events and the reason every queued action ended (see Diagnostics.VMDiagnostics).
        /// Does not change behaviour.
        /// </summary>
        public static bool Diagnostics = true;
    }
}
