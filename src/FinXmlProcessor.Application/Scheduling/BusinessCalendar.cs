using NodaTime;

namespace FinXmlProcessor.Application.Scheduling;

/// <summary>The business zone is fixed to America/New_York regardless of the host machine zone.</summary>
public static class BusinessCalendar
{
    public const string ZoneId = "America/New_York";

    public static DateTimeZone EasternZone { get; } = DateTimeZoneProviders.Tzdb[ZoneId];

    public static LocalTime DefaultRunTime { get; } = new(19, 0);

    public static DateOnly BusinessDateFor(Instant instant)
    {
        LocalDate date = instant.InZone(EasternZone).Date;
        return new DateOnly(date.Year, date.Month, date.Day);
    }

    public static LocalDate ToLocalDate(DateOnly date) => new(date.Year, date.Month, date.Day);

    public static DateOnly ToDateOnly(LocalDate date) => new(date.Year, date.Month, date.Day);
}
