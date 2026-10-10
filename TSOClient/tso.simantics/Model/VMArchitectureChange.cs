namespace FSO.SimAntics.Model
{
    /// <summary>
    /// A change a player's architecture command (walls, floors, terrain) or roof command just made, reported to
    /// VM.OnArchitectureChange so the client can offer undo. Not part of the simulation state; does not affect determinism.
    /// </summary>
    public class VMArchitectureChange
    {
        /// <summary>Walls, floors, terrain and roof as they were before the change (VMArchitecture.Snapshot). Null for roof changes.</summary>
        public byte[] Before;
        /// <summary>What the change cost (negative if it paid the player back).</summary>
        public int Cost;
        public uint ActorUID;

        public bool Roof;
        public float OldPitch, NewPitch;
        public uint OldStyle, NewStyle;
    }
}
