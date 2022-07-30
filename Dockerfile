# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build-env
WORKDIR /source

# Copy csproj and restore as distinct layers
COPY *.sln .
COPY Plogon/*.csproj ./Plogon/
RUN dotnet restore

# Copy everything else and build
COPY Plogon/. ./Plogon/
WORKDIR /source/Plogon
RUN dotnet publish -c Release -o /app --no-restore

# Build runtime image
FROM mcr.microsoft.com/dotnet/sdk:6.0
WORKDIR /app
COPY --from=build-env /app ./
ENTRYPOINT ["dotnet", "Plogon.dll"]