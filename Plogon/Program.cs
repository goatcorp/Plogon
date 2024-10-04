using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Amazon.S3;

using Discord;

using Octokit;

using Plogon.Repo;

using Serilog;

namespace Plogon;

class Program
{
    private enum ModeOfOperation
    {
        /// <summary>
        /// No mode set.
        /// </summary>
        Unknown,

        /// <summary>
        /// We are building a plugin for someone in a Pull Request.
        /// </summary>
        PullRequest,

        /// <summary>
        /// We are building pending plugins to submit them to the repo.
        /// </summary>
        Commit,

        /// <summary>
        /// We are running a continuous verification build for Dalamud.
        /// </summary>
        Continuous,

        /// <summary>
        /// We are building a plugin in dev mode to validate Plogon itself.
        /// </summary>
        Development,
    }

    /// <summary>
    /// The main entry point for the application.
    /// </summary>
    /// <param name="outputFolder">The folder used for storing output and state.</param>
    /// <param name="manifestFolder">The folder used for storing plugin manifests.</param>
    /// <param name="workFolder">The folder to store temporary files and build output in.</param>
    /// <param name="staticFolder">The 'static' folder that holds script files.</param>
    /// <param name="artifactFolder">The folder to store artifacts in.</param>
    /// <param name="mode">Mode to run Plogon in.</param>
    /// <param name="buildOverridesFile">Path to file containing build overrides.</param>
    /// <param name="ci">Running in CI.</param>
    /// <param name="buildAll">Ignore actor checks.</param>
    static async Task Main(DirectoryInfo outputFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder, DirectoryInfo artifactFolder, ModeOfOperation mode, FileInfo? buildOverridesFile = null, bool ci = false, bool buildAll = false)
    {
        SetupLogging();

        if (mode == ModeOfOperation.Unknown)
            throw new Exception("No mode of operation specified.");

        var s3AccessKey = Environment.GetEnvironmentVariable("PLOGON_S3_ACCESSKEY");
        var s3Secret = Environment.GetEnvironmentVariable("PLOGON_S3_SECRET");
        var s3Region = Environment.GetEnvironmentVariable("PLOGON_S3_REGION");

        IAmazonS3? s3Client = null;
        if (s3AccessKey != null && s3Secret != null && s3Region != null)
        {
            var s3Creds = new Amazon.Runtime.BasicAWSCredentials(s3AccessKey, s3Secret);
            s3Client = new AmazonS3Client(s3Creds, Amazon.RegionEndpoint.GetBySystemName(s3Region));
        }

        var publicChannelWebhook = new DiscordWebhook(Environment.GetEnvironmentVariable("DISCORD_WEBHOOK"));
        var pacChannelWebhook = new DiscordWebhook(Environment.GetEnvironmentVariable("PAC_DISCORD_WEBHOOK"));
        var webservices = new WebServices();

        var githubSummary = "## Build Summary\n";
        GitHubOutputBuilder.SetActive(ci);

        var githubActor = Environment.GetEnvironmentVariable("PLOGON_ACTOR");
        var repoParts = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")?.Split("/");
        var repoOwner = repoParts?[0];
        var repoName = repoParts?[1];
        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUM");

        if (mode == ModeOfOperation.PullRequest && string.IsNullOrEmpty(prNumber))
            throw new Exception("PR number not set");
        
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
            
            if (githubActor == null)
                throw new Exception("GITHUB_ACTOR not set");

            gitHubApi = new GitHubApi(repoOwner, repoName, token);
            Log.Verbose("GitHub API OK, running for {Actor}", githubActor);
        }

        var secretsPk = Environment.GetEnvironmentVariable("PLOGON_SECRETS_PK");
        var secretsPkBytes = string.IsNullOrEmpty(secretsPk) ? null : System.Text.Encoding.ASCII.GetBytes(secretsPk);
        var secretsPkPassword = Environment.GetEnvironmentVariable("PLOGON_SECRETS_PK_PASSWORD");

