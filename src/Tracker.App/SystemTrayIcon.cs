using System.Windows;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Tracker.App;

internal sealed class SystemTrayIcon : IDisposable
{
    private readonly Drawing.Icon image;
    private readonly Forms.ContextMenuStrip menu;
    private readonly Forms.NotifyIcon icon;
    private readonly Action restore;
    private bool disposed;

    internal bool IsVisible => !disposed && icon.Visible;

    internal SystemTrayIcon(Action restore, Action exit)
    {
        this.restore = restore;
        var assemblyName = typeof(SystemTrayIcon).Assembly.GetName().Name;
        var resource = Application.GetResourceStream(
            new Uri($"pack://application:,,,/{assemblyName};component/Assets/Tracker.ico"))
            ?? throw new InvalidOperationException("The tray icon resource is missing.");
        using (resource.Stream)
        using (var embedded = new Drawing.Icon(resource.Stream))
            image = (Drawing.Icon)embedded.Clone();

        menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Shift Tracker", null, (_, _) => restore());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        icon = new Forms.NotifyIcon
        {
            Icon = image,
            Text = "Shift Tracker — click to restore",
            ContextMenuStrip = menu,
            Visible = false
        };
        icon.MouseClick += IconClicked;
    }

    private void IconClicked(object? sender, Forms.MouseEventArgs e)
    {
        if (e.Button == Forms.MouseButtons.Left) restore();
    }

    internal void Show() => icon.Visible = true;
    internal void Hide() => icon.Visible = false;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.MouseClick -= IconClicked;
        icon.Dispose();
        menu.Dispose();
        image.Dispose();
    }
}
