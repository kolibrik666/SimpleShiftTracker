using System.Windows;
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
    }
}
