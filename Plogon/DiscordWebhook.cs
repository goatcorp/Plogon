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
        return await this.Client.SendMessageAsync(embeds: new[] { embed });
    }
}