using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using LibGit2Sharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Plogon.Manifests;
using Plogon.Repo;
using Serilog;

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
    
    private readonly DockerClient dockerClient;

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
    private const string DOCKER_TAG = "6.0.300";
    // This has to match the SDK's version identified by DOCKER_TAG
    // See https://dotnet.microsoft.com/en-us/download/dotnet/6.0 for a mapping of SDK version <-> Runtime version
    private const string RUNTIME_VERSION = "6.0.5";

    private const string EXTENDED_IMAGE_HASH = "38f9afcc7475646604cba1fe5a63333f7443097f390604295c982a00740f35c6";
    
    /// <summary>
    /// Set up build processor
    /// </summary>
    /// <param name="repoFolder">Repo</param>
    /// <param name="manifestFolder">Manifests</param>
    /// <param name="workFolder">Work</param>
    /// <param name="staticFolder">Static</param>
    /// <param name="artifactFolder">Artifacts</param>
    /// <param name="prDiff">Diff in unified format that contains the changes requested by the PR</param>
    public BuildProcessor(DirectoryInfo repoFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder, DirectoryInfo artifactFolder, string? prDiff)
    {
        this.repoFolder = repoFolder;
        this.manifestFolder = manifestFolder;
        this.workFolder = workFolder;
        this.staticFolder = staticFolder;
        this.artifactFolder = artifactFolder;

        this.pluginRepository = new PluginRepository(repoFolder);
        this.manifestStorage = new ManifestStorage(manifestFolder, prDiff, true);
        this.dalamudReleases = new DalamudReleases(workFolder.CreateSubdirectory("dalamud_releases_work"), manifestFolder);

        this.dockerClient = new DockerClientConfiguration().CreateClient();
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
    /// <returns>A set of tasks that are pending</returns>
    public ISet<BuildTask> GetBuildTasks()
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

                if (state == null || state.BuiltCommit != manifest.Value.Plugin.Commit)
                {
                    tasks.Add(new BuildTask
                    {
                        InternalName = manifest.Key,
                        Manifest = manifest.Value,
                        Channel = channel.Key,
                        HaveCommit = state?.BuiltCommit,
                        HaveTimeBuilt = state?.TimeBuilt,
                        HaveVersion = state?.EffectiveVersion,
                        Type = BuildTask.TaskType.Build,
                    });
                }
            }
        }

        needExtendedImage = tasks.Any(x => x.Manifest?.Build?.Image == "extended");

        return tasks;
    }

    async Task GetDependency(string name, NugetLockfile.Dependency dependency, DirectoryInfo pkgFolder, HttpClient client)
    {
        var pkgName = name.ToLower();
        var fileName = $"{pkgName}.{dependency.Resolved}.nupkg";
        var depPath = Path.Combine(pkgFolder.FullName, fileName);
        
        if (File.Exists(depPath))
            return;
        
        Log.Information("   => Getting {DepName}(v{Version})", name, dependency.Resolved);
        var url =
            $"https://api.nuget.org/v3-flatcontainer/{pkgName}/{dependency.Resolved}/{fileName}";

        var data = await client.GetByteArrayAsync(url);
            
        // TODO: verify content hash
            
        await File.WriteAllBytesAsync(depPath, data);
    }

    private async Task RestorePackages(DirectoryInfo pkgFolder, NugetLockfile lockFileData, HttpClient client)
    {
        foreach (var runtime in lockFileData.Runtimes)
        {
            Log.Information("Getting packages for runtime {Runtime}", runtime.Key);
            
            await Task.WhenAll(runtime.Value
                .Where(x => x.Value.Type != NugetLockfile.Dependency.DependencyType.Project)
                .Select(dependency => GetDependency(dependency.Key, dependency.Value, pkgFolder,client)).ToList());
        }
    }

    private async Task RestoreAllPackages(BuildTask task, DirectoryInfo localWorkFolder, DirectoryInfo pkgFolder)
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
            
            await RestorePackages(pkgFolder, lockFileData, client);
        }
        
        // fetch runtime packages
        await Task.WhenAll(runtimeDependencies.Select(dependency => GetDependency(dependency.Item1, new() { Resolved = dependency.Item2 }, pkgFolder, client)));
    }

    async Task GetNeeds(BuildTask task, DirectoryInfo needs)
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
            
            var fileToWriteTo = Path.Combine(needs.FullName, need.Dest!);
            {
                await using Stream streamToWriteTo = File.Open(fileToWriteTo, FileMode.Create);
            
                await streamToReadFrom.CopyToAsync(streamToWriteTo);
                streamToWriteTo.Close();
            }
            
            Log.Information("Downloaded need {Url} to {Dest}", need.Url, need.Dest);
        }
    }

    private class HasteResponse
    {
        [JsonPropertyName("key")]
        public string? Key { get; set; }
    };
    
    private async Task<string> GetDiffUrl(DirectoryInfo workDir, BuildTask task, IEnumerable<BuildTask> tasks)
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

        switch (host.Host)
        {
            case "github.com":
                return $"{host.AbsoluteUri[..^4]}/compare/{haveCommit}..{wantCommit}";
            case "gitlab.com":
                return $"{host.AbsoluteUri[..^4]}/-/compare/{haveCommit}...{wantCommit}";
            default:
                // Check if relevant commit is still in the repo
                if (!await CheckCommitExists(workDir, haveCommit))
                    haveCommit = emptyTree;
                    
                var diffPsi = new ProcessStartInfo("git",
                    $"diff --submodule=diff {haveCommit}..{wantCommit}")
                {
                    RedirectStandardOutput = true,
                    WorkingDirectory = workDir.FullName,
                };

                var process = Process.Start(diffPsi);
                if (process == null)
                    throw new Exception("Diff process was null.");

                var output = await process.StandardOutput.ReadToEndAsync();

                await process.WaitForExitAsync();
                if (process.ExitCode != 0)
                    throw new Exception($"Git could not diff: {process.ExitCode} -- {diffPsi.Arguments}");
        
                Log.Verbose("{Args}: {Length}", diffPsi.Arguments, output.Length);

                var res = await client.PostAsync("https://haste.soulja-boy-told.me/documents", new StringContent(output));
                res.EnsureSuccessStatusCode();

                var json = await res.Content.ReadFromJsonAsync<HasteResponse>();

                return $"https://haste.soulja-boy-told.me/{json!.Key}.diff";
        }
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
            // check if framework identifier also specifies a runtime identifier
            var runtimeId = runtime.Key.Split('/').Skip(1).FirstOrDefault();
            
            var version = runtime.Key[..6] switch
            {
                "net5.0" => "5.0.0",
                "net6.0" when runtimeId is null => "6.0.0",
                "net6.0" => RUNTIME_VERSION, // if RUNTIME_VERSION doesn't match SDKs runtime version build will fail later on
                _ => throw new ArgumentOutOfRangeException($"Unknown runtime requested: {runtime}")
            };

            if (runtimeId is null)
            {
                // only generic reference packages are required
                dependencies.Add(new("Microsoft.NETCore.App.Ref", version));
                dependencies.Add(new ("Microsoft.AspNetCore.App.Ref", version));
            }
            else
            {
                // specific runtime packages are required
                dependencies.Add(new($"Microsoft.NETCore.App.Runtime.{runtimeId}", version));
                dependencies.Add(new ($"Microsoft.AspNetCore.App.Runtime.{runtimeId}", version));
                dependencies.Add(new ($"Microsoft.NETCore.App.Host.{runtimeId}", version));
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
        /// <param name="diffUrl">diff url</param>
        /// <param name="version">plugin version</param>
        /// <param name="task">processed task</param>
        public BuildResult(bool success, string? diffUrl, string? version, BuildTask task)
        {
            this.Success = success;
            this.DiffUrl = diffUrl;
            this.Version = version;
            this.Task = task;
        }
        
        /// <summary>
        /// If it worked
        /// </summary>
        public bool Success { get; private set; }
        
        /// <summary>
        /// Where the diff is
        /// </summary>
        public string? DiffUrl { get; private set; }

        /// <summary>
        /// The version of the plugin artifact
        /// </summary>
        public string? Version { get; private set; }
        
        /// <summary>
        /// The task that was processed
        /// </summary>
        public BuildTask Task { get; private set; }
    }

    private class LegacyPluginManifest
    {
        [JsonProperty]
        public string? AssemblyVersion { get; set; }
    }
    
    /// <summary>
    /// Check out and build a plugin from a task
    /// </summary>
    /// <param name="task">The task to build</param>
    /// <param name="commit">Whether or not the plugin should be committed to the repo</param>
    /// <param name="changelog">The plugin changelog</param>
    /// <param name="otherTasks">All other queued tasks</param>
    /// <returns>The result of the build</returns>
    /// <exception cref="Exception">Generic build system errors</exception>
    /// <exception cref="PluginCommitException">Error during repo commit, all no further work should be done</exception>
    public async Task<BuildResult> ProcessTask(BuildTask task, bool commit, string? changelog, ISet<BuildTask> otherTasks)
    {
        if (task.Type == BuildTask.TaskType.Remove)
        {
            if (!commit)
                throw new Exception("Can't remove plugins if not committing");
            
            this.pluginRepository.RemovePlugin(task.Channel, task.InternalName);
            
            var repoOutputDir = this.pluginRepository.GetPluginOutputDirectory(task.Channel, task.InternalName);
            repoOutputDir.Delete(true);

            return new BuildResult(true, null, null, task);
        }

        if (task.Manifest == null)
            throw new Exception("Manifest was null");
        
        var folderName = $"{task.InternalName}-{task.Manifest.Plugin.Commit}";
        var work = this.workFolder.CreateSubdirectory($"{folderName}-work");
        var output = this.workFolder.CreateSubdirectory($"{folderName}-output");
        var packages = this.workFolder.CreateSubdirectory($"{folderName}-packages");
        var needs = this.workFolder.CreateSubdirectory($"{folderName}-needs");

        Debug.Assert(staticFolder.Exists);

        if (string.IsNullOrWhiteSpace(task.Manifest.Plugin.Repository))
            throw new Exception("No repository specified");
        
        if (!task.Manifest.Plugin.Repository.StartsWith("https://") ||
            !task.Manifest.Plugin.Repository.EndsWith(".git"))
            throw new Exception("Only HTTPS repository URLs ending in .git are supported");

        if (string.IsNullOrWhiteSpace(task.Manifest.Plugin.Commit))
            throw new Exception("No commit specified");

        task.Manifest.Plugin.ProjectPath ??= string.Empty;
        
        if (task.Manifest.Plugin.ProjectPath.Contains(".."))
            throw new Exception("Not allowed");

        if (!work.Exists || work.GetFiles().Length == 0)
        {
            Repository.Clone(task.Manifest.Plugin.Repository, work.FullName, new CloneOptions
            {
                Checkout = false,
                RecurseSubmodules = false,
            });
        }

        var repo = new Repository(work.FullName);
        Commands.Fetch(repo, "origin", new string[] { task.Manifest.Plugin.Commit }, new FetchOptions
        {
        }, null);
        repo.Reset(ResetMode.Hard, task.Manifest.Plugin.Commit);

        foreach (var submodule in repo.Submodules)
        {
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions
            {
                Init = true,
            });
        }
        
        if (!await CheckIfTrueCommit(work, task.Manifest.Plugin.Commit))
            throw new Exception("Commit in manifest is not a true commit, please don't specify tags");

        var diffUrl = await GetDiffUrl(work, task, otherTasks);

        var dalamudAssemblyDir = await this.dalamudReleases.GetDalamudAssemblyDirAsync(task.Channel);

        await GetNeeds(task, needs);
        await RestoreAllPackages(task, work, packages);
        var needsExtendedImage = task.Manifest?.Build?.Image == "extended";
        
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
                        $"{work.FullName}:/work/repo",
                        $"{dalamudAssemblyDir.FullName}:/work/dalamud:ro",
                        $"{staticFolder.FullName}:/static:ro",
                        $"{output.FullName}:/output",
                        $"{packages.FullName}:/packages:ro",
                        $"{needs.FullName}:/needs:ro"
                    }
                },
                Env = new List<string>
                {
                    $"PLOGON_PROJECT_DIR={task.Manifest!.Plugin.ProjectPath}",
                    $"PLOGON_PLUGIN_NAME={task.InternalName}",
                    $"PLOGON_PLUGIN_COMMIT={task.Manifest.Plugin.Commit}",
                    $"PLOGON_PLUGIN_VERSION={task.Manifest.Plugin.Version}",
                    "DALAMUD_LIB_PATH=/work/dalamud/"
                },
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

        await this.dockerClient.Containers.RemoveContainerAsync(containerCreateResponse.ID,
            new ContainerRemoveParameters
            {
                Force = true,
            });

        var outputFiles = output.GetFiles("*.dll", SearchOption.AllDirectories);
        foreach (var outputFile in outputFiles)
        {
            if (DalamudInternalDll.Any(x => x == outputFile.Name))
            {
                throw new Exception($"Build is emitting Dalamud-internal DLL({outputFile.Name}), this will cause issues.");
            }
        }

        var dpOutput = new DirectoryInfo(Path.Combine(output.FullName, task.InternalName));
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

                version = manifest.AssemblyVersion ?? throw new Exception("AssemblyVersion in generated manifest was null");
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
                    this.pluginRepository.UpdatePluginHave(task.Channel, task.InternalName, task.Manifest.Plugin.Commit, version!, changelog);
                    var repoOutputDir = this.pluginRepository.GetPluginOutputDirectory(task.Channel, task.InternalName);

                    foreach (var file in dpOutput.GetFiles())
                    {
                        file.CopyTo(Path.Combine(repoOutputDir.FullName, file.Name), true);
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

                    // DELETE THIS!!
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
        
        return new BuildResult(exitCode == 0, diffUrl, version, task);
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
}