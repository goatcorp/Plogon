using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using LibGit2Sharp;
using Plogon.Manifests;
using Plogon.Repo;
using Serilog;

namespace Plogon;

public class BuildProcessor
{
    private readonly DirectoryInfo repoFolder;
    private readonly DirectoryInfo manifestFolder;
    private readonly DirectoryInfo workFolder;
    private readonly DirectoryInfo staticFolder;


    private readonly DockerClient dockerClient;

    private PluginRepository pluginRepository;
    private ManifestStorage manifestStorage;
    private DalamudReleases dalamudReleases;

    private const string DOCKER_IMAGE = "mcr.microsoft.com/dotnet/sdk";
    private const string DOCKER_TAG = "6.0.300";

    public BuildProcessor(DirectoryInfo repoFolder, DirectoryInfo manifestFolder, DirectoryInfo workFolder,
        DirectoryInfo staticFolder)
    {
        this.repoFolder = repoFolder;
        this.manifestFolder = manifestFolder;
        this.workFolder = workFolder;
        this.staticFolder = staticFolder;

        this.pluginRepository = new PluginRepository(repoFolder);
        this.manifestStorage = new ManifestStorage(manifestFolder);
        this.dalamudReleases = new DalamudReleases(workFolder.CreateSubdirectory("dalamud_releases_work"));

        this.dockerClient = new DockerClientConfiguration().CreateClient();
    }

    public async Task SetupDockerImage()
    {
        await this.dockerClient.Images.CreateImageAsync(new ImagesCreateParameters
            {
                //FromImage = "fedora/memcached",
                FromImage = DOCKER_IMAGE,
                //FromSrc = DOCKER_REPO,
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
    }

    public ISet<BuildTask> GetTasks()
    {
        var tasks = new HashSet<BuildTask>();

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
                    });
                }
            }
        }

        return tasks;
    }

    public async Task<bool> ProcessTask(BuildTask task)
    {
        var folderName = $"{task.InternalName}-{task.Manifest.Plugin.Commit}";
        var work = this.workFolder.CreateSubdirectory($"{folderName}-work");
        var output = this.workFolder.CreateSubdirectory($"{folderName}-output");

        if (work.GetFiles().Length != 0)
        {
            //work.Delete(true);
            //work.Create();
        }

        var repoPath = Repository.Clone(task.Manifest.Plugin.Repository, work.FullName, new CloneOptions
        {
            Checkout = false,
            RecurseSubmodules = false,
            OnProgress = output =>
            {
                Log.Verbose("Cloning: {GitOutput}", output);
                return true;
            }
        });

        var repo = new Repository(repoPath);
        Commands.Fetch(repo, "origin", new string[] { task.Manifest.Plugin.Commit }, new FetchOptions
        {
            OnProgress = output =>
            {
                Log.Verbose("Fetching: {GitOutput}", output);
                return true;
            }
        }, null);
        repo.Reset(ResetMode.Hard, task.Manifest.Plugin.Commit);

        foreach (var submodule in repo.Submodules)
        {
            repo.Submodules.Update(submodule.Name, new SubmoduleUpdateOptions
            {
                Init = true,
                OnProgress = output =>
                {
                    Log.Verbose("Updating submodule {ModuleName}: {GitOutput}", submodule.Name, output);
                    return true;
                }
            });
        }

        var dalamudAssemblyDir = await this.dalamudReleases.GetDalamudAssemblyDirAsync(task.Channel);

        var containerCreateResponse = await this.dockerClient.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = $"{DOCKER_IMAGE}:{DOCKER_TAG}",

                // TODO: This depends on a change in DalamudPackager to extract dependencies on dev machines
                // NetworkDisabled = true,

                AttachStderr = true,
                AttachStdout = true,
                HostConfig = new HostConfig
                {
                    Privileged = false,
                    IpcMode = "none",
                    AutoRemove = false,
                    Binds = new List<string>()
                    {
                        $"{work.FullName}:/work/repo",
                        $"{dalamudAssemblyDir.FullName}:/work/dalamud:ro",
                        $"{staticFolder.FullName}:/static:ro",
                        $"{output.FullName}:/output",
                    }
                },
                Env = new List<string>
                {
                    $"PLOGON_PROJECT_DIR={task.Manifest.Plugin.ProjectPath}",
                    $"PLOGON_PLUGIN_NAME={task.InternalName}",
                    $"PLOGON_PLUGIN_COMMIT={task.Manifest.Plugin.Commit}",
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

        return exitCode == 0;
    }
}