using System;

namespace FSO.SimAntics.Seasons
{
    public enum SeasonWeatherKind
    {
        Clear = 0,
        Rain = 1,
        Storm = 2,
        Snow = 3,
        Fog = 4,
        Heatwave = 5
    }

    public struct SeasonWeatherState
    {
        public SeasonWeatherKind Kind;
        /// <summary>0-100. Rain/snow/fog density, or how hot a heatwave is.</summary>
        public int Intensity;
    }

    /// <summary>
    /// Picks the weather for a season. Stateless: the weather is a hash of the date and a six hour slot, so it needs no saving
    /// and a reloaded lot shows the same sky. The result is encoded into the existing 16-bit weather value
    /// (VM global 18 in TSO; WeatherController.WeatherData):
    ///   bits 0-7 intensity, bit 8 manual, bits 9-10 type (0 rain, 1 snow, 2 hail, 3 fog), bit 11 thunder,
    ///   bit 12 heatwave, bit 13 snow cover on the ground.
    /// Bits 12 and 13 and type 3 are Simitone's additions; the rest is as FreeSO defined it.
    /// </summary>
    public static class SeasonWeather
    {
        public const int SlotHours = 6;
        public const int SlotsPerDay = 24 / SlotHours;

        public const int ManualBit = 1 << 8;
        public const int ThunderBit = 1 << 11;
        public const int HeatwaveBit = 1 << 12;
        public const int SnowCoverBit = 1 << 13;
        public const int TypeRain = 0, TypeSnow = 1, TypeHail = 2, TypeFog = 3;

        //percent chances per season, in the order of the kinds below each table; each row sums to 100.
        private static readonly SeasonWeatherKind[][] Kinds = new SeasonWeatherKind[][]
        {
            new[] { SeasonWeatherKind.Clear, SeasonWeatherKind.Rain, SeasonWeatherKind.Storm },                                     //spring
            new[] { SeasonWeatherKind.Clear, SeasonWeatherKind.Heatwave, SeasonWeatherKind.Storm, SeasonWeatherKind.Rain },         //summer
            new[] { SeasonWeatherKind.Clear, SeasonWeatherKind.Rain, SeasonWeatherKind.Fog },                                       //autumn
            new[] { SeasonWeatherKind.Snow, SeasonWeatherKind.Clear },                                                              //winter
        };

        private static readonly int[][] Percent = new int[][]
        {
            new[] { 55, 38, 7 },
            new[] { 50, 25, 20, 5 },
            new[] { 25, 40, 35 },
            new[] { 65, 35 },
        };

        /// <summary>The chance (percent) of a weather kind in a season.</summary>
        public static int Chance(Season season, SeasonWeatherKind kind)
        {
            var k = Kinds[(int)season];
            for (int i = 0; i < k.Length; i++) if (k[i] == kind) return Percent[(int)season][i];
            return 0;
        }

        /// <summary>Small well-mixed integer hash (SplitMix64 finaliser), deterministic on every platform.</summary>
        public static ulong Mix(ulong x)
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        public static long SlotOf(long absoluteDay, int hour)
        {
            return absoluteDay * SlotsPerDay + Math.Max(0, Math.Min(23, hour)) / SlotHours;
        }

        public static SeasonWeatherState Forecast(Season season, long slot)
        {
            var h = Mix((ulong)slot * 4 + (ulong)season);
            int roll = (int)(h % 100);
            var kinds = Kinds[(int)season];
            var pct = Percent[(int)season];
            var kind = kinds[kinds.Length - 1];
            int sum = 0;
            for (int i = 0; i < kinds.Length; i++)
            {
                sum += pct[i];
                if (roll < sum) { kind = kinds[i]; break; }
            }

            var h2 = Mix(h);
            int lo, hi;
            switch (kind)
            {
                case SeasonWeatherKind.Clear: lo = hi = 0; break;
                case SeasonWeatherKind.Rain: lo = 25; hi = 75; break;
                case SeasonWeatherKind.Storm: lo = 70; hi = 100; break;
                case SeasonWeatherKind.Snow: lo = 25; hi = 100; break;
                case SeasonWeatherKind.Fog: lo = 40; hi = 90; break;
                default: lo = 60; hi = 100; break; //heatwave
            }
            return new SeasonWeatherState { Kind = kind, Intensity = lo + (int)(h2 % (ulong)(hi - lo + 1)) };
        }

        public static SeasonWeatherState Forecast(Season season, long absoluteDay, int hour)
        {
            return Forecast(season, SlotOf(absoluteDay, hour));
        }

        /// <summary>Encodes a forecast as a manual weather value. Winter always has snow cover on the ground.</summary>
        public static short ToWeatherData(SeasonWeatherState state, Season season)
        {
            int data = ManualBit | Math.Max(0, Math.Min(100, state.Intensity));
            switch (state.Kind)
            {
                case SeasonWeatherKind.Clear: data = ManualBit; break;
                case SeasonWeatherKind.Rain: data |= TypeRain << 9; break;
                case SeasonWeatherKind.Storm: data |= TypeRain << 9 | ThunderBit; break;
                case SeasonWeatherKind.Snow: data |= TypeSnow << 9; break;
                case SeasonWeatherKind.Fog: data |= TypeFog << 9; break;
                case SeasonWeatherKind.Heatwave: data |= HeatwaveBit; break;
            }
            if (season == Season.Winter) data |= SnowCoverBit;
            return (short)data;
        }
    }
}
