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

    /// <summary>Which pixels of a sprite count as leaves.</summary>
    public enum FoliageLeaf
    {
        Green = 0,
        /// <summary>Crimson leaves (the prune tree).</summary>
        Red,
        /// <summary>Green leaves with pink flowers that go with the leaves (crape myrtle).</summary>
        GreenAndPink
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
        private static readonly string[] Evergreens = { "pine", "fir", "spruce", "palm", "cypress", "hedge", "round", "bamboo", "spellbound", "gypsy", "bush - autumn" };
        private static readonly string[] Blossoming = { "apple", "cherry", "prune", "orange" };
        //desert plants and the water plants do not change with the seasons
        private static readonly string[] Skipped = { "lily", "cat tails", "cactus", "prickly", "agave", "joshua", "saguaro" };
        //fruit trees: the fruit goes in winter and spring
        private static readonly string[] Fruiting = { "apple", "orange" };

        //Sims Online objects that are not in the Sims 1 files, by GUID (FSO's VMLotTerrainRestoreTools names them)
        private static readonly System.Collections.Generic.Dictionary<uint, FoliageKind> ByGuid = new System.Collections.Generic.Dictionary<uint, FoliageKind>
        {
            { 0x70E7B9A5, FoliageKind.None }, //prickly pear
            { 0xD297087F, FoliageKind.None }, //agave bloom
            { 0x2FF38F53, FoliageKind.None }, //cactus
            { 0xD8F649FF, FoliageKind.None }, //joshua tree
            { 0x56040A7B, FoliageKind.None }, //pine with snow
            { 0x563504AC, FoliageKind.None }, //fir with snow
            { 0x56D56301, FoliageKind.None }, //spruce with snow
            { 0x154522AF, FoliageKind.Evergreen }, //juniper bush
            { 0x15460FCA, FoliageKind.Evergreen }, //sword fern
            { 0xD5CA5425, FoliageKind.Evergreen }, //rose bush
            { 0x2163F6F7, FoliageKind.Evergreen }, //tumbleweed
            { 0x263FAA5F, FoliageKind.Evergreen }, //century plant
            { 0x65627AE4, FoliageKind.Evergreen }, //small palm
            { 0x6779ED37, FoliageKind.Evergreen }, //palm 12 foot
        };

        /// <summary>Classifies an object by the label of its OBJD ("Trees - Apple Front", "Shrub - Rose Bush", ...).</summary>
        public static FoliageKind Classify(string label)
        {
            if (string.IsNullOrEmpty(label)) return FoliageKind.None;
            var l = label.ToLowerInvariant();
            //the Tree of Paradise is a street lamp built from a tree
            bool lampTree = l.StartsWith("lamps - street - tree");
            if (!(lampTree || l.StartsWith("trees - ") || l.StartsWith("tree - ") || l.StartsWith("shrub - "))) return FoliageKind.None;
            foreach (var s in Skipped) if (l.Contains(s)) return FoliageKind.None;
            if (lampTree) return FoliageKind.Deciduous;
            foreach (var s in Evergreens) if (l.Contains(s)) return FoliageKind.Evergreen;
            foreach (var s in Blossoming) if (l.Contains(s)) return FoliageKind.Blossom;
            //every other shrub keeps its leaves (rose bush, juniper, sword fern, tumbleweed...)
            if (l.StartsWith("shrub - ")) return FoliageKind.Evergreen;
            return FoliageKind.Deciduous;
        }

        /// <summary>As Classify, but known GUIDs (objects outside the Sims 1 files) take priority over the label.</summary>
        public static FoliageKind Classify(string label, uint guid)
        {
            if (guid != 0 && ByGuid.TryGetValue(guid, out var kind)) return kind;
            return Classify(label);
        }

        public static FoliageLeaf LeafOf(string label)
        {
            if (string.IsNullOrEmpty(label)) return FoliageLeaf.Green;
            var l = label.ToLowerInvariant();
            if (l.Contains("prune")) return FoliageLeaf.Red;
            //the crape myrtle tiles are labelled "Trees - Oak - ..." in TreesHD.iff
            if (l.Contains("crape") || l.Contains("oak")) return FoliageLeaf.GreenAndPink;
            return FoliageLeaf.Green;
        }

        public static bool HasFruit(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            var l = label.ToLowerInvariant();
            foreach (var s in Fruiting) if (l.Contains(s)) return true;
            return false;
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

        /// <summary>Crimson leaves (the prune tree): red leads clearly over both other channels, which bark and shadow do not.</summary>
        public static bool IsRedLeaf(byte r, byte g, byte b)
        {
            return r >= 45 && r >= g * 1.5f && r >= b * 1.25f;
        }

        /// <summary>Pink or red flowers (crape myrtle blooms, apple blossom): bright, saturated, and red or pink rather than brown.</summary>
        public static bool IsFlower(byte r, byte g, byte b)
        {
            return r >= 100 && r >= g * 1.45f && b >= g * 0.75f && r >= b;
        }

        /// <summary>Apples are red, oranges orange. Bark is orange-brown too, but darker, so oranges need a high value.</summary>
        public static bool IsFruit(byte r, byte g, byte b)
        {
            bool red = r >= 80 && r >= g * 2f && r >= b * 1.8f;
            bool orange = r >= 165 && g >= 85 && g <= 175 && b <= 70 && r >= g * 1.3f;
            return red || orange;
        }

        /// <summary>Share of a deciduous crown's leaf pixels kept as twigs in winter.</summary>
        public const int BareKeepPercent = 48;

        private static uint Hash(int x, int y, int salt)
        {
            uint h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return h;
        }

        /// <summary>Recolours one pixel of a Green-leafed tree without fruit.</summary>
        public static void Recolor(ref byte r, ref byte g, ref byte b, ref byte a, FoliageLook look, int x, int y)
        {
            Recolor(ref r, ref g, ref b, ref a, look, x, y, FoliageLeaf.Green, false);
        }

        /// <summary>
        /// Recolours one pixel. Pixels that are not leaves (or flowers, or fruit where it applies), and the Normal and Frosted
        /// looks, are returned unchanged: frost needs the neighbouring pixels, see Frost.
        /// </summary>
        public static void Recolor(ref byte r, ref byte g, ref byte b, ref byte a, FoliageLook look, int x, int y, FoliageLeaf leafType, bool fruit)
        {
            if (look == FoliageLook.Normal || look == FoliageLook.Frosted || a == 0) return;

            bool leaf = leafType == FoliageLeaf.Red ? IsRedLeaf(r, g, b) : IsLeaf(r, g, b);
            bool flower = leafType == FoliageLeaf.GreenAndPink && !leaf && IsFlower(r, g, b);
            bool isFruit = fruit && !leaf && IsFruit(r, g, b);
            if (!leaf && !flower && !isFruit) return;
            float v = Math.Max(r, Math.Max(g, b)) / 255f;

            switch (look)
            {
                case FoliageLook.Bare:
                    {
                        //fruit and flowers go with the leaves
                        if (isFruit) { a = 0; r = g = b = 0; return; }
                        //a bare crown is not empty: keep a share of the leaf pixels as twig and branch texture so the
                        //silhouette stays readable, and dust a few with snow. Removing every leaf pixel left the
                        //trees looking half transparent (the sprites have little bark of their own).
                        uint hh = Hash(x, y, 5) % 100;
                        if (hh >= BareKeepPercent || flower) { a = 0; r = g = b = 0; return; }
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
                case FoliageLook.Autumn:
                    {
                        //leaves turn in patches of 4x4 pixels so the tree reads as clusters, not noise; flowers turn with them
                        float[] hues = { 52f, 42f, 28f, 14f, 22f };
                        float[] sats = { 0.85f, 0.88f, 0.9f, 0.88f, 0.7f };
                        int c = (int)(Hash(x >> 2, y >> 2, 2) % 5);
                        if (isFruit) return; //fruit hangs on in autumn
                        FromHsv(hues[c], sats[c], Math.Min(1f, (flower ? 0.55f : v) * 1.25f + 0.1f), out r, out g, out b);
                        return;
                    }
                case FoliageLook.Blossom:
                case FoliageLook.Spring:
                    {
                        if (flower) return; //crape myrtle keeps its own flowers
                        if (isFruit) { FromHsv(95f, 0.55f, 0.45f, out r, out g, out b); return; }
                        if (look == FoliageLook.Blossom)
                        {
                            //small clusters of blossom: each 4x4 cell has a 42% chance of one, a few pixels around a jittered centre
                            int cx = x >> 2, cy = y >> 2;
                            if (Hash(cx, cy, 3) % 100 < 42)
                            {
                                uint j = Hash(cx, cy, 5);
                                int jx = cx * 4 + 1 + (int)(j & 1), jy = cy * 4 + 1 + (int)((j >> 1) & 1);
                                int d2 = (x - jx) * (x - jx) + (y - jy) * (y - jy);
                                if (d2 <= 2)
                                {
                                    uint pal = Hash(cx, cy, 4) % 3;
                                    int pr = pal == 0 ? 255 : (pal == 1 ? 252 : 240);
                                    int pg = pal == 0 ? 214 : (pal == 1 ? 244 : 160);
                                    int pb = pal == 0 ? 226 : (pal == 1 ? 246 : 190);
                                    float shade = 0.80f + 0.20f * Math.Min(1f, v * 1.6f);
                                    if (d2 == 0) shade = Math.Min(1f, shade + 0.12f);
                                    r = (byte)Math.Min(255, (int)(pr * shade));
                                    g = (byte)Math.Min(255, (int)(pg * shade));
                                    b = (byte)Math.Min(255, (int)(pb * shade));
                                    return;
                                }
                            }
                        }
                        if (leafType == FoliageLeaf.Red)
                        {
                            //crimson leaves are green again in spring
                            FromHsv(95f, 0.55f, Math.Min(1f, v + 0.05f), out r, out g, out b);
                            return;
                        }
                        //fresher: pull the hue towards yellow-green and lighten
                        RgbToHsv(r, g, b, out float h, out float s, out float val);
                        h = h + (86f - h) * 0.4f;
                        FromHsv(h, s * 0.92f, Math.Min(1f, val * 1.18f + 0.04f), out r, out g, out b);
                        return;
                    }
            }
        }

        /// <summary>
        /// Winter snow for evergreens and other plants that keep their shape, on a whole sprite (RGBA, 4 bytes a pixel, alpha 0 =
        /// empty). Snow settles where the sprite faces up: along the upper edge of every clump, on pixels brighter than their
        /// surroundings (lit from above), and in the upper part of the sprite. Everything else is cooled a little. Pixel by pixel
        /// randomness made a speckle that looked wrong on dense shrubs, so the noise here comes in 2x2 blocks.
        /// </summary>
        public static void Frost(byte[] rgba, int w, int h)
        {
            if (rgba == null || w <= 0 || h <= 0 || rgba.Length < w * h * 4) return;
            var lum = new float[w * h];
            int top = -1, bottom = -1;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (rgba[i + 3] == 0) continue;
                    lum[y * w + x] = 0.299f * rgba[i] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2];
                    if (top < 0) top = y;
                    bottom = y;
                }
            }
            if (top < 0) return;
            float span = Math.Max(1, bottom - top);
            var snow = new bool[w * h];
            for (int y = top; y <= bottom; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    if (rgba[p * 4 + 3] == 0) continue;
                    //rows of solid pixels above this one, up to 5
                    int d = 5;
                    for (int k = 1; k <= 5; k++)
                    {
                        if (y - k < 0 || rgba[((y - k) * w + x) * 4 + 3] == 0) { d = k - 1; break; }
                    }
                    float sum = 0; int n = 0;
                    for (int dy = -2; dy <= 2; dy++)
                    {
                        int yy = y + dy; if (yy < 0 || yy >= h) continue;
                        for (int dx = -2; dx <= 2; dx++)
                        {
                            int xx = x + dx; if (xx < 0 || xx >= w) continue;
                            if (rgba[(yy * w + xx) * 4 + 3] == 0) continue;
                            sum += lum[yy * w + xx]; n++;
                        }
                    }
                    float mean = n > 0 ? sum / n : lum[p];
                    float edge = Math.Max(0f, 1f - d / 5f) * 0.8f;
                    float bright = Math.Max(0f, Math.Min(1f, (lum[p] - mean) / 36f)) * 0.5f;
                    float upper = Math.Max(0f, (1f - (y - top) / span) - 0.35f) * 0.9f;
                    float noise = (Hash(x >> 1, y >> 1, 7) % 100) / 100f * 0.35f;
                    snow[p] = edge + bright + upper + noise > 0.66f;
                }
            }
            for (int y = top; y <= bottom; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int p = y * w + x;
                    int i = p * 4;
                    if (rgba[i + 3] == 0) continue;
                    if (snow[p])
                    {
                        float shade = 0.80f + 0.20f * Math.Min(1f, lum[p] / 150f);
                        rgba[i] = (byte)(236 * shade); rgba[i + 1] = (byte)(242 * shade); rgba[i + 2] = (byte)(250 * shade);
                    }
                    else
                    {
                        //a cold cast on what the snow does not cover
                        rgba[i] = (byte)(rgba[i] + (112 - rgba[i]) * 0.14f);
                        rgba[i + 1] = (byte)(rgba[i + 1] + (128 - rgba[i + 1]) * 0.14f);
                        rgba[i + 2] = (byte)(rgba[i + 2] + (138 - rgba[i + 2]) * 0.14f);
                    }
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
