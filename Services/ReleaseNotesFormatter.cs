using System.Text.RegularExpressions;

namespace MediaFlux.Services;

public static class ReleaseNotesFormatter
{
    public static string Format(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "No release notes were provided for this version.";
        string text = markdown.Trim();
        text = Regex.Replace(text, @"^#{1,6}\s+", "", RegexOptions.Multiline);
        text = Regex.Replace(text, @"^\s*[-*+]\s+", "• ", RegexOptions.Multiline);
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        text = text.Replace("**", "").Replace("`", "");
        return text;
    }
}
