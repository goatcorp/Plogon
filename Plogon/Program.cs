using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Serilog;

namespace Plogon;

class Program
{
    private static readonly string[] AlwaysBuildUsers = new[] { "goaaats", "reiichi001", "lmcintyre", "ackwell", "karashiiro", "philpax" };

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="outputFolder">The folder used for storing output and state.</param>
    /// <param name="manifestFolder">The folder used for storing plugin manifests.</param>
    /// <param name="workFolder">The folder to store temporary files and build output in.</param>
    /// <param name="staticFolder">The 'static' folder that holds script files.</param>
    /// <param name="artifactFolder">The folder to store artifacts in.</param>
    /// <param name="ci">Running in CI.</param>
    /// <param name="commit">Commit to repo.</param>
    /// <param name="buildAll">Ignore actor checks.</param>
    static async Task Main(DirectoryInfo outputFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder, DirectoryInfo artifactFolder, bool ci = false, bool commit = false, bool buildAll = false)
    {
        SetupLogging();

        var webhook = new DiscordWebhook();
        var webservices = new WebServices();

        var githubSummary = "## Build Summary\n";
        GitHubOutputBuilder.SetActive(ci);

        var actor = Environment.GetEnvironmentVariable("PR_ACTOR");
        var repoParts = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")?.Split("/");
        var repoOwner = repoParts?[0];
        var repoName = repoParts?[1];
        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUM");

        GitHubApi? gitHubApi = null;
        if (ci)
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(token))
                throw new Exception("GITHUB_TOKEN not set");

            if (string.IsNullOrEmpty(repoOwner))
                throw new Exception("repoOwner null or empty");

            if (string.IsNullOrEmpty(repoName))
                throw new Exception("repoName null or empty");

