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
    private DiscordWebhookClient Client { get; }

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
    public async Task Send(Color color, string message, string title, string footer)
    {
        var embed = new EmbedBuilder()
            .WithColor(color)
            .WithTitle(title)
            .WithFooter(footer)
            .WithDescription(message)
            .Build();
        await this.Client.SendMessageAsync(embeds: new[] { embed });
    }
}