using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Tracker.App;
using Tracker.App.ViewModels;
using Tracker.Core;

namespace Tracker.UiSmoke;

internal static class Program
{
    private static readonly List<string> BindingErrors = [];
    private static int result = 1;
    private static string output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        var index = Array.IndexOf(args, "--data-dir");
        if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("UI smoke tests require --data-dir with an isolated test folder.");
        output = Path.GetFullPath(args[index + 1]);
        if (File.Exists(Path.Combine(output, "tracker.db"))) throw new InvalidOperationException("Use a fresh test folder to avoid touching existing data.");
        Directory.CreateDirectory(output);
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingListener());
        var app = new Tracker.App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        app.Startup += async (_, _) =>
        {
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            try { await Exercise(app); result = 0; }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                File.WriteAllText(Path.Combine(output, "failure.txt"), exception.ToString());
            }
            finally { app.Shutdown(result); }
        };
        app.Run();
        return result;
    }

    private static async Task Exercise(Application app)
    {
        var main = app.MainWindow as MainWindow ?? throw new InvalidOperationException("App startup did not produce its main window.");
        var vm = (MainViewModel)main.DataContext;
        var db = new TrackerDatabase(Path.Combine(output, "tracker.db"));
        var service = new TrackerService(db);
        var zone = vm.Zone;
        var today = LocalTimes.Display(service.NowUtc, zone).Date;
        ShiftSnapshot? editable = null;
        for (var i = 1; i <= 12; i++)
        {
            var day = today.AddDays(-i);
            var start = LocalTimes.Resolve(day.AddHours(9), zone);
            var end = LocalTimes.Resolve(day.AddHours(17), zone);
            var pause = new BreakPeriod(LocalTimes.Resolve(day.AddHours(12), zone), LocalTimes.Resolve(day.AddHours(12.5), zone));
            var added = service.SaveShift(new(null, start, end, [pause]));
            editable ??= added;
        }
        vm.Refresh();
        await Settle();
        Capture(main, "main");
        Check(vm.Rows.Count > 0, "History should contain seeded shifts.");
        Check(vm.StartCommand.CanExecute(null), "Start should be available while idle.");
        vm.StartCommand.Execute(null);
        Check(vm.IsRunning, "Start must update the UI to running.");
        var runningBeforeMinimize = service.GetActiveShift()!;
        main.WindowState = WindowState.Minimized;
        await Settle();
        Check(!main.IsVisible && !main.ShowInTaskbar, "Minimizing must hide the window and remove its taskbar button.");
        Check(main.IsTrayIconVisible, "A minimized window must remain accessible through its tray icon.");
        await Task.Delay(16000);
        var runningInTray = service.GetActiveShift()!;
        Check(runningInTray.State == ShiftState.Running, "Minimizing must not pause the shift.");
        Check(runningInTray.WorkedSeconds(service.NowUtc) >= 15, "Worked time must continue in the tray.");
        Check(runningInTray.LastCheckpointUtc > runningBeforeMinimize.LastCheckpointUtc, "Checkpoints must continue while the window is hidden.");
        main.RestoreFromTray();
        await Settle();
        Check(main.IsVisible && main.ShowInTaskbar && !main.IsTrayIconVisible, "Restoring must show the window and taskbar button and hide the tray icon.");
        Check(main.WindowState == WindowState.Normal, "A normal window must restore normally.");
        main.WindowState = WindowState.Maximized;
        main.WindowState = WindowState.Minimized;
        await Settle();
        main.RestoreFromTray();
        Check(main.WindowState == WindowState.Maximized, "A maximized window must restore maximized.");
        main.WindowState = WindowState.Normal;
        ExerciseActiveEditor(app, main, vm, service);
        await Task.Delay(1200);
        vm.PauseCommand.Execute(null);
        Check(vm.IsPaused, "Pause must update the UI.");
        var pausedTime = vm.Timer;
        main.WindowState = WindowState.Minimized;
        await Settle();
        await Task.Delay(1200);
        Check(vm.Timer == pausedTime, "Paused timer must stay still.");
        Check(service.GetActiveShift()!.State == ShiftState.Paused, "A paused shift must remain paused in the tray.");
        Check(SingleInstanceActivation.TryRequestRestore(SingleInstanceActivation.IdentityFor(output)), "The existing instance must accept a restore request.");
        for (var attempt = 0; attempt < 20 && !main.IsVisible; attempt++) await Task.Delay(50);
        Check(main.IsVisible && main.ShowInTaskbar && !main.IsTrayIconVisible, "A second-launch restore request must bring back the existing window.");
        vm.ResumeCommand.Execute(null);
        await Task.Delay(1200);
        vm.EndCommand.Execute(null);
        Check(vm.IsIdle, "End must return to idle.");
        Check(service.GetActiveShift() is null, "End must persist a completed shift.");

        var editor = new ShiftEditorWindow(service, editable, zone, new(today.Year, today.Month, 1)) { Owner = main };
        Exception? dialogError = null;
        editor.ContentRendered += (_, _) => editor.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var editVm = (ShiftEditorViewModel)editor.DataContext;
                Check(editVm.SaveCommand.CanExecute(null), "Existing timestamps must parse correctly.");
                editVm.End.TimeText = "25:00:00";
                Check(!editVm.SaveCommand.CanExecute(null), "An invalid time must disable Save.");
                editVm.End.TimeText = "17:00:00";
                var startPicker = Descendants<DatePicker>(editor).Single(p => ReferenceEquals(p.DataContext, editVm.Start));
                var previousDate = editVm.Start.Date;
                startPicker.Text = "invalid-date";
                Check(!editVm.SaveCommand.CanExecute(null), "An invalid date must disable Save.");
                editVm.Start.Date = previousDate;
                editVm.Breaks[0].End.TimeText = "13:00:00";
                Check(editVm.Preview.Contains("07:00:00", StringComparison.Ordinal), "Break edit must update the preview.");
                Check(editVm.Preview.Contains("€70.00", StringComparison.Ordinal), "Preview should retain the saved wage.");
                editVm.AddBreakCommand.Execute(null);
                Check(editVm.Breaks.Count == 2, "Add break must create another editor.");
                editVm.RemoveBreakCommand.Execute(editVm.Breaks[1]);
                editor.UpdateLayout();
                Capture(editor, "editor");
                editVm.SaveCommand.Execute(null);
                Check(editor.SavedShift is not null, "Save must persist and close the dialog.");
            }
            catch (Exception exception) { dialogError = exception; editor.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        Check(editor.ShowDialog() == true || dialogError is not null, "Editor should save successfully.");
        if (dialogError is not null) throw dialogError;

        // Exercise the two occurrences of a daylight-saving timestamp in real controls.
        var fallDay = new DateTime(today.Year - 1, 10, 31);
        while (fallDay.DayOfWeek != DayOfWeek.Sunday) fallDay = fallDay.AddDays(-1);
        var repeatedTime = fallDay.AddHours(2.5);
        var offsets = zone.GetAmbiguousTimeOffsets(repeatedTime).OrderDescending().ToArray();
        var dstShift = service.SaveShift(new(null, LocalTimes.Resolve(repeatedTime, zone, offsets[0]),
            LocalTimes.Resolve(repeatedTime, zone, offsets[1]), []));
        var dstEditor = new ShiftEditorWindow(service, dstShift, zone, fallDay) { Owner = main };
        dstEditor.ContentRendered += (_, _) => dstEditor.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var editVm = (ShiftEditorViewModel)dstEditor.DataContext;
                Check(editVm.Start.IsAmbiguous && editVm.End.IsAmbiguous, "Ambiguous times must show offset choices.");
                var startOffset = Descendants<ComboBox>(dstEditor).Single(c => ReferenceEquals(c.DataContext, editVm.Start));
                var endOffset = Descendants<ComboBox>(dstEditor).Single(c => ReferenceEquals(c.DataContext, editVm.End));
                startOffset.SelectedIndex = -1;
                Check(!editVm.SaveCommand.CanExecute(null), "Ambiguous time needs an explicit offset.");
                startOffset.SelectedIndex = 0;
                endOffset.SelectedIndex = 1;
                Check(editVm.Preview.Contains("01:00:00", StringComparison.Ordinal), "UTC offset choices must preserve one actual worked hour.");
                dstEditor.UpdateLayout();
                Capture(dstEditor, "daylight-saving-editor");
                editVm.SaveCommand.Execute(null);
            }
            catch (Exception exception) { dialogError = exception; dstEditor.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        dstEditor.ShowDialog();
        if (dialogError is not null) throw dialogError;
        Check(editor.SavedShift!.WorkedSeconds(service.NowUtc) == 7 * 3600, "Edited work must be persisted.");

        var add = new ShiftEditorWindow(service, null, zone, new(today.Year, today.Month, 1)) { Owner = main };
        add.ContentRendered += (_, _) => add.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var editVm = (ShiftEditorViewModel)add.DataContext;
                editVm.SaveCommand.Execute(null);
                Check(editVm.HasError, "Overlapping manual entry must display an error.");
                editVm.Start.Date = today.AddDays(-20);
                editVm.End.Date = today.AddDays(-20);
                editVm.SaveCommand.Execute(null);
                Check(add.SavedShift is not null, "Corrected manual entry must save.");
            }
            catch (Exception exception) { dialogError = exception; add.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        add.ShowDialog();
        if (dialogError is not null) throw dialogError;

        var settings = new SettingsWindow(service, db.DatabasePath) { Owner = main };
        settings.ContentRendered += (_, _) => settings.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ((TextBox)settings.FindName("RateInput")).Text = "12,50";
                Descendants<Button>(settings).Single(b => Equals(b.Content, "Save rate")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(service.GetSettings().DefaultHourlyRateCents == 1250, "Settings must accept a comma decimal wage.");
            }
            catch (Exception exception) { dialogError = exception; settings.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        settings.ShowDialog();
        if (dialogError is not null) throw dialogError;
        vm.Refresh();
        Check(vm.DefaultRate.Contains("12.50", StringComparison.Ordinal), "Settings changes must refresh the main view.");

        var currentMonth = vm.SelectedDate;
        vm.PreviousMonthCommand.Execute(null);
        Check(vm.SelectedDate < currentMonth, "Previous month navigation must work.");
        vm.CurrentMonthCommand.Execute(null);
        Check(vm.SelectedDate == currentMonth, "This month must restore the current month.");
        var grid = Descendants<DataGrid>(main).Single();
        var scroll = Descendants<ScrollViewer>(grid).First();
        await Settle();
        if (vm.Rows.Count >= 5)
        {
            Check(scroll.ScrollableHeight > 0, "History must scroll when rows exceed the available space.");
            scroll.ScrollToEnd();
            await Settle();
            Check(scroll.VerticalOffset > 0, "History scrolling must move to older shifts.");
        }
        var startButton = Descendants<Button>(main).Single(b => Equals(b.Content, "▶  Start shift"));
        main.Activate();
        await Settle();
        var focused = startButton.Focus();
        await Settle();
        Check(focused && startButton.IsKeyboardFocusWithin, "A restored window must accept keyboard focus.");
        Check(startButton.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)), "Keyboard navigation must move between controls.");
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await Settle();
        Capture(main, "minimum-size");

        vm.StartCommand.Execute(null);
        await Task.Delay(1200);
        main.PauseForSystemEvent();
        Check(vm.IsPaused, "A system suspend/shutdown notification must pause the UI and stored shift.");
        var systemPaused = service.GetActiveShift()!.WorkedSeconds(service.NowUtc);
        await Task.Delay(1200);
        Check(service.GetActiveShift()!.WorkedSeconds(service.NowUtc) == systemPaused, "System-paused time must stay unpaid.");
        vm.ResumeCommand.Execute(null);
        main.Close();
        Check(service.GetActiveShift()?.State == ShiftState.Paused, "Closing the window must persist a paused shift.");
        var paused = service.GetActiveShift()!.WorkedSeconds(service.NowUtc);
        await Task.Delay(1200);
        var reopened = new MainWindow(service, db, false);
        app.MainWindow = reopened;
        reopened.Show();
        var reopenedVm = (MainViewModel)reopened.DataContext;
        Check(reopenedVm.IsPaused, "Reopened UI must restore the paused shift.");
        Check(service.GetActiveShift()!.WorkedSeconds(service.NowUtc) == paused, "Offline time must stay unpaid.");
        reopenedVm.EndCommand.Execute(null);
        await Settle();
        Capture(reopened, "reopened");
        reopenedVm.StartCommand.Execute(null);
        await Task.Delay(1200);
        reopened.WindowState = WindowState.Minimized;
        await Settle();
        Check(reopened.IsTrayIconVisible, "The reopened window must also support minimize-to-tray.");
        reopened.ExitFromTray();
        Check(service.GetActiveShift()?.State == ShiftState.Paused, "Exiting from the tray must pause and save the unfinished shift.");
        Check(!reopened.IsTrayIconVisible, "Exiting must dispose the tray icon.");
        Check(BindingErrors.Count == 0, "WPF binding errors:\n" + string.Join("\n", BindingErrors));
        File.WriteAllText(Path.Combine(output, "passed.txt"), "Passed: startup, history, timer controls, active timer editing while running and paused, live editor preview, edit cancellation, active history controls, minimize-to-tray, hidden-window checkpoints, normal/maximized restore, second-launch restore signal, paused-shift minimize, tray exit and cleanup, editor and breaks, invalid dates/times, daylight-saving offset choices, overlap validation, manual entries, wage settings, month navigation, scrolling, keyboard navigation, system pause, closing, reopening, and WPF bindings.\n");
        Console.WriteLine("UI smoke passed: " + output);
    }

    private static void ExerciseActiveEditor(Application app, MainWindow main, MainViewModel vm, TrackerService service)
    {
        var original = service.GetActiveShift()!;
        Check(vm.EditCurrentCommand.CanExecute(null), "The current running timer must be editable.");
        var row = vm.Rows.Single(r => r.Row.Shift.Id == original.Id);
        Check(vm.EditCommand.CanExecute(row) && !vm.DeleteCommand.CanExecute(row), "An active history row can be edited but cannot be deleted.");
        Exception? dialogError = null;
        main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            var editor = app.Windows.OfType<ShiftEditorWindow>().Single();
            try
            {
                await Settle();
                var editVm = (ShiftEditorViewModel)editor.DataContext;
                Check(editVm.IsActive && editVm.IsRunning && !editVm.IsCompleted, "A running shift needs the active editor.");
                Check(!Descendants<DatePicker>(editor).Any(p => ReferenceEquals(p.DataContext, editVm.End) && p.IsVisible), "An unfinished shift must not expose a completed end time.");
                SetTimestamp(editVm.Start, service.NowUtc.AddHours(1), vm.Zone);
                Check(!editVm.SaveCommand.CanExecute(null), "A future start must disable Save.");
                SetTimestamp(editVm.Start, original.StartedUtc.AddHours(-1), vm.Zone);
                editVm.AddBreakCommand.Execute(null);
                Check(editVm.Breaks.Count == 1, "A running shift can add a forgotten break.");
                SetTimestamp(editVm.Breaks[0].Start, original.StartedUtc.AddMinutes(-30), vm.Zone);
                SetTimestamp(editVm.Breaks[0].End, original.StartedUtc.AddMinutes(-15), vm.Zone);
                var preview = editVm.Preview;
                await Task.Delay(2200);
                Check(editVm.Preview != preview, "The editor preview must keep counting while working.");
                Check(service.GetActiveShift()!.State == ShiftState.Running, "Opening the editor must keep the shift running.");
                Capture(editor, "running-editor");
                Check(editVm.SaveCommand.CanExecute(null), "Valid running times must be saveable.");
                editVm.SaveCommand.Execute(null);
                Check(editor.SavedShift?.State == ShiftState.Running, "Saving the current timer must leave it running.");
            }
            catch (Exception exception) { dialogError = exception; editor.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        vm.EditCurrentCommand.Execute(null); // Exercise the actual main-window command and modal dialog.
        if (dialogError is not null) throw dialogError;
        Check(vm.IsRunning, "Saving must refresh the main view without stopping its timer.");
        var edited = service.GetActiveShift()!;
        Check(edited.StartedUtc == original.StartedUtc.AddHours(-1), "The corrected start must persist.");
        Check(edited.WorkedSeconds(service.NowUtc) == original.WorkedSeconds(service.NowUtc) + 2700,
            "Saving must include the time spent in the editor and subtract the edited break.");

        main.Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = app.Windows.OfType<ShiftEditorWindow>().Single();
            try
            {
                var editVm = (ShiftEditorViewModel)editor.DataContext;
                SetTimestamp(editVm.Start, edited.StartedUtc.AddHours(-5), vm.Zone);
                editor.DialogResult = false;
            }
            catch (Exception exception) { dialogError = exception; editor.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        vm.EditCurrentCommand.Execute(null);
        if (dialogError is not null) throw dialogError;
        var afterCancel = service.GetActiveShift()!;
        Check(afterCancel.StartedUtc == edited.StartedUtc && afterCancel.WorkPeriods.SequenceEqual(edited.WorkPeriods),
            "Cancel must preserve the saved times and breaks.");

        vm.PauseCommand.Execute(null);
        var paused = service.GetActiveShift()!;
        main.Dispatcher.BeginInvoke(new Action(() =>
        {
            var editor = app.Windows.OfType<ShiftEditorWindow>().Single();
            try
            {
                var editVm = (ShiftEditorViewModel)editor.DataContext;
                Check(editVm.IsActive && !editVm.IsRunning, "A paused shift must also use the active editor.");
                SetTimestamp(editVm.Start, paused.StartedUtc.AddMinutes(-5), vm.Zone);
                Capture(editor, "paused-editor");
                editVm.SaveCommand.Execute(null);
                Check(editor.SavedShift?.State == ShiftState.Paused, "Saving a paused shift must keep it paused.");
            }
            catch (Exception exception) { dialogError = exception; editor.Close(); }
        }), DispatcherPriority.ApplicationIdle);
        vm.EditCurrentCommand.Execute(null);
        if (dialogError is not null) throw dialogError;
        Check(vm.IsPaused && service.GetActiveShift()!.WorkedSeconds(service.NowUtc) == paused.WorkedSeconds(service.NowUtc) + 300,
            "A paused edit must update worked time without billing the current break.");
        vm.ResumeCommand.Execute(null);
    }

    private static void SetTimestamp(TimestampViewModel target, DateTime utc, TimeZoneInfo zone)
    {
        var local = LocalTimes.Display(utc, zone);
        target.Date = local.Date;
        target.TimeText = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static async Task Settle() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth + content.Margin.Left + content.Margin.Right),
            (int)Math.Ceiling(content.ActualHeight + content.Margin.Top + content.Margin.Bottom), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(file);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BindingListener : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (message?.Contains("Error", StringComparison.OrdinalIgnoreCase) == true) BindingErrors.Add(message);
        }
    }
}
