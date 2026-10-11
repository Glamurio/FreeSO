using System;

namespace FSO.LotView.Utils
{
    public enum FoliageKind
    {
        None = 0,
        /// <summary>Loses its leaves in winter, turns orange in autumn.</summary>
        Deciduous,
        /// <summary>As Deciduous, and flowers in spring (apple, cherry, prune, crape myrtle).</summary>
        Blossom,
        /// <summary>Keeps its leaves all year, gets a dusting of snow in winter.</summary>
        Evergreen
    }

    public enum FoliageLook
    {
        Normal = 0,
        Spring,
        Blossom,
        Autumn,
        Bare,
        Frosted
    }

    /// <summary>
    /// Seasonal looks for trees and shrubs. The Sims 1 has no seasonal tree sprites (CONFIRMED: Trees.iff holds one pre-rendered
    /// sprite set per tree, the Spellbound pack only adds fixed "dead" and "autumn" trees), so the look is made by recolouring
    /// the leaf pixels of the existing sprites: green pixels are leaves, brown/grey/white pixels are bark and stay as they are.
    /// Everything here is a pure function of the label, season and pixel position, so it is deterministic and testable;
    /// SeasonalSprites turns the result into textures.
    /// </summary>
    public static class SeasonalFoliage
    {
        private static readonly string[] Evergreens = { "pine", "fir", "spruce", "palm", "cypress", "orange", "hedge", "round", "bamboo" };
        private static readonly string[] Blossoming = { "apple", "cherry", "prune", "crape" };
        //not recoloured: the pack's own autumn/dead trees, plants of water and beach, the topiary
        private static readonly string[] Skipped = { "spellbound", "snowy", "beach", "lily", "cat tails", "gypsy", "fame", "bush - autumn" };

        /// <summary>Classifies an object by the label of its OBJD ("Trees - Apple Front", "Shrub - Rose Bush", ...).</summary>
        public static FoliageKind Classify(string label)
        {
            if (string.IsNullOrEmpty(label)) return FoliageKind.None;
            var l = label.ToLowerInvariant();
            if (!(l.StartsWith("trees - ") || l.StartsWith("tree - ") || l.StartsWith("shrub - "))) return FoliageKind.None;
            foreach (var s in Skipped) if (l.Contains(s)) return FoliageKind.None;
            foreach (var s in Evergreens) if (l.Contains(s)) return FoliageKind.Evergreen;
            foreach (var s in Blossoming) if (l.Contains(s)) return FoliageKind.Blossom;
            return FoliageKind.Deciduous;
        }

        /// <summary>The look of a kind of foliage in a season (0 spring, 1 summer, 2 autumn, 3 winter; anything else is normal).</summary>
        public static FoliageLook LookFor(FoliageKind kind, int season)
        {
            if (kind == FoliageKind.None || season < 0 || season > 3) return FoliageLook.Normal;
            switch (season)
            {
                case 0: return kind == FoliageKind.Blossom ? FoliageLook.Blossom : (kind == FoliageKind.Deciduous ? FoliageLook.Spring : FoliageLook.Normal);
                case 2: return kind == FoliageKind.Evergreen ? FoliageLook.Normal : FoliageLook.Autumn;
                case 3: return kind == FoliageKind.Evergreen ? FoliageLook.Frosted : FoliageLook.Bare;
                default: return FoliageLook.Normal;
            }
        }

        /// <summary>Heuristic: leaves are the green pixels. Bark is brown (red leads), grey or white (no colour).</summary>
        public static bool IsLeaf(byte r, byte g, byte b)
        {
            return g > r && g > b && g - Math.Min(r, b) >= 10;
        }

        /// <summary>Share of a deciduous crown's leaf pixels kept as twigs in winter.</summary>
        public const int BareKeepPercent = 42;

