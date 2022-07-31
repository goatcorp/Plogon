using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;

namespace Plogon;

/// <summary>
/// Things that talk to the GitHub api
/// </summary>
public class GitHubApi
{
    private readonly HttpClient client;

    /// <summary>
    /// Make new GitHub API
    /// </summary>
    /// <param name="token">Github token</param>
    public GitHubApi(string token)
    {
        this.client = new HttpClient()
        {
            BaseAddress = new Uri("https://api.github.com"),
            DefaultRequestHeaders =
            {
                Accept = {new MediaTypeWithQualityHeaderValue("application/vnd.github+json")},
                Authorization = new AuthenticationHeaderValue("token", token),
            }
        };
    }

    private class CommentCreateBody
    {
        public string? Body { get; set; }
    }

    /// <summary>
    /// Add comment to issue
    /// </summary>
    /// <param name="repo">The repo to use</param>
    /// <param name="issueNumber">The issue number</param>
    /// <param name="body">The body</param>
    public async Task AddComment(string repo, int issueNumber, string body)
    {
        var jsonBody = new CommentCreateBody()
        {
            Body = body,
        };

        var response = await this.client.PostAsync($"/repos/{repo}/issues/{issueNumber}/comments", JsonContent.Create(jsonBody));
        response.EnsureSuccessStatusCode();
    }
}