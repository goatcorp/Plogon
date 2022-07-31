using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Serilog;

namespace Plogon;

/// <summary>
/// Things that talk to the GitHub api
/// </summary>
public class GitHubApi
{
    private readonly string token;
    private readonly HttpClient client;

    /// <summary>
    /// Make new GitHub API
    /// </summary>
    /// <param name="token">Github token</param>
    public GitHubApi(string token)
    {
        this.token = token;
        this.client = new HttpClient()
        {
            DefaultRequestHeaders =
            {
                Accept = {new MediaTypeWithQualityHeaderValue("application/vnd.github+json")},
                UserAgent = { new ProductInfoHeaderValue("PlogonBuild", "1.0.0") }
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

        Log.Verbose("Sending comment to PR #{PrNumber} for {RepoName}", issueNumber, repo);

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.github.com/repos/{repo}/issues/{issueNumber}/comments");
        request.Headers.Add("Authorization", $"token {this.token}");
        request.Content = JsonContent.Create(jsonBody);

        var response = await this.client.SendAsync(request);
        Log.Verbose(await response.Content.ReadAsStringAsync());
        
        response.EnsureSuccessStatusCode();
    }
}