        var aborted = false;
        var numFailed = 0;
        var numTried = 0;
        var numNoIcon = 0;

        var allResults = new List<BuildProcessor.BuildResult>();

        WebServices.Stats? stats = null;
        if (mode == ModeOfOperation.PullRequest)
            stats = await webservices.GetStats();

        try
        {
            string? prDiff = null;
            if (gitHubApi is not null && repoName is not null && prNumber is not null)
            {
                prDiff = await gitHubApi.GetPullRequestDiff(prNumber);
            }
            else if (mode == ModeOfOperation.PullRequest)
            {
                Log.Error("Diff for PR is not available, this might lead to unnecessary builds being performed");
            }

            var setup = new BuildProcessor.BuildProcessorSetup
            {
                RepoFolder = outputFolder,
                ManifestFolder = manifestFolder,
                WorkFolder = workFolder,
                StaticFolder = staticFolder,
                ArtifactFolder = artifactFolder,
                BuildOverridesFile = buildOverridesFile,
                SecretsPrivateKeyBytes = secretsPkBytes,
                SecretsPrivateKeyPassword = secretsPkPassword,
                PrDiff = prDiff,
                AllowNonDefaultImages = mode != ModeOfOperation.Continuous, // HACK, fix it
                CutoffDate = null,
                S3Client = s3Client,
                GitHubActor = githubActor,
            };

            // HACK, we don't know the API level a plugin is for before building it...
            // Feels like a design flaw, but we can't do much about it until we change how
            // packager works
            if (mode == ModeOfOperation.Continuous)
            {
                // API8 release
                setup.CutoffDate = new DateTime(2023, 06, 10);
            }

            var buildProcessor = new BuildProcessor(setup);
            var tasks = buildProcessor.GetBuildTasks(mode == ModeOfOperation.Continuous);

            GitHubOutputBuilder.StartGroup("List all tasks");

            foreach (var buildTask in tasks)
            {
                Log.Information("{TaskName}", buildTask.ToString());
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
                            // If we are not committing, removal tasks don't do anything and we should not consider them
                            if (mode != ModeOfOperation.Commit)
                                continue;

                            GitHubOutputBuilder.StartGroup($"Remove {task.InternalName}");
                            Log.Information("Remove: {Name} - {Channel}", task.InternalName, task.Channel);

                            var removeStatus = await buildProcessor.ProcessTask(task, true, null, tasks);
                            allResults.Add(removeStatus);

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

                        GitHubOutputBuilder.StartGroup($"Build {task.InternalName}[{task.Channel}] ({task.Manifest!.Plugin!.Commit})");

                        if (!buildAll && (task.Manifest.Plugin.Owners.All(x => x != githubActor) &&
                                          PlogonSystemDefine.PacMembers.All(x => x != githubActor)))
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

                        Log.Information("Need: {Name}[{Channel}] - {Sha} (have {HaveCommit})", task.InternalName,
                            task.Channel,
                            task.Manifest.Plugin.Commit,
                            task.HaveCommit ?? "nothing");

                        numTried++;

                        var changelog = task.Manifest.Plugin.Changelog;
                        if (string.IsNullOrEmpty(changelog) && repoName != null && prNumber != null &&
                            gitHubApi != null && mode == ModeOfOperation.Commit)
                        {
                            changelog = await gitHubApi.GetIssueBody(int.Parse(prNumber));
                        }

                        var buildResult = await buildProcessor.ProcessTask(task, mode == ModeOfOperation.Commit, changelog, tasks);
                        allResults.Add(buildResult);

                        var mainDiffUrl = buildResult.Diff?.HosterUrl ?? buildResult.Diff?.RegularDiffLink;
                        
                        if (buildResult.Success)
                        {
                            Log.Information("Built: {Name} - {Sha} - {DiffUrl} +{LinesAdded} -{LinesRemoved}", task.InternalName,
                                task.Manifest.Plugin.Commit, mainDiffUrl ?? "null", buildResult.Diff?.LinesAdded ?? -1, buildResult.Diff?.LinesRemoved ?? -1);


                            var linesAddedText = buildResult.Diff?.LinesAdded == null ? "?" : buildResult.Diff.LinesAdded.ToString();
                            var prevVersionText = string.IsNullOrEmpty(buildResult.PreviousVersion)
                                ? string.Empty
                                : $", prev. {buildResult.PreviousVersion}";
                            var diffLink = mainDiffUrl == null ? $"[Repo]({url}) <sup><sup>(New plugin)</sup></sup>" :
                                               $"[Diff]({mainDiffUrl}) <sup><sub>({linesAddedText} lines{prevVersionText})</sub></sup>";
                            
                            if (buildResult.Diff?.SemanticDiffLink != null)
                            {
                                diffLink += $" - [Semantic]({buildResult.Diff.SemanticDiffLink})";
                            }

                            // We don't want to indicate success for continuous builds
                            if (mode != ModeOfOperation.Continuous)
                            {
                                if (task.HaveVersion != null &&
                                    Version.Parse(buildResult.Version!) <= Version.Parse(task.HaveVersion))
                                {
                                    buildsMd.AddRow("⚠️", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                        $"{(buildResult.Version == task.HaveVersion ? "Same" : "Lower")} version!!! v{buildResult.Version} - {diffLink}");
                                    prLabels |= GitHubApi.PrLabel.VersionConflict;
                                }
                                else
                                {
                                    buildsMd.AddRow("✔️", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                        $"v{buildResult.Version} - {diffLink}");
                                }
                            }

                            if (!string.IsNullOrEmpty(prNumber) && mode == ModeOfOperation.PullRequest)
                                await webservices.RegisterPrNumber(task.InternalName, task.Manifest.Plugin.Commit,
                                    prNumber);

                            if (buildResult.Diff != null)
                            {
                                if (buildResult.Diff.LinesAdded > 1000)
                                {
                                    prLabels &= ~GitHubApi.PrLabel.SizeSmall;
                                    prLabels &= ~GitHubApi.PrLabel.SizeMid;
                                    prLabels |= GitHubApi.PrLabel.SizeLarge;
                                }
                                else if (buildResult.Diff.LinesAdded > 400 && !prLabels.HasFlag(GitHubApi.PrLabel.SizeLarge))
                                {
                                    prLabels &= ~GitHubApi.PrLabel.SizeSmall;
                                    prLabels |= GitHubApi.PrLabel.SizeMid;
                                }
                                else if (!prLabels.HasFlag(GitHubApi.PrLabel.SizeMid) && !prLabels.HasFlag(GitHubApi.PrLabel.SizeLarge))
                                    prLabels |= GitHubApi.PrLabel.SizeSmall;
                            }

                            if (mode == ModeOfOperation.Commit)
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
                                    Version = buildResult.Version!,
                                    Dip17Track = task.Channel,
                                    PrNumber = prInt,
                                    Changelog = changelog,
                                    IsInitialRelease = task.IsNewPlugin,
                                    DiffLinesAdded = buildResult.Diff?.LinesAdded,
                                    DiffLinesRemoved = buildResult.Diff?.LinesRemoved,
                                });
                            }
                        }
                        else
                        {
                            Log.Error("Could not build: {Name} - {Sha}", task.InternalName,
                                task.Manifest.Plugin.Commit);

                            buildsMd.AddRow("❌", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                                $"Build failed ([Diff]({mainDiffUrl}))");
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
                    catch (BuildProcessor.ApiLevelException api)
                    {
                        Log.Error("Bad API level!");
                        buildsMd.AddRow("🚦", $"{task.InternalName} [{task.Channel}]", fancyCommit,
                            $"Wrong API level! (have: {api.Have}, need: {api.Want})");
                        numFailed++;
                        numNoIcon++;

                        prLabels |= GitHubApi.PrLabel.VersionConflict;
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

                if (mode == ModeOfOperation.PullRequest)
                {
                    var existingMessages = await webservices.GetMessageIds(prNumber!);
                    var alreadyPosted = existingMessages.Length > 0;

                    var links =
                        $"[Show log](https://github.com/goatcorp/DalamudPluginsD17/actions/runs/{actionRunId}) - [Review](https://github.com/goatcorp/DalamudPluginsD17/pull/{prNumber}/files#submit-review)";

                    var commentText = anyFailed ? "Builds failed, please check action output." : "All builds OK!";
                    commentText += "\n\n**Take care!** Please test your plugins in-game before submitting them here to prevent crashes and instability. We really appreciate it!\n\n";

                    if (!anyTried)
                        commentText =
                            "⚠️ No builds attempted! This probably means that your owners property is misconfigured.";

                    var parsedPrNum = int.Parse(prNumber!);

                    var crossOutTask = gitHubApi?.CrossOutAllOfMyComments(parsedPrNum);

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

                    // List needs in detail
                    var allNeeds = allResults.SelectMany(x => x.Needs).ToList();
                    var needsText = string.Empty;
                    if (allNeeds.Count > 0)
                    {
                        var numUnreviewed = 0;
                        var numHidden = 0;
                        var needsTable = MarkdownTableBuilder.Create("Type", "Name", "Version", "Reviewed by");
                        foreach (var need in allNeeds.OrderByDescending(x => x.ReviewedBy == null))
                        {
                            var name = need.Name;
                            if (need.Type == State.Need.NeedType.NuGet)
                            {
                                if (PlogonSystemDefine.SafeNugetNamespaces.Any(x => name.StartsWith(x)))
                                {
                                    numHidden++;
                                    continue;
                                }
                                
                                name = $"[{need.Name}](https://www.nuget.org/packages/{need.Name})";
                            }
                            
                            needsTable.AddRow(
                                need.Type.ToString(),
                                name,
                                need.Version,
                                need.ReviewedBy ?? "⚠️ " + (need.OldVersion == null ? "NEW" : "Upd. from " + need.OldVersion));

                            if (need.ReviewedBy == null)
                                numUnreviewed++;
                        }

                        var hiddenText = string.Empty;
                        if (numHidden > 0)
                            hiddenText = $"\n\n##### {numHidden} hidden needs (known safe NuGet packages).\n";
                        
                        needsText = 
                            $"\n\n<details>\n<summary>{allNeeds.Count} Needs " + 
                            (numUnreviewed > 0 ? $"(⚠️ {numUnreviewed} UNREVIEWED)" : "(✅ All reviewed)") +
                            "</summary>\n\n" + needsTable.GetText() + hiddenText +
                            "</details>\n\n";
                        
                        if (numHidden == allNeeds.Count)
                            needsText = hiddenText;
                    }
                    
                    var commentTask = gitHubApi?.AddComment(parsedPrNum,
                        commentText + mergeTimeText + "\n\n" + buildsMd + needsText + "\n##### " + links);

                    if (commentTask != null)
                        await commentTask;

                    var hookTitle = $"PR #{prNumber}";
                    var buildInfo = string.Empty;

                    if (!alreadyPosted)
                    {
                        hookTitle += " created";

                        var prDesc = await gitHubApi!.GetIssueBody(parsedPrNum);
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
                    var id = await publicChannelWebhook.Send(ok ? Color.Purple : Color.Red,
                        $"{buildInfo}\n\n{links} - [PR](https://github.com/goatcorp/DalamudPluginsD17/pull/{prNumber})",
                        hookTitle, ok ? "Accepted" : "Rejected");
                    await webservices.RegisterMessageId(prNumber!, id);

                    if (gitHubApi != null)
                        await gitHubApi.SetPrLabels(parsedPrNum, prLabels);

                    if (prLabels.HasFlag(GitHubApi.PrLabel.NewPlugin) && gitHubApi != null)
                    {
                        await DoPacRoundRobinAssign(gitHubApi, parsedPrNum);
                    }
                }

                if (repoName != null && mode == ModeOfOperation.Commit && anyTried && publicChannelWebhook.Client != null)
                {
                    var committedText =
                        $"{ReplaceDiscordEmotes(buildsMd.GetText(true, true))}\n\n[Show log](https://github.com/goatcorp/DalamudPluginsD17/actions/runs/{actionRunId})";
                    var committedColor = !anyFailed ? Color.Green : Color.Red;
                    var committedTitle = !anyFailed ? "Builds committed" : "Repo commit failed!";
                    await publicChannelWebhook.SendSplitting(committedColor, committedText, committedTitle, string.Empty);
                    await pacChannelWebhook.SendSplitting(committedColor, committedText, committedTitle, string.Empty);

                    // TODO: We don't support this for removals for now
                    foreach (var buildResult in allResults.Where(x => x.Task.Type == BuildTask.TaskType.Build))
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
                                await publicChannelWebhook.Client.ModifyMessageAsync(ulong.Parse(id), properties =>
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
            aborted = true;
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
                Log.Error("Was a PR, but did not build any plugins - failing");
                anyFailed = true;
            }

            if (aborted || anyFailed) Environment.Exit(1);
        }
    }

    private static async Task DoPacRoundRobinAssign(GitHubApi gitHubApi, int prNumber)
    {
        var thisPr = await gitHubApi.GetPullRequest(prNumber);

        if (thisPr == null)
        {
            Log.Error("Could not get PR for round robin assign");
            return;
        }

        // Only go on if we don't have an assignee
        if (thisPr.Assignees.Any())
            return;

        string? loginToAssign;

        // Find the last new plugin PR
        //var prs = await gitHubApi.Client.PullRequest.GetAllForRepository(gitHubApi.RepoOwner, gitHubApi.RepoName);
        var result = await gitHubApi.Client.Search.SearchIssues(
                      new SearchIssuesRequest
                      {
                          Repos = new RepositoryCollection()
                          {
                              { gitHubApi.RepoOwner, gitHubApi.RepoName },
                          },
                          Is = new[] { IssueIsQualifier.PullRequest },
                          Labels = new[] { PlogonSystemDefine.PR_LABEL_NEW_PLUGIN },
                          SortField = IssueSearchSort.Created,
                      });
        var lastNewPluginPr = result?.Items.FirstOrDefault(x => x.Number != prNumber);
        if (lastNewPluginPr == null)
        {
            Log.Error("Could not find last new plugin PR for round robin assign");
            loginToAssign = PlogonSystemDefine.PacMembers[0];
        }
        else
        {
            // Find the last assignee
            var lastAssignee = lastNewPluginPr.Assignees.FirstOrDefault()?.Login;
            if (lastAssignee == null)
                loginToAssign = PlogonSystemDefine.PacMembers[0];
            else
            {
                var lastAssigneeIndex = Array.IndexOf(PlogonSystemDefine.PacMembers, lastAssignee);
                if (lastAssigneeIndex == -1)
                    loginToAssign = PlogonSystemDefine.PacMembers[0];
                else
                {
                    var nextAssigneeIndex = (lastAssigneeIndex + 1) % PlogonSystemDefine.PacMembers.Length;
                    loginToAssign = PlogonSystemDefine.PacMembers[nextAssigneeIndex];
                }
            }
        }

        await gitHubApi.Assign(prNumber, loginToAssign);
    }

    private static void SetupLogging()
    {
        Log.Logger = new LoggerConfiguration()
            .WriteTo.Console()
            .MinimumLevel.Verbose()
            .CreateLogger();
    }
}
