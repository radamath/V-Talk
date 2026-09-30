using System.Windows;
using VTalk.Core;
using Forms = System.Windows.Forms;

namespace VTalk.Services;

/// <summary>
/// Sistem tepsisi ikonu ve Windows bildirimleri. Windows 10/11 balon ipuçlarını
/// otomatik olarak sağ alt köşedeki yerel bildirim (toast) olarak gösterir.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly System.Drawing.Icon _appIcon;
    private Action? _balloonAction;

    public event Action? OpenRequested;
    public event Action? ExitRequested;
    public event Action<PresenceStatus>? StatusRequested;

    public TrayService()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/vtalk.ico"))!.Stream;
        _appIcon = new System.Drawing.Icon(stream);
        _icon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "V-Talk",
            Visible = true,
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("V-Talk'u Aç", null, (_, _) => OpenRequested?.Invoke());
        var status = new Forms.ToolStripMenuItem("Durum");
        foreach (var s in new[] { PresenceStatus.Online, PresenceStatus.Busy, PresenceStatus.Away })
        {
            var captured = s;
            status.DropDownItems.Add(StatusNames.ToDisplay(s), null, (_, _) => StatusRequested?.Invoke(captured));
        }
        menu.Items.Add(status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            (action ?? (() => OpenRequested?.Invoke()))();
        };
    }

    public void SetTooltip(string text) => _icon.Text = text.Length > 63 ? text[..63] : text;

    public void Notify(string title, string text, Action? onClick = null, bool warning = false)
    {
        _balloonAction = onClick;
        if (text.Length > 200) text = text[..200] + "…";
        _icon.ShowBalloonTip(5000, title, string.IsNullOrWhiteSpace(text) ? " " : text,
            warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _appIcon.Dispose();
    }
}
