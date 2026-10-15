using System.Globalization;
using System.Text;

namespace Tracker.Core;

public static class CsvExporter
{
    public static string Generate(MonthlyReport report, TimeZoneInfo zone)
    {
        var text = new StringBuilder();
        void Row(params string[] values) => text.AppendLine(string.Join(",", values.Select(v =>
            "\"" + v.Replace("\"", "\"\"") + "\"")));
        string Date(DateTime utc) => LocalTimes.Display(utc, zone).ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
        string Money(decimal value) => TimeMath.RoundedMoney(value).ToString("0.00", CultureInfo.InvariantCulture);
        Row("Month", "Shift ID", "Shift start", "Shift end", "Period start", "Period end", "Status",
            "Break time", "Worked time", "Hourly rate EUR", "Earnings EUR");
        foreach (var row in report.Rows)
            Row($"{report.Year:D4}-{report.Month:D2}", row.Shift.Id.ToString(), Date(row.Shift.StartedUtc),
                row.Shift.EndedUtc is { } end ? Date(end) : "", Date(row.PeriodStartUtc), Date(row.PeriodEndUtc),
                row.Shift.State.ToString(), TimeMath.Duration(row.BreakSeconds), TimeMath.Duration(row.WorkedSeconds),
                Money(row.Shift.HourlyRateCents / 100m), Money(row.Earnings));
        Row("TOTAL", "", "", "", "", "", "", TimeMath.Duration(report.Rows.Sum(r => r.BreakSeconds)),
            TimeMath.Duration(report.WorkedSeconds), "", Money(report.Earnings));
        Row("Generated at", Date(report.GeneratedUtc), "Timezone", zone.Id);
        Row("Earnings are calculated from exact worked seconds; totals are rounded independently to cents.");
        return text.ToString();
    }
}
