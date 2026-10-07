using FSO.Vitaboy;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Engine.Routing
{
    /// <summary>
    /// Builds looping, in-place versions of the standing-adjust animations for shimmying.
    ///
    /// CONFIRMED (Animation.far, adult/kid-standing-adjust-e/w): each animation is one side-step whose ROOT bone
    /// translates sideways by ~0.95 world units (adults) or ~0.71 (kids) over 36 frames; -e moves along -X and -w along
    /// +X, with the forward (-n) step along +Z. Played as-is in a loop, the Sim would drift sideways and snap back
    /// every step, on top of the routing movement. The in-place copy removes the linear ROOT drift (keeping the
    /// sway), so the routing frame supplies the movement, exactly as it does for the walk loops.
    ///
    /// The copy keeps the original name, so saves still refer to the real animation. After loading, the routing
    /// frame notices the animation is not the in-place copy and restarts it (VMRoutingFrame.StartWalkAnimation).
    /// </summary>
    public static class VMShimmyAnimation
    {
        //world units per tile, see FSO.LotView.WorldSpace / WorldState.WorldUnitsPerTile
        private const float WORLD_UNITS_PER_TILE = 3f;

        public class Entry
        {
            public Animation Animation;
            /// <summary>Sideways distance covered by one loop, in routing units (1/16 tile).</summary>
            public float StepDistance;
        }

        private static Dictionary<Animation, Entry> Cache = new Dictionary<Animation, Entry>();
        private static HashSet<Animation> Generated = new HashSet<Animation>();

        public static bool IsInPlace(Animation anim)
        {
            lock (Cache) return anim != null && Generated.Contains(anim);
        }

        public static Entry Get(Animation source)
        {
            if (source == null) return null;
            lock (Cache)
            {
                if (Cache.TryGetValue(source, out var entry)) return entry;
                entry = Build(source);
                Cache[source] = entry;
                if (entry != null) Generated.Add(entry.Animation);
                return entry;
            }
        }

        private static Entry Build(Animation source)
        {
            if (source.Translations == null || source.Motions == null) return null;
            var translations = (Vector3[])source.Translations.Clone();
            float drift = 0;
            foreach (var motion in source.Motions)
            {
                if (!motion.HasTranslation || motion.FrameCount < 2) continue;
                if (!string.Equals(motion.BoneName, "ROOT", StringComparison.InvariantCultureIgnoreCase)) continue;
                var first = translations[motion.FirstTranslationIndex];
                var frames = (int)motion.FrameCount;
                var last = translations[motion.FirstTranslationIndex + frames - 1];
                var dx = last.X - first.X;
                var dz = last.Z - first.Z;
                drift = (float)Math.Sqrt(dx * dx + dz * dz);
                var n = frames - 1;
                for (int i = 0; i <= n; i++)
                {
                    var t = i / (float)n;
                    var index = motion.FirstTranslationIndex + i;
                    translations[index].X -= dx * t;
                    translations[index].Z -= dz * t;
                }
            }
            if (drift <= 0) return null;

            var copy = new Animation()
            {
                Name = source.Name,
                XSkillName = source.XSkillName,
                Duration = source.Duration,
                Distance = 0,
                IsMoving = 0,
                TranslationCount = source.TranslationCount,
                RotationCount = source.RotationCount,
                Translations = translations,
                Rotations = source.Rotations,
                Motions = source.Motions,
                NumFrames = source.NumFrames,
                ParentBCF = source.ParentBCF
            };
            copy.UpdateFPS();
            return new Entry() { Animation = copy, StepDistance = drift * 16 / WORLD_UNITS_PER_TILE };
        }
    }
}
