using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LumenLite;

/// <summary>
/// The interface language. The app is written in Vietnamese; every Vietnamese text in the code goes through
/// <see cref="T"/> (plain text) or <see cref="F"/> (text with values), which return the English wording when English
/// is chosen. The English wordings live in Assets/en.tsv, one "Vietnamese TAB English" line per text; a text missing
/// there stays Vietnamese. The language is read once at start: changing it restarts the app.
/// </summary>
public static class L
{
    private static Dictionary<string, string>? _words;

    /// <summary>"vi" or "en".</summary>
    public static string Code { get; private set; } = "vi";

    public static bool English => Code == "en";

    public static void Init(string? code)
    {
        Code = code == "en" ? "en" : "vi";
        _words = English ? Load() : null;
    }

    /// <summary>The text in the chosen language.</summary>
    public static string T(string vi)
    {
        if (_words == null || vi == null)
            return vi!;
        return _words.TryGetValue(vi, out var en) ? en : vi;
    }

    /// <summary>
    /// An interpolated text in the chosen language: its wording (with {0}, {1}… where the values go) is looked up,
    /// then the values are put in.
    /// </summary>
    public static string F(FormattableString text)
    {
        var format = text.Format;
        if (_words != null && _words.TryGetValue(format, out var en))
        {
            try
            {
                return string.Format(CultureInfo.CurrentCulture, en, text.GetArguments());
            }
            catch (FormatException)
            {
                Store.Log("bad translation: " + en);
            }
        }
        return text.ToString(CultureInfo.CurrentCulture);
    }

    private static Dictionary<string, string> Load()
    {
        var words = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream("en.tsv");
            if (stream == null)
                return words;
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0 || line[0] == '#' && line.IndexOf('\t') < 0)
                    continue;
                var tab = line.IndexOf('\t');
                if (tab <= 0)
                    continue;
                var en = Unescape(line[(tab + 1)..]);
                if (en.Length > 0)
                    words[Unescape(line[..tab])] = en;
            }
        }
        catch (Exception ex)
        {
            Store.Log("language load failed: " + ex.Message);
        }
        return words;
    }

    /// <summary>The file writes a backslash, a new line and a tab as \\, \n and \t.</summary>
    private static string Unescape(string s)
    {
        if (s.IndexOf('\\') < 0)
            return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[++i];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', 'r' => '\r', _ => next });
            }
            else
                sb.Append(s[i]);
        }
        return sb.ToString();
    }
}
