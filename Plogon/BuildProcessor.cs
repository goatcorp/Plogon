using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Amazon.S3;
using Amazon.S3.Model;

using Docker.DotNet;
using Docker.DotNet.Models;

using LibGit2Sharp;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using PgpCore;

using Plogon.Manifests;
using Plogon.Repo;

using Serilog;

using Tag = Amazon.S3.Model.Tag;

namespace Plogon;

/// <summary>
/// Class that generates and processes build tasks
/// </summary>
public class BuildProcessor
{
    private readonly DirectoryInfo repoFolder;
    private readonly DirectoryInfo manifestFolder;
    private readonly DirectoryInfo workFolder;
    private readonly DirectoryInfo staticFolder;
    private readonly DirectoryInfo artifactFolder;
    private readonly byte[]? secretsPrivateKeyBytes;
    private readonly string? secretsPrivateKeyPassword;
    private readonly bool allowNonDefaultImages;

    private readonly DockerClient dockerClient;

    private readonly IAmazonS3? s3Client;
    
    private static readonly string[] DalamudInternalDll = new[]
    {
        "Dalamud.dll",
        // "Lumina.dll",
        // "Lumina.Excel.dll",
        "ImGui.NET.dll",
        "ImGuiScene.dll",
    };

    private PluginRepository pluginRepository;
    private ManifestStorage manifestStorage;
    private DalamudReleases dalamudReleases;

    private bool needExtendedImage;

    private const string DOCKER_IMAGE = "mcr.microsoft.com/dotnet/sdk";
    private const string DOCKER_TAG = "8.0";

    // This field specifies which dependency package is to be fetched depending on the .net target framework.
    // The values to use in turn depend on the used SDK (see DOCKER_TAG) and what gets resolved at compile time.
    // If a plugin breaks with a missing runtime package you might want to add the package here.
    private readonly Dictionary<string, string[]> RUNTIME_PACKAGES = new()
    {
        { ".NETStandard,Version=v2.0", new[]
            { "2.0.0" }
        },
        { "net5.0", new[]
            { "5.0.0" }
        },
        { "net6.0", new[]
            { "6.0.0", "6.0.11" }
        },
        { "net7.0", new[]
            { "7.0.0", "7.0.1", "7.0.14", "7.0.15" }
        },
        { "net8.0", new[]
            { "8.0.0" }
        }
    };

    // This field specifies a list of packages that must be present in the package cache, no matter
    // whether they are present in the lockfile. This is necessary for SDK packages, as they are not
    // added to lockfiles.
    private readonly Dictionary<string, string[]> FORCE_PACKAGES = new()
    {
        { "Dalamud.NET.Sdk", new[]
            // This should have all the SDK packages we still support.
            { "9.0.2", "10.0.0" }
        },
    };

    private const string EXTENDED_IMAGE_HASH = "fba5ce59717fba4371149b8ae39d222a29a7f402c10e0941c85a27e8d1bb6ce4";

    private const string S3_BUCKET_NAME = "dalamud-plugin-archive";
    
    /// <summary>
    /// Parameters for build processor.
    /// </summary>
    public struct BuildProcessorSetup
    {
        /// <summary>
        /// Directory containing build output.
        /// </summary>
        public DirectoryInfo RepoFolder { get; set; }

        /// <summary>
        /// Directory containing manifests.
        /// </summary>
        public DirectoryInfo ManifestFolder { get; set; }

        /// <summary>
        /// Directory builds will be made in.
        /// </summary>
        public DirectoryInfo WorkFolder { get; set; }

        /// <summary>
        /// Directory containing static files.
        /// </summary>
        public DirectoryInfo StaticFolder { get; set; }

        /// <summary>
        /// Directory artifacts will be stored in.
        /// </summary>
        public DirectoryInfo ArtifactFolder { get; set; }

        /// <summary>
        /// Path to file containing overrides for the Dalamud version used.
        /// </summary>
        public FileInfo? BuildOverridesFile { get; set; }

        /// <summary>
        /// Whether or not non-default build images are allowed.
        /// </summary>
        public bool AllowNonDefaultImages { get; set; }

        /// <summary>
        /// When set, plugins whose manifest was modified before this date will not be built.
        /// </summary>
        public DateTime? CutoffDate { get; set; }

        /// <summary>
        /// Bytes of the secrets private key.
        /// </summary>
        public byte[]? SecretsPrivateKeyBytes { get; set; }

        /// <summary>
        /// Password for the aforementioned private key.
        /// </summary>
        public string? SecretsPrivateKeyPassword { get; set; }

        /// <summary>
        /// Diff in unified format that contains the changes requested by the PR we are running as
        /// </summary>
        public string? PrDiff { get; set; }
        
        /// <summary>
        /// S3 client to use for artifact uploads.
        /// </summary>
        public IAmazonS3? S3Client { get; set; }
    }

    /// <summary>
    /// Set up build processor
    /// </summary>
    public BuildProcessor(BuildProcessorSetup setup)
    {
        this.repoFolder = setup.RepoFolder;
        this.manifestFolder = setup.ManifestFolder;
        this.workFolder = setup.WorkFolder;
        this.staticFolder = setup.StaticFolder;
        this.artifactFolder = setup.ArtifactFolder;
        this.secretsPrivateKeyBytes = setup.SecretsPrivateKeyBytes;
        this.secretsPrivateKeyPassword = setup.SecretsPrivateKeyPassword;
        this.allowNonDefaultImages = setup.AllowNonDefaultImages;

        this.pluginRepository = new PluginRepository(repoFolder);
        this.manifestStorage = new ManifestStorage(manifestFolder, setup.PrDiff, true, setup.CutoffDate);
        this.dalamudReleases = new DalamudReleases(setup.BuildOverridesFile, workFolder.CreateSubdirectory("dalamud_releases_work"));

        this.dockerClient = new DockerClientConfiguration().CreateClient();

        this.s3Client = setup.S3Client;
    }

