using FSO.SimAntics.Model;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// Takes back an object bought in this build/buy session (Simitone undo, TS1 only). Unlike VMNetDeleteObjectCmd,
    /// which refunds the object's current value (80% of the price for a new TS1 object, as wear starts at 20), this
    /// refunds exactly what was paid (InitialPrice), so undoing a purchase costs nothing. The client keeps the undo
    /// history only until it leaves buy/build mode, so a used object can't be returned this way later.
    /// </summary>
    public class VMNetUndoBuyCmd : VMNetCommandBodyAbstract
    {
        public short ObjectID;
        /// <summary>The bought object's GUID, so a different object that took over the ID is never removed.</summary>
        public uint GUID;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var obj = vm.GetObjectById(ObjectID);
            if (!vm.TS1 || obj == null || obj is VMAvatar || obj.MultitileGroup.GUID != GUID) return false;
            var value = obj.MultitileGroup.InitialPrice;

            obj.ExecuteEntryPoint(12, vm.Context, true); //user pickup, as for a delete
            obj.Delete(true, vm.Context);

            vm.GlobalLink?.PerformTransaction(vm, false, uint.MaxValue, caller?.PersistID ?? uint.MaxValue, value,
                (bool success, int transferAmount, uint uid1, uint budget1, uint uid2, uint budget2) => { });
            vm.SignalChatEvent(new VMChatEvent(caller, VMChatEventType.Arch,
                caller?.Name ?? "Unknown", vm.GetUserIP(caller?.PersistID ?? 0), "took back " + obj.ToString()));
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            var obj = vm.GetObjectById(ObjectID);
            if (obj == null || obj is VMAvatar || obj.MultitileGroup.GUID != GUID) return false;
            if (vm.Context.Cheats.MoveObjects) return true;
            if (obj.IsUserMovable(vm.Context, true) != VMPlacementError.Success) return false;
            return (((VMGameObject)obj).Disabled & VMGameObjectDisableFlags.TransactionIncomplete) == 0;
        }

        #region VMSerializable Members
        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(ObjectID);
            writer.Write(GUID);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            ObjectID = reader.ReadInt16();
            GUID = reader.ReadUInt32();
        }
        #endregion
    }
}
