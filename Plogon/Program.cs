using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;

namespace Plogon;

class Program
{
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
    static async Task Main(DirectoryInfo outputFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder, DirectoryInfo artifactFolder, bool ci = false, bool commit = false)
    {
        SetupLogging();

        var githubSummary = "## Build Summary\n";
        GitHubOutputBuilder.SetActive(ci);

        GitHubApi? gitHubApi = null;
        if (ci)
        {
            var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
            if (string.IsNullOrEmpty(token))
                throw new Exception("GITHUB_TOKEN not set");
            
            gitHubApi = new GitHubApi(token);
            Log.Verbose("GitHub API OK");
        }

        var actor = Environment.GetEnvironmentVariable("GITHUB_ACTOR");
        var repoName = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        var prNumber = Environment.GetEnvironmentVariable("GITHUB_PR_NUM");
        
        var aborted = false;
        var anyFailed = false;

        try
        {
            var buildProcessor = new BuildProcessor(outputFolder, manifestFolder, workFolder, staticFolder, artifactFolder);
            var tasks = buildProcessor.GetTasks();

            if (!tasks.Any())
            {
                Log.Information("Nothing to do, goodbye...");
                githubSummary += "No tasks were detected, this is probably an issue on our side, please report.";
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
                
                foreach (var task in tasks)
                {
                    GitHubOutputBuilder.StartGroup($"Build {task.InternalName} ({task.Manifest.Plugin.Commit})");

                    if (actor != null && task.Manifest.Plugin.Owners.All(x => x != actor))
                    {
                        Log.Information("Not owned: {Name} - {Sha} (have {HaveCommit})", task.InternalName,
                            task.Manifest.Plugin.Commit,
                            task.HaveCommit ?? "nothing");

                        buildsMd.AddRow("👽", task.InternalName, task.Manifest.Plugin.Commit, "Not your plugin");
                        continue;
                    }
                    
                    if (aborted)
                    {
                        Log.Information("Aborted, won't run: {Name} - {Sha} (have {HaveCommit})", task.InternalName,
                            task.Manifest.Plugin.Commit,
                            task.HaveCommit ?? "nothing");

                        buildsMd.AddRow("❔", task.InternalName, task.Manifest.Plugin.Commit, "Not ran");
                        continue;
                    }
                    
                    try
                    {
                        Log.Information("Need: {Name} - {Sha} (have {HaveCommit})", task.InternalName,
                            task.Manifest.Plugin.Commit,
                            task.HaveCommit ?? "nothing");
                        var status = await buildProcessor.ProcessTask(task, commit);

                        if (status.Success)
                        {
                            Log.Information("Built: {Name} - {Sha} - {DiffUrl}", task.InternalName,
                                task.Manifest.Plugin.Commit, status.DiffUrl);
                            
                            buildsMd.AddRow("✔️", task.InternalName, task.Manifest.Plugin.Commit, $"[Diff]({status.DiffUrl})");
                        }
                        else
                        {
                            Log.Error("Could not build: {Name} - {Sha}", task.InternalName,
                                task.Manifest.Plugin.Commit);
                            
                            buildsMd.AddRow("❌", task.InternalName, task.Manifest.Plugin.Commit, $"Build failed ([Diff]({status.DiffUrl}))");
                            anyFailed = true;
                        }
                    }
                    catch (BuildProcessor.PluginCommitException ex)
                    {
                        // We just can't make sure that the state of the repo is consistent here...
                        // Need to abort.
                        
                        Log.Error(ex, "Repo consistency can't be guaranteed, aborting...");
                        buildsMd.AddRow("⁉️", task.InternalName, task.Manifest.Plugin.Commit, "Could not commit to repo");
                        aborted = true;
                        anyFailed = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Could not build");
                        buildsMd.AddRow("😰", task.InternalName, task.Manifest.Plugin.Commit, $"Build system error: {ex.Message}");
                        anyFailed = true;
                    }

                    GitHubOutputBuilder.EndGroup();
                }

                githubSummary += buildsMd.ToString();

                githubSummary += "### Images used\n";
                githubSummary += imagesMd.ToString();

                if (repoName != null && prNumber != null)
                {
                    var commentTask = gitHubApi?.AddComment(repoName, int.Parse(prNumber),
                        (anyFailed ? "Builds failed, please check action output." : "All builds OK!") +
                        "\n\n" + buildsMd.ToString());

                    if (commentTask != null)
                        await commentTask;
                }
            }
        }
        finally
        {
            var githubSummaryFilePath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
            if (!string.IsNullOrEmpty(githubSummaryFilePath))
            {
                await File.WriteAllTextAsync(githubSummaryFilePath, githubSummary);
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