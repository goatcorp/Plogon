using System;
using System.Threading.Tasks;
using Discord;
using Discord.Webhook;

namespace Plogon;

/// <summary>
/// Responsible for sending discord webhooks
/// </summary>
public class DiscordWebhook
{
    /// <summary>
    /// Webhook client
    /// </summary>
    public DiscordWebhookClient Client { get; }

    /// <summary>
    /// Init with webhook from env var
    /// </summary>
    public DiscordWebhook()
    {
        this.Client = new DiscordWebhookClient(Environment.GetEnvironmentVariable("DISCORD_WEBHOOK"));
    }

    private static DateTime GetPacificStandardTime()
    {
        var utc = DateTime.UtcNow;
        var pacificZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        var pacificTime = TimeZoneInfo.ConvertTimeFromUtc(utc, pacificZone);
        return pacificTime;
    }
    
    /// <summary>
    /// Send a webhook
    /// </summary>
    /// <param name="color"></param>
    /// <param name="message"></param>
    /// <param name="title"></param>
    /// <param name="footer"></param>
    public async Task<ulong> Send(Color color, string message, string title, string footer)
    {
        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .WithFooter(footer)
            .WithDescription(message)
            .Build();

        var time = GetPacificStandardTime();
        var username = "Plo";
        var avatarUrl = "https://goatcorp.github.io/icons/plo.png";
        if (time.Hour is > 20 or < 7)
        {
            username = "Gon";
            avatarUrl = "https://goatcorp.github.io/icons/gon.png";
        }
        
        return await this.Client.SendMessageAsync(embeds: new[] { embed }, username: username, avatarUrl: avatarUrl);
    }
}