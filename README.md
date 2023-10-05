# Plogon

Plogon is the build system used in [DalamudPluginsD17](https://github.com/goatcorp/DalamudPluginsD17). It builds Dalamud plugins from source in isolated Docker containers, allowing for reproducible and verifiable plugin binaries.

Plogon is part of the implementation of [DIP17](https://github.com/goatcorp/DIPs/blob/main/text/17-automated-build-and-submit-pipeline.md). Combined with the GitHub Actions workflows in the DalamudPluginsD17 repository and XLWebServices, it is used in the official Dalamud plugin repository to enhance the developer experience and security.

Plogon is not designed to be ran as an end user, but rather as part of a CI system. However, if you plan to develop/test it locally, or want to adopt Plogon in your own CI system, usage instructions are available below.

## Running locally

You will need to have the following:

- The source code to Plogon and the .NET SDK to build/run it
- Docker
- Folders for every required part of Plogon (see below)
- Manifest files for the plugins you want to build

There are five folders required to run Plogon:

- The `static` folder. This should point to the folder inside of this repository, which contains Docker entrypoint scripts.
- The `manifest` folder. This contains a collection of folders with TOML files. Reference the DalamudPluginsD17 repository or the DIP17 specification for more information.
- The `work` folder. Source code and dependencies are cloned into this folder by Plogon.
- The `artifact` folder. Plugin binaries are built into this folder by Plogon, but are later copied into the `output` folder.
- The `output` folder. This is the final destination of the built plugin binaries, along with a `State.toml` that tracks what has been built.

A folder named `local` at the root of this repository is ignored by Git, so we will use it for the sake of this demonstration. Let's make these required folders:

```shell
mkdir local
mkdir local/manifest
mkdir local/work
mkdir local/artifact
mkdir local/output
```

Now, create the `stable` and `testing` tracks in the manifest folder (required):

```shell
mkdir local/manifest/stable
mkdir local/manifest/testing
```

Let's use the manifests from [Wotsit](https://github.com/goatcorp/DalamudPluginsD17/tree/main/stable/Dalamud.FindAnything) for demonstration - you can of course use your own:

```shell
mkdir local/manifest/stable/Dalamud.FindAnything
cd local/manifest/stable/Dalamud.FindAnything
wget https://raw.githubusercontent.com/goatcorp/DalamudPluginsD17/main/stable/Dalamud.FindAnything/manifest.toml

# Plugin icons are required
mkdir images
cd images
wget https://raw.githubusercontent.com/goatcorp/DalamudPluginsD17/main/stable/Dalamud.FindAnything/images/icon.png

cd ../../../../..
```

Now, run Plogon:

```shell
dotnet run --project Plogon -- \
--static-folder "./Plogon/static" \
--manifest-folder "./local/manifest" \
--work-folder "./local/work" \
--artifact-folder "./local/artifact" \
--output-folder "./local/output" \
--mode=Commit --build-all=true
```

You can also specify `--build-overrides-file` to a file that [looks like this one](https://github.com/goatcorp/DalamudPluginsD17/blob/main/overrides.toml) to set what Dalamud track to use.

We can now investigate the output directory:

```shell
$ tree local/output
local/output
├── stable
│   └── Dalamud.FindAnything
│       ├── Dalamud.FindAnything.json
│       ├── images
│       │   └── icon.png
│       └── latest.zip
└── State.toml
```

We can confirm this matches the structure of [PluginDistD17](https://github.com/goatcorp/PluginDistD17).
