using MediaFlux.Services;

namespace MediaFlux;

public sealed class HelpGuideForm : MediaFluxForm
{
    private readonly HelpGuideDocument _guide;
    private readonly TreeView _topics = new() { Dock = DockStyle.Fill, HideSelection = false };
    private readonly RichTextBox _content = new() { Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = SystemColors.Window, Font = new Font("Segoe UI", 10F), DetectUrls = false };
    private readonly FlowLayoutPanel _related = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 4, 8, 6), WrapContents = true };
    private readonly Label _status = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8, 4, 8, 4), ForeColor = Color.DarkOrange };

    public HelpGuideForm(HelpGuideDocument guide, string? topicId = null)
    {
        _guide = guide ?? throw new ArgumentNullException(nameof(guide));
        Text = guide.Title; Size = new Size(1080, 720); MinimumSize = new Size(760, 500); StartPosition = FormStartPosition.CenterParent;
        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 120 };
        split.Resize += (_, _) => { if (split.Width > 620 && split.SplitterDistance < 180) split.SplitterDistance = Math.Min(260, split.Width - 340); };
        split.Panel1.Controls.Add(_topics); split.Panel2.Controls.Add(_content); split.Panel2.Controls.Add(_related); split.Panel2.Controls.Add(_status); Controls.Add(split);
        foreach (HelpGuideTopic topic in guide.Topics) _topics.Nodes.Add(new TreeNode(topic.Title) { Tag = topic });
        _topics.AfterSelect += (_, _) => ShowSelectedTopic();
        if (!string.IsNullOrWhiteSpace(guide.Error)) _status.Text = guide.Error;
        SelectTopic(topicId ?? "getting-started");
    }

    public static void ShowGuide(IWin32Window? owner, string? topicId = null)
    {
        using var form = new HelpGuideForm(new HelpGuideService().LoadDefault(), topicId);
        form.ShowDialog(owner);
    }

    public bool SelectTopic(string? topicId)
    {
        HelpGuideTopic? topic = _guide.FindTopic(topicId) ?? _guide.Topics.FirstOrDefault();
        TreeNode? node = _topics.Nodes.Cast<TreeNode>().FirstOrDefault(value => ReferenceEquals(value.Tag, topic));
        if (node == null) return false;
        _topics.SelectedNode = node; node.EnsureVisible(); return true;
    }

    private void ShowSelectedTopic()
    {
        if (_topics.SelectedNode?.Tag is not HelpGuideTopic topic) return;
        _content.Text = topic.Title + Environment.NewLine + Environment.NewLine + HelpGuideMarkdownRenderer.Render(topic.Markdown);
        _content.Select(0, topic.Title.Length); _content.SelectionFont = new Font(_content.Font, FontStyle.Bold); _content.Select(0, 0);
        _related.Controls.Clear();
        foreach (string id in topic.RelatedTopicIds)
        {
            HelpGuideTopic? related = _guide.FindTopic(id); if (related == null) continue;
            var link = new LinkLabel { Text = related.Title, AutoSize = true, Margin = new Padding(4) };
            link.Click += (_, _) => SelectTopic(related.Id); _related.Controls.Add(link);
        }
    }
}
