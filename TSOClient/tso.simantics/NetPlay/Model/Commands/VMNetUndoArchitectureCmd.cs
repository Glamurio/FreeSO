using FSO.SimAntics.Model;
using System;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// Puts the walls, floors, terrain and roof back to an earlier state (Simitone undo and redo, TS1 only), with the
    /// money moved the other way: what the change cost is paid back exactly, and redoing it charges it again. The
    /// client keeps the snapshots and only offers this while it is in buy/build mode.
    /// </summary>
    public class VMNetUndoArchitectureCmd : VMNetCommandBodyAbstract
    {
        /// <summary>VMArchitecture.Snapshot() of the state to go back to.</summary>
        public byte[] Snapshot;
        /// <summary>Positive: the player pays this (redo of a purchase). Negative: the player is paid back.</summary>
        public int Cost;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1 || Snapshot == null || Snapshot.Length == 0) return false;
            if (Cost > 0 && vm.TS1State?.CurrentFamily != null && vm.TS1State.CurrentFamily.Budget < Cost) return false;

            vm.Context.Architecture.RestoreSnapshot(Snapshot);

            if (Cost != 0)
            {
                uint source = (Cost > 0) ? ActorUID : uint.MaxValue;
                uint target = (Cost > 0) ? uint.MaxValue : ActorUID;
                vm.GlobalLink?.PerformTransaction(vm, false, source, target, Math.Abs(Cost),
                    (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) => { });
            }
            vm.SignalChatEvent(new VMChatEvent(caller, VMChatEventType.Arch,
                caller?.Name ?? "Unknown", vm.GetUserIP(caller?.PersistID ?? 0), Cost > 0 ? "redid a build change" : "undid a build change"));
            return true;
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Cost);
            writer.Write(Snapshot?.Length ?? 0);
            if (Snapshot != null) writer.Write(Snapshot);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Cost = reader.ReadInt32();
            var length = reader.ReadInt32();
            Snapshot = reader.ReadBytes(length);
        }
        #endregion
    }
}