    /// <summary>
    /// Set up needed docker images for containers.
    /// </summary>
    /// <returns>List of images</returns>
    public async Task<List<ImageInspectResponse>> SetupDockerImage()
    {
        if (needExtendedImage)
        {
            using var client = new HttpClient();

            var cacheFolder = new DirectoryInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".plogon_cache"));
            if (!cacheFolder.Exists)
                cacheFolder.Create();

            var imageFile = new FileInfo(Path.Combine(cacheFolder.FullName, "extended-image.tar.bz2"));
            Stream? loadStream = null;
            if (imageFile.Exists)
            {
                loadStream = File.OpenRead(imageFile.FullName);
                Log.Information("Opened extended image from cache: {Path}", imageFile.FullName);
            }
            else
            {
                var url = Environment.GetEnvironmentVariable("EXTENDED_IMAGE_LINK");
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                await using var streamToReadFrom = await response.Content.ReadAsStreamAsync();

                await using Stream streamToWriteTo = File.Open(imageFile.FullName, FileMode.Create);

                await streamToReadFrom.CopyToAsync(streamToWriteTo);
                streamToWriteTo.Close();

                loadStream = File.OpenRead(imageFile.FullName);
                Log.Information("Downloaded extended image to cache: {Path}", imageFile.FullName);
            }

