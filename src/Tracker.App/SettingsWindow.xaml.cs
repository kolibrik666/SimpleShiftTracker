using System.Globalization;
using System.Windows;
using Tracker.Core;

namespace Tracker.App;

public partial class SettingsWindow : Window
{
    private readonly TrackerService service;
    public SettingsWindow(TrackerService service, string databasePath)
    {
        InitializeComponent();
        this.service = service;
        var settings = service.GetSettings();
        RateInput.Text = (settings.DefaultHourlyRateCents / 100m).ToString("0.00", CultureInfo.InvariantCulture);
        DatabasePathText.Text = databasePath;
        ZoneText.Text = "Reporting timezone: " + settings.TimeZoneId;
        Loaded += (_, _) => { RateInput.Focus(); RateInput.SelectAll(); };
    }
    private void SaveRate(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!decimal.TryParse(RateInput.Text.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rate))
                throw new TrackerValidationException("Enter an hourly rate such as 10.00.");
            service.SetHourlyRate(rate);
            DialogResult = true;
        }
        catch (Exception exception) { App.Log(exception); ErrorText.Text = UiErrors.Describe(exception); }
    }
}
