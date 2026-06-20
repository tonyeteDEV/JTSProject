using System.Globalization;

namespace JTS_App.Services;

public static class DisplayFormat
{
    private static readonly CultureInfo SpanishCulture = new("es-ES");
    private static readonly TimeZoneInfo SpainTimeZone = ResolveSpainTimeZone();

    public static string Date(DateTime value) => value.ToString("dd/MM/yyyy", SpanishCulture);

    public static string DateTimeFromUtc(DateTime value) =>
        ToSpainTime(value).ToString("dd/MM/yyyy HH:mm", SpanishCulture);

    public static string SummaryDate(DateTime value) => value.ToString("dd/MM/yyyy", SpanishCulture);

    public static DateTime SpainDayStartUtc(DateTime spainDate)
    {
        var localStart = DateTime.SpecifyKind(spainDate.Date, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localStart, SpainTimeZone);
    }

    public static DateTime SpainTimeToUtc(DateTime spainTime)
    {
        var local = DateTime.SpecifyKind(spainTime, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, SpainTimeZone);
    }

    public static DateTime ToSpainTime(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, SpainTimeZone);
    }

    private static TimeZoneInfo ResolveSpainTimeZone()
    {
        foreach (var id in new[] { "Romance Standard Time", "Europe/Madrid" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.Local;
    }
}
