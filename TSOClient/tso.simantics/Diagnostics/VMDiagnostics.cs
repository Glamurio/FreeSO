using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FSO.SimAntics.Diagnostics
{
    /// <summary>
    /// Why a queued action left an avatar's queue. Interaction results are otherwise discarded by the engine, so without
    /// this a player only sees an action vanish.
    /// </summary>
    public enum VMActionEndReason : byte
    {
        /// <summary>The action tree returned true.</summary>
        Completed,
        /// <summary>The action tree returned false (it gave up, e.g. after a failed route).</summary>
        CompletedFalse,
        /// <summary>The action was running and was cancelled (by the player, or by a higher priority action).</summary>
        Cancelled,
        /// <summary>The check tree failed when the action was about to start, so it was dropped without running.</summary>
        CheckFailedAtStart,
        /// <summary>The object the action targets was deleted while the action was queued.</summary>
        CalleeDeleted,
        /// <summary>The action was cancelled before it started (removed from the queue).</summary>
        QueueSkipped,
        /// <summary>A primitive interrupted the whole stack.</summary>
        Interrupted,
        /// <summary>The running action's object was deleted, and the avatar was reset (this wipes the whole queue).</summary>
        OwnerDeadReset,
        /// <summary>A SimAntics exception reset the avatar.</summary>
        Exception,
        /// <summary>Removed by Simitone's queue recovery.</summary>
        Pruned,
    }

    public enum VMRouteEventType : byte
    {
        /// <summary>A new route request started.</summary>
        Begin,
        /// <summary>A goal (destination choice) is being attempted.</summary>
        Goal,
        /// <summary>No room-to-room (portal) route exists to the goal.</summary>
        NoRoomRoute,
        /// <summary>A within-room path was planned.</summary>
        Walk,
        /// <summary>No within-room path to the goal or the next portal.</summary>
        WalkFail,
        /// <summary>The avatar started waiting for something in its way.</summary>
        Wait,
        /// <summary>The avatar waited too long and gave up on the current goal.</summary>
        Timeout,
        /// <summary>The avatar ran into something while walking.</summary>
        Collision,
        /// <summary>The current goal failed; the next goal is tried.</summary>
        SoftFail,
        /// <summary>The route failed for good (runs the route failure tree, shows the balloon).</summary>
        HardFail,
        /// <summary>The avatar arrived.</summary>
        Arrived,
        /// <summary>The remaining route was planned again because the room changed.</summary>
        Replan,
        /// <summary>A door that failed was tried again.</summary>
        DoorRetry,
        /// <summary>The avatar was moved out of an obstacle it was standing in.</summary>
        Unstuck,
    }

    public struct VMRouteEvent
    {
        public uint Tick;
        public VMRouteEventType Type;
        public VMRouteFailCode Code;
        /// <summary>Object involved (blocker, door), or 0.</summary>
        public short Other;
        public LotTilePos Position;
        public LotTilePos Goal;
        public string Detail;
    }

    public struct VMActionEnd
    {
        public uint Tick;
        public VMActionEndReason Reason;
        public string Name;
        public short Callee;
        public short Priority;
    }

    /// <summary>A fixed-size buffer keeping the most recent items.</summary>
    public class VMRingBuffer<T>
    {
        private readonly T[] Items;
        private int Next;
        public int Count { get; private set; }

        public VMRingBuffer(int capacity)
        {
            Items = new T[capacity];
        }

        public void Add(T item)
        {
            Items[Next] = item;
            Next = (Next + 1) % Items.Length;
            if (Count < Items.Length) Count++;
        }

        /// <summary>Oldest first.</summary>
        public IEnumerable<T> InOrder()
        {
            var start = (Count < Items.Length) ? 0 : Next;
            for (int i = 0; i < Count; i++) yield return Items[(start + i) % Items.Length];
        }

        public T Last()
        {
            if (Count == 0) return default(T);
            return Items[(Next + Items.Length - 1) % Items.Length];
        }
    }

    /// <summary>Per-entity record. Transient: not saved, starts empty after loading.</summary>
    public class VMEntityDiagnostics
    {
        public VMRingBuffer<VMRouteEvent> Routes = new VMRingBuffer<VMRouteEvent>(48);
        public VMRingBuffer<VMActionEnd> Actions = new VMRingBuffer<VMActionEnd>(32);
        public int RouteHardFails;
        public int RouteSoftFails;
    }

    /// <summary>
    /// Records what routing and the action queue do, so failures stay observable. Only records; never changes behaviour.
    /// Readable through <see cref="Dump"/> (Simitone's "write_routes" cheat and the settings dialog).
    /// </summary>
    public static class VMDiagnostics
    {
        /// <summary>Raised whenever an action leaves an avatar's queue (on the VM thread).</summary>
        public static event Action<VMEntity, VMActionEnd> OnActionEnded;

        private static VMEntityDiagnostics For(VMEntity ent)
        {
            if (!VMFeatures.Diagnostics || ent == null || !(ent is VMAvatar)) return null;
            if (ent.Diagnostics == null) ent.Diagnostics = new VMEntityDiagnostics();
            return ent.Diagnostics;
        }

        private static uint Tick(VMEntity ent)
        {
            return ent.Thread?.Context?.VM?.Scheduler?.CurrentTickID ?? 0;
        }

        public static void Route(VMEntity ent, VMRouteEventType type, VMRouteFailCode code = VMRouteFailCode.Success,
            VMEntity other = null, LotTilePos? goal = null, string detail = null)
        {
            var d = For(ent);
            if (d == null) return;
            d.Routes.Add(new VMRouteEvent()
            {
                Tick = Tick(ent),
                Type = type,
                Code = code,
                Other = other?.ObjectID ?? 0,
                Position = ent.Position,
                Goal = goal ?? LotTilePos.OUT_OF_WORLD,
                Detail = detail
            });
            if (type == VMRouteEventType.HardFail) d.RouteHardFails++;
            else if (type == VMRouteEventType.SoftFail) d.RouteSoftFails++;
        }

        public static void ActionEnded(VMEntity ent, VMQueuedAction action, VMActionEndReason reason)
        {
            var d = For(ent);
            if (d == null || action == null) return;
            var end = new VMActionEnd()
            {
                Tick = Tick(ent),
                Reason = reason,
                Name = action.Name,
                Callee = action.Callee?.ObjectID ?? 0,
                Priority = action.Priority
            };
            d.Actions.Add(end);
            OnActionEnded?.Invoke(ent, end);
        }

        /// <summary>
        /// Human-readable report for every avatar on the lot: the current queue, the most recent route events and the most
        /// recent ended actions.
        /// </summary>
        public static string Dump(VM vm)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Simitone diagnostics, tick {vm.Scheduler.CurrentTickID}, {DateTime.Now:s}");
            sb.AppendLine("Routing and queue events are recorded per avatar (most recent last). Positions are (x, y, level) in 1/16 tile.");
            foreach (var ent in vm.Entities.OfType<VMAvatar>().OrderBy(x => x.ObjectID))
            {
                sb.AppendLine();
                sb.AppendLine($"== {ent.Name} (object {ent.ObjectID}) at {ent.Position}");
                var queue = ent.Thread?.Queue;
                if (queue != null && queue.Count > 0)
                {
                    sb.AppendLine("  queue:");
                    for (int i = 0; i < queue.Count; i++)
                    {
                        var q = queue[i];
                        var active = (i <= ent.Thread.ActiveQueueBlock) ? "*" : " ";
                        sb.AppendLine($"   {active} {q.Name} -> {Describe(vm, q.Callee?.ObjectID ?? 0)} (priority {q.Priority}, mode {q.Mode})");
                    }
                }
                var d = ent.Diagnostics;
                if (d == null) { sb.AppendLine("  (no events recorded)"); continue; }
                sb.AppendLine($"  route failures: {d.RouteSoftFails} goal(s) given up, {d.RouteHardFails} route(s) failed");
                sb.AppendLine("  route events:");
                foreach (var e in d.Routes.InOrder())
                {
                    var line = $"    [{e.Tick}] {e.Type}";
                    if (e.Code != VMRouteFailCode.Success) line += $" {e.Code}";
                    if (e.Other != 0) line += $" object {Describe(vm, e.Other)}";
                    line += $" at {e.Position}";
                    if (e.Goal.x != LotTilePos.OUT_OF_WORLD.x) line += $" goal {e.Goal}";
                    if (e.Detail != null) line += " - " + e.Detail;
                    sb.AppendLine(line);
                }
                sb.AppendLine("  ended actions:");
                foreach (var a in d.Actions.InOrder())
                {
                    sb.AppendLine($"    [{a.Tick}] {a.Name} -> {Describe(vm, a.Callee)}: {a.Reason}");
                }
            }
            return sb.ToString();
        }

        private static string Describe(VM vm, short objectID)
        {
            if (objectID == 0) return "nothing";
            var obj = vm.GetObjectById(objectID);
            if (obj == null) return $"#{objectID} (deleted)";
            return $"{obj.ToString()} #{objectID}";
        }
    }
}
