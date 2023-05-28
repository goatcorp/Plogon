using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <param name="repoOwner">owner of the repo</param>
    /// <param name="repoName">name of the repo</param>
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
    /// <param name="issueNumber">The issue number</param>
    /// <param name="body">The body</param>
    public async Task AddComment(int issueNumber, string body)
    {
        await this.ghClient.Issue.Comment.Create(repoOwner, repoName, issueNumber, body);
    }

    /// <summary>
    /// For all comments left by the current user on an issue/PR, put them into a "details" md fold.
    /// </summary>
    /// <param name="issueNumber">issue/pr number</param>
    public async Task<bool> CrossOutAllOfMyComments(int issueNumber)
    {
        var me = await this.ghClient.User.Current();
        if (me == null)
            throw new Exception("Couldn't get auth'd user");

        var comments = await this.ghClient.Issue.Comment.GetAllForIssue(repoOwner, repoName, issueNumber);
        if (comments == null)
            throw new Exception("Couldn't get issue comments");

        var any = false;
        foreach (var comment in comments)
        {
            if (comment.User.Id != me.Id)
                continue;
            any = true;
            
            // Only do this once
            if (comment.Body.StartsWith("<details>"))
                continue;

            var newComment = $"<details>\n<summary>Outdated attempt</summary>\n\n{comment.Body}\n</details>";
            await this.ghClient.Issue.Comment.Update(repoOwner, repoName, comment.Id, newComment);
        }

        return any;
    }

    /// <summary>
    /// Retrieves a diff as string for a given pull request
    /// </summary>
    /// <param name="prNum">The pull request number</param>
    /// <returns></returns>
    public async Task<string> GetPullRequestDiff(string prNum)
    {
        using var client = new HttpClient();
        return await client.GetStringAsync($"https://github.com/{repoOwner}/{repoName}/pull/{prNum}.diff");
    }

    /// <summary>
    /// Get the body of an issue.
    /// </summary>
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
    
    private const string PR_LABEL_NEW_PLUGIN = "new plugin";
    private const string PR_LABEL_NEED_ICON = "need icon";
    private const string PR_LABEL_BUILD_FAILED = "build failed";
    private const string PR_LABEL_VERSION_CONFLICT = "version conflict";
    private const string PR_LABEL_MOVE_CHANNEL = "move channel";
    private const string PR_LABEL_SIZE_SMALL = "size-small";
    private const string PR_LABEL_SIZE_MID = "size-mid";
    private const string PR_LABEL_SIZE_LARGE = "size-large";

    /// <summary>
    /// Labels for DIP17 PRs
    /// </summary>
    [Flags]
    public enum PrLabel
    {
        /// <summary>
        /// No label
        /// </summary>
        None = 0,
        
        /// <summary>
        /// "new plugin"
        /// </summary>
        NewPlugin = 1 << 0,
        
        /// <summary>
        /// "need icon"
        /// </summary>
        NeedIcon = 1 << 1,
        
        /// <summary>
        /// "build failed"
        /// </summary>
        BuildFailed = 1 << 2,
        
        /// <summary>
        /// "version conflict"
        /// </summary>
        VersionConflict = 1 << 3,
        
        /// <summary>
        /// "move channel"
        /// </summary>
        MoveChannel = 1 << 4,
        
        /// <summary>
        /// "size-s"
        /// </summary>
        SizeSmall = 1 << 5,
        
        /// <summary>
        /// "size-m"
        /// </summary>
        SizeMid = 1 << 6,
        
        /// <summary>
        /// "size-l"
        /// </summary>
        SizeLarge = 1 << 7
    }

    /// <summary>
    /// Set the PR labels on a PR. Existing unmanaged labels are preserved.
    /// </summary>
    /// <param name="issueNumber">pr/issue number</param>
    /// <param name="label">labels to set</param>
    public async Task SetPrLabels(int issueNumber, PrLabel label)
    {
        var managedLabels = new HashSet<string>();
        
        var existing = await this.ghClient.Issue.Labels.GetAllForIssue(repoOwner, repoName, issueNumber);
        if (existing != null)
        {
            foreach (var existingLabel in existing)
            {
                managedLabels.Add(existingLabel.Name);
            }
        }

        if (label.HasFlag(PrLabel.NewPlugin))
            managedLabels.Add(PR_LABEL_NEW_PLUGIN);
        else
            managedLabels.Remove(PR_LABEL_NEW_PLUGIN);
        
        if (label.HasFlag(PrLabel.NeedIcon))
            managedLabels.Add(PR_LABEL_NEED_ICON);
        else
            managedLabels.Remove(PR_LABEL_NEED_ICON);
        
        if (label.HasFlag(PrLabel.BuildFailed))
            managedLabels.Add(PR_LABEL_BUILD_FAILED);
        else
            managedLabels.Remove(PR_LABEL_BUILD_FAILED);
        
        if (label.HasFlag(PrLabel.VersionConflict))
            managedLabels.Add(PR_LABEL_VERSION_CONFLICT);
        else
            managedLabels.Remove(PR_LABEL_VERSION_CONFLICT);
        
        if (label.HasFlag(PrLabel.MoveChannel))
            managedLabels.Add(PR_LABEL_MOVE_CHANNEL);
        else
            managedLabels.Remove(PR_LABEL_MOVE_CHANNEL);
        
        if (label.HasFlag(PrLabel.SizeSmall))
            managedLabels.Add(PR_LABEL_SIZE_SMALL);
        else
            managedLabels.Remove(PR_LABEL_SIZE_SMALL);
        
        if (label.HasFlag(PrLabel.SizeMid))
            managedLabels.Add(PR_LABEL_SIZE_MID);
        else
            managedLabels.Remove(PR_LABEL_SIZE_MID);
        
        if (label.HasFlag(PrLabel.SizeLarge))
            managedLabels.Add(PR_LABEL_SIZE_LARGE);
        else
            managedLabels.Remove(PR_LABEL_SIZE_LARGE);

        await this.ghClient.Issue.Labels.ReplaceAllForIssue(repoOwner, repoName, issueNumber, managedLabels.ToArray());
    }
}