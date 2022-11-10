#! /bin/bash

echo "Starting a build for $PLOGON_PLUGIN_NAME at $PLOGON_PLUGIN_COMMIT"

cd "/work/repo/$PLOGON_PROJECT_DIR"
if [ $PLOGON_PLUGIN_VERSION ]; then
    dotnet build -c Release --source /packages -o /output -p:DalamudLibPath=$DALAMUD_LIB_PATH -p:Version=$PLOGON_PLUGIN_VERSION -p:IsPlogonBuild=True
else
    dotnet build -c Release --source /packages -o /output -p:DalamudLibPath=$DALAMUD_LIB_PATH -p:IsPlogonBuild=True
fi
