using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Tracker.Core;

namespace Tracker.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly TrackerService service;
    private readonly Func<ShiftSnapshot?, DateTime, ShiftSnapshot?> edit;
    private readonly Action settings;
    private readonly Action<MonthlyReport, TimeZoneInfo> export;
    private readonly Func<string, bool> confirmDelete;
    private ShiftSnapshot? active;
    private TrackerSettings preferences;
    private MonthlyReport report;
    private DateTime month;
    private DateTime previousCurrentMonth;
    private string notice = "";
    private bool isError;
    public TimeZoneInfo Zone { get; }
    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    public MainViewModel(TrackerService service,
        Func<ShiftSnapshot?, DateTime, ShiftSnapshot?> edit, Action settings,
        Action<MonthlyReport, TimeZoneInfo> export, Func<string, bool> confirmDelete)
    {
        this.service = service;
        this.edit = edit;
        this.settings = settings;
        this.export = export;
        this.confirmDelete = confirmDelete;
        preferences = service.GetSettings();
        Zone = LocalTimes.FindZone(preferences.TimeZoneId);
        month = CurrentMonth;
        previousCurrentMonth = month;
        report = new(month.Year, month.Month, service.NowUtc, []);
        StartCommand = new RelayCommand(_ => Run(() => service.StartShift(), "Shift started."), _ => active is null);
        PauseCommand = new RelayCommand(_ => Run(() => service.PauseShift(), "Shift paused. Break time is excluded."), _ => active?.State == ShiftState.Running);
        ResumeCommand = new RelayCommand(_ => Run(() => service.ResumeShift(), "Back to work. Your shift is running."), _ => active?.State == ShiftState.Paused);
        EndCommand = new RelayCommand(_ => Run(() => service.EndShift(), "Shift saved to your history."), _ => active is not null);
        AddCommand = new RelayCommand(_ => Edit(null));
        EditCommand = new RelayCommand(p => Edit(((HistoryRowViewModel)p!).Row.Shift), p => p is HistoryRowViewModel { CanEdit: true });
        DeleteCommand = new RelayCommand(p => Delete((HistoryRowViewModel)p!), p => p is HistoryRowViewModel { CanEdit: true });
        SettingsCommand = new RelayCommand(_ => Run(() => settings()));
        ExportCommand = new RelayCommand(_ => Run(() => export(service.GetMonthlyReport(month.Year, month.Month, Zone), Zone)), _ => Rows.Count > 0);
        PreviousMonthCommand = new RelayCommand(_ => SelectedDate = month.AddMonths(-1), _ => month.Year > 1 || month.Month > 1);
        NextMonthCommand = new RelayCommand(_ => SelectedDate = month.AddMonths(1), _ => month < CurrentMonth);
        CurrentMonthCommand = new RelayCommand(_ => SelectedDate = CurrentMonth);
        DismissNoticeCommand = new RelayCommand(_ => Notice = "");
        Refresh();
    }

    private DateTime CurrentMonth
    {
        get
        {
            var now = LocalTimes.Display(service.NowUtc, Zone);
            return new DateTime(now.Year, now.Month, 1);
        }
    }

    public DateTime? SelectedDate
    {
        get => month;
        set
        {
            if (value is null) { Raise(); return; }
            var chosen = new DateTime(value.Value.Year, value.Value.Month, 1);
            if (chosen > CurrentMonth) chosen = CurrentMonth;
            if (Set(ref month, chosen))
            {
                Raise(nameof(MonthLabel));
                Run(RefreshReport);
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    public string MonthLabel => month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
    public string MonthTotal => TimeMath.Duration(report.WorkedSeconds);
    public string MonthEarnings => Money(report.Earnings);
    public string ShiftCount => $"{Rows.Count} {(Rows.Count == 1 ? "shift" : "shifts")}";
    public bool IsEmpty => Rows.Count == 0;
    public string Status => active?.State switch { ShiftState.Running => "Working", ShiftState.Paused => "On a break", _ => "Ready to work" };
    public bool IsRunning => active?.State == ShiftState.Running;
    public bool IsPaused => active?.State == ShiftState.Paused;
    public bool IsIdle => active is null;
    public string Timer => TimeMath.Duration(active?.WorkedSeconds(service.NowUtc) ?? 0);
    public string TimerDetail => active is null ? "Start a shift when you are ready."
        : $"Started {LocalTimes.Display(active.StartedUtc, Zone).ToString("ddd, dd MMM · HH:mm:ss", CultureInfo.InvariantCulture)}";
    public string CurrentPay => Money(active is null ? 0 : TimeMath.Earnings(active.WorkedSeconds(service.NowUtc), active.HourlyRateCents));
    public string CurrentRate => $"{Money((active?.HourlyRateCents ?? preferences.DefaultHourlyRateCents) / 100m)} / hour";
    public string DefaultRate => $"Hourly rate · {Money(preferences.DefaultHourlyRateCents / 100m)}";
    public string TimeZoneLabel => $"Local time · {preferences.TimeZoneId}";
    public string Notice { get => notice; private set { if (Set(ref notice, value)) Raise(nameof(HasNotice)); } }
    public bool HasNotice => Notice.Length > 0;
    public bool IsError { get => isError; private set => Set(ref isError, value); }

    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand PreviousMonthCommand { get; }
    public ICommand NextMonthCommand { get; }
    public ICommand CurrentMonthCommand { get; }
    public ICommand DismissNoticeCommand { get; }

    public void Notify(string message, bool error = false) { IsError = error; Notice = message; }

    public void Refresh()
    {
        active = service.GetActiveShift();
        preferences = service.GetSettings();
        RefreshReport();
        RaiseCurrent();
        Raise(nameof(DefaultRate));
        CommandManager.InvalidateRequerySuggested();
    }

    public void Tick()
    {
        var current = CurrentMonth;
        if (current != previousCurrentMonth)
        {
            if (month == previousCurrentMonth) SelectedDate = current;
            previousCurrentMonth = current;
        }
        RaiseCurrent();
        if (IsRunning) RefreshReport();
    }

    private void RaiseCurrent()
    {
        foreach (var name in new[] { nameof(Status), nameof(IsRunning), nameof(IsPaused), nameof(IsIdle),
            nameof(Timer), nameof(TimerDetail), nameof(CurrentPay), nameof(CurrentRate) }) Raise(name);
    }

    private void RefreshReport()
    {
        report = service.GetMonthlyReport(month.Year, month.Month, Zone);
        if (Rows.Count == report.Rows.Count && Rows.Select(r => r.Row.Shift.Id).SequenceEqual(report.Rows.Select(r => r.Shift.Id)))
        {
            for (var i = 0; i < Rows.Count; i++) Rows[i].Update(report.Rows[i]);
        }
        else
        {
            Rows.Clear();
            foreach (var row in report.Rows) Rows.Add(new(row, Zone));
        }
        foreach (var name in new[] { nameof(MonthTotal), nameof(MonthEarnings), nameof(ShiftCount), nameof(IsEmpty) }) Raise(name);
    }

    private void Edit(ShiftSnapshot? shift) => Run(() =>
    {
        var saved = edit(shift, month);
        if (saved is null) return;
        SelectedDate = LocalTimes.Display(saved.StartedUtc, Zone).Date;
        Notify(shift is null ? "Shift added to your history." : "Times updated. Your monthly totals have been recalculated.");
    });

    private void Delete(HistoryRowViewModel row)
    {
        if (!confirmDelete($"Delete the shift starting {LocalTimes.Display(row.Row.Shift.StartedUtc, Zone):dd MMM yyyy 'at' HH:mm:ss}?\n\nThis removes it from your work history and monthly totals.")) return;
        Run(() => service.DeleteShift(row.Row.Shift.Id), "Shift deleted.");
    }

    private void Run(Action action, string? success = null)
    {
        try { action(); Refresh(); if (success is not null) Notify(success); }
        catch (Exception exception) { App.Log(exception); Notify(UiErrors.Describe(exception), true); }
    }

    public static string Money(decimal amount) => "€" + TimeMath.RoundedMoney(amount).ToString("N2", CultureInfo.InvariantCulture);
}

public sealed class HistoryRowViewModel(ShiftReportRow row, TimeZoneInfo zone) : ObservableObject
{
    public ShiftReportRow Row { get; private set; } = row;
    public bool CanEdit => Row.Shift.State == ShiftState.Ended;
    public string Day => LocalTimes.Display(Row.Shift.StartedUtc, zone).ToString("ddd, dd MMM", CultureInfo.InvariantCulture);
    public string Detail => Row.Shift.State == ShiftState.Ended
        ? LocalTimes.Display(Row.Shift.StartedUtc, zone).ToString("yyyy", CultureInfo.InvariantCulture) : Row.Shift.State.ToString();
    public string Times => $"{LocalTimes.Display(Row.Shift.StartedUtc, zone):HH:mm:ss} → " +
        (Row.Shift.EndedUtc is { } end ? $"{LocalTimes.Display(end, zone):HH:mm:ss}" : Row.Shift.State.ToString());
    public string EndDate => Row.Shift.EndedUtc is { } end &&
        LocalTimes.Display(end, zone).Date != LocalTimes.Display(Row.Shift.StartedUtc, zone).Date
        ? $"Ends {LocalTimes.Display(end, zone):dd MMM yyyy}" : "";
    public string Breaks => TimeMath.Duration(Row.BreakSeconds);
    public string Worked => TimeMath.Duration(Row.WorkedSeconds);
    public string Earned => MainViewModel.Money(Row.Earnings);
    public string Rate => $"{MainViewModel.Money(Row.Shift.HourlyRateCents / 100m)} per hour";
    public void Update(ShiftReportRow updated) { Row = updated; Raise(null); }
}
