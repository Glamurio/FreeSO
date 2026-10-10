using System;

namespace FSO.SimAntics.Seasons
{
    public enum Season
    {
        Spring = 0,
        Summer = 1,
        Autumn = 2,
        Winter = 3
    }

    /// <summary>
    /// Maps the VM clock's calendar (12 months of 30 days, see VMClock) to seasons. Pure functions of the date, so nothing
    /// extra has to be saved: a lot that loads with a given date always gets the same season.
    ///
    /// Season length is a number of days. At the standard 90 a season is three months (Spring = months 3-5, Summer 6-8,
    /// Autumn 9-11, Winter 12, 1, 2). Shorter lengths compress the cycle: 30 makes every month a season (a "compact year"),
    /// 10 makes a season ten days long. Only lengths that divide 90 are offered so that the cycle of four seasons always
    /// fits a whole number of times into the 360 day year.
    /// </summary>
    public static class SeasonModel
    {
        public const int DaysPerMonth = 30;
        public const int MonthsPerYear = 12;
        public const int DaysPerYear = DaysPerMonth * MonthsPerYear;
        public const int StandardSeasonLength = 90;

        /// <summary>Spring begins on this day of the year (0-based) at every season length: 1 March.</summary>
        public const int SpringStartDay = 2 * DaysPerMonth;

        /// <summary>Season lengths in days the settings offer.</summary>
        public static readonly int[] SeasonLengths = new int[] { 10, 15, 30, 45, 90 };

        public static readonly string[] MonthNames = new string[]
        {
            "January", "February", "March", "April", "May", "June",
            "July", "August", "September", "October", "November", "December"
        };

        /// <summary>The nearest allowed season length; anything unusable becomes the standard 90.</summary>
        public static int NormaliseLength(int days)
        {
            if (days <= 0) return StandardSeasonLength;
            int best = SeasonLengths[0];
            foreach (var l in SeasonLengths)
                if (Math.Abs(l - days) < Math.Abs(best - days)) best = l;
            return best;
        }

        /// <summary>Settings choice index to season length (out of range = standard).</summary>
        public static int LengthFromIndex(int index)
        {
            return (index >= 0 && index < SeasonLengths.Length) ? SeasonLengths[index] : StandardSeasonLength;
        }

        public static string MonthName(int month)
        {
            return MonthNames[Math.Max(1, Math.Min(MonthsPerYear, month)) - 1];
        }

        /// <summary>Day of the year, 0-359. Out of range months and days are clamped (the clock restores them from saves).</summary>
        public static int DayOfYear(int month, int dayOfMonth)
        {
            month = Math.Max(1, Math.Min(MonthsPerYear, month));
            dayOfMonth = Math.Max(1, Math.Min(DaysPerMonth, dayOfMonth));
            return (month - 1) * DaysPerMonth + (dayOfMonth - 1);
        }

        /// <summary>Days since year 0, counting every day as the clock does.</summary>
        public static long AbsoluteDay(int year, int month, int dayOfMonth)
        {
            return (long)year * DaysPerYear + DayOfYear(month, dayOfMonth);
        }

        private static long FloorDiv(long a, long b)
        {
            var q = a / b;
            return (a % b != 0 && ((a < 0) != (b < 0))) ? q - 1 : q;
        }

        /// <summary>The inverse of AbsoluteDay.</summary>
        public static void FromAbsoluteDay(long absoluteDay, out int year, out int month, out int dayOfMonth)
        {
            long y = FloorDiv(absoluteDay, DaysPerYear);
            int doy = (int)(absoluteDay - y * DaysPerYear);
            year = (int)y;
            month = doy / DaysPerMonth + 1;
            dayOfMonth = doy % DaysPerMonth + 1;
        }

        /// <summary>The absolute day on which the season after the one containing this day begins.</summary>
        public static long NextSeasonStart(long absoluteDay, int seasonLength)
        {
            seasonLength = NormaliseLength(seasonLength);
            return absoluteDay - DayInSeason(absoluteDay, seasonLength) + seasonLength;
        }

        public static Season SeasonOf(long absoluteDay, int seasonLength)
        {
            seasonLength = NormaliseLength(seasonLength);
            var n = FloorDiv(absoluteDay - SpringStartDay, seasonLength);
            return (Season)(int)(((n % 4) + 4) % 4);
        }

        public static Season SeasonOf(int year, int month, int dayOfMonth, int seasonLength)
        {
            return SeasonOf(AbsoluteDay(year, month, dayOfMonth), seasonLength);
        }

        /// <summary>0-based day within the current season.</summary>
        public static int DayInSeason(long absoluteDay, int seasonLength)
        {
            seasonLength = NormaliseLength(seasonLength);
            var d = (absoluteDay - SpringStartDay) % seasonLength;
            return (int)((d + seasonLength) % seasonLength);
        }

        /// <summary>The first day of the year (0-based) that starts the given season at this season length.</summary>
        public static int FirstDayOfSeason(Season season, int seasonLength)
        {
            for (int d = 0; d < DaysPerYear; d++)
            {
                if (SeasonOf(d, seasonLength) == season && DayInSeason(d, seasonLength) == 0) return d;
            }
            return SpringStartDay; //not reachable: every allowed length divides the year
        }

        /// <summary>Colour tint (RGB) the season puts over outdoor light, and how strongly (0-1).</summary>
        public static void SeasonTint(Season season, out byte r, out byte g, out byte b, out float amount)
        {
            switch (season)
            {
                case Season.Spring: r = 238; g = 255; b = 232; amount = 0.45f; break;
                case Season.Summer: r = 255; g = 247; b = 222; amount = 0.45f; break;
                case Season.Autumn: r = 255; g = 224; b = 186; amount = 0.6f; break;
                default: r = 212; g = 225; b = 255; amount = 0.6f; break;
            }
        }
    }
}
