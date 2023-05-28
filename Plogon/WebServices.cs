using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Serilog;
#pragma warning disable CS1591

namespace Plogon;

/// <summary>
/// Communicates with xlweb
/// </summary>
public class WebServices
{
    private readonly string key;

    /// <summary>
    /// ctor
    /// </summary>
    public WebServices()
    {
        this.key = Environment.GetEnvironmentVariable("XLWEB_KEY")!;
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="prNumber"></param>
    /// <param name="messageId"></param>
    public async Task RegisterMessageId(string prNumber, ulong messageId)
    {
        using var client = new HttpClient();
        var result = await client.PostAsync(
            $"https://kamori.goats.dev/Plogon/RegisterMessageId?key={this.key}&prNumber={prNumber}&messageId={messageId}",
            null);
        result.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="prNumber"></param>
    /// <returns></returns>
    public async Task<string[]> GetMessageIds(string prNumber)
    {
        using var client = new HttpClient();
        var result = await client.GetAsync(
            $"https://kamori.goats.dev/Plogon/GetMessageIds?prNumber={prNumber}");
        result.EnsureSuccessStatusCode();

        return await result.Content.ReadFromJsonAsync<string[]>() ?? Array.Empty<string>();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="internalName"></param>
    /// <param name="version"></param>
    /// <param name="prNumber"></param>
    public async Task RegisterPrNumber(string internalName, string version, string prNumber)
    {
        using var client = new HttpClient();
        var result = await client.PostAsync(
            $"https://kamori.goats.dev/Plogon/RegisterVersionPrNumber?key={this.key}&prNumber={prNumber}&internalName={internalName}&version={version}",
            null);
        
        Log.Information(await result.Content.ReadAsStringAsync());
        result.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="internalName"></param>
    /// <param name="version"></param>
    /// <returns></returns>
    public async Task<string?> GetPrNumber(string internalName, string version)
    {
        using var client = new HttpClient();
        var result = await client.GetAsync(
            $"https://kamori.goats.dev/Plogon/GetVersionChangelog?internalName={internalName}&version={version}");

        if (result.StatusCode == HttpStatusCode.NotFound)
            return null;
        
        var text = await result.Content.ReadAsStringAsync();
        Log.Information("PR: {Text}", text);
        result.EnsureSuccessStatusCode();

        return text;
    }
    
    public class StagedPluginInfo
    {
        public string InternalName { get; set; } = null!;
        public string Version { get; set; } = null!;
        public string Dip17Track { get; set; } = null!;
        public int? PrNumber { get; set; }
        public string? Changelog { get; set; }
        public bool IsInitialRelease { get; set; }
        public int? DiffLinesAdded { get; set; }
        public int? DiffLinesRemoved { get; set; }
    }
    
    public async Task StagePluginBuild(StagedPluginInfo info)
    {
        using var client = new HttpClient();
        client.DefaultRequestHeaders.Add("X-XL-Key", this.key);
        var result = await client.PostAsync(
            $"https://kamori.goats.dev/Plogon/StagePluginBuild",
            JsonContent.Create(info));
        
        Log.Information(await result.Content.ReadAsStringAsync());
        result.EnsureSuccessStatusCode();
    }

    public class Stats
    {
        public TimeSpan MeanMergeTimeNew { get; set; }
        public TimeSpan MeanMergeTimeUpdate { get; set; }
    }

    public async Task<Stats?> GetStats()
    {
        try
        {
            using var client = new HttpClient();
            client.DefaultRequestHeaders.Add("X-XL-Key", this.key);
            var result = await client.GetAsync(
                "https://kamori.goats.dev/Plogon/Stats");

            result.EnsureSuccessStatusCode();
            return await result.Content.ReadFromJsonAsync<Stats>();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not get stats");
        }

        return null;
    }
}