            gitHubApi = new GitHubApi(repoOwner, repoName, token);
            Log.Verbose("GitHub API OK, running for {Actor}", actor);
        }

        var secretsPk = Environment.GetEnvironmentVariable("PLOGON_SECRETS_PK");
        if (string.IsNullOrEmpty(secretsPk))
            throw new Exception("No secrets private key");
        var secretsPkBytes = System.Text.Encoding.ASCII.GetBytes(secretsPk);

        var secretsPkPassword = Environment.GetEnvironmentVariable("PLOGON_SECRETS_PK_PASSWORD");
        if (string.IsNullOrEmpty(secretsPkPassword))
            throw new Exception("No secrets private key password");

        var aborted = false;
        var numFailed = 0;
        var numTried = 0;
        var numNoIcon = 0;

        var statuses = new List<BuildProcessor.BuildResult>();

        WebServices.Stats? stats = null;
        if (!commit)
            stats = await webservices.GetStats();

        try
        {
            string? prDiff = null;
            if (gitHubApi is not null && repoName is not null && prNumber is not null)
            {
                prDiff = await gitHubApi.GetPullRequestDiff(prNumber);
            }
            else
            {
                Log.Information("Diff for PR is not available, this might lead to unnecessary builds being performed.");
            }

            var buildProcessor = new BuildProcessor(outputFolder, manifestFolder, workFolder, staticFolder,
                artifactFolder, secretsPkBytes, secretsPkPassword, prDiff);
            var tasks = buildProcessor.GetBuildTasks();

            GitHubOutputBuilder.StartGroup("List all tasks");

            foreach (var buildTask in tasks)
            {
                Log.Information(buildTask.ToString());
            }

            GitHubOutputBuilder.EndGroup();

            if (!tasks.Any())
            {
                Log.Information("Nothing to do, goodbye...");
                githubSummary += "\nNo tasks were detected, if you didn't change any manifests, this is intended.";
            }
            else
            {
                GitHubOutputBuilder.StartGroup("Get images");
                var images = await buildProcessor.SetupDockerImage();
                Debug.Assert(images.Any(), "No images returned");

                var imagesMd = MarkdownTableBuilder.Create("Tags", "Created");
                foreach (var imageInspectResponse in images)
                {
                    imagesMd.AddRow(string.Join(",", imageInspectResponse.RepoTags),
                        imageInspectResponse.Created.ToLongDateString());
                }

                GitHubOutputBuilder.EndGroup();

                githubSummary += "### Build Results\n";

                var buildsMd = MarkdownTableBuilder.Create(" ", "Name", "Commit", "Status");

                // label flags
                var prLabels = GitHubApi.PrLabel.None;

                foreach (var task in tasks)
                {
                    string? fancyCommit = null;
                    var url = task.Manifest?.Plugin.Repository.Replace(".git", string.Empty);
                    if (task.Manifest?.Plugin.Commit != null && url != null)
                    {
                        fancyCommit = task.Manifest.Plugin.Commit.Length > 7
                            ? task.Manifest.Plugin.Commit[..7]
                            : task.Manifest.Plugin.Commit;

                        if (task.IsGitHub)
                        {
                            fancyCommit = $"[{fancyCommit}]({url}/commit/{task.Manifest.Plugin.Commit})";
                        }
                        else if (task.IsGitLab)
                        {
                            fancyCommit = $"[{fancyCommit}]({url}/-/commit/{task.Manifest.Plugin.Commit})";
                        }
                    }

                    fancyCommit ??= "n/a";

                    if (aborted)
                    {
                        Log.Information("Aborted, won't run: {Name}", task.InternalName);

                        buildsMd.AddRow("❔", $"{task.InternalName} [{task.Channel}]", fancyCommit, "Not ran");
                        continue;
                    }

                    if (task.IsNewPlugin)
                        prLabels |= GitHubApi.PrLabel.NewPlugin;
                    else if (task.IsNewInThisChannel)
                        prLabels |= GitHubApi.PrLabel.MoveChannel;

                    try
                    {
                        if (task.Type == BuildTask.TaskType.Remove)
                        {
                            if (!commit)
                                continue;

                            GitHubOutputBuilder.StartGroup($"Remove {task.InternalName}");
                            Log.Information("Remove: {Name} - {Channel}", task.InternalName, task.Channel);

                            var removeStatus = await buildProcessor.ProcessTask(task, commit, null, tasks);
                            statuses.Add(removeStatus);

                            if (removeStatus.Success)
                            {
                                buildsMd.AddRow("🚮", $"{task.InternalName} [{task.Channel}]", "n/a", "Removed");
                            }
                            else
                            {
                                buildsMd.AddRow("🚯", $"{task.InternalName} [{task.Channel}]", "n/a", "Removal failed");
                            }

                            GitHubOutputBuilder.EndGroup();
                            continue;
                        }

                        GitHubOutputBuilder.StartGroup($"Build {task.InternalName} ({task.Manifest!.Plugin!.Commit})");

                        if (!buildAll && (task.Manifest.Plugin.Owners.All(x => x != actor) &&
                                          AlwaysBuildUsers.All(x => x != actor)))
                        {
                            Log.Information("Not owned: {Name} - {Sha} (have {HaveCommit})", task.InternalName,
                                task.Manifest.Plugin.Commit,
                                task.HaveCommit ?? "nothing");

                            // Only complain if the last build was less recent, indicates configuration error
                            if (!task.HaveTimeBuilt.HasValue || task.HaveTimeBuilt.Value <= DateTime.Now)
                                buildsMd.AddRow("👽", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                    "Not your plugin");

                            continue;
                        }

                        Log.Information("Need: {Name} - {Sha} (have {HaveCommit})", task.InternalName,
                            task.Manifest.Plugin.Commit,
                            task.HaveCommit ?? "nothing");

                        numTried++;

                        var changelog = task.Manifest.Plugin.Changelog;
                        if (string.IsNullOrEmpty(changelog) && repoName != null && prNumber != null &&
                            gitHubApi != null && commit)
                        {
                            changelog = await gitHubApi.GetIssueBody(int.Parse(prNumber));
                        }

                        var status = await buildProcessor.ProcessTask(task, commit, changelog, tasks);
                        statuses.Add(status);

                        if (status.Success)
                        {
                            Log.Information("Built: {Name} - {Sha} - {DiffUrl} +{LinesAdded} -{LinesRemoved}", task.InternalName,
                                task.Manifest.Plugin.Commit, status.DiffUrl ?? "null", status.DiffLinesAdded ?? -1, status.DiffLinesRemoved ?? -1);

                            var prevVersionText = string.IsNullOrEmpty(status.PreviousVersion)
                                ? string.Empty
                                : $", prev. {status.PreviousVersion}";
                            var diffLink = status.DiffUrl == url ? $"[Repo]({url}) <sup><sup>(New plugin)</sup></sup>" :
                                $"[Diff]({status.DiffUrl}) <sup><sub>({status.DiffLinesAdded} lines{prevVersionText})</sub></sup>";

                            if (task.HaveVersion != null &&
                                Version.Parse(status.Version!) <= Version.Parse(task.HaveVersion))
                            {
                                buildsMd.AddRow("⚠️", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                    $"{(status.Version == task.HaveVersion ? "Same" : "Lower")} version!!! v{status.Version} - {diffLink}");
                                prLabels |= GitHubApi.PrLabel.VersionConflict;
                            }
                            else
                            {
                                buildsMd.AddRow("✔️", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                    $"v{status.Version} - {diffLink}");
                            }

                            if (!string.IsNullOrEmpty(prNumber) && !commit)
                                await webservices.RegisterPrNumber(task.InternalName, task.Manifest.Plugin.Commit,
                                    prNumber);

                            if (status.DiffLinesAdded.HasValue)
                            {
                                if (status.DiffLinesAdded > 1000)
                                {
                                    prLabels &= ~GitHubApi.PrLabel.SizeSmall;
                                    prLabels &= ~GitHubApi.PrLabel.SizeMid;
                                    prLabels |= GitHubApi.PrLabel.SizeLarge;
                                }
                                else if (status.DiffLinesAdded > 400 && !prLabels.HasFlag(GitHubApi.PrLabel.SizeLarge))
                                {
                                    prLabels &= ~GitHubApi.PrLabel.SizeSmall;
                                    prLabels |= GitHubApi.PrLabel.SizeMid;
                                }
                                else if (!prLabels.HasFlag(GitHubApi.PrLabel.SizeMid) && !prLabels.HasFlag(GitHubApi.PrLabel.SizeLarge))
                                    prLabels |= GitHubApi.PrLabel.SizeSmall;
                            }

                            if (commit)
                            {
                                int? prInt = null;
                                if (int.TryParse(
                                        await webservices.GetPrNumber(task.InternalName, task.Manifest.Plugin.Commit),
                                        out var commitPrNum))
                                {
                                    // Let's try again here in case we didn't get it the first time around
                                    if (string.IsNullOrEmpty(changelog) && repoName != null &&
                                        gitHubApi != null)
                                    {
                                        changelog = await gitHubApi.GetIssueBody(commitPrNum);
                                    }

                                    prInt = commitPrNum;
                                }

                                await webservices.StagePluginBuild(new WebServices.StagedPluginInfo
                                {
                                    InternalName = task.InternalName,
                                    Version = status.Version!,
                                    Dip17Track = task.Channel,
                                    PrNumber = prInt,
                                    Changelog = changelog,
                                    IsInitialRelease = task.IsNewPlugin,
                                    DiffLinesAdded = status.DiffLinesAdded,
                                    DiffLinesRemoved = status.DiffLinesRemoved,
                                });
                            }
                        }
                        else
                        {
                            Log.Error("Could not build: {Name} - {Sha}", task.InternalName,
                                task.Manifest.Plugin.Commit);

                            buildsMd.AddRow("❌", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                $"Build failed ([Diff]({status.DiffUrl}))");
                            numFailed++;
                        }
                    }
                    catch (BuildProcessor.PluginCommitException ex)
                    {
                        // We just can't make sure that the state of the repo is consistent here...
                        // Need to abort.

                        Log.Error(ex, "Repo consistency can't be guaranteed, aborting...");
                        buildsMd.AddRow("⁉️", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                            "Could not commit to repo");
                        aborted = true;
                        numFailed++;
                    }
                    catch (BuildProcessor.MissingIconException)
                    {
                        Log.Error("Missing icon!");
                        buildsMd.AddRow("🖼️", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                            "Missing icon in images/ build output!");
                        numFailed++;
                        numNoIcon++;

                        prLabels |= GitHubApi.PrLabel.NeedIcon;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not build");
                        buildsMd.AddRow("😰", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                            $"Build system error: {ex.Message}");
                        numFailed++;
                    }

                    GitHubOutputBuilder.EndGroup();
                }

                githubSummary += buildsMd.ToString();

                githubSummary += "### Images used\n";
                githubSummary += imagesMd.ToString();

                var actionRunId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");

                string ReplaceDiscordEmotes(string text)
                {
                    text = text.Replace("✔️", "<:yeah:980227103725342810>");
                    text = text.Replace("❌", "<:whaaa:980227735421079622>");
                    text = text.Replace("😰", "<:dogeatbee:539585692439674881>");
                    return text;
                }

                if (aborted || (numFailed > 0 && numFailed != numNoIcon))
                    prLabels |= GitHubApi.PrLabel.BuildFailed;

                var anyTried = numTried > 0;
                var anyFailed = numFailed > 0;

                if (repoName != null && prNumber != null)
                {
                    var existingMessages = await webservices.GetMessageIds(prNumber);
                    var alreadyPosted = existingMessages.Length > 0;

                    var links =
                        $"[Show log](https://github.com/goatcorp/DalamudPluginsD17/actions/runs/{actionRunId}) - [Review](https://github.com/goatcorp/DalamudPluginsD17/pull/{prNumber}/files#submit-review)";

                    var commentText = anyFailed ? "Builds failed, please check action output." : "All builds OK!";
                    if (!anyTried)
                        commentText =
                            "⚠️ No builds attempted! This probably means that your owners property is misconfigured.";

                    var prNum = int.Parse(prNumber);

                    var crossOutTask = gitHubApi?.CrossOutAllOfMyComments(prNum);

                    var anyComments = true;
                    if (crossOutTask != null)
                        anyComments = await crossOutTask;

                    var mergeTimeText = string.Empty;
                    if (!anyComments && stats != null)
                    {
                        var timeText = stats.MeanMergeTimeUpdate.TotalHours switch
                        {
                            < 1 => "less than an hour",
                            1 => "1 hour",
                            > 1 and < 24 => $"{stats.MeanMergeTimeUpdate.Hours} hours",
                            _ => "more than a day"
                        };

                        mergeTimeText =
                            $"\nThe average merge time for plugin updates is currently {timeText}.";
                    }

                    var commentTask = gitHubApi?.AddComment(prNum,
                        commentText + mergeTimeText + "\n\n" + buildsMd + "\n##### " + links);

                    if (commentTask != null)
                        await commentTask;

                    var hookTitle = $"PR #{prNumber}";
                    var buildInfo = string.Empty;

                    if (!alreadyPosted)
                    {
                        hookTitle += " created";

                        var prDesc = await gitHubApi!.GetIssueBody(prNum);
                        if (!string.IsNullOrEmpty(prDesc))
                            buildInfo += $"```\n{prDesc}\n```\n";
                    }
                    else
                    {
                        hookTitle += " updated";
                    }

                    buildInfo += anyTried ? buildsMd.GetText(true, true) : "No builds made.";
                    buildInfo = ReplaceDiscordEmotes(buildInfo);

                    var nameTask = tasks.FirstOrDefault(x => x.Type == BuildTask.TaskType.Build);
                    var numBuildTasks = tasks.Count(x => x.Type == BuildTask.TaskType.Build);

                    if (nameTask != null)
                        hookTitle +=
                            $": {nameTask.InternalName} [{nameTask.Channel}]{(numBuildTasks > 1 ? $" (+{numBuildTasks - 1})" : string.Empty)}";

                    var ok = !anyFailed && anyTried;
                    var id = await webhook.Send(ok ? Color.Purple : Color.Red,
                        $"{buildInfo}\n\n{links} - [PR](https://github.com/goatcorp/DalamudPluginsD17/pull/{prNumber})",
                        hookTitle, ok ? "Accepted" : "Rejected");
                    await webservices.RegisterMessageId(prNumber!, id);

                    if (gitHubApi != null)
                        await gitHubApi.SetPrLabels(prNum, prLabels);
                }

                if (repoName != null && commit && anyTried)
                {
                    await webhook.Send(!anyFailed ? Color.Green : Color.Red,
                        $"{ReplaceDiscordEmotes(buildsMd.GetText(true, true))}\n\n[Show log](https://github.com/goatcorp/DalamudPluginsD17/actions/runs/{actionRunId})",
                        "Builds committed", string.Empty);

                    // TODO: We don't support this for removals for now
                    foreach (var buildResult in statuses.Where(x => x.Task.Type == BuildTask.TaskType.Build))
                    {
                        if (!buildResult.Success && !aborted)
                            continue;

                        var resultPrNum =
                            await webservices.GetPrNumber(buildResult.Task.InternalName, buildResult.Task.Manifest!.Plugin.Commit);
                        if (resultPrNum == null)
                        {
                            Log.Warning("No PR for {InternalName} - {Version}", buildResult.Task.InternalName,
                                buildResult.Version);
                            continue;
                        }

                        try
                        {
                            var msgIds = await webservices.GetMessageIds(resultPrNum);

                            foreach (var id in msgIds)
                            {
                                await webhook.Client.ModifyMessageAsync(ulong.Parse(id), properties =>
                                {
                                    var embed = properties.Embeds.Value.First();
                                    var newEmbed = new EmbedBuilder()
                                        .WithColor(Color.LightGrey)
                                        .WithTitle(embed.Title)
                                        .WithCurrentTimestamp()
                                        .WithDescription(embed.Description);

                                    if (embed.Author.HasValue)
                                        newEmbed = newEmbed.WithAuthor(embed.Author.Value.Name,
                                            embed.Author.Value.IconUrl,
                                            embed.Author.Value.Url);

                                    if (embed.Footer.HasValue)
                                    {
                                        if (embed.Footer.Value.Text.Contains("Comment"))
                                        {
                                            newEmbed = newEmbed.WithFooter(
                                                embed.Footer.Value.Text.Replace("Comment", "Committed"),
                                                embed.Footer.Value.IconUrl);
                                        }
                                        else
                                        {
                                            newEmbed = newEmbed.WithFooter("Committed");
                                        }
                                    }

                                    properties.Embeds = new[] { newEmbed.Build() };
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "Could not update messages");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed during init");
        }
        finally
        {
            var githubSummaryFilePath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (!string.IsNullOrEmpty(githubSummaryFilePath))
            {
                await File.WriteAllTextAsync(githubSummaryFilePath, githubSummary);
            }

            var anyFailed = numFailed > 0;

            if (numTried == 0 && prNumber != null)
            {
                Log.Error("Was a PR, but did not build any plugins - failing.");
                anyFailed = true;
            }

            if (aborted || anyFailed) Environment.Exit(1);
        }
    }

    private static void SetupLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Verbose()
            .CreateLogger();
    }
}