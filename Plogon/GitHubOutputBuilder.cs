using System;
#pragma warning disable CS1591

namespace Plogon;

public static class GitHubOutputBuilder
{
    private static bool isActive = false;

    public static void SetActive(bool active) => isActive = active;

    public static void StartGroup(string name)
    {
        if (!isActive)
            return;

        Console.WriteLine($"::group::{name}");
    }

    public static void EndGroup()
    {
        if (!isActive)
            return;

        Console.WriteLine($"::endgroup::");
    }
}
