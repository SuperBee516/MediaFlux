using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MediaFlux.Services
{
    public static class DiscordWebhookService
    {
        private static readonly HttpClient Client = new()
        {
            Timeout = TimeSpan.FromSeconds(10)
        };

        public static bool IsValidWebhookUrl(string? value)
        {
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps)
            {
                return false;
            }

            bool discordHost = uri.Host.Equals("discord.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.Equals("discordapp.com", StringComparison.OrdinalIgnoreCase) ||
                uri.Host.EndsWith(".discordapp.com", StringComparison.OrdinalIgnoreCase);

            return discordHost &&
                uri.AbsolutePath.Contains("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsValidUserMentionId(string? value)
        {
            string userId = value?.Trim() ?? string.Empty;
            return userId.Length is >= 15 and <= 25 &&
                ulong.TryParse(userId, out var parsed) &&
                parsed > 0;
        }

        public static async Task SendAsync(
            string webhookUrl,
            string message,
            string? userMentionId = null,
            CancellationToken cancellationToken = default)
        {
            if (!IsValidWebhookUrl(webhookUrl))
                throw new ArgumentException("Enter a valid Discord webhook URL.", nameof(webhookUrl));

            string mentionId = userMentionId?.Trim() ?? string.Empty;
            if (mentionId.Length > 0 && !IsValidUserMentionId(mentionId))
                throw new ArgumentException("Enter a valid numeric Discord user ID.", nameof(userMentionId));

            string content = message?.Trim() ?? string.Empty;
            if (content.Length == 0)
                throw new ArgumentException("The Discord message cannot be empty.", nameof(message));

            if (mentionId.Length > 0)
                content = $"<@{mentionId}> {content}";

            if (content.Length > 2000)
                throw new ArgumentException("Discord messages cannot exceed 2,000 characters.", nameof(message));

            string[] allowedUsers = mentionId.Length > 0 ? new[] { mentionId } : Array.Empty<string>();
            string json = JsonSerializer.Serialize(new
            {
                content,
                allowed_mentions = new
                {
                    parse = Array.Empty<string>(),
                    users = allowedUsers
                }
            });
            using var body = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await Client.PostAsync(webhookUrl.Trim(), body, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }
}
