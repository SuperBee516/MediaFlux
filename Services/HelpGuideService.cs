using System.Text;
using System.Text.RegularExpressions;

namespace MediaFlux.Services;

public sealed record HelpGuideTopic(string Id, string Title, string Markdown, IReadOnlyList<string> RelatedTopicIds);

public sealed record HelpGuideDocument(string Title, IReadOnlyList<HelpGuideTopic> Topics, string? Error = null)
{
    public HelpGuideTopic? FindTopic(string? id) => string.IsNullOrWhiteSpace(id)
        ? null
        : Topics.FirstOrDefault(topic => topic.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
}

public sealed class HelpGuideService
{
    public const string RelativeGuidePath = "Documentation\\UserGuide.md";
    private static readonly Regex Heading = new(@"^##\s+(.+?)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private static readonly Regex RelatedLink = new(@"\]\(#([a-z0-9-]+)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public HelpGuideDocument LoadDefault() => Load(Path.Combine(AppContext.BaseDirectory, RelativeGuidePath));

    public HelpGuideDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Unavailable("The User Guide file is unavailable. Reinstall or repair MediaFlux to restore Documentation\\UserGuide.md.");
        try { return Parse(File.ReadAllText(path)); }
        catch (Exception ex) { return Unavailable($"The User Guide could not be read: {ex.Message}"); }
    }

    public HelpGuideDocument Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return Unavailable("The User Guide is empty or malformed.");
        MatchCollection headings = Heading.Matches(markdown);
        if (headings.Count == 0) return Unavailable("The User Guide has no topics. The documentation file may be malformed.");
        string title = markdown.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?[2..].Trim() ?? "MediaFlux User Guide";
        var topics = new List<HelpGuideTopic>(headings.Count);
        for (int index = 0; index < headings.Count; index++)
        {
            Match heading = headings[index]; string topicTitle = heading.Groups[1].Value.Trim();
            int contentStart = heading.Index + heading.Length;
            int contentEnd = index + 1 < headings.Count ? headings[index + 1].Index : markdown.Length;
            string content = markdown[contentStart..contentEnd].Trim();
            string id = ToId(topicTitle);
            topics.Add(new HelpGuideTopic(id, topicTitle, content,
                RelatedLink.Matches(content).Select(match => match.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        return new HelpGuideDocument(title, topics);
    }

    public static string ToId(string title) => Regex.Replace(string.Concat(title.Trim().ToLowerInvariant().Select(character =>
        char.IsLetterOrDigit(character) ? character : '-')).Trim('-'), "-+", "-");

    private static HelpGuideDocument Unavailable(string error) => new("MediaFlux User Guide",
        new[] { new HelpGuideTopic("guide-unavailable", "Guide unavailable", error, Array.Empty<string>()) }, error);
}

public static class HelpGuideMarkdownRenderer
{
    private static readonly Regex Link = new(@"\[([^]]+)\]\(#([a-z0-9-]+)\)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Render(string markdown) => Link.Replace(markdown ?? "", "$1").Replace("**", "").Trim();
}
