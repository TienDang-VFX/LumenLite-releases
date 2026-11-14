using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LumenLite.Panel;

/// <summary>Settings for syncing the Scratchpad notes with a GitHub repository.</summary>
internal sealed class NotesSyncPage : Page
{
    private const string TokenUrl = "https://github.com/settings/personal-access-tokens/new";
    private const string NewRepoUrl = "https://github.com/new?name=lumenlite-notes&visibility=private";

    private readonly TextBox _repo;
    private readonly TextBox _branch;
    private readonly PasswordBox _token;
    private readonly TextBlock _tokenHint;
    private readonly TextBlock _status = Ui.Note("");
    private readonly Row _history;
    private readonly ActionButton _connect;
    private readonly ActionButton _syncNow;
    private bool _working;

    public NotesSyncPage(PanelWindow host) : base(host)
    {
        var cfg = Store.S.NotesSync;
        (var repoBox, _repo) = Field(L.T("tên-bạn/lumenlite-notes"), cfg.Repo);
        (var branchBox, _branch) = Field("main", cfg.Branch);
        _repo.LostFocus += (_, _) => { cfg.Repo = _repo.Text.Trim(); Store.SaveSoon(); };
        _branch.LostFocus += (_, _) => { cfg.Branch = _branch.Text.Trim().Length > 0 ? _branch.Text.Trim() : "main"; Store.SaveSoon(); };

        var m = Ui.M;
        _token = new PasswordBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = m.Text,
            CaretBrush = m.Accent,
            FontSize = 13.5,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _tokenHint = new TextBlock { Foreground = m.Secondary, FontSize = 13.5, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        _token.PasswordChanged += (_, _) => UpdateTokenHint();
        var tokenField = new Grid();
        tokenField.Children.Add(_tokenHint);
        tokenField.Children.Add(_token);
        var tokenBox = Box(tokenField);
        UpdateTokenHint();

        _connect = Ui.Action(L.T("Kết nối và đồng bộ"), Connect, Tone.Primary, wide: true);
        _syncNow = Ui.Action(L.T("Đồng bộ ngay"), async () => await NotesSync.SyncAsync(), wide: true);
        _history = Ui.Row("", L.T("Lịch sử trên GitHub"), L.T("Mọi lần sửa ghi chú là một commit; xem và khôi phục bản cũ trên GitHub."), Ui.Nav(), () =>
        {
            if (NotesSync.HistoryUrl() is { } url) CommandBar.Launch(url);
        });

        var useGh = Ui.Row("", L.T("Dùng GitHub CLI (gh) trên máy này"), L.T("Không cần dán token: dùng đăng nhập của gh, tạo repo riêng tư nếu chưa có (tên ở ô Repo, mặc định tin-notes)."), Ui.Nav(), UseGh);
        if (!GhCli.Installed)
            useGh.Visibility = Visibility.Collapsed;

        Add(
            Ui.Group(L.T("Nhanh nhất"), useGh),
            Ui.Group(L.T("Cách thiết lập"),
                Ui.Note(L.T("1. Tạo một repo riêng tư (private) trên GitHub để chứa ghi chú.\n2. Tạo fine-grained token: chỉ chọn repo đó, quyền Contents: Read and write.\n3. Dán tên repo và token vào dưới rồi bấm Kết nối. Làm vậy trên mỗi máy dùng chung ghi chú.")),
                Ui.Row("", L.T("Tạo repo riêng tư"), control: Ui.Nav(), onClick: () => CommandBar.Launch(NewRepoUrl)),
                Ui.Row("", L.T("Tạo token trên GitHub"), control: Ui.Nav(), onClick: () => CommandBar.Launch(TokenUrl))),
            Ui.Group(L.T("Repo GitHub"),
                Labeled(L.T("Repo (chủ/tên)"), repoBox),
                Labeled(L.T("Nhánh"), branchBox),
                Labeled(L.T("Token"), tokenBox),
                Ui.Bar(_connect)),
            Ui.Group(L.T("Đồng bộ"),
                Ui.Row(null, L.T("Tự đồng bộ"), L.T("15 giây sau khi bạn ngừng gõ, mỗi 3 phút và khi mở app."), Ui.Switch(cfg.Enabled, on =>
                {
                    cfg.Enabled = on;
                    Store.SaveSoon();
                    if (on) _ = NotesSync.SyncAsync();
                    Refresh();
                })),
                _status,
                Ui.Bar(_syncNow),
                _history,
                Ui.Row("", L.T("Ngắt kết nối"), L.T("Xóa token trên máy này. Ghi chú vẫn còn ở đây và trên GitHub."), Ui.Nav(), () =>
                    Host.Confirm(L.T("Ngắt kết nối GitHub?"), L.T("Token trên máy này sẽ bị xóa và ghi chú ngừng đồng bộ. Không xóa gì trên GitHub."), L.T("Ngắt kết nối"), () =>
                    {
                        NotesSync.Token.Save(null);
                        cfg.Enabled = false;
                        cfg.TokenSource = "stored";
                        foreach (var pad in Store.S.Pads) pad.SyncedSha = null;
                        cfg.Tombstones.Clear();
                        cfg.LastCommit = null;
                        Store.SaveSoon();
                        _token.Clear();
                        UpdateTokenHint();
                        Refresh();
                    }))));

        WhileShown(() => NotesSync.StatusChanged += Refresh, () => NotesSync.StatusChanged -= Refresh);
        Refresh();
    }

    public override string Title => L.T("Đồng bộ ghi chú");

    public override void Shown() => Refresh();

    private async void UseGh()
    {
        if (_working) return;
        _working = true;
        _status.Text = L.T("Đang thiết lập qua GitHub CLI…");
        _status.Foreground = Ui.M.Secondary;
        var (ok, message) = await NotesSync.SetupWithGhAsync(_repo.Text, _branch.Text, create: true);
        _working = false;
        _repo.Text = Store.S.NotesSync.Repo;
        _branch.Text = Store.S.NotesSync.Branch;
        UpdateTokenHint();
        Refresh();
        if (!ok)
        {
            _status.Text = message;
            _status.Foreground = Ui.M.Red;
        }
        else
            Host.Toast(L.T("Đã kết nối qua GitHub CLI."));
    }

    private async void Connect()
    {
        if (_working) return;
        var cfg = Store.S.NotesSync;
        var repo = _repo.Text.Trim();
        var token = _token.Password.Trim().Length > 0 ? _token.Password.Trim() : NotesSync.Token.Load();
        if (!repo.Contains('/') || string.IsNullOrEmpty(token))
        {
            Host.Toast(L.T("Cần tên repo dạng chủ/tên và token."));
            return;
        }
        _working = true;
        _status.Text = L.T("Đang kiểm tra repo và token…");
        _connect.IsEnabled = false;
        var (ok, message, defaultBranch) = await NotesSync.CheckAsync(repo, token);
        _working = false;
        _connect.IsEnabled = true;
        if (!ok)
        {
            _status.Text = message;
            _status.Foreground = Ui.M.Red;
            return;
        }
        var repoChanged = !string.Equals(cfg.Repo, repo, StringComparison.OrdinalIgnoreCase);
        cfg.Repo = repo;
        if (_branch.Text.Trim().Length == 0 && defaultBranch != null)
            _branch.Text = defaultBranch;
        cfg.Branch = _branch.Text.Trim().Length > 0 ? _branch.Text.Trim() : "main";
        if (repoChanged)
        {
            // Another repository: nothing in it was synced from here yet.
            foreach (var pad in Store.S.Pads) pad.SyncedSha = null;
            cfg.Tombstones.Clear();
            cfg.LastCommit = null;
        }
        cfg.Enabled = true;
        cfg.TokenSource = "stored";
        NotesSync.Token.Save(token);
        Store.SaveNow();
        _token.Clear();
        UpdateTokenHint();
        Host.Toast(message);
        await NotesSync.SyncAsync();
        Refresh();
    }

    private void Refresh()
    {
        var hasToken = NotesSync.Token.Available;
        var cfg = Store.S.NotesSync;
        _status.Text = NotesSync.Status.Length > 0 ? NotesSync.Status
            : !hasToken ? L.T("Chưa kết nối.")
            : cfg.Enabled ? L.T("Đã kết nối, chưa đồng bộ lần nào.") : L.T("Đã tắt tự đồng bộ.");
        _status.Foreground = NotesSync.Failed ? Ui.M.Red : Ui.M.Secondary;
        _syncNow.IsEnabled = NotesSync.Active && !NotesSync.Busy;
        _history.Disabled(!cfg.Repo.Contains('/'));
    }

    private void UpdateTokenHint()
    {
        _tokenHint.Text = NotesSync.Token.FromGh ? L.T("Dùng đăng nhập GitHub CLI (dán token để thay)")
            : NotesSync.Token.Load() != null ? L.T("Đã lưu (dán token mới để thay)") : "github_pat_…";
        _tokenHint.Visibility = _token.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A caption over its field, as one block so the group draws no line between them.</summary>
    private static StackPanel Labeled(string text, Border box)
    {
        var label = Ui.Note(text);
        label.Margin = new Thickness(2, 6, 0, 4);
        var block = new StackPanel();
        block.Children.Add(label);
        block.Children.Add(box);
        return block;
    }

    /// <summary>A text field would keep the mouse wheel for itself; the page scrolls instead.</summary>
    private static void PassWheel(UIElement field)
    {
        field.PreviewMouseWheel += (sender, e) =>
        {
            if (e.Handled || sender is not UIElement element || VisualTreeHelper.GetParent(element) is not UIElement parent)
                return;
            e.Handled = true;
            parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
        };
    }

    private static (Border Box, TextBox Input) Field(string placeholder, string text)
    {
        var m = Ui.M;
        var input = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = m.Text,
            CaretBrush = m.Accent,
            SelectionBrush = m.Accent,
            FontSize = 13.5,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = text,
        };
        var hint = new TextBlock { Text = placeholder, Foreground = m.Secondary, FontSize = 13.5, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        hint.Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        input.TextChanged += (_, _) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(input);
        return (Box(field), input);
    }

    private static Border Box(UIElement child)
    {
        PassWheel(child);
        if (child is System.Windows.Controls.Panel panel)
            foreach (UIElement inner in panel.Children)
                PassWheel(inner);
        return new()
        {
        Background = Ui.M.Inset,
        CornerRadius = new CornerRadius(Ui.M.ControlRadius),
        Padding = new Thickness(10, 7, 10, 7),
        Margin = new Thickness(0, 0, 0, 8),
        Child = child,
        };
    }
}
