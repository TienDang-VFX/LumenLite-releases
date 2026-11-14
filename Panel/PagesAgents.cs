using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LumenLite.Panel;

/// <summary>Claude Code on this machine: tokens today and this week, per model, and the sessions running now.</summary>
internal sealed class AgentsPage : Page
{
    private readonly Readout _today = new("\uE823", L.T("Hôm nay"));
    private readonly Readout _week = new("\uE787", L.T("7 ngày"));
    private readonly Readout _running = new("\uE768", L.T("Đang chạy"));
    private readonly Grid _chart = new() { Height = 70 };
    private readonly Group _models = Ui.Group(L.T("Theo model · hôm nay"));
    private readonly Group _sessions = Ui.Group(L.T("Phiên · bấm để mở"));

    public AgentsPage(PanelWindow host) : base(host)
    {
        if (!AgentMonitor.Available)
        {
            Add(Ui.Group(Title, Ui.Note(L.T("Chưa thấy Claude Code trên máy này (thư mục %USERPROFILE%\\.claude\\projects). Codex, Copilot hay OpenCode cũng chưa có log để đọc."))));
            return;
        }
        Add(Ui.Group(Title, Readout.Strip(_today, _week, _running), _chart)
                .Hint(L.T("Đọc từ log Claude Code trên máy, không gửi dữ liệu đi đâu. Log không ghi giới hạn gói hay thời điểm reset, nên mục đó không có. Token gồm vào, ra, ghi cache và đọc cache.")),
            _models,
            _sessions,
            Ui.Group(L.T("Thông báo"),
                Ui.Row(null, L.T("Báo khi Claude xong việc"), L.T("Dynamic Island báo khi một phiên chạy hơn 1 phút rồi xong lượt."), Ui.Switch(Store.S.AgentAlerts, on => { Store.S.AgentAlerts = on; Store.SaveSoon(); })),
                Ui.Row(null, L.T("Hiện trên Island khi đang chạy"), L.T("Viên Island hiện biểu tượng ✳ và số phiên đang làm việc."), Ui.Switch(Store.S.AgentOnIsland, on => { Store.S.AgentOnIsland = on; Store.SaveSoon(); AgentMonitor.Nudge(); }))));
        WhileShown(() => AgentMonitor.Updated += Draw, () => AgentMonitor.Updated -= Draw);
    }

    public override string Title => "AI agent";

    public override void Shown() => Draw();

    private void Draw()
    {
        if (!AgentMonitor.Available)
            return;
        var m = Ui.M;
        var today = AgentMonitor.UsageBetween(DateTime.Today, DateTime.Today);
        var week = AgentMonitor.UsageBetween(DateTime.Today.AddDays(-6), DateTime.Today);
        var sessions = AgentMonitor.Sessions();
        _today.Value = AgentMonitor.Tokens(today.Sum(u => u.Total));
        _week.Value = AgentMonitor.Tokens(week.Sum(u => u.Total));
        _running.Value = sessions.Count(s => s.Working).ToString();

        DrawChart();

        _models.Set(today.Count == 0
            ? new UIElement[] { Ui.Note(L.T("Hôm nay chưa dùng.")) }
            : today.Select(model => (UIElement)Ui.Row(null, model.Model,
                L.F($"vào {AgentMonitor.Tokens(model.Input)} · ra {AgentMonitor.Tokens(model.Output)} · ghi cache {AgentMonitor.Tokens(model.CacheWrite)} · đọc cache {AgentMonitor.Tokens(model.CacheRead)}"),
                Ui.Value(AgentMonitor.Tokens(model.Total), m.Text))).ToArray());

        _sessions.Set(sessions.Count == 0
            ? new UIElement[] { Ui.Note(L.T("Không có phiên nào trong 12 giờ qua.")) }
            : sessions.Take(12).Select(session =>
            {
                var dot = new Ellipse { Width = 7, Height = 7, Fill = session.Working ? m.Accent : m.Track };
                var where = string.Join(" · ", new[] { session.Machine.Length > 0 ? L.T("Máy ") + session.Machine : "", session.Project }.Where(x => x.Length > 0));
                // A click opens the session where it lives, as the Island's rows do.
                var row = Ui.LeadRow(dot, session.Title, (where.Length > 0 ? where + "\n" : "") + AgentOpen.Describe(session), Ui.Value(AgentMonitor.Tokens(session.Tokens)), () =>
                {
                    if (AgentOpen.Open(session))
                        Host.HidePanel();
                    else
                        Host.Toast(L.F($"Không mở được app {AgentMonitor.Name(session.Provider)}"));
                });
                // What it is doing right now stays in view: it is the reason to look at this list.
                return (UIElement)row.Detail(session.NeedsInput ? L.T("Đang chờ bạn") : session.Working ? (session.Activity.Length > 0 ? session.Activity : L.T("Đang chạy")) : $"Xong {Ago(session.LastActive)}",
                    session.NeedsInput ? m.Amber : null);
            }).ToArray());
    }

    /// <summary>Seven bars, one per day, today on the right.</summary>
    private void DrawChart()
    {
        _chart.Children.Clear();
        _chart.ColumnDefinitions.Clear();
        var days = AgentMonitor.Daily(7);
        var max = Math.Max(1, days.Max(d => d.Tokens));
        for (var i = 0; i < days.Count; i++)
        {
            _chart.ColumnDefinitions.Add(new ColumnDefinition());
            var (day, tokens) = days[i];
            var column = new Grid { Margin = new Thickness(4, 0, 4, 0) };
            column.RowDefinitions.Add(new RowDefinition());
            column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new Border
            {
                Height = Math.Max(3, 50.0 * tokens / max),
                CornerRadius = new CornerRadius(1),
                Background = day == DateTime.Today ? Ui.M.Accent : Ui.M.Track,
                VerticalAlignment = VerticalAlignment.Bottom,
                ToolTip = $"{day:dd/MM}: {AgentMonitor.Tokens(tokens)} token",
            };
            column.Children.Add(bar);
            var label = Ui.Mono(day == DateTime.Today ? L.T("Nay") : $"{day:dd}", 10, day == DateTime.Today ? Ui.M.Text : Ui.M.Secondary);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            Grid.SetRow(label, 1);
            column.Children.Add(label);
            Grid.SetColumn(column, i);
            _chart.Children.Add(column);
        }
    }

    private static string Ago(DateTime when)
    {
        var span = DateTime.Now - when;
        return span.TotalMinutes < 1 ? L.T("vừa xong") : span.TotalHours < 1 ? L.F($"{(int)span.TotalMinutes} phút trước") : L.F($"{(int)span.TotalHours} giờ trước");
    }
}
