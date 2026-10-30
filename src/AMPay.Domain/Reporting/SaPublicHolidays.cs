namespace AMPay.Domain.Reporting;

/// <summary>
/// South African public holidays under the Public Holidays Act 36 of 1994, and business days
/// built on them. Used wherever a period is counted in business days: a quote binding for five
/// business days, the cooling-off window.
/// <para>
/// The Act's rule: a holiday that falls on a Sunday is observed on the Monday after. Good Friday
/// and Family Day follow Easter. Holidays the President declares for a single year (election
/// days, for instance) are not in the Act; add them to <see cref="Declared"/> when gazetted.
/// </para>
/// </summary>
public static class SaPublicHolidays
{
    /// <summary>One-off holidays declared by proclamation. Edit when one is gazetted.</summary>
    public static readonly HashSet<DateTime> Declared = new()
    {
        new DateTime(2024, 5, 29), // national and provincial elections
    };

    private static readonly (int Month, int Day, string Name)[] Fixed =
    {
        (1, 1, "New Year's Day"),
        (3, 21, "Human Rights Day"),
        (4, 27, "Freedom Day"),
        (5, 1, "Workers' Day"),
        (6, 16, "Youth Day"),
        (8, 9, "National Women's Day"),
        (9, 24, "Heritage Day"),
        (12, 16, "Day of Reconciliation"),
        (12, 25, "Christmas Day"),
        (12, 26, "Day of Goodwill")
    };

    /// <summary>Every public holiday observed in <paramref name="year"/>, with its name.</summary>
    public static IReadOnlyList<(DateTime Date, string Name)> For(int year)
    {
        var list = new List<(DateTime, string)>();
        var easter = EasterSunday(year);
        list.Add((easter.AddDays(-2), "Good Friday"));
        list.Add((easter.AddDays(1), "Family Day"));

        foreach (var (month, day, name) in Fixed)
        {
            var date = new DateTime(year, month, day);
            list.Add((date, name));
            // Sunday holidays move to Monday. (Day of Goodwill on a Monday after a Sunday
            // Christmas is already a holiday, so that Monday is simply observed for both.)
            if (date.DayOfWeek == DayOfWeek.Sunday) list.Add((date.AddDays(1), name + " (observed)"));
        }

        list.AddRange(Declared.Where(d => d.Year == year).Select(d => (d, "Declared public holiday")));
        return list.OrderBy(h => h.Item1).ToList();
    }

    public static bool IsHoliday(DateTime date) => For(date.Year).Any(h => h.Date == date.Date);

    public static bool IsBusinessDay(DateTime date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(date);

    /// <summary>The date <paramref name="days"/> business days after <paramref name="date"/> (forwards, or backwards when negative).</summary>
    public static DateTime AddBusinessDays(DateTime date, int days)
    {
        var d = date.Date;
        var step = days >= 0 ? 1 : -1;
        for (var counted = 0; counted < Math.Abs(days);)
        {
            d = d.AddDays(step);
            if (IsBusinessDay(d)) counted++;
        }
        return d;
    }

    /// <summary>Easter Sunday by the anonymous Gregorian algorithm (Meeus/Jones/Butcher).</summary>
    public static DateTime EasterSunday(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = (19 * a + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7;
        int m = (a + 11 * h + 22 * l) / 451;
        int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateTime(year, month, day);
    }
}
