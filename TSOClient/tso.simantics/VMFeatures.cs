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
        /// Record route events and the reason every queued action ended (see Diagnostics.VMDiagnostics).
        /// Does not change behaviour.
        /// </summary>
        public static bool Diagnostics = true;
    }
}
