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
        await Task.Delay(1200);
        vm.PauseCommand.Execute(null);
        Check(vm.IsPaused, "Pause must update the UI.");
        var pausedTime = vm.Timer;
        await Task.Delay(1200);
        Check(vm.Timer == pausedTime, "Paused timer must stay still.");
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
        startButton.Focus();
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
        reopened.Close();
        Check(BindingErrors.Count == 0, "WPF binding errors:\n" + string.Join("\n", BindingErrors));
        File.WriteAllText(Path.Combine(output, "passed.txt"), "Passed: startup, history, timer controls, editor and breaks, invalid dates/times, daylight-saving offset choices, overlap validation, manual entries, wage settings, month navigation, scrolling, keyboard navigation, system pause, closing, reopening, and WPF bindings.\n");
        Console.WriteLine("UI smoke passed: " + output);
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
