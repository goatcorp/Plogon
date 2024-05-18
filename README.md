# Plogon

A build tool for Dalamud plugins. It handles build and continuous integration tasks and is used for the [Dalamud plugin manifest](https://github.com/goatcorp/DalamudPluginsD17/) repository.

## Usage

Plogon can be run on both Windows and Linux systems, and needs .NET 6.0 and Docker installed to function.

**Note:** If the Docker connection fails, make sure you're not using Podman as the C# Docker API doesn't seem to work with it yet.

**Note:** If building fails with `/bin/bash: /static/entrypoint.sh: Permission denied` try disabling SELinux temporarily with `setenforce 0`.

Plogon also requires a specific folder structure to work. Here's a preview of the folder structure we will create:

```
Plogon /
   ...
manifests /
   PluginA /
       manifest.toml
   PluginB /
       manifest.toml
output /
work /
artifacts /
```

First, of course you need to clone Plogon itself:

```
git clone https://github.com/goatcorp/Plogon.git
```

Then we need a `manifests` folder which will house the plugin manifests. For each plugin, create a `manifest.toml` that describes the git repository to use among other information.

```
[plugin]
repository = "<git repository>"
commit = "<git hash>"
owners = [ "<your name>" ]
changelog = '''
Your changelog in here.
'''
```

You also need to place the plugin's icon into a subfolder named `icon.png`.

You can see plenty of examples in the [Dalamud plugin manifest](https://github.com/goatcorp/DalamudPluginsD17/) repository. Next you need to create some empty folders:

```
mkdir output
mkdir work
mkdir artifacts
```

The `output` directory will be the Dalamud repository that's outputted (you can subsitute this for a pre-existing repository once you run Plogon once.) The `work` and `artifacts` are for temporary files and the compiled plugins respectively.

Now it's time to run Plogon and start compiling our plugins:

```
cd Plogon/Plogon
dotnet run -- \
  --manifest-folder="../../manifests" \
  --output-folder="../../output" \
  --work-folder="../../work" \
  --static-folder="static" \
  --artifact-folder="../../artifacts" \
  --build-overrides-file="../../manifests/overrides.toml" \
  --mode=Development --build-all
```
(The build overrides file doesn't have to exist for now.)

It may seem like a lot of options but most of the arguments are used to specify the paths to the many locations Plogon needs. Once it's complete, plugin are available under `artifacts` and the repsoitory should be under `output`.
