# LumenLite

Dynamic Island và bảng điều khiển cho Windows 10/11, viết bằng C# / WPF (.NET 9). Repo này chứa mã nguồn bản **1.4.2** và các bản build trong mục [Releases](https://github.com/TienDang-VFX/LumenLite-releases/releases).

**Chỉ muốn dùng:** tải [LumenLite.exe](https://github.com/TienDang-VFX/LumenLite-releases/releases/download/v1.4.2/LumenLite.exe), chạy là xong, không cần cài .NET. Dùng kèm [RustDesk](https://github.com/rustdesk/rustdesk/releases/latest) để đổi máy từ xa.

## Tính năng

- **Dynamic Island** ở mép trên màn hình: nhạc đang phát, âm lượng/độ sáng, lưới tiện ích, lịch, hàng máy RustDesk (bấm hiện, bấm lại thu nhỏ), ô Tắt máy/Khởi động lại (bấm hai lần trong 4 giây), chỉ số CPU · RAM hoặc mạng trên viên thu gọn.
- **AI Agents**: phiên Claude Code và Codex đang chạy hoặc chờ bạn (kể cả trên máy khác qua session-fleet), hạn mức phiên/tuần, chi tiêu quy ra giá API. Bấm phiên để mở thẳng trong app.
- **Bảng điều khiển** (biểu tượng khay hoặc `Ctrl+Alt+V`): âm thanh từng app, thiết bị (UniKey, màn hình, độ sáng DDC/CI), hệ thống (biểu đồ, cảnh báo), mạng (lưu lượng, đo tốc độ), lưu trữ (dọn dẹp), ứng dụng (thoát, cập nhật winget, gỡ cài đặt, cổng mạng), AI agent, tiện ích, bật/tắt nhanh.
- **Ghi chú dán màn hình**: kéo tab Scratchpad ra ngoài cửa sổ (hoặc bấm nút dán) để đặt ghi chú lên desktop, kéo đi đâu cũng được rồi ghim lại (giữ chỗ, nổi trên cửa sổ khác). Đổi màu, tự nhớ vị trí.
- **Đồng bộ ghi chú qua GitHub**: Cài đặt → Ghi chú → Đồng bộ với GitHub. Mỗi ghi chú là một file Markdown trong repo riêng tư của bạn (`notes/<id>.md`); mỗi lần đồng bộ là một commit nên có đủ lịch sử phiên bản. Sửa cùng một ghi chú trên hai máy thì giữ bản trên GitHub và tạo thêm bản "xung đột". Token (fine-grained, chỉ một repo, Contents: Read and write) được mã hóa bằng DPAPI, không nằm trong settings.json.
- **English**: Cài đặt → Ngôn ngữ · Language. Bản dịch nằm trong `Assets/en.tsv`.
- **Command Bar** (`Ctrl+Alt+Space`), **menu vòng tròn** (giữ chuột giữa hoặc `Ctrl+Alt+R`), **Scratchpad**, làm sạch link khi copy.
- **Tự cập nhật** từ mục Releases của repo này.

## Đồng bộ ghi chú trên máy mới bằng agent (không cần dán token)

Trên máy đã cài LumenLite và GitHub CLI (`gh`), nhờ agent (Claude Code, Codex…) chạy các lệnh dưới. LumenLite mượn đăng nhập của `gh` (hỏi lại mỗi 30 phút, không lưu token), tạo repo riêng tư nếu chưa có, rồi đồng bộ.

For agents on a new PC (no token copy-paste needed):

```powershell
gh auth status                       # gh must be signed in to github.com; if not, ask the user to run: gh auth login
& "$env:LOCALAPPDATA\Programs\LumenLite\LumenLite.exe" --notes-sync tin-notes --create
# wait a few seconds, then read the outcome: first line OK or FAILED, then the message
Get-Content "$env:APPDATA\LumenLite
otes-sync-result.txt"
```

- `tin-notes` without an owner means `<gh user>/tin-notes`; `owner/name` works too. `--branch name` picks a branch (default: the repo's default branch). Without `--create`, a missing repo is an error.
- Works whether LumenLite is already running or not, and from an agent's terminal: the request goes through `%APPDATA%\LumenLite
otes-sync-request.txt`, picked up by the running copy.
- The result file says `PENDING`/`RUNNING` until it is done. Details also go to `%APPDATA%\LumenLite\lumen.log`.
- Same thing by hand: Settings → Ghi chú → Đồng bộ với GitHub → Dùng GitHub CLI (gh) trên máy này.

## Build

Cần [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0).

```powershell
dotnet build -c Release                     # chạy thử: bin\Release\net9.0-windows10.0.19041.0\LumenLite.exe
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o dist/LumenLite-win-x64
```

Cài đặt lưu ở `%APPDATA%\LumenLite\settings.json`, log ở `%APPDATA%\LumenLite\lumen.log`.

## Tự custom

1. **Fork** repo này về tài khoản của bạn.
2. **Tự cập nhật chỉ bật cho bản phát hành.** Bản build bằng `dotnet build`/`dotnet publish` không tự cập nhật, nên không bị bản chính thức ghi đè. `release.ps1` gắn repo trong `$repo` vào bản build; đổi `$repo` thành repo của bạn (ví dụ `ten-ban/LumenLite`).
3. Sửa code, build như trên. Muốn phát hành cho máy khác tự cập nhật: sửa `$repo` trong `release.ps1` thành repo của bạn rồi chạy `./release.ps1 1.5.0 "ghi chú"` (cần [GitHub CLI](https://cli.github.com/) đã đăng nhập).

Chỗ hay sửa:

| Muốn đổi | File |
| --- | --- |
| Logo, icon | `Assets/logo.png`, `Assets/LumenLite.ico` |
| Màu, font | `Theme.cs`, `Panel/Kit.cs` |
| Ô trong Island, lưới tiện ích | `IslandWindow.cs`, `IslandViews.cs` |
| Trang AI Agents | `IslandAgents.cs`, `Services/AgentMonitor.cs` |
| Tab bảng điều khiển | `Panel/Pages*.cs`, đăng ký tab trong `App.cs` |
| Lệnh Command Bar, mục menu vòng tròn | `App.cs` (`Commands()`, `RadialItems`) |
| Phím tắt (Ctrl+Alt+…) | `Background.cs` |
| Cài đặt và giá trị mặc định | `Store.cs` |
| Ghi chú dán màn hình | `StickyNote.cs` |
| Đồng bộ ghi chú với GitHub | `NotesSync.cs`, `Panel/PagesNotes.cs` |
| Bản dịch tiếng Anh | `Assets/en.tsv` (chữ Việt mới trong code: bọc bằng `L.T("…")` hoặc `L.F($"…")` rồi thêm một dòng vào file) |

Lưu ý khi chạy thử: đừng chạy LumenLite từ trong terminal của app agent (Claude, Codex). Các app đó giữ tiến trình con trong sandbox, LumenLite sẽ mất cài đặt hoặc tắt theo. Mở file exe bằng Explorer.
