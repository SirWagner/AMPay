namespace AMPay.Domain.Reporting;

/// <summary>
/// South African Standard Time for reporting boundaries. SAST is UTC+2 all year (no daylight
/// saving), so a fixed offset is exact - and it does not depend on the host's time-zone database.
/// <para>
/// Instants (CreatedUtc, DisbursedUtc, CompletedUtc) are stored in UTC; calendar dates
/// (instalment due dates) are already South African dates. A "month" is the SAST calendar
/// month: a payment at 23:30 UTC on 31 October happened at 01:30 on 1 November in Johannesburg.
/// </para>
/// </summary>
public static class Sast
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(2);

    public static DateTime ToLocal(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(Offset);

    /// <summary>The SAST calendar date of a UTC instant.</summary>
    public static DateTime DateOf(DateTime utc) => ToLocal(utc).Date;

    /// <summary>The UTC instant at which SAST midnight starts this SAST date.</summary>
    public static DateTime StartUtc(DateTime sastDate) =>
        DateTime.SpecifyKind(sastDate.Date - Offset, DateTimeKind.Utc);

    public static DateTime MonthStart(DateTime sastDate) => new(sastDate.Year, sastDate.Month, 1);

    /// <summary>
    /// The date <paramref name="businessDays"/> business days before <paramref name="sastDate"/>:
    /// weekdays that are not South African public holidays.
    /// </summary>
    public static DateTime BusinessDaysBefore(DateTime sastDate, int businessDays) =>
        SaPublicHolidays.AddBusinessDays(sastDate, -businessDays);
}
