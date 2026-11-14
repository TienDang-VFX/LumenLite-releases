using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using WinForms = System.Windows.Forms;

namespace LumenLite;

/// <summary>System tray icon: show or hide each widget, edit the layout, autostart, quit.</summary>
internal sealed class Tray : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly IReadOnlyList<WidgetWindow> _widgets;

    public Tray(IReadOnlyList<WidgetWindow> widgets, IslandWindow island, Background background, Action quit, Action openPanel)
    {
        _widgets = widgets;
        var menu = new WinForms.ContextMenuStrip();

        var open = new WinForms.ToolStripMenuItem(L.T("Mở bảng điều khiển  (") + (background.PanelHotkey ?? L.T("chuột trái")) + ")") { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) };
        open.Click += (_, _) => openPanel();
        menu.Items.Add(open);
        menu.Items.Add(new WinForms.ToolStripSeparator());

        foreach (var widget in widgets)
        {
            var item = new WinForms.ToolStripMenuItem(widget.Title) { CheckOnClick = true };
            item.Click += (_, _) => widget.SetShown(item.Checked);
            menu.Items.Add(item);
            menu.Opening += (_, _) => item.Checked = widget.IsVisible;
        }


        var islandItem = new WinForms.ToolStripMenuItem("Dynamic Island") { CheckOnClick = true, Checked = Store.S.Island };
        islandItem.Click += (_, _) =>
        {
            island.Enabled = islandItem.Checked;
            Store.S.Island = islandItem.Checked;
            Store.SaveSoon();
        };
        menu.Items.Add(islandItem);

        var islandIdle = new WinForms.ToolStripMenuItem(L.T("Island luôn hiện viên nhỏ")) { CheckOnClick = true, Checked = Store.S.IslandAlwaysShow };
        islandIdle.Click += (_, _) =>
        {
            island.AlwaysShow = islandIdle.Checked;
            Store.S.IslandAlwaysShow = islandIdle.Checked;
            Store.SaveSoon();
        };
        menu.Items.Add(islandIdle);

        var cleanLinks = new WinForms.ToolStripMenuItem(L.T("Bỏ mã theo dõi khỏi link khi copy")) { CheckOnClick = true, Checked = Store.S.CleanLinks };
        cleanLinks.Click += (_, _) =>
        {
            Store.S.CleanLinks = cleanLinks.Checked;
            Store.SaveSoon();
        };
        menu.Items.Add(cleanLinks);

        var downloads = new WinForms.ToolStripMenuItem(L.T("Báo khi tải xong file")) { CheckOnClick = true, Checked = Store.S.DownloadAlerts };
        downloads.Click += (_, _) =>
        {
            background.DownloadAlerts = downloads.Checked;
            Store.S.DownloadAlerts = downloads.Checked;
            Store.SaveSoon();
        };
        menu.Items.Add(downloads);

        var keys = new WinForms.ToolStripMenuItem(L.T("Phím tắt")) { };
        keys.DropDownItems.Add(new WinForms.ToolStripMenuItem(L.T("Tắt/bật micro: ") + (background.MicHotkey ?? L.T("bị app khác chiếm"))) { Enabled = false });
        keys.DropDownItems.Add(new WinForms.ToolStripMenuItem(L.T("Đổi đầu ra âm thanh: ") + (background.OutputHotkey ?? L.T("bị app khác chiếm"))) { Enabled = false });
        menu.Items.Add(keys);

        menu.Items.Add(new WinForms.ToolStripSeparator());

        var front = new WinForms.ToolStripMenuItem(L.T("Đưa tất cả lên trên"));
        front.Click += (_, _) => BringToFront();
        menu.Items.Add(front);

        var edit = new WinForms.ToolStripMenuItem(L.T("Chỉnh sửa bố cục")) { CheckOnClick = true };
        edit.Click += (_, _) =>
        {
            WidgetWindow.SetEditing(edit.Checked);
            if (edit.Checked)
                BringToFront();
        };
        menu.Opening += (_, _) => edit.Checked = WidgetWindow.Editing;
        menu.Items.Add(edit);

        var autostart = new WinForms.ToolStripMenuItem(L.T("Khởi động cùng Windows")) { CheckOnClick = true };
        autostart.Click += (_, _) =>
        {
            try
            {
                Autostart.Set(autostart.Checked);
            }
            catch (Exception ex)
            {
                Store.Log("autostart failed: " + ex.Message);
            }
        };
        menu.Opening += (_, _) => autostart.Checked = Autostart.IsEnabled;
        menu.Items.Add(autostart);

        menu.Items.Add(new WinForms.ToolStripSeparator());
        var exit = new WinForms.ToolStripMenuItem(L.T("Thoát"));
        exit.Click += (_, _) => quit();
        menu.Items.Add(exit);

        _icon = new WinForms.NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "LumenLite",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                openPanel();
        };
    }

    /// <summary>A Windows notification from the tray icon, for alerts.</summary>
    public void Balloon(string text)
    {
        try
        {
            _icon.ShowBalloonTip(5000, "LumenLite", text, WinForms.ToolTipIcon.Warning);
        }
        catch (Exception ex)
        {
            Store.Log("balloon failed: " + ex.Message);
        }
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private void BringToFront()
    {
        foreach (var widget in _widgets)
        {
            if (widget.IsVisible)
                widget.Activate();
        }
    }

    /// <summary>The app's TD logo at the size Windows uses for tray icons on this screen.</summary>
    private static Icon DrawIcon()
    {
        using var stream = typeof(Tray).Assembly.GetManifestResourceStream("LumenLite.ico")!;
        return new Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }
}