        private static uint Hash(int x, int y, int salt)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        /// <summary>Recolours one pixel. Pixels that are not leaves, and the Normal look, are returned unchanged.</summary>
        public static void Recolor(ref byte r, ref byte g, ref byte b, ref byte a, FoliageLook look, int x, int y)
        {
            if (look == FoliageLook.Normal || a == 0 || !IsLeaf(r, g, b)) return;
            float v = Math.Max(r, Math.Max(g, b)) / 255f;
            switch (look)
            {
                case FoliageLook.Bare:
                    {
                        //a bare crown is not empty: keep a share of the leaf pixels as twig and branch texture so the
                        //silhouette stays readable, and dust a few with snow. Removing every leaf pixel left the
                        //trees looking half transparent (the sprites have little bark of their own).
                        uint hh = Hash(x, y, 5) % 100;
                        if (hh >= BareKeepPercent) { a = 0; r = g = b = 0; return; }
                        if (hh % 6 == 0) { r = 226; g = 229; b = 236; }
                        else
                        {
                            float k = 0.7f + 0.7f * v;
                            r = (byte)Math.Min(255, (int)(96 * k));
                            g = (byte)Math.Min(255, (int)(82 * k));
                            b = (byte)Math.Min(255, (int)(72 * k));
                        }
                        a = 255;
                        return;
                    }
                case FoliageLook.Frosted:
                    if ((Hash(x >> 1, y >> 1, 1) & 3) == 0) Lerp(ref r, ref g, ref b, 236, 242, 250, 0.72f);
                    else Lerp(ref r, ref g, ref b, 120, 140, 150, 0.22f);
                    return;
                case FoliageLook.Autumn:
                    {
                        //leaves turn in patches of 4x4 pixels so the tree reads as clusters, not noise
                        float[] hues = { 52f, 42f, 28f, 14f, 22f };
                        float[] sats = { 0.85f, 0.88f, 0.9f, 0.88f, 0.7f };
                        int c = (int)(Hash(x >> 2, y >> 2, 2) % 5);
                        FromHsv(hues[c], sats[c], Math.Min(1f, v * 1.25f + 0.1f), out r, out g, out b);
                        return;
                    }
                case FoliageLook.Blossom:
                    if (Hash(x >> 1, y >> 1, 3) % 100 < 45)
                    {
                        bool white = (Hash(x >> 2, y >> 2, 4) & 1) == 0;
                        float shade = 0.82f + 0.18f * Math.Min(1f, v * 1.6f);
                        r = (byte)(255 * shade);
                        g = (byte)((white ? 238 : 190) * shade);
                        b = (byte)((white ? 242 : 212) * shade);
                        return;
                    }
                    goto case FoliageLook.Spring;
                case FoliageLook.Spring:
                    {
                        //fresher: pull the hue towards yellow-green and lighten
                        RgbToHsv(r, g, b, out float h, out float s, out float val);
                        h = h + (86f - h) * 0.4f;
                        FromHsv(h, s * 0.92f, Math.Min(1f, val * 1.18f + 0.04f), out r, out g, out b);
                        return;
                    }
            }
        }

        private static void Lerp(ref byte r, ref byte g, ref byte b, int tr, int tg, int tb, float t)
        {
            r = (byte)(r + (tr - r) * t); g = (byte)(g + (tg - g) * t); b = (byte)(b + (tb - b) * t);
        }

        public static void RgbToHsv(byte r, byte g, byte b, out float h, out float s, out float v)
        {
            float rf = r / 255f, gf = g / 255f, bf = b / 255f;
            float max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), d = max - min;
            v = max;
            s = max <= 0 ? 0 : d / max;
            if (d <= 0) h = 0;
            else if (max == rf) h = 60f * (((gf - bf) / d) % 6f);
            else if (max == gf) h = 60f * ((bf - rf) / d + 2f);
            else h = 60f * ((rf - gf) / d + 4f);
            if (h < 0) h += 360f;
        }

        public static void FromHsv(float h, float s, float v, out byte r, out byte g, out byte b)
        {
            h = ((h % 360f) + 360f) % 360f;
            float c = v * s, x = c * (1f - Math.Abs((h / 60f) % 2f - 1f)), m = v - c;
            float rf, gf, bf;
            if (h < 60) { rf = c; gf = x; bf = 0; }
            else if (h < 120) { rf = x; gf = c; bf = 0; }
            else if (h < 180) { rf = 0; gf = c; bf = x; }
            else if (h < 240) { rf = 0; gf = x; bf = c; }
            else if (h < 300) { rf = x; gf = 0; bf = c; }
            else { rf = c; gf = 0; bf = x; }
            r = (byte)Math.Round(Math.Max(0, Math.Min(1, rf + m)) * 255f);
            g = (byte)Math.Round(Math.Max(0, Math.Min(1, gf + m)) * 255f);
            b = (byte)Math.Round(Math.Max(0, Math.Min(1, bf + m)) * 255f);
        }
    }
}
