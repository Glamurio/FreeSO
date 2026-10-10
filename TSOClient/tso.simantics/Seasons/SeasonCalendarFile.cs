using System;
using System.Globalization;

namespace FSO.SimAntics.Seasons
{
    /// <summary>
    /// The text format of the neighbourhood calendar file: "year month day" on one line. The calendar is the one date every
    /// lot of the neighbourhood shares (each lot used to keep its own clock). Pure parsing, the game does the file reading.
    /// </summary>
    public static class SeasonCalendarFile
    {
        public static string Format(int year, int month, int day)
        {
            return year.ToString(CultureInfo.InvariantCulture) + " " + month.ToString(CultureInfo.InvariantCulture) + " " + day.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Reads a date. Returns false for anything that is not three numbers with a month of 1-12 and a day of 1-30.</summary>
        public static bool TryParse(string text, out int year, out int month, out int day)
        {
            year = month = day = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(new[] { ' ', '\t', '\r', '\n', ',', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out month)) return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out day)) return false;
            return year >= 0 && year < 32000 && month >= 1 && month <= SeasonModel.MonthsPerYear && day >= 1 && day <= SeasonModel.DaysPerMonth;
        }
    }
}