            await this.dockerClient.Images.LoadImageAsync(new ImageLoadParameters(), loadStream,
                new Progress<JSONMessage>(progress =>
                {
                    Log.Verbose("Docker image load ({Id}): {Status}", progress.ID, progress.Status);
                }));
        }

        await this.dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
        {
            FromImage = DOCKER_IMAGE,
            Tag = DOCKER_TAG,
        }, null,
            new Progress<JSONMessage>(progress =>
            {
                Log.Verbose("Docker image pull ({Id}): {Status}", progress.ID, progress.Status);
            }));

        var images = await this.dockerClient.Images.ListImagesAsync(new ImagesListParameters
        {
            All = true,
        });

        List<ImageInspectResponse> inspects = new();
        foreach (var imagesListResponse in images)
        {
            var inspect = await this.dockerClient.Images.InspectImageAsync(imagesListResponse.ID);
            inspects.Add(inspect);
        }

        return inspects;
    }

    /// <summary>
    /// Get all tasks that need to be done
    /// </summary>
    /// <param name="continuous">If we are running a continuous verification build.</param>
    /// <returns>A set of tasks that are pending</returns>
    public ISet<BuildTask> GetBuildTasks(bool continuous)
    {
        var tasks = new HashSet<BuildTask>();

        foreach (var channel in this.pluginRepository.State.Channels)
        {
            foreach (var plugin in channel.Value.Plugins)
            {
                // Channel decommissioned or no longer in manifests
                if (!this.manifestStorage.Channels.ContainsKey(channel.Key) ||
                    this.manifestStorage.Channels[channel.Key].All(x => x.Key != plugin.Key))
                {
                    tasks.Add(new BuildTask
                    {
                        InternalName = plugin.Key,
                        Manifest = null,
                        Channel = channel.Key,
                        HaveCommit = plugin.Value.BuiltCommit,
                        HaveTimeBuilt = null,
                        HaveVersion = null,
                        Type = BuildTask.TaskType.Remove,
                    });
                }
            }
        }

        foreach (var channel in this.manifestStorage.Channels)
        {
            foreach (var manifest in channel.Value)
            {
                var state = this.pluginRepository.GetPluginState(channel.Key, manifest.Key);
                var isInAnyChannel = this.pluginRepository.IsPluginInAnyChannel(manifest.Key);

                if (state != null && state.BuiltCommit == manifest.Value.Plugin.Commit && !continuous)
                    continue;

                if (manifest.Value.Build?.Image != null && !allowNonDefaultImages)
                    continue;

                tasks.Add(new BuildTask
                {
                    InternalName = manifest.Key,
                    Manifest = manifest.Value,
                    Channel = channel.Key,
                    HaveCommit = state?.BuiltCommit,
                    HaveTimeBuilt = state?.TimeBuilt,
                    HaveVersion = state?.EffectiveVersion,
                    IsNewPlugin = state == null && !isInAnyChannel,
                    IsNewInThisChannel = state == null && isInAnyChannel,
                    Type = BuildTask.TaskType.Build,
                });
            }
        }

        needExtendedImage = tasks.Any(x => x.Manifest?.Build?.Image == "extended");

        return tasks;
    }

    async Task<BuildResult.ReviewedNeed> GetDependency(string name, NugetLockfile.Dependency dependency, DirectoryInfo pkgFolder, HttpClient client)
    {
        var pkgName = name.ToLower();
        var fileName = $"{pkgName}.{dependency.Resolved}.nupkg";
        var depPath = Path.Combine(pkgFolder.FullName, fileName);

        var need = GetNeedStatus(name, dependency.Resolved, State.Need.NeedType.NuGet);
        
        if (File.Exists(depPath))
            return need;

        Log.Information("   => Getting {DepName}(v{Version})", name, dependency.Resolved);
        var url =
            $"https://api.nuget.org/v3-flatcontainer/{pkgName}/{dependency.Resolved}/{fileName}";

        var data = await client.GetByteArrayAsync(url);

        // TODO: verify content hash

        await File.WriteAllBytesAsync(depPath, data);
        return need;
    }

    private async Task RestorePackages(DirectoryInfo pkgFolder, NugetLockfile lockFileData, HttpClient client, HashSet<BuildResult.ReviewedNeed> reviewedNeeds, bool includeInReview)
    {
        foreach (var runtime in lockFileData.Runtimes)
        {
            Log.Information("Getting packages for runtime {Runtime}", runtime.Key);

            var resultNeeds = await Task.WhenAll(runtime.Value
                .Where(x => x.Value.Type != NugetLockfile.Dependency.DependencyType.Project)
                .Select(dependency => GetDependency(dependency.Key, dependency.Value, pkgFolder, client)).ToList());

            if (includeInReview)
            {
                foreach (var reviewedNeed in resultNeeds)
                    reviewedNeeds.Add(reviewedNeed);
            }
        }
    }

    private async Task RestoreAllPackages(DirectoryInfo localWorkFolder, DirectoryInfo projectPath, DirectoryInfo pkgFolder, HashSet<BuildResult.ReviewedNeed> reviewedNeeds)
    {
        var lockFiles = localWorkFolder.GetFiles("packages.lock.json", SearchOption.AllDirectories);

        if (lockFiles.Length == 0)
            throw new Exception("No lockfiles present - please set \"RestorePackagesWithLockFile\" to true in your project file!");

        using var client = new HttpClient();

        HashSet<Tuple<string, string>> runtimeDependencies = new();
        foreach (var file in lockFiles)
        {
            var lockFileData = JsonConvert.DeserializeObject<NugetLockfile>(File.ReadAllText(file.FullName));
            if (lockFileData == null)
                throw new Exception($"Lockfile did not deserialize: {file.FullName}");

            if (lockFileData.Version != 1)
                throw new Exception($"Unknown lockfile version: {lockFileData.Version}");

            runtimeDependencies.UnionWith(GetRuntimeDependencies(lockFileData));

            await RestorePackages(pkgFolder, lockFileData, client, reviewedNeeds, file.Directory?.FullName == projectPath.FullName);
        }

        // fetch runtime packages
        try
        {
            await Task.WhenAll(
                runtimeDependencies.Select(
                    dependency => GetDependency(
                        dependency.Item1,
                        new() { Resolved = dependency.Item2 },
                        pkgFolder,
                        client)));
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to fetch runtime dependency");
        }

        foreach (var (name, versions) in this.FORCE_PACKAGES)
        {
            await Task.WhenAll(versions.Select(version => GetDependency(name, new() { Resolved = version }, pkgFolder, client)));
        }
    }

    private async Task GetNeeds(BuildTask task, DirectoryInfo needsDir, HashSet<BuildResult.ReviewedNeed> reviewedNeeds)
    {
        if (task.Manifest?.Build?.Needs == null || !task.Manifest.Build.Needs.Any())
            return;

        using var client = new HttpClient();
        
        foreach (var need in task.Manifest!.Build!.Needs)
        {
            using var response = await client.GetAsync(need.Url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var streamToReadFrom = await response.Content.ReadAsStreamAsync();

            if (need.Dest!.Contains(".."))
                throw new Exception();

            string hash;
            
            var fileToWriteTo = Path.Combine(needsDir.FullName, need.Dest!);
            {
                await using Stream streamToWriteTo = File.Open(fileToWriteTo, FileMode.Create);

                await streamToReadFrom.CopyToAsync(streamToWriteTo);
            
                streamToWriteTo.Seek(0, SeekOrigin.Begin);
                var sha512 = SHA512.Create();
                hash = BitConverter.ToString(sha512.ComputeHash(streamToWriteTo)).Replace("-", "").ToLower();
                
                streamToWriteTo.Close();
            }
            
            reviewedNeeds.Add(GetNeedStatus(need.Url, hash, State.Need.NeedType.File));

            Log.Information("Downloaded need {Url} to {Dest}", need.Url, need.Dest);
        }
        
    }

    private class HasteResponse
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }
    };
    
    /// <summary>
    /// A set of diffs.
    /// </summary>
    /// <param name="HosterUrl">Url on git hosting platform.</param>
    /// <param name="RegularDiffLink">Regular diff info.</param>
    /// <param name="SemanticDiffLink">Semantic diff info.</param>
    /// <param name="LinesAdded">Number of lines added.</param>
    /// <param name="LinesRemoved">Number of lines removed.</param>
    public record PluginDiffSet(string? HosterUrl, string? RegularDiffLink, string? SemanticDiffLink, int LinesAdded, int LinesRemoved);

    private async Task<PluginDiffSet> GetPluginDiff(DirectoryInfo workDir, BuildTask task, IEnumerable<BuildTask> tasks, bool doSemantic)
    {
        var internalName = task.InternalName;
        var haveCommit = task.HaveCommit;
        var wantCommit = task.Manifest!.Plugin.Commit;
        var host = new Uri(task.Manifest!.Plugin.Repository);
        const string emptyTree = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";

        if (string.IsNullOrEmpty(haveCommit))
        {
            haveCommit = emptyTree; // "empty tree"

            var removeTask = tasks.FirstOrDefault(x =>
                x.InternalName == internalName && x.Type == BuildTask.TaskType.Remove);
            if (removeTask != null)
            {
                haveCommit = removeTask.HaveCommit!;
                Log.Information("Overriding diff haveCommit with {Commit} from {Channel}", haveCommit, removeTask.Channel);
            }
        }

        using var client = new HttpClient();

        var url = host.AbsoluteUri.Replace(".git", string.Empty);

        string? hosterUrl = null;
        switch (host.Host)
        {
            case "github.com":
                // GitHub does not support diffing from 0
                if (haveCommit != emptyTree)
                    hosterUrl = $"{url}/compare/{haveCommit}..{wantCommit}";
                break;
            case "gitlab.com":
                hosterUrl = $"{url}/-/compare/{haveCommit}...{wantCommit}";
                break;
        }

        // Check if relevant commit is still in the repo
        if (!await CheckCommitExists(workDir, haveCommit))
            haveCommit = emptyTree;

        async Task<string?> MakeAndUploadDiff()
        {
            var diffPsi = new ProcessStartInfo("git",
            $"diff --submodule=diff {haveCommit}..{wantCommit}")
            {
                RedirectStandardOutput = true,
                WorkingDirectory = workDir.FullName,
            };

            var process = Process.Start(diffPsi);
            if (process == null)
                throw new Exception("Diff process was null.");

            var diffOutput = await process.StandardOutput.ReadToEndAsync();
            Log.Verbose("{Args}: {Length}", diffPsi.Arguments, diffOutput.Length);

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new Exception($"Git could not diff: {process.ExitCode} -- {diffPsi.Arguments}");

            if (haveCommit == emptyTree)
                return null;

            var res = await client.PostAsync("https://haste.soulja-boy-told.me/documents", new StringContent(diffOutput));
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadFromJsonAsync<HasteResponse>();
            return $"https://haste.soulja-boy-told.me/{json!.Key}.diff";
        }
        
        async Task<string?> MakeAndUploadSemantic()
        {
            var diffPsi = new ProcessStartInfo("/bin/bash",
                                               $"-c \"set -o pipefail && git diff --submodule=diff {haveCommit}..{wantCommit} | terminal-to-html -preview\"")
            {
                RedirectStandardOutput = true,
                WorkingDirectory = workDir.FullName,
            };
            
            diffPsi.Environment["GIT_EXTERNAL_DIFF"] = "difft";
            diffPsi.Environment["DFT_COLOR"] = "always";
            diffPsi.Environment["DFT_WIDTH"] = "240";

            var process = Process.Start(diffPsi);
            if (process == null)
                throw new Exception("Diff process was null.");

            var diffOutput = await process.StandardOutput.ReadToEndAsync();
            Log.Verbose("{Args}: {Length}", diffPsi.Arguments, diffOutput.Length);

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new Exception($"Git could not diff: {process.ExitCode} -- {diffPsi.Arguments}");

            if (haveCommit == emptyTree)
                return null;

            var res = await client.PostAsync("https://haste.soulja-boy-told.me/documents", new StringContent(diffOutput));
            res.EnsureSuccessStatusCode();

            var json = await res.Content.ReadFromJsonAsync<HasteResponse>();
            return $"https://haste.soulja-boy-told.me/raw/{json!.Key}.html";
        }

        var linesAdded = 0;
        var linesRemoved = 0;
        
        // shortstat
        {
            var diffPsi = new ProcessStartInfo("git",
                                           $"diff --shortstat --submodule=diff {haveCommit}..{wantCommit}")
            {
                RedirectStandardOutput = true,
                WorkingDirectory = workDir.FullName,
            };

            var process = Process.Start(diffPsi);
            if (process == null)
                throw new Exception("Diff process was null.");

            var shortstatOutput = await process.StandardOutput.ReadToEndAsync();

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new Exception($"Git could not diff: {process.ExitCode} -- {diffPsi.Arguments}");
            

            var regex = new Regex(@"^\s*(?:(?<numFilesChanged>[0-9]+) files? changed)?(?:, )?(?:(?<numInsertions>[0-9]+) insertions?\(\+\))?(?:, )?(?:(?<numDeletions>[0-9]+) deletions?\(-\))?\s*$");
            var match = regex.Match(shortstatOutput);

            if (match.Success)
            {
                if (!match.Groups.TryGetValue("numInsertions", out var groupInsertions) || !int.TryParse(groupInsertions?.Value, out linesAdded))
                {
                    Log.Error("Could not parse insertions");
                }

                if (!match.Groups.TryGetValue("numDeletions", out var groupDeletions) || !int.TryParse(groupDeletions?.Value, out linesRemoved))
                {
                    Log.Error("Could not parse deletions");
                }
            }
            
            Log.Verbose("{Args}: {Output} - {Length}, +{LinesAdded} -{LinesRemoved}", diffPsi.Arguments, shortstatOutput, shortstatOutput.Length, linesAdded, linesRemoved);
        }
        
        var diffNormal = await MakeAndUploadDiff();

        string? diffSemantic = null;

        if (doSemantic)
        {
            try
            {
                diffSemantic = await MakeAndUploadSemantic();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Semantic diff failed");
            }
        }
        
        return new PluginDiffSet(hosterUrl, diffNormal, diffSemantic, linesAdded, linesRemoved);
    }

    private async Task<bool> CheckCommitExists(DirectoryInfo workDir, string commit)
    {
        var psi = new ProcessStartInfo("git",
            $"cat-file -e {commit}^{{commit}}")
        {
            WorkingDirectory = workDir.FullName,
        };

        var process = Process.Start(psi);
        if (process == null)
            throw new Exception("Cat-file process was null.");

        await process.WaitForExitAsync();
        Log.Verbose("CheckIfCommitExists: {Arguments}: {ExitCode}", psi.Arguments, process.ExitCode);

        return process.ExitCode == 0;
    }

    private async Task<bool> CheckIfTrueCommit(DirectoryInfo workDir, string commit)
    {
        var psi = new ProcessStartInfo("git",
            $"rev-parse --symbolic-full-name {commit}")
        {
            RedirectStandardOutput = true,
            WorkingDirectory = workDir.FullName,
        };

        var process = Process.Start(psi);
        if (process == null)
            throw new Exception("rev-parse process was null.");

        await process.WaitForExitAsync();
        var output = await process.StandardOutput.ReadToEndAsync();

        return string.IsNullOrEmpty(output);
    }

    HashSet<Tuple<string, string>> GetRuntimeDependencies(NugetLockfile lockFileData)
    {
        HashSet<Tuple<string, string>> dependencies = new();

        foreach (var runtime in lockFileData.Runtimes)
        {
            // e.g. net7.0, net7.0/linux-x64, net7.0-windows7.0
            var key = runtime.Key.Split('/', '-')[0];
            // check if framework identifier also specifies a runtime identifier
            var runtimeId = runtime.Key.Split('/').ElementAtOrDefault(1);

            // add runtime packages to dependency list
            if (!RUNTIME_PACKAGES.TryGetValue(key, out string[]? versions))
            {
                throw new ArgumentOutOfRangeException($"Unknown runtime requested: {runtime}");
            }

            foreach (var version in versions)
            {
                if (runtimeId is null)
                {
                    // only generic reference packages are required
                    dependencies.Add(new("Microsoft.NETCore.App.Ref", version));
                    dependencies.Add(new("Microsoft.AspNetCore.App.Ref", version));
                    dependencies.Add(new("Microsoft.WindowsDesktop.App.Ref", version));
                }
                else
                {
                    // specific runtime packages are required
                    dependencies.Add(new($"Microsoft.NETCore.App.Runtime.{runtimeId}", version));
                    dependencies.Add(new($"Microsoft.AspNetCore.App.Runtime.{runtimeId}", version));
                    dependencies.Add(new($"Microsoft.NETCore.App.Host.{runtimeId}", version));
                }
            }
        }

        return dependencies;
    }

    /// <summary>
    /// Info about build status
    /// </summary>
    public class BuildResult
    {
        /// <summary>
        /// 
        /// </summary>
        /// <param name="success">If it worked</param>
        /// <param name="diff">diff url</param>
        /// <param name="version">plugin version</param>
        /// <param name="task">processed task</param>
        /// <param name="needs">List of needs</param>
        public BuildResult(bool success, PluginDiffSet? diff, string? version, BuildTask task, IEnumerable<ReviewedNeed> needs)
        {
            this.Success = success;
            this.Diff = diff;
            this.Version = version;
            this.PreviousVersion = task.HaveVersion;
            this.Task = task;
            this.Needs = needs;
        }

        /// <summary>
        /// If it worked
        /// </summary>
        public bool Success { get; private set; }

        /// <summary>
        /// Where the diff is
        /// </summary>
        public PluginDiffSet? Diff { get; private set; }

        /// <summary>
        /// The version of the plugin artifact
        /// </summary>
        public string? Version { get; private set; }

        /// <summary>
        /// The previous version of this plugin in this channel
        /// </summary>
        public string? PreviousVersion { get; private set; }

        /// <summary>
        /// The task that was processed
        /// </summary>
        public BuildTask Task { get; private set; }
        
        /// <summary>
        /// A need of this plugin.
        /// </summary>
        /// <param name="Name">The name of this need.</param>
        /// <param name="ReviewedBy">Who reviewed this need. Null if nobody did.</param>
        /// <param name="Version">The version of the need.</param>
        /// <param name="OldVersion">The old version of the need, if it changed. Null if it didn't.</param>
        /// <param name="DiffUrl">Link to diff, if available.</param>
        /// <param name="Type">Type of the need</param>
        public record ReviewedNeed(string Name, string? ReviewedBy, string Version, string? OldVersion, string? DiffUrl, State.Need.NeedType Type);
        
        /// <summary>
        /// Needs of this plugin to be displayed to a reviewer.
        /// </summary>
        public IEnumerable<ReviewedNeed> Needs { get; set; }
    }

    private class NeedComparer : IEqualityComparer<BuildResult.ReviewedNeed>
    {
        public bool Equals(BuildResult.ReviewedNeed? x, BuildResult.ReviewedNeed? y)
        {
            if (x is null || y is null)
                return false;
            
            return x.Name == y.Name && x.Version == y.Version;
        }

        public int GetHashCode(BuildResult.ReviewedNeed obj)
        {
            return HashCode.Combine(obj.Name, obj.Version);
        }
    }
    
    private class LegacyPluginManifest
    {
        [JsonProperty]
        public string? AssemblyVersion { get; set; }

        [JsonProperty]
        public string? InternalName { get; set; }
        
        [JsonProperty]
        public int? DalamudApiLevel { get; set; }
    }

    private static async Task RetryUntil(Func<Task> what, int maxTries = 10)
    {
        while (true)
        {
            try
            {
                await what();
                return;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Task failed, tries left: {TriesLeft}", maxTries);

                maxTries--;
                if (maxTries <= 0)
                    throw;
            }
        }
    }

    private static void ParanoiaValidateTask(BuildTask task)
    {
        // Take care, this could still match a branch or tag name
        // Verified by CheckIfTrueCommit() later
        var gitShaRegex = new Regex("^[0-9a-f]{5,40}$");
        if (!gitShaRegex.IsMatch(task.Manifest!.Plugin.Commit))
            throw new Exception("Provided commit hash is not a valid Git SHA.");
    }

    private async Task<Dictionary<string, string>> DecryptSecrets(BuildTask task)
    {
        if (this.secretsPrivateKeyBytes is null
            || this.secretsPrivateKeyPassword is null
            || task.Manifest!.Plugin.Secrets.Count == 0)
            return new Dictionary<string, string>();

        // Load keys
        EncryptionKeys encryptionKeys;
        await using (Stream privateKeyStream = new MemoryStream(secretsPrivateKeyBytes))
            encryptionKeys = new EncryptionKeys(privateKeyStream, secretsPrivateKeyPassword);

        var pgp = new PGP(encryptionKeys);

        var decrypted = new Dictionary<string, string>();
        foreach (var secret in task.Manifest.Plugin.Secrets)
        {
            decrypted.Add(secret.Key, await pgp.DecryptArmoredStringAsync(secret.Value));
        }

        return decrypted;
    }

    private static void WriteNugetConfig(FileInfo output)
    {
        var nugetConfigText =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key=\"plogon\" value=\"/packages\" />\n  </packageSources>\n</configuration>";
        File.WriteAllText(output.FullName, nugetConfigText);
    }

    /// <summary>
    /// Check out and build a plugin from a task
    /// </summary>
    /// <param name="task">The task to build</param>
    /// <param name="commit">Whether the plugin should be committed to the repo</param>
    /// <param name="changelog">The plugin changelog</param>
    /// <param name="reviewer">Reviewer of this task</param>
    /// <param name="otherTasks">All other queued tasks</param>
    /// <returns>The result of the build</returns>
    /// <exception cref="Exception">Generic build system errors</exception>
    /// <exception cref="PluginCommitException">Error during repo commit, all no further work should be done</exception>
    public async Task<BuildResult> ProcessTask(BuildTask task, bool commit, string? changelog, string? reviewer, ISet<BuildTask> otherTasks)
    {
        if (commit && string.IsNullOrWhiteSpace(reviewer))
            throw new Exception("Reviewer must be set when committing");
        
        if (task.Type == BuildTask.TaskType.Remove)
        {
            if (!commit)
                throw new Exception("Can't remove plugins if not committing");

            this.pluginRepository.RemovePlugin(task.Channel, task.InternalName);

            var repoOutputDir = this.pluginRepository.GetPluginOutputDirectory(task.Channel, task.InternalName);
            repoOutputDir.Delete(true);

            return new BuildResult(true, null, null, task, []);
        }

        if (task.Manifest == null)
            throw new Exception("Manifest was null");

        if (string.IsNullOrWhiteSpace(task.Manifest.Plugin.Commit))
            throw new Exception("No commit specified");

        ParanoiaValidateTask(task);

        var taskFolderName = $"{task.InternalName}-{task.Manifest.Plugin.Commit}-{task.Channel}";
        var taskRootDir = this.workFolder.CreateSubdirectory(taskFolderName);
        Log.Verbose("taskRoot: {TaskRoot}", taskRootDir.FullName);
        var workDir = taskRootDir.CreateSubdirectory("work");
        var archiveDir = taskRootDir.CreateSubdirectory("archive");
        var outputDir = taskRootDir.CreateSubdirectory("output");
        var packagesDir = taskRootDir.CreateSubdirectory("packages");
        var externalNeedsDir = taskRootDir.CreateSubdirectory("needs");

        if (!staticFolder.Exists)
            throw new Exception("Static folder does not exist");

        if (string.IsNullOrWhiteSpace(task.Manifest.Plugin.Repository))
            throw new Exception("No repository specified");

        if (!task.Manifest.Plugin.Repository.StartsWith("https://") ||
            !task.Manifest.Plugin.Repository.EndsWith(".git"))
            throw new Exception("Only HTTPS repository URLs ending in .git are supported");

        task.Manifest.Plugin.ProjectPath ??= string.Empty;

        if (task.Manifest.Plugin.ProjectPath.Contains(".."))
            throw new Exception("Not allowed");
        
        // Always clone fresh
        if (workDir.Exists)
        {
            workDir.Delete(true);
            workDir.Create();
        }

        Repository.Clone(task.Manifest.Plugin.Repository, workDir.FullName, new CloneOptions
        {
            Checkout = false,
            RecurseSubmodules = false,
        });

        var repo = new Repository(workDir.FullName);
        Commands.Fetch(repo, "origin", new [] { task.Manifest.Plugin.Commit }, new FetchOptions
        {
        }, null);
        repo.Reset(ResetMode.Hard, task.Manifest.Plugin.Commit);
        
        HashSet<BuildResult.ReviewedNeed> allNeeds = new(new NeedComparer());
        FetchSubmodules(repo, allNeeds);

        if (!await CheckIfTrueCommit(workDir, task.Manifest.Plugin.Commit))
            throw new Exception("Commit in manifest is not a true commit, please don't specify tags");

        // Archive source code before build
        CopySourceForArchive(workDir, archiveDir);

        // Create archive zip
        var archiveZipFile =
            new FileInfo(Path.Combine(this.workFolder.FullName, $"{taskFolderName}-{archiveDir.Name}.zip"));
        ZipFile.CreateFromDirectory(archiveDir.FullName, archiveZipFile.FullName);

        var diff = await GetPluginDiff(workDir, task, otherTasks, !commit);

        var dalamudAssemblyDir = await this.dalamudReleases.GetDalamudAssemblyDirAsync(task.Channel);

        WriteNugetConfig(new FileInfo(Path.Combine(workDir.FullName, "nuget.config")));

        var projectPath = new DirectoryInfo(Path.Combine(workDir.FullName, task.Manifest.Plugin.ProjectPath));

        await RetryUntil(async () => await GetNeeds(task, externalNeedsDir, allNeeds));
        await RetryUntil(async () => await RestoreAllPackages(workDir, projectPath, packagesDir, allNeeds));

        var needsExtendedImage = task.Manifest?.Build?.Image == "extended";

        var dockerEnv = new List<string>
        {
            $"PLOGON_PROJECT_DIR={task.Manifest!.Plugin.ProjectPath}",
            $"PLOGON_PLUGIN_NAME={task.InternalName}",
            $"PLOGON_PLUGIN_COMMIT={task.Manifest.Plugin.Commit}",
            $"PLOGON_PLUGIN_VERSION={task.Manifest.Plugin.Version}",
            "DALAMUD_LIB_PATH=/work/dalamud/"
        };

        // Decrypt secrets and add them as env vars to the container, so that msbuild can see them
        var secrets = await DecryptSecrets(task);
        foreach (var secret in secrets)
        {
            var bannedCharacters = new[] { '=', ';', '"', '\'' };
            if (secret.Key.Any(x => bannedCharacters.Contains(x)) ||
                secret.Value.Any(x => bannedCharacters.Contains(x)))
            {
                throw new Exception("Disallowed characters in secret name or value.");
            }

            var secretName = $"PLOGON_SECRET_{secret.Key}";
            dockerEnv.Add($"{secretName}={secret.Value}");

            Log.Verbose("Added secret {Name}", secretName);
        }

        var containerCreateResponse = await this.dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = needsExtendedImage ? EXTENDED_IMAGE_HASH : $"{DOCKER_IMAGE}:{DOCKER_TAG}",

                NetworkDisabled = true,

                AttachStderr = true,
                AttachStdout = true,
                HostConfig = new HostConfig
                {
                    Privileged = false,
                    IpcMode = "none",
                    AutoRemove = false,
                    Binds = new List<string>
                    {
                        $"{workDir.FullName}:/work/repo",
                        $"{dalamudAssemblyDir.FullName}:/work/dalamud:ro",
                        $"{staticFolder.FullName}:/static:ro",
                        $"{outputDir.FullName}:/output",
                        $"{packagesDir.FullName}:/packages:ro",
                        $"{externalNeedsDir.FullName}:/needs:ro"
                    }
                },
                Env = dockerEnv,
                Entrypoint = new List<string>
                {
                    "/static/entrypoint.sh"
                }
            });

        var startResponse =
            await this.dockerClient.Containers.StartContainerAsync(containerCreateResponse.ID,
                new ContainerStartParameters());

        if (!startResponse)
        {
            throw new Exception("Couldn't start container");
        }

        var logResponse = await this.dockerClient.Containers.GetContainerLogsAsync(containerCreateResponse.ID, false,
            new ContainerLogsParameters
            {
                Follow = true,
                ShowStderr = true,
                ShowStdout = true,
            });

        var hasExited = false;
        while (!hasExited)
        {
            var inspectResponse = await this.dockerClient.Containers.InspectContainerAsync(containerCreateResponse.ID);
            hasExited = inspectResponse.State.Running == false;

            // Get logs from multiplexed stream
            var buffer = new byte[4096];
            var eof = false;
            while (!eof)
            {
                var result = await logResponse.ReadOutputAsync(buffer, 0, buffer.Length, CancellationToken.None);
                eof = result.EOF;

                var log = System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count);
                Log.Information(log.Replace("\n", string.Empty));
            }
        }

        var containerInspectResponse =
            await this.dockerClient.Containers.InspectContainerAsync(containerCreateResponse.ID);
        var exitCode = containerInspectResponse.State.ExitCode;

        Log.Information("Container for build exited, exit code: {Code}", exitCode);

        if (exitCode == 0 && !commit && File.Exists(Path.Combine(task.Manifest.Directory.FullName, "images", "icon.png")) == false)
        {
            throw new MissingIconException();
        }

        await this.dockerClient.Containers.RemoveContainerAsync(containerCreateResponse.ID,
            new ContainerRemoveParameters
            {
                Force = true,
            });

        var outputFiles = outputDir.GetFiles("*.dll", SearchOption.AllDirectories);
        foreach (var outputFile in outputFiles)
        {
            if (DalamudInternalDll.Any(x => x == outputFile.Name))
            {
                throw new Exception($"Build is emitting Dalamud-internal DLL({outputFile.Name}), this will cause issues.");
            }
        }

        var dpOutput = new DirectoryInfo(Path.Combine(outputDir.FullName, task.InternalName));
        string? version = null;

        if (dpOutput.Exists)
        {
            var artifact = this.artifactFolder.CreateSubdirectory($"{task.InternalName}-{task.Manifest.Plugin.Commit}");
            try
            {
                foreach (var file in dpOutput.GetFiles())
                {
                    file.CopyTo(Path.Combine(artifact.FullName, file.Name), true);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not copy to artifact output");
                throw new Exception("Could not copy to artifact", ex);
            }

            try
            {
                var manifestFile = new FileInfo(Path.Combine(dpOutput.FullName, $"{task.InternalName}.json"));
                if (!manifestFile.Exists)
                    throw new Exception("Generated manifest didn't exist");

                var manifestText = await manifestFile.OpenText().ReadToEndAsync();
                var manifest = JsonConvert.DeserializeObject<LegacyPluginManifest>(manifestText);

                if (manifest == null)
                    throw new Exception("Generated manifest was null");

                if (manifest.InternalName != task.InternalName)
                    throw new Exception("Internal name in generated manifest JSON differs from DIP17 folder name.");

                version = manifest.AssemblyVersion ?? throw new Exception("AssemblyVersion in generated manifest was null");
                
                // TODO: Get this from an API or something
                if (manifest.DalamudApiLevel != PlogonSystemDefine.API_LEVEL)
                    throw new ApiLevelException(manifest.DalamudApiLevel ?? -1, PlogonSystemDefine.API_LEVEL);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Couldn't read generated manifest");
                if (exitCode == 0)
                    throw;
            }

            if (exitCode == 0 && commit)
            {
                try
                {
                    this.pluginRepository.UpdatePluginHave(
                        task.Channel,
                        task.InternalName,
                        task.Manifest.Plugin.Commit,
                        version ?? throw new Exception("Committing, but version is null"),
                        task.Manifest.Plugin.MinimumVersion,
                        changelog,
                        reviewer!,
                        allNeeds.Select(x => (x.Name, x.Version)));
                    
                    this.CommitReviewedNeeds(allNeeds, reviewer!);

                    var repoOutputDir = this.pluginRepository.GetPluginOutputDirectory(task.Channel, task.InternalName);

                    foreach (var file in dpOutput.GetFiles())
                    {
                        file.CopyTo(Path.Combine(repoOutputDir.FullName, file.Name), true);
                    }

                    if (this.s3Client != null)
                    {
                        var key =
                            $"sources/{task.InternalName}/{task.Manifest.Plugin.Commit}.zip";
                        
                        // Check if exist
                        bool mustUpload;
                        try
                        {
                            await this.s3Client.GetObjectMetadataAsync(S3_BUCKET_NAME, key);
                            mustUpload = false;
                        }
                        catch (AmazonS3Exception exception)
                        {
                            if (exception.StatusCode == HttpStatusCode.NotFound)
                            {
                                mustUpload = true;
                            }
                            else
                            {
                                throw;
                            }
                        }

                        if (mustUpload)
                        {
                            var result = await this.s3Client.PutObjectAsync(new PutObjectRequest
                            {
                                BucketName = S3_BUCKET_NAME,
                                Key = key,
                                FilePath = archiveZipFile.FullName,
                                TagSet =
                                {
                                    new Tag
                                    {
                                        Key = "dev.dalamud.plugin/Version",
                                        Value = version
                                    },
                                    new Tag
                                    {
                                        Key = "dev.dalamud.plugin/CommitHash",
                                        Value = task.Manifest.Plugin.Commit
                                    },
                                    new Tag
                                    {
                                        Key = "dev.dalamud.plugin/DistributionChannel",
                                        Value = task.Channel
                                    },
                                    new Tag
                                    {
                                        Key = "dev.dalamud.plugin/InternalName",
                                        Value = task.InternalName
                                    }
                                }
                            });
                        
                            if (result.HttpStatusCode != HttpStatusCode.OK)
                                throw new Exception($"Failed to upload archive to S3(code: {result.HttpStatusCode})");
                        
                            Log.Information("Uploaded archive to S3: {Key} - {ETag}", key, result.ETag);
                        }
                        else
                        {
                            Log.Warning("Archive already exists on S3, not uploading (key: {Key})", key);
                        }
                    }
                    else
                    {
                        Log.Warning("No S3 client, not uploading archive");
                    }
                    
                    if (task.Manifest.Directory == null)
                        throw new Exception("Manifest had no directory set");

                    var imagesSourcePath = Path.Combine(task.Manifest.Directory.FullName, "images");
                    if (Directory.Exists(imagesSourcePath))
                    {
                        var imagesDestinationPath = Path.Combine(repoOutputDir.FullName, "images");
                        if (Directory.Exists(imagesDestinationPath))
                            Directory.Delete(imagesDestinationPath, true);
                        Directory.Move(imagesSourcePath, imagesDestinationPath);
                    }

                    var manifestFile = new FileInfo(Path.Combine(repoOutputDir.FullName, $"{task.InternalName}.json"));
                    var manifestText = await File.ReadAllTextAsync(manifestFile.FullName);

                    var manifestObj = JObject.Parse(manifestText);
                    manifestObj["_isDip17Plugin"] = true;
                    manifestObj["_Dip17Channel"] = task.Channel;

                    await File.WriteAllTextAsync(manifestFile.FullName, manifestObj.ToString());
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error during plugin commit");
                    throw new PluginCommitException(ex);
                }
            }
        }
        else if (exitCode == 0)
        {
            throw new Exception("DalamudPackager output not found, make sure it is installed");
        }

        try
        {
            // Cleanup work folder to save storage space on actions
            workDir.Delete(true);
            archiveZipFile.Delete();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not cleanup workspace");
        }
        
        return new BuildResult(exitCode == 0, diff, version, task, allNeeds);
    }

    private BuildResult.ReviewedNeed GetNeedStatus(string key, string version, State.Need.NeedType type)
    {
        var existingReview = this.pluginRepository.State.ReviewedNeeds
                                 .FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.Ordinal) &&
                                                      string.Equals(x.Version, version, StringComparison.Ordinal));

        if (existingReview == null)
        {
            var lastReview = this.pluginRepository.State.ReviewedNeeds
                                 .Where(x => string.Equals(x.Key, key, StringComparison.Ordinal))
                                 .OrderByDescending(x => x.ReviewedAt)
                                 .FirstOrDefault();

            string? diffUrl = null;
            if (type == State.Need.NeedType.Submodule && lastReview != null)
            {
                if (Uri.TryCreate(key, UriKind.Absolute, out var uri) && uri.Host == "github.com")
                {
                    var parts = uri.AbsolutePath.Split('/');
                    if (parts.Length >= 3)
                    {
                        diffUrl =
                            $"https://{uri.Host}/{parts[1]}/{parts[2]}/compare/{lastReview.Version}...{version}";
                    }
                }
            }
            
            return new(key, null, version, lastReview?.Version, diffUrl, type);
        }
        
        return new(key, existingReview.ReviewedBy, version, existingReview.Version, null, type);
    }
    
    private void CommitReviewedNeeds(IEnumerable<BuildResult.ReviewedNeed> needs, string reviewer)
    {
        var newNeeds = needs
                       .Where(need => need.ReviewedBy == null)
                       .Select(
                           need => new State.Need
                           {
                               Key = need.Name,
                               ReviewedBy = reviewer,
                               Version = need.Version,
                               ReviewedAt = DateTime.UtcNow,
                               Type = need.Type,
                           }).ToList();
        
        Log.Information("Adding {Count} newly reviewed needs to repo state", newNeeds.Count);
        this.pluginRepository.AddReviewedNeeds(newNeeds);
    }
    
    private static void CopySourceForArchive(DirectoryInfo from, DirectoryInfo to, int depth = 0)
    {
        if (!to.Exists)
            to.Create();

        foreach (var file in from.GetFiles())
        {
            file.CopyTo(Path.Combine(to.FullName, file.Name), true);
        }

        foreach (var dir in from.GetDirectories())
        {
            // Skip root-level .git
            if (depth == 0 && dir.Name == ".git")
                continue;
            
            CopySourceForArchive(dir, to.CreateSubdirectory(dir.Name), depth + 1);
        }
    }
    
    private void FetchSubmodules(Repository repo, HashSet<BuildResult.ReviewedNeed> reviewedNeeds)
    {
        foreach (var submodule in repo.Submodules)
        {
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions
            {
                Init = true,
            });
            
            reviewedNeeds.Add(GetNeedStatus(submodule.Url, submodule.WorkDirCommitId.Sha, State.Need.NeedType.Submodule));

            // In the case of recursive submodules
            var submoduleRepo = new Repository(Path.Combine(repo.Info.WorkingDirectory, submodule.Path));
            FetchSubmodules(submoduleRepo, reviewedNeeds);
        }
    }

    /// <summary>
    /// Exception when repo commit fails
    /// </summary>
    public class PluginCommitException : Exception
    {
        /// <summary>
        /// ctor
        /// </summary>
        /// <param name="inner">Actual error</param>
        public PluginCommitException(Exception inner)
            : base("Could not commit plugin.", inner)
        {
        }
    }

    /// <summary>
    /// Exception when icon is missing
    /// </summary>
    public class MissingIconException : Exception
    {
        /// <summary>
        /// ctor
        /// </summary>
        public MissingIconException()
            : base("Missing icon.")
        {
        }
    }

    /// <summary>
    /// Exception when wrong API level is used
    /// </summary>
    public class ApiLevelException : Exception
    {
        /// <summary>
        /// Have version
        /// </summary>
        public int Have { get; }

        /// <summary>
        /// Want version
        /// </summary>
        public int Want { get; }

        /// <summary>
        /// ctor
        /// </summary>
        public ApiLevelException(int have, int want)
            : base("Wrong API level.")
        {
            Have = have;
            Want = want;
        }
    }
}
