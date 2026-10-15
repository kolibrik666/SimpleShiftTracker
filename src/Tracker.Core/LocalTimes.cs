namespace Tracker.Core;

public static class LocalTimes
{
    public static TimeZoneInfo FindZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) when (id == "Europe/Bratislava")
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Central Europe Standard Time");
        }
    }

    public static DateTime Resolve(DateTime local, TimeZoneInfo zone, TimeSpan? selectedOffset = null)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
            throw new TrackerValidationException("This local time does not exist because the clocks move forward. Choose another time.");
        if (zone.IsAmbiguousTime(local))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(local);
            if (selectedOffset is null || !offsets.Contains(selectedOffset.Value))
                throw new TrackerValidationException("This local time occurs twice. Choose its UTC offset.");
            return new DateTimeOffset(local, selectedOffset.Value).UtcDateTime;
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static DateTimeOffset Display(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(new DateTimeOffset(utc), zone);
}
