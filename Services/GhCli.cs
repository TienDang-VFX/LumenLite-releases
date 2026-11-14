using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LumenLite;

/// <summary>
/// The GitHub CLI (gh), when it is installed and signed in: the notes sync can borrow its sign-in instead of a
/// token pasted by hand, and an agent on a new PC can set the sync up with one command.
/// </summary>
internal static class GhCli
{
    private static string? _path;
    private static bool _looked;

    /// <summary>gh.exe on the PATH or in its usual install folders; null when it is not installed.</summary>
    public static string? Path
    {
        get
        {
            if (_looked) return _path;
            _looked = true;
            var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
            // Explorer's PATH can predate an install made after sign-in.
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI"));
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI"));
            dirs.Add(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims"));
            foreach (var dir in dirs)
            {
                try
                {
                    var candidate = System.IO.Path.Combine(dir.Trim().Trim('"'), "gh.exe");
                    if (File.Exists(candidate))
                        return _path = candidate;
                }
                catch (ArgumentException)
                {
                }
            }
            return null;
        }
    }

    public static bool Installed => Path != null;

    /// <summary>Runs gh with the arguments; the exit code and what it printed.</summary>
    public static (int Code, string Output, string Error) Run(params string[] args)
    {
        if (Path is not { } gh)
            return (-1, "", L.T("Chưa cài GitHub CLI (gh)."));
        var info = new ProcessStartInfo(gh)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        // gh must not stop to ask anything: there is nobody at its prompt.
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["NO_COLOR"] = "1";
        try
        {
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(); } catch { }
                return (-1, "", L.T("GitHub CLI không trả lời."));
            }
            return (process.ExitCode, output.Result.Trim(), error.Result.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return (-1, "", ex.Message);
        }
    }

    public static Task<(int Code, string Output, string Error)> RunAsync(params string[] args) => Task.Run(() => Run(args));

    /// <summary>The token gh signed in with, or null when gh is missing or signed out.</summary>
    public static string? Token()
    {
        var (code, output, _) = Run("auth", "token", "--hostname", "github.com");
        return code == 0 && output.Length > 0 ? output : null;
    }

    /// <summary>The GitHub user gh is signed in as.</summary>
    public static async Task<string?> UserAsync()
    {
        var (code, output, _) = await RunAsync("api", "user", "--jq", ".login");
        return code == 0 && output.Length > 0 ? output : null;
    }

    public static async Task<bool> RepoExistsAsync(string repo)
    {
        var (code, _, _) = await RunAsync("repo", "view", repo, "--json", "name");
        return code == 0;
    }

    /// <summary>Creates a private repository; null when it worked, else gh's message.</summary>
    public static async Task<string?> CreatePrivateRepoAsync(string repo)
    {
        var (code, _, error) = await RunAsync("repo", "create", repo, "--private", "--description", "LumenLite notes");
        return code == 0 ? null : error;
    }
}
