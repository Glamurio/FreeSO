using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Model;
using System;
using System.Collections.Generic;
using System.IO;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// Superstar fame levels: generic Sims calls 34 "PromoteFameIfNeeded" and 36 "DemoteFameIfNeeded".
    ///
    /// Reverse engineered from Sims.exe of the Complete Collection (the Legacy Collection's code section is encrypted by
    /// SteamStub). Addresses are for that exe. CONFIRMED by disassembly:
    ///  - The generic call dispatcher (0x44F7F0, jump table 0x45147A) gets the stack object's neighbour and calls the fame
    ///    manager's Promote (0x404CEF) for call 34 and Demote (0x404DF6) for call 36; the primitive returns their result.
    ///  - The manager (0x404E49) reads GameData/fame.iff: STR# 5 "Fame Score Needed", STR# 4 "Friend Star Power Needed"
    ///    and STR# 6 "Skills Needed" ("cooking,repair,charisma,body,logic,creativity"), 11 rows each, indexed by the
    ///    current level.
    ///  - Person data: 80 fame score, 81 star power level (0..10), 82 highest level reached.
    ///  - Promote, at level L (L &lt; 10): score &gt;= ScoreNeeded[L], every skill / 100 (clamped 0..10) &gt;= SkillsNeeded[L],
    ///    and FriendNeeded[L] &lt;= 0.5 * (sum of the star power of the Sim's famous friends) (0x405333). Then L+1, and 82
    ///    is raised to L+1 if lower. One level per call.
    ///  - Demote, at level L &gt; 0: score &lt; ScoreNeeded[L-1] gives L-1. Skills and friends don't matter; 82 is kept.
    ///  - Friends (0x427281, 0x42720F): every other neighbour of a different family number (person data 61, not 0) whose
    ///    relationship with the Sim and the Sim's with them both exist and both have a daily value (index 0) of at
    ///    least the "friendship threshold" (global 0x6C4388; named simulator constant, default 25 in the exe, 50 in
    ///    Global.iff FCNS 2) adds their star power (person data 81 of their neighbour record).
    /// LIKELY/unknown: the original also requires the friend's record to pass 0x423F63 (a flag test on its object
    /// definition); here every neighbour with person data counts.
    /// </summary>
    public static class VMTS1Fame
    {
        public const int MAX_LEVEL = 10;
        /// <summary>The exe's default for the "friendship threshold" simulator constant, used when Global.iff lacks it.</summary>
        public const int DEFAULT_FRIENDSHIP_THRESHOLD = 25;
        private static int? _FriendshipThreshold;

        /// <summary>"friendship threshold" from Global.iff FCNS 2 (the constants Sims.exe 0x4593xx reads by name).</summary>
        public static int FriendshipThreshold
        {
            get
            {
                if (_FriendshipThreshold == null)
                {
                    _FriendshipThreshold = DEFAULT_FRIENDSHIP_THRESHOLD;
                    try
                    {
                        var fcns = Content.Content.Get().WorldObjectGlobals.Get("global")?.Resource.Get<FCNS>(2);
                        var value = FindConstant(fcns, "friendship threshold");
                        if (value != null) _FriendshipThreshold = (int)Math.Round(value.Value);
                    }
                    catch (Exception) { }
                }
                return _FriendshipThreshold.Value;
            }
            set { _FriendshipThreshold = value; }
        }

        /// <summary>A named constant from an FCNS chunk (FCNS stores each entry as "name: value").</summary>
        public static float? FindConstant(STR fcns, string name)
        {
            if (fcns == null) return null;
            for (int i = 0; i < fcns.Length; i++)
            {
                var entry = fcns.GetString(i);
                if (entry == null || !entry.StartsWith(name + ": ", StringComparison.OrdinalIgnoreCase)) continue;
                var text = entry.Substring(name.Length + 2).Trim();
                float v;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out v)) return v;
                if (float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out v)) return v;
            }
            return null;
        }
        public const float FRIEND_POWER_FACTOR = 0.5f;
        /// <summary>Person data skill indices in the order of the "Skills Needed" strings (table at 0x666538).</summary>
        public static readonly int[] SkillPersonData = new int[] { 10, 12, 11, 17, 18, 15 };

        public class Tables
        {
            public int[] ScoreNeeded = new int[11];
            public int[] FriendPowerNeeded = new int[11];
            /// <summary>[level][skill], skill in SkillPersonData order.</summary>
            public int[][] SkillsNeeded = new int[11][];

            public static Tables FromIff(IffFile iff)
            {
                var t = new Tables();
                var score = iff.Get<STR>(5);
                var friends = iff.Get<STR>(4);
                var skills = iff.Get<STR>(6);
                if (score == null || friends == null || skills == null) return null;
                for (int i = 0; i < 11; i++)
                {
                    t.ScoreNeeded[i] = ParseInt(score.GetString(i, STRLangCode.EnglishUS) ?? score.GetString(i));
                    t.FriendPowerNeeded[i] = ParseInt(friends.GetString(i, STRLangCode.EnglishUS) ?? friends.GetString(i));
                    var row = (skills.GetString(i, STRLangCode.EnglishUS) ?? skills.GetString(i) ?? "").Split(',');
                    t.SkillsNeeded[i] = new int[6];
                    for (int j = 0; j < 6 && j < row.Length; j++) t.SkillsNeeded[i][j] = ParseInt(row[j]);
                }
                return t;
            }

            private static int ParseInt(string s)
            {
                int v;
                return int.TryParse((s ?? "").Trim(), out v) ? v : 0;
            }
        }

        private static Tables Cached;
        private static bool Loaded;

        /// <summary>The tables from GameData/fame.iff, or null if it isn't installed (no Superstar).</summary>
        public static Tables LoadTables()
        {
            if (Loaded) return Cached;
            Loaded = true;
            try
            {
                var path = Path.Combine(Content.Content.Get().TS1BasePath, "GameData", "fame.iff");
                if (File.Exists(path)) Cached = Tables.FromIff(new IffFile(path));
            }
            catch (Exception) { Cached = null; }
            return Cached;
        }

        /// <summary>
        /// Whether a Sim at this level, with this score, these skill points (person data values, SkillPersonData order)
        /// and this sum of friends' star power, is promoted by call 34.
        /// </summary>
        public static bool CanPromote(int level, int score, int[] skills, int friendStarPower, Tables t)
        {
            if (t == null || level < 0 || level >= MAX_LEVEL) return false;
            if (score < t.ScoreNeeded[level]) return false;
            for (int i = 0; i < 6; i++)
            {
                var have = Math.Max(0, Math.Min(10, skills[i] / 100));
                if (t.SkillsNeeded[level][i] > have) return false;
            }
            return t.FriendPowerNeeded[level] <= friendStarPower * FRIEND_POWER_FACTOR;
        }

        /// <summary>Whether call 36 demotes a Sim at this level with this score.</summary>
        public static bool CanDemote(int level, int score, Tables t)
        {
            if (t == null || level <= 0 || level > 11) return false;
            return score < t.ScoreNeeded[level - 1];
        }

        /// <summary>Whether two Sims count as friends for fame: each has a relationship to the other with a daily value of at least the threshold.</summary>
        public static bool AreFameFriends(Dictionary<int, List<short>> aRels, int aID, Dictionary<int, List<short>> bRels, int bID, int threshold)
        {
            List<short> ab, ba;
            if (aRels == null || bRels == null) return false;
            if (!aRels.TryGetValue(bID, out ab) || ab.Count < 1) return false;
            if (!bRels.TryGetValue(aID, out ba) || ba.Count < 1) return false;
            return ab[0] >= threshold && ba[0] >= threshold;
        }

        /// <summary>The sum of the star power of a Sim's friends from other families, from the neighbourhood records.</summary>
        public static int FriendStarPower(VMAvatar sim)
        {
            var hood = Content.Content.Get().Neighborhood;
            var myID = sim.GetPersonData(VMPersonDataVariable.NeighborId);
            var me = hood?.GetNeighborByID(myID);
            if (me == null || hood.Neighbors == null) return 0;
            var family = sim.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
            int sum = 0;
            var threshold = FriendshipThreshold;
            foreach (var other in hood.Neighbors.Entries)
            {
                if (other == null || other == me || other.PersonData == null || other.PersonData.Length <= 81) continue;
                var otherFamily = other.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber];
                if (otherFamily == 0 || otherFamily == family) continue;
                if (!AreFameFriends(other.Relationships, other.NeighbourID, me.Relationships, myID, threshold)) continue;
                sum += other.PersonData[(int)VMPersonDataVariable.TS1FameStarPower];
            }
            return sum;
        }

        /// <summary>Call 34 (promote) or 36 (demote) on a Sim. Returns whether the level changed.</summary>
        public static bool Apply(VMAvatar sim, bool promote, Tables t)
        {
            if (sim == null || t == null) return false;
            int level = sim.GetPersonData(VMPersonDataVariable.TS1FameStarPower);
            int score = sim.GetPersonData(VMPersonDataVariable.TS1FameScore);
            if (promote)
            {
                var skills = new int[6];
                for (int i = 0; i < 6; i++) skills[i] = sim.GetPersonData((VMPersonDataVariable)SkillPersonData[i]);
                if (!CanPromote(level, score, skills, FriendStarPower(sim), t)) return false;
                level++;
                sim.SetPersonData(VMPersonDataVariable.TS1FameStarPower, (short)level);
                if (sim.GetPersonData(VMPersonDataVariable.TS1FameStarHighWatermark) < level)
                    sim.SetPersonData(VMPersonDataVariable.TS1FameStarHighWatermark, (short)level);
                return true;
            }
            if (!CanDemote(level, score, t)) return false;
            sim.SetPersonData(VMPersonDataVariable.TS1FameStarPower, (short)(level - 1));
            return true;
        }
    }
}
