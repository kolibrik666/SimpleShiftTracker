using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Tracker.App.ViewModels;
using Tracker.Core;

namespace Tracker.App;

public partial class MainWindow : Window
{
    private readonly TrackerService service;
    private readonly TrackerDatabase database;
    private readonly MainViewModel viewModel;
    private readonly DispatcherTimer timer;
    private DateTime lastCheckpoint;
    private bool storageErrorShown;

    public MainWindow(TrackerService service, TrackerDatabase database, bool recovered)
    {
        InitializeComponent();
        this.service = service;
        this.database = database;
        viewModel = new(service, EditShift, ShowSettings, Export, text => MessageBox.Show(this, text, "Delete shift?", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        DataContext = viewModel;
        if (recovered) viewModel.Notify("An interrupted shift was recovered and paused at its last checkpoint. Review its times after ending it.");
        lastCheckpoint = service.NowUtc;
        timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, Tick, Dispatcher);
        SystemEvents.PowerModeChanged += PowerModeChanged;
    }

    private void Tick(object? sender, EventArgs e)
    {
        try
        {
            var now = service.NowUtc;
            if ((now - lastCheckpoint).TotalSeconds >= 15 || now < lastCheckpoint)
            {
                if (service.Checkpoint())
                {
                    viewModel.Refresh();
                    viewModel.Notify("A gap in tracking was detected. Your shift was paused at its last checkpoint; review its times if needed.");
                }
                lastCheckpoint = now;
            }
            viewModel.Tick();
            storageErrorShown = false;
        }
        catch (Exception exception)
        {
            if (!storageErrorShown)
            {
                App.Log(exception);
                viewModel.Notify(UiErrors.Describe(exception), true);
                storageErrorShown = true;
            }
        }
    }

    private ShiftSnapshot? EditShift(ShiftSnapshot? shift, DateTime month)
    {
        var window = new ShiftEditorWindow(service, shift, viewModel.Zone, month) { Owner = this };
        return window.ShowDialog() == true ? window.SavedShift : null;
    }

    private void ShowSettings() => new SettingsWindow(service, database.DatabasePath) { Owner = this }.ShowDialog();

    private void Export(MonthlyReport report, TimeZoneInfo zone)
    {
        var dialog = new SaveFileDialog { Title = "Export monthly work summary", Filter = "CSV files (*.csv)|*.csv", FileName = $"work-summary-{report.Year:D4}-{report.Month:D2}.csv", DefaultExt = ".csv", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, CsvExporter.Generate(report, zone), new UTF8Encoding(true));
            File.Move(temporary, dialog.FileName, true);
            viewModel.Notify($"Monthly summary exported to {Path.GetFileName(dialog.FileName)}.");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend) Dispatcher.Invoke(PauseForSystemEvent, DispatcherPriority.Send);
        else if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (service.RecoverInterruptedShift()) viewModel.Notify("Your shift was paused at its last checkpoint after the PC resumed.");
                viewModel.Refresh();
                lastCheckpoint = service.NowUtc;
            }
            catch (Exception exception) { App.Log(exception); viewModel.Notify(UiErrors.Describe(exception), true); }
        });
    }

    internal void PauseForSystemEvent()
    {
        try
        {
            if (service.GetActiveShift()?.State == ShiftState.Running)
            {
                var recovered = service.Checkpoint();
                if (!recovered) service.PauseShift();
                viewModel.Refresh();
                viewModel.Notify(recovered
                    ? "Your shift was automatically paused at its last checkpoint. Review its times if needed."
                    : "Your shift was automatically paused. Resume when you are ready.");
            }
        }
        catch (Exception exception) { App.Log(exception); viewModel.Notify(UiErrors.Describe(exception), true); }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        try { if (service.GetActiveShift()?.State == ShiftState.Running) service.PauseShift(); }
        catch (Exception exception)
        {
            App.Log(exception);
            e.Cancel = true;
            MessageBox.Show(this, "Your shift could not be paused and saved.\n\n" + UiErrors.Describe(exception) + "\n\nResolve the issue and close the app again.", "Unable to save shift", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        timer.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        base.OnClosed(e);
    }
}
