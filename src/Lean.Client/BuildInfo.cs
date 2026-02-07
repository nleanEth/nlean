using System.Reflection;

namespace Lean.Client;

internal static class BuildInfo
{
    public static string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version?.ToString() ?? "0.1.0";
        }
    }

    public static string? GitSha
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("LEAN_GIT_SHA")
                ?? Environment.GetEnvironmentVariable("GIT_SHA");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            return TryReadGitSha();
        }
    }

    public static string VersionString
    {
        get
        {
            return GitSha is null ? Version : $"{Version}+{GitSha}";
        }
    }

    private static string? TryReadGitSha()
    {
        var root = Directory.GetCurrentDirectory();
        var gitDir = Path.Combine(root, ".git");
        if (!Directory.Exists(gitDir))
        {
            return null;
        }

        var headPath = Path.Combine(gitDir, "HEAD");
        if (!File.Exists(headPath))
        {
            return null;
        }

        var head = File.ReadAllText(headPath).Trim();
        if (head.StartsWith("ref:", StringComparison.Ordinal))
        {
            var refPath = head.Replace("ref:", string.Empty).Trim();
            var fullRefPath = Path.Combine(gitDir, refPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullRefPath))
            {
                return File.ReadAllText(fullRefPath).Trim();
            }
            return null;
        }

        return head.Length >= 8 ? head : null;
    }
}
