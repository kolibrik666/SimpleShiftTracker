using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Markup;
using System.Windows.Controls;
using Tracker.App.ViewModels;
using Microsoft.Win32;
using Tracker.Core;

namespace Tracker.App;

public partial class App : Application
{
    private Mutex? instanceMutex;
    private SingleInstanceActivation? instanceActivation;
    private bool ownsMutex;
    private string? themeOverride;
    internal static string DataDirectory { get; private set; } = "";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-GB");
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
                new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("en-GB")));
            DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SimpleShiftTracker");
            for (var i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--data-dir" && i + 1 < e.Args.Length) DataDirectory = Path.GetFullPath(e.Args[++i]);
                else if (e.Args[i] == "--theme" && i + 1 < e.Args.Length && e.Args[i + 1] is "light" or "dark") themeOverride = e.Args[++i];
                else throw new ArgumentException("Supported options: --data-dir <folder> and --theme <light|dark>.");
            }
            DataDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DataDirectory));
            var identity = SingleInstanceActivation.IdentityFor(DataDirectory);
            instanceMutex = new Mutex(true, @"Local\SimpleShiftTracker-" + identity, out ownsMutex);
            if (!ownsMutex)
            {
                if (!SingleInstanceActivation.TryRequestRestore(identity))
                    MessageBox.Show("Shift Tracker is already open for this data folder. Switch to its existing window or restore it from the tray icon.", "Shift Tracker", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }
            instanceActivation = new SingleInstanceActivation(identity, () =>
            {
                if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                    Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.RestoreFromTray());
            });
            ApplyPalette();
            SystemEvents.UserPreferenceChanged += PreferenceChanged;
            var database = new TrackerDatabase(Path.Combine(DataDirectory, "tracker.db"));
            database.Initialize(hasExclusiveStartupLock: true);
            var service = new TrackerService(database);
            var recovered = service.RecoverInterruptedShift();
            var window = new MainWindow(service, database, recovered);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            Log(exception);
            MessageBox.Show($"Shift Tracker could not open your data.\n\n{UiErrors.Describe(exception)}\n\nData folder: {DataDirectory}\n\nYour existing database has been preserved.", "Unable to open Shift Tracker", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void PreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyPalette);

    private void TimestampPickerLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is DatePicker picker)
        {
            picker.DateValidationError -= InvalidTimestampDate;
            picker.DateValidationError += InvalidTimestampDate;
        }
    }

    private void InvalidTimestampDate(object? sender, DatePickerDateValidationErrorEventArgs e)
    {
        e.ThrowException = false;
        if (sender is DatePicker { DataContext: TimestampViewModel timestamp }) timestamp.Date = null;
    }

    private void ApplyPalette()
    {
        var dark = themeOverride == "dark" || (themeOverride is null && Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0);
#pragma warning disable WPF0001
        ThemeMode = dark ? System.Windows.ThemeMode.Dark : System.Windows.ThemeMode.Light;
#pragma warning restore WPF0001
        var colors = new Dictionary<string, string>
        {
            ["AppBackground"] = dark ? "#111923" : "#F4F6F8", ["Surface"] = dark ? "#1B2735" : "#FFFFFF",
            ["SurfaceAlt"] = dark ? "#202F40" : "#F8FAFB", ["Edge"] = dark ? "#344354" : "#E2E7EB",
            ["PrimaryText"] = dark ? "#EFF5F7" : "#1C2D37", ["SecondaryText"] = dark ? "#A4B4C3" : "#687983",
            ["Accent"] = dark ? "#6FD2B8" : "#176E5D", ["OnAccent"] = dark ? "#0D3026" : "#FFFFFF",
            ["AccentTint"] = dark ? "#233F39" : "#EAF5F0", ["Danger"] = dark ? "#FFADAD" : "#B43B3B"
        };
        foreach (var (name, color) in colors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            Resources[name] = brush;
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window) window.PauseForSystemEvent();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= PreferenceChanged;
        instanceActivation?.Dispose();
        if (ownsMutex) instanceMutex?.ReleaseMutex();
        instanceMutex?.Dispose();
        base.OnExit(e);
    }

    internal static void Log(Exception exception)
    {
        try
        {
            if (string.IsNullOrEmpty(DataDirectory)) return;
            Directory.CreateDirectory(DataDirectory);
            File.AppendAllText(Path.Combine(DataDirectory, "tracker.log"), $"{DateTimeOffset.UtcNow:O} {exception}\n\n");
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
}
