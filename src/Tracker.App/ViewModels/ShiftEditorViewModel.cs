using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using Tracker.Core;

namespace Tracker.App.ViewModels;

public sealed class BreakViewModel : ObservableObject
{
    private string title = "Break";
    public string Title { get => title; set => Set(ref title, value); }
    public TimestampViewModel Start { get; }
    public TimestampViewModel End { get; }
    public BreakViewModel(BreakPeriod pause, TimeZoneInfo zone)
    {
        Start = new("Break start", pause.StartedUtc, zone);
        End = new("Break end", pause.EndedUtc, zone);
    }
}

public sealed class ShiftEditorViewModel : ObservableObject
{
    private readonly TrackerService service;
    private readonly ShiftSnapshot? original;
    private readonly TimeZoneInfo zone;
    private readonly long rate;
    private string preview = "";
    private string error = "";
    private bool valid;
    public TimestampViewModel Start { get; }
    public TimestampViewModel End { get; }
    public ObservableCollection<BreakViewModel> Breaks { get; } = [];
    public string Title => original is null ? "Add a forgotten shift" : "Edit shift times";
    public string Subtitle => $"Dates and times in {zone.Id}. Breaks are unpaid.";
    public string RateLabel => $"Saved rate: {MainViewModel.Money(rate / 100m)} / hour";
    public string Preview { get => preview; private set => Set(ref preview, value); }
    public string Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public bool HasNoBreaks => Breaks.Count == 0;
    public ICommand AddBreakCommand { get; }
    public ICommand RemoveBreakCommand { get; }
    public ICommand SaveCommand { get; }

    public ShiftEditorViewModel(TrackerService service, ShiftSnapshot? original, TimeZoneInfo zone,
        DateTime selectedMonth, Action<ShiftSnapshot> saved)
    {
        this.service = service;
        this.original = original;
        this.zone = zone;
        rate = original?.HourlyRateCents ?? service.GetSettings().DefaultHourlyRateCents;
        var now = LocalTimes.Display(service.NowUtc, zone);
        var day = selectedMonth.Year == now.Year && selectedMonth.Month == now.Month
            ? now.Date.AddDays(-1) : selectedMonth.AddMonths(1).AddDays(-1);
        Start = new("Shift start", original?.StartedUtc ?? LocalTimes.Resolve(day.AddHours(9), zone), zone);
        End = new("Shift end", original?.EndedUtc ?? LocalTimes.Resolve(day.AddHours(17), zone), zone);
        Start.PropertyChanged += Changed;
        End.PropertyChanged += Changed;
        if (original is not null)
            foreach (var pause in original.GetBreaks()) AddBreak(new(pause, zone));
        AddBreakCommand = new RelayCommand(_ =>
        {
            try
            {
                var start = Start.ResolveUtc();
                var end = End.ResolveUtc();
                if (end <= start) throw new TrackerValidationException("Set the shift start and end before adding a break.");
                var breakStart = Breaks.Count > 0 ? Breaks[^1].End.ResolveUtc().AddMinutes(15) : start.AddSeconds(TimeMath.Seconds(start, end) / 2);
                if (breakStart >= end) breakStart = start;
                var breakEnd = breakStart.AddMinutes(15);
                if (breakEnd > end) breakEnd = end;
                AddBreak(new(new(breakStart, breakEnd), zone));
                RefreshPreview();
            }
            catch (TrackerValidationException exception) { Error = exception.Message; }
        });
        RemoveBreakCommand = new RelayCommand(p =>
        {
            var item = (BreakViewModel)p!;
            item.Start.PropertyChanged -= Changed;
            item.End.PropertyChanged -= Changed;
            Breaks.Remove(item);
            RenumberBreaks();
            RefreshPreview();
        });
        SaveCommand = new RelayCommand(_ =>
        {
            try { saved(service.SaveShift(BuildEdit())); }
            catch (Exception exception) { App.Log(exception); Error = UiErrors.Describe(exception); }
        }, _ => valid);
        RefreshPreview();
    }

    private ShiftEdit BuildEdit() => new(original?.Id, Start.ResolveUtc(), End.ResolveUtc(),
        Breaks.Select(b => new BreakPeriod(b.Start.ResolveUtc(), b.End.ResolveUtc())).ToArray());

    private void Changed(object? sender, PropertyChangedEventArgs e) => RefreshPreview();

    private void AddBreak(BreakViewModel item)
    {
        item.Start.PropertyChanged += Changed;
        item.End.PropertyChanged += Changed;
        Breaks.Add(item);
        RenumberBreaks();
    }

    private void RenumberBreaks()
    {
        for (var i = 0; i < Breaks.Count; i++) Breaks[i].Title = $"Break {i + 1}";
        Raise(nameof(HasNoBreaks));
    }

    private void RefreshPreview()
    {
        try
        {
            var edit = ShiftValidation.Normalize(BuildEdit(), service.NowUtc);
            var worked = TimeMath.Seconds(edit.StartedUtc, edit.EndedUtc) - edit.Breaks.Sum(b => TimeMath.Seconds(b.StartedUtc, b.EndedUtc));
            Preview = $"{TimeMath.Duration(worked)} worked   ·   {MainViewModel.Money(TimeMath.Earnings(worked, rate))} earned";
            valid = true;
            Error = "";
        }
        catch (TrackerValidationException exception) { Preview = exception.Message; valid = false; }
        CommandManager.InvalidateRequerySuggested();
    }
}
