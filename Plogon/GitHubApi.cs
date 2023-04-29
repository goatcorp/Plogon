using System;
using System.Net.Http;
using System.Threading.Tasks;
using Octokit;

namespace Plogon;

/// <summary>
/// Things that talk to the GitHub api
/// </summary>
public class GitHubApi
{
    private readonly string repoOwner;
    private readonly string repoName;
    private readonly GitHubClient ghClient;

    /// <summary>
    /// Make new GitHub API
    /// </summary>
    /// <param name="token">Github token</param>
    public GitHubApi(string repoOwner, string repoName, string token)
    {
        this.repoOwner = repoOwner;
        this.repoName = repoName;
        this.ghClient = new GitHubClient(new ProductHeaderValue("PlogonBuild", "1.0.0"))
        {
            Credentials = new Credentials(token)
        };
    }

    /// <summary>
    /// Add comment to issue
    /// </summary>
    /// <param name="repo">The repo to use</param>
    /// <param name="issueNumber">The issue number</param>
    /// <param name="body">The body</param>
    public async Task AddComment(int issueNumber, string body)
    {
        await this.ghClient.Issue.Comment.Create(repoOwner, repoName, issueNumber, body);
    }

    public async Task CrossOutAllOfMyComments(int issueNumber)
    {
        var me = await this.ghClient.User.Current();
        if (me == null)
            throw new Exception("Couldn't get auth'd user");

        var comments = await this.ghClient.Issue.Comment.GetAllForIssue(repoOwner, repoName, issueNumber);
        if (comments == null)
            throw new Exception("Couldn't get issue comments");
        
        foreach (var comment in comments)
        {
            if (comment.User.Id != me.Id)
                continue;
            
            // Only do this once
            if (comment.Body.StartsWith("<details>"))
                continue;

            var newComment = $"<details>\n<summary>Outdated attempt</summary>\n\n{comment.Body}\n</details>";
            await this.ghClient.Issue.Comment.Update(repoOwner, repoName, comment.Id, newComment);
        }
    }

    /// <summary>
    /// Retrieves a diff as string for a given pull request
    /// </summary>
    /// <param name="repo">The repo to use</param>
    /// <param name="prNum">The pull request number</param>
    /// <returns></returns>
    public async Task<string> GetPullRequestDiff(string repo, string prNum)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync($"https://github.com/{repo}/pull/{prNum}.diff");
    }

    /// <summary>
    /// Get the body of an issue.
    /// </summary>
    /// <param name="repo">Repo name</param>
    /// <param name="issueNumber">Issue number</param>
    /// <returns>PR body</returns>
    /// <exception cref="Exception">Thrown when the body couldn't be read</exception>
    public async Task<string> GetIssueBody(int issueNumber)
    {
        var pr = await this.ghClient.PullRequest.Get(repoOwner, repoName, issueNumber);
        if (pr == null)
            throw new Exception("Could not get PR");

        return pr.Body;
    }
}