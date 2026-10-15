using System.Collections.ObjectModel;
using System.Globalization;
using Tracker.Core;

namespace Tracker.App.ViewModels;

public sealed record OffsetChoice(TimeSpan Offset, string Label);

public sealed class TimestampViewModel : ObservableObject
{
    private readonly TimeZoneInfo zone;
    private DateTime? date;
    private string timeText = "09:00:00";
    private OffsetChoice? selectedOffset;
    public ObservableCollection<OffsetChoice> Offsets { get; } = [];
    public string Label { get; }
    public bool IsAmbiguous => Offsets.Count > 1;
    public DateTime? Date { get => date; set { if (Set(ref date, value)) RebuildOffsets(); } }
    public string TimeText { get => timeText; set { if (Set(ref timeText, value)) RebuildOffsets(); } }
    public OffsetChoice? SelectedOffset { get => selectedOffset; set => Set(ref selectedOffset, value); }

    public TimestampViewModel(string label, DateTime utc, TimeZoneInfo zone)
    {
        Label = label;
        this.zone = zone;
        var local = LocalTimes.Display(utc, zone);
        date = local.Date;
        timeText = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        RebuildOffsets();
        selectedOffset = Offsets.FirstOrDefault(o => o.Offset == local.Offset);
    }

    private DateTime ParseLocal()
    {
        if (date is null || !TimeSpan.TryParseExact(timeText.Trim(), [@"h\:mm\:ss", @"hh\:mm\:ss", @"h\:mm", @"hh\:mm"],
            CultureInfo.InvariantCulture, out var time) || time < TimeSpan.Zero || time >= TimeSpan.FromDays(1))
            throw new TrackerValidationException($"{Label}: enter a date and a time such as 09:30:00.");
        return DateTime.SpecifyKind(date.Value.Date.Add(time), DateTimeKind.Unspecified);
    }

    public DateTime ResolveUtc() => LocalTimes.Resolve(ParseLocal(), zone, SelectedOffset?.Offset);

    private void RebuildOffsets()
    {
        var previous = selectedOffset?.Offset;
        Offsets.Clear();
        try
        {
            var local = ParseLocal();
            if (zone.IsAmbiguousTime(local))
            {
                var offsets = zone.GetAmbiguousTimeOffsets(local).OrderDescending().ToArray();
                for (var i = 0; i < offsets.Length; i++)
                {
                    var offset = offsets[i];
                    Offsets.Add(new(offset, $"UTC{(offset < TimeSpan.Zero ? "−" : "+")}{offset.Duration():hh\\:mm} · {(i == 0 ? "first" : "second")} occurrence"));
                }
            }
        }
        catch (TrackerValidationException) { /* The preview supplies the input error. */ }
        SelectedOffset = Offsets.FirstOrDefault(o => o.Offset == previous);
        Raise(nameof(IsAmbiguous));
    }
}
