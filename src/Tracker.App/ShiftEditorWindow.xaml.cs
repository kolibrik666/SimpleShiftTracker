using System.Windows;
using System.Windows.Threading;
using Tracker.App.ViewModels;
using Tracker.Core;

namespace Tracker.App;

public partial class ShiftEditorWindow : Window
{
    public ShiftSnapshot? SavedShift { get; private set; }
    public ShiftEditorWindow(TrackerService service, ShiftSnapshot? shift, TimeZoneInfo zone, DateTime month)
    {
        InitializeComponent();
        DataContext = new ShiftEditorViewModel(service, shift, zone, month, saved => { SavedShift = saved; DialogResult = true; });
        if (shift?.State == ShiftState.Running)
        {
            var previewTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            previewTimer.Tick += (_, _) => ((ShiftEditorViewModel)DataContext).RefreshPreview();
            Loaded += (_, _) => previewTimer.Start();
            Closed += (_, _) => previewTimer.Stop();
        }
    }
}
