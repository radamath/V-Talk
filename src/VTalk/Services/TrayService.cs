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
    private readonly System.Windows.Threading.Dispatcher _ui = System.Windows.Threading.Dispatcher.CurrentDispatcher;
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
        menu.Items.Add("V-Talk'u Aç", null, (_, _) => Post(() => OpenRequested?.Invoke()));
        var status = new Forms.ToolStripMenuItem("Durum");
        foreach (var s in new[] { PresenceStatus.Online, PresenceStatus.Busy, PresenceStatus.Away })
        {
            var captured = s;
            status.DropDownItems.Add(StatusNames.ToDisplay(s), null, (_, _) => Post(() => StatusRequested?.Invoke(captured)));
        }
        menu.Items.Add(status);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Çıkış", null, (_, _) => Post(() => ExitRequested?.Invoke()));
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Post(() => OpenRequested?.Invoke());
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            Post(action ?? (() => OpenRequested?.Invoke()));
        };
    }

    /// <summary>
    /// Tepsi olayları WinForms pencere işleyicisinin içinden gelir. WPF pencereleri orada açılmamalı ve oradaki
    /// hatalar WPF hata işleyicisine ulaşmaz; bu yüzden iş, mesaj döngüsüne sıradan bir öğe olarak bırakılır.
    /// </summary>
    private void Post(Action action) => _ui.BeginInvoke(action);

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
