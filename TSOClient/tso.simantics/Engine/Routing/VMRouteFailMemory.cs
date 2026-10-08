using System.Collections.Generic;
using System.Linq;

namespace FSO.SimAntics.Engine.Routing
{
    /// <summary>
    /// Objects an avatar recently failed to route to, so that object selection and free will can prefer others for a
    /// while (VMFeatures.ObjectSelection, VMFeatures.AutonomyFixes). Keyed by object ID; a multitile object is
    /// remembered by all its parts.
    ///
    /// Transient like VMEntity.Diagnostics: not saved, so it starts empty after loading a lot. Recording it never
    /// changes behaviour on its own; only the consumers above read it.
    /// </summary>
    public class VMRouteFailMemory
    {
        /// <summary>One Sim hour (TS1 runs 30 ticks per Sim minute).</summary>
        public const uint DURATION = 30 * 60;
        private const int MAX_ENTRIES = 32;

        private Dictionary<short, uint> Expiry = new Dictionary<short, uint>();

        public int Count => Expiry.Count;

        public void Add(IEnumerable<short> objectIDs, uint now)
        {
            foreach (var id in objectIDs) Expiry[id] = now + DURATION;
            if (Expiry.Count > MAX_ENTRIES) Prune(now);
            //still too many (unlikely): forget the ones that expire first.
            while (Expiry.Count > MAX_ENTRIES) Expiry.Remove(Expiry.OrderBy(x => x.Value).First().Key);
        }

        public bool Contains(short objectID, uint now)
        {
            uint until;
            if (!Expiry.TryGetValue(objectID, out until)) return false;
            if (until > now) return true;
            Expiry.Remove(objectID);
            return false;
        }

        public void Prune(uint now)
        {
            foreach (var id in Expiry.Where(x => x.Value <= now).Select(x => x.Key).ToList()) Expiry.Remove(id);
        }

        public void Clear()
        {
            Expiry.Clear();
        }

        /// <summary>Records a failed route of the avatar to the target (all its parts).</summary>
        public static void Record(VMEntity avatar, VMEntity target)
        {
            if (avatar == null || target == null || target == avatar || target.Dead) return;
            var now = avatar.Thread?.Context?.VM?.Scheduler?.CurrentTickID ?? 0;
            if (avatar.RouteFailMemory == null) avatar.RouteFailMemory = new VMRouteFailMemory();
            var parts = target.MultitileGroup?.Objects;
            if (parts != null && parts.Count > 0) avatar.RouteFailMemory.Add(parts.Select(x => x.ObjectID), now);
            else avatar.RouteFailMemory.Add(new short[] { target.ObjectID }, now);
        }

        /// <summary>True if the avatar failed to route to this object within the last Sim hour.</summary>
        public static bool RecentlyFailed(VMEntity avatar, VMEntity obj)
        {
            var memory = avatar?.RouteFailMemory;
            if (memory == null || obj == null) return false;
            return memory.Contains(obj.ObjectID, avatar.Thread?.Context?.VM?.Scheduler?.CurrentTickID ?? 0);
        }
    }

    /// <summary>
    /// Which rooms an avatar can get to through doors and stairs (room portals), ignoring whether anything is in the
    /// way. Used to tell objects that can't be reached at all (walled in, upstairs without stairs) from ones that are
    /// merely far away (VMFeatures.ObjectSelection, VMFeatures.AutonomyFixes).
    /// </summary>
    public static class VMReachability
    {
        /// <summary>
        /// Rooms reachable from the start room by following room portals. Room 0 is not a room; a start room of 0 or
        /// out of range gives null, meaning "unknown, treat everything as reachable".
        /// </summary>
        public static HashSet<ushort> Flood(ushort start, int roomCount, System.Func<ushort, IEnumerable<ushort>> neighbours)
        {
            if (start == 0 || start >= roomCount) return null;
            var rooms = new HashSet<ushort>() { start };
            var open = new Queue<ushort>();
            open.Enqueue(start);
            while (open.Count > 0)
            {
                var room = open.Dequeue();
                var next = neighbours(room);
                if (next == null) continue;
                foreach (var target in next)
                {
                    if (target == 0 || target >= roomCount) continue;
                    if (rooms.Add(target)) open.Enqueue(target);
                }
            }
            return rooms;
        }

        public static HashSet<ushort> RoomsReachableBy(VMEntity avatar, VMContext context)
        {
            var info = context.RoomInfo;
            if (info == null) return null;
            return Flood(context.GetObjectRoom(avatar), info.Length,
                (room) => info[room].Portals?.Select(x => x.TargetRoom));
        }

        /// <summary>False only if the object is in a known room that is not in the reachable set.</summary>
        public static bool CanReach(HashSet<ushort> reachable, VMEntity obj, VMContext context)
        {
            if (reachable == null) return true;
            var room = context.GetObjectRoom(obj);
            if (room == 0 || room >= context.RoomInfo.Length) return true;
            return reachable.Contains(room);
        }
    }
}
