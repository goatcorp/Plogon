#! /bin/bash

echo "Starting build for $PLOGON_PLUGIN_NAME at $PLOGON_PLUGIN_COMMIT"

mkdir /output
cd /work/repo/$PLOGON_PROJECT_DIR
dotnet publish -c Release --source /packages -o /output -p:DalamudLibPath=$DALAMUD_LIB_PATH