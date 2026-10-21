using System.Globalization;
using System.Runtime.InteropServices;

namespace Frd;

/// <summary>
/// Interface language: Chinese (the source language) or English. Chosen with <c>--lang en|zh</c> on the command line, otherwise it follows the
/// Windows display language. Source strings are written in Chinese; <see cref="T"/> returns the English text from <see cref="LocEnglish"/> when
/// English is active (and the Chinese text when there is no translation, so a missing entry is never an error).
/// </summary>
static class Loc
{
    /// <summary>True when the interface is English.</summary>
    public static bool English { get; set; }

    /// <summary>Call first thing in Main. Removes <c>--lang X</c> / <c>--lang=X</c> from <paramref name="args"/> and sets the language.</summary>
    public static string[] Initialize(string[] args)
    {
        string? requested = null;
        var rest = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--lang" && i + 1 < args.Length) { requested = args[++i]; continue; }
            if (args[i].StartsWith("--lang=", StringComparison.Ordinal)) { requested = args[i][7..]; continue; }
            rest.Add(args[i]);
        }
        var env = Environment.GetEnvironmentVariable("FRD_LANG");
        English = Parse(requested ?? env) ?? !SystemIsChinese();
        return rest.ToArray();
    }

    /// <summary>"en", "en-US", "english" -> true (English); "zh", "zh-CN", "cn", "chinese" -> false; anything else -> null (use the system language).</summary>
    public static bool? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim().ToLowerInvariant();
        if (text.StartsWith("en", StringComparison.Ordinal)) return true;
        if (text.StartsWith("zh", StringComparison.Ordinal) || text is "cn" or "chinese" or "中文") return false;
        return null;
    }

    // InvariantGlobalization is on (NativeAOT), so CultureInfo cannot tell; ask Windows for the display language (primary language 0x04 = Chinese).
    static bool SystemIsChinese()
    {
        try { return (GetUserDefaultUILanguage() & 0x3FF) == 0x04; }
        catch (Exception) { return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh"; }
    }

    [DllImport("kernel32.dll")] static extern ushort GetUserDefaultUILanguage();

    /// <summary>The text in the current language.</summary>
    public static string T(string zh) => English && LocEnglish.Table.TryGetValue(zh, out var en) ? en : zh;

    /// <summary>A format string in the current language with its arguments (<c>{0}</c>-style placeholders, the same in both languages).</summary>
    public static string F(string zhFormat, params object?[] args) => string.Format(CultureInfo.InvariantCulture, T(zhFormat), args);
}
