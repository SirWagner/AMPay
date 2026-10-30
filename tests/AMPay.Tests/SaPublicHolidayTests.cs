using AMPay.Domain.Reporting;

namespace AMPay.Tests;

public class SaPublicHolidayTests
{
    [Theory]
    [InlineData(2026, 4, 5)]
    [InlineData(2027, 3, 28)]
    [InlineData(2025, 4, 20)]
    public void EasterSunday_IsRight(int year, int month, int day) =>
        Assert.Equal(new DateTime(year, month, day), SaPublicHolidays.EasterSunday(year));

    [Theory]
    [InlineData("2026-04-03")] // Good Friday
    [InlineData("2026-04-06")] // Family Day
    [InlineData("2026-08-09")] // National Women's Day, a Sunday...
    [InlineData("2026-08-10")] // ...observed on the Monday
    [InlineData("2026-12-16")]
    [InlineData("2024-05-29")] // declared: election day
    public void Holidays_AreRecognised(string date) =>
        Assert.True(SaPublicHolidays.IsHoliday(DateTime.Parse(date)));

    [Fact]
    public void AnOrdinaryWeekday_IsABusinessDay_AndAWeekendIsNot()
    {
        Assert.True(SaPublicHolidays.IsBusinessDay(new DateTime(2026, 10, 30)));  // Friday
        Assert.False(SaPublicHolidays.IsBusinessDay(new DateTime(2026, 10, 31))); // Saturday
    }

    [Fact]
    public void FiveBusinessDays_SkipTheEasterWeekend()
    {
        // Issued Thursday 2 April 2026: Good Friday and Family Day do not count.
        Assert.Equal(new DateTime(2026, 4, 13), SaPublicHolidays.AddBusinessDays(new DateTime(2026, 4, 2), 5));
    }

    [Fact]
    public void BusinessDaysBefore_SkipsHolidaysToo() =>
        // Five business days before Tuesday 7 April 2026 (6 and 3 April are holidays): 2, 1, 31, 30, 27.
        Assert.Equal(new DateTime(2026, 3, 27), Sast.BusinessDaysBefore(new DateTime(2026, 4, 7), 5));
}
