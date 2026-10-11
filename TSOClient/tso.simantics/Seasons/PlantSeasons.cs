using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Seasons
{
    public enum PlantAction
    {
        None = 0,
        /// <summary>Rain: top up the water of every living plant, so nobody has to carry a watering can.</summary>
        Water,
        /// <summary>Early winter: healthy plants wilt.</summary>
        Wilt,
        /// <summary>Late winter: every plant dies.</summary>
        Kill,
        /// <summary>Early spring: dead and wilted plants grow back.</summary>
        Revive
    }

    /// <summary>
    /// What the seasons do to the outdoor flowers. NEW behaviour, not in the original game.
    /// The flowers (FlowersOutdoor.iff and the Unleashed and Spellbound copies of it) keep their state in object attributes
    /// (CONFIRMED by disassembly): attribute 0 is the state (0 healthy, 1 wilted, 2 dead), attribute 1 the water (watering
    /// sets 100, the "process" pass takes 15 off), attribute 2 the health. Their own BHAVs "update", "Force Healthy",
    /// "Force Wilted" and "Force Dead" apply a change and redraw the plant, so those are what is run.
    /// </summary>
    public static class PlantSeasons
    {
        public const string OutdoorFlowerPrefix = "Flowers - Outdoor";

        public const short StateHealthy = 0, StateWilted = 1, StateDead = 2;
        public const int AttrState = 0, AttrWater = 1;

        /// <summary>Winter wilts the plants for its first third and kills them after that.</summary>
        public const float WiltFraction = 1f / 3f;
        /// <summary>The first third of spring brings dead and wilted plants back.</summary>
        public const float ReviveFraction = 1f / 3f;

        public static bool IsOutdoorPlant(string objdLabel)
        {
            return objdLabel != null && objdLabel.StartsWith(OutdoorFlowerPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>What the season (and the rain) do to plants on this day. dayInSeason counts from 0.</summary>
        public static PlantAction Decide(Season season, int dayInSeason, int seasonLength, bool raining)
        {
            float fraction = seasonLength <= 0 ? 0f : (float)dayInSeason / seasonLength;
            switch (season)
            {
                case Season.Winter:
                    return fraction < WiltFraction ? PlantAction.Wilt : PlantAction.Kill;
                case Season.Spring:
                    if (fraction < ReviveFraction) return PlantAction.Revive;
                    return raining ? PlantAction.Water : PlantAction.None;
                default:
                    return raining ? PlantAction.Water : PlantAction.None;
            }
        }

        /// <summary>Does this action change a plant in this state? Keeps the hourly pass from touching plants that are already as wanted.</summary>
        public static bool Affects(PlantAction action, short state, short water)
        {
            switch (action)
            {
                case PlantAction.Water: return state != StateDead && water < 100;
                case PlantAction.Wilt: return state == StateHealthy;
                case PlantAction.Kill: return state != StateDead;
                case PlantAction.Revive: return state != StateHealthy;
                default: return false;
            }
        }

        private static readonly Dictionary<uint, ushort[]> Routines = new Dictionary<uint, ushort[]>();
        private const int RoutineUpdate = 0, RoutineHealthy = 1, RoutineWilted = 2, RoutineDead = 3;

        private static ushort[] RoutinesOf(VMEntity ent)
        {
            var guid = ent.Object.OBJ.GUID;
            if (Routines.TryGetValue(guid, out var found)) return found;
            var ids = new ushort[4];
            var bhavs = ent.Object.Resource.MainIff?.List<BHAV>();
            if (bhavs != null)
            {
                foreach (var b in bhavs)
                {
                    var label = (b.ChunkLabel ?? "").Trim();
                    if (label.Equals("update", StringComparison.OrdinalIgnoreCase)) ids[RoutineUpdate] = b.ChunkID;
                    else if (label.Equals("Force Healthy", StringComparison.OrdinalIgnoreCase)) ids[RoutineHealthy] = b.ChunkID;
                    else if (label.Equals("Force Wilted", StringComparison.OrdinalIgnoreCase)) ids[RoutineWilted] = b.ChunkID;
                    else if (label.Equals("Force Dead", StringComparison.OrdinalIgnoreCase)) ids[RoutineDead] = b.ChunkID;
                }
            }
            Routines[guid] = ids;
            return ids;
        }

        private static void Run(VM vm, VMEntity plant, ushort routine)
        {
            if (routine == 0) return;
            //queued on the plant's own thread, like any entry point, so it runs with the next tick
            plant.ExecuteGenericEntryPoint(new OBJfFunctionEntry { ActionFunction = routine }, vm.Context, false, plant, new short[4]);
            //the plant sleeps for hours between its own passes (CONFIRMED: its main loop idles 22000 ticks), and a frame pushed
            //onto a sleeping thread would wait until it wakes. Wake it now, as the "notify out of idle" primitive does.
            vm.Scheduler.RescheduleInterrupt(plant);
        }

        /// <summary>Summary for the season_info diagnostic: how many outdoor plants there are, by state.</summary>
        public static string Describe(VM vm)
        {
            int total = 0, healthy = 0, wilted = 0, dead = 0, noRoutine = 0;
            foreach (var ent in vm.Entities)
            {
                if (ent == null || ent.Dead || !(ent is VMGameObject) || ent.Object?.OBJ == null) continue;
                if (!IsOutdoorPlant(ent.Object.OBJ.ChunkLabel)) continue;
                total++;
                var st = ent.GetAttribute(AttrState);
                if (st == StateDead) dead++; else if (st == StateWilted) wilted++; else healthy++;
                var r = RoutinesOf(ent);
                if (r[RoutineUpdate] == 0 || r[RoutineDead] == 0) noRoutine++;
            }
            return "outdoor plants " + total + " (healthy " + healthy + ", wilted " + wilted + ", dead " + dead + "), without routines " + noRoutine;
        }

        /// <summary>Applies the action to every outdoor plant on the lot. Returns how many plants were changed.</summary>
        public static int Apply(VM vm, PlantAction action)
        {
            if (action == PlantAction.None) return 0;
            int changed = 0;
            foreach (var ent in vm.Entities)
            {
                if (ent == null || ent.Dead || !(ent is VMGameObject) || ent.Object?.OBJ == null) continue;
                if (ent.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (!IsOutdoorPlant(ent.Object.OBJ.ChunkLabel)) continue;
                if (!Affects(action, ent.GetAttribute(AttrState), ent.GetAttribute(AttrWater))) continue;

                var routines = RoutinesOf(ent);
                switch (action)
                {
                    case PlantAction.Water:
                        ent.SetAttribute(AttrWater, 100);
                        Run(vm, ent, routines[RoutineUpdate]);
                        break;
                    case PlantAction.Wilt: Run(vm, ent, routines[RoutineWilted]); break;
                    case PlantAction.Kill: Run(vm, ent, routines[RoutineDead]); break;
                    case PlantAction.Revive: Run(vm, ent, routines[RoutineHealthy]); break;
                }
                changed++;
            }
            return changed;
        }
    }
}
