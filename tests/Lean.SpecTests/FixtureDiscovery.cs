using System.Text.Json;
using NUnit.Framework;

namespace Lean.SpecTests;

public static class FixtureDiscovery
{
    private static readonly string FixturesRoot = ResolveFixturesRoot();

    private static string ResolveFixturesRoot()
    {
        var envPath = Environment.GetEnvironmentVariable("LEAN_SPECTEST_FIXTURES");
        if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
            return Path.GetFullPath(envPath);

        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
            return string.Empty;

        // Prefer sibling leanSpec checkout: <workspace>/nlean & <workspace>/leanSpec
        foreach (var candidate in new[]
        {
            Path.Combine(repoRoot, "..", "leanSpec", "fixtures", "consensus"),
            Path.Combine(repoRoot, "..", "leanspec", "fixtures", "consensus"),
            Path.Combine(repoRoot, "leanSpec", "fixtures", "consensus"),
            Path.Combine(repoRoot, "vendor", "leanSpec", "fixtures", "consensus"),
        })
        {
            if (Directory.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return string.Empty;
    }

    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, "Lean.sln")))
                return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir)
                break;
            dir = parent;
        }
        return null;
    }

    public static bool IsAvailable => !string.IsNullOrEmpty(FixturesRoot) && Directory.Exists(FixturesRoot);

    public static IEnumerable<TestCaseData> DiscoverTests(string fixtureKind)
    {
        // Strip any path separators so callers can't escape FixturesRoot.
        var safeKind = Path.GetFileName(fixtureKind);

        if (!IsAvailable)
        {
            yield return new TestCaseData("(no fixtures)", "{}")
                .SetName($"{safeKind}_fixtures_unavailable")
                .Ignore("Fixtures directory not available. Set LEAN_SPECTEST_FIXTURES env var.");
            yield break;
        }

        var kindDir = Path.Combine(FixturesRoot, safeKind);
        if (!Directory.Exists(kindDir))
        {
            yield return new TestCaseData("(empty)", "{}")
                .SetName($"{safeKind}_no_directory")
                .Ignore($"No {safeKind} fixtures found at {kindDir}");
            yield break;
        }

        foreach (var jsonFile in Directory.EnumerateFiles(kindDir, "*.json", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(FixturesRoot, jsonFile);

            var (json, readError) = TryReadAllText(jsonFile);
            if (readError is not null)
            {
                yield return new TestCaseData(relativePath, "{}")
                    .SetName(SanitizeTestName($"{safeKind}/{relativePath}_read_error"))
                    .Ignore($"Failed to read fixture: {readError}");
                continue;
            }

            var (tests, parseError) = TryParseFixture(json!);
            if (parseError is not null)
            {
                yield return new TestCaseData(relativePath, json ?? "{}")
                    .SetName(SanitizeTestName($"{safeKind}/{relativePath}_parse_error"))
                    .Ignore($"Failed to parse fixture JSON: {parseError}");
                continue;
            }

            if (tests is null || tests.Count == 0) continue;

            foreach (var (testId, testElement) in tests)
            {
                var testJson = testElement.GetRawText();
                var shortName = ExtractShortName(testId, relativePath);
                yield return new TestCaseData(testId, testJson)
                    .SetName(SanitizeTestName($"{safeKind}/{shortName}"));
            }
        }
    }

    private static string ExtractShortName(string testId, string relativePath)
    {
        // testId example: "tests/consensus/devnet/fc/test_fork_choice_head.py::test_name[variant]"
        var colonIdx = testId.LastIndexOf("::", StringComparison.Ordinal);
        if (colonIdx >= 0 && colonIdx + 2 < testId.Length)
            return testId[(colonIdx + 2)..];

        return Path.GetFileNameWithoutExtension(relativePath);
    }

    private static (string? Content, string? Error) TryReadAllText(string path)
    {
        try
        {
            return (File.ReadAllText(path), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static (Dictionary<string, JsonElement>? Tests, string? Error) TryParseFixture(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private static string SanitizeTestName(string name)
    {
        // NUnit test names can't contain certain chars reliably.
        return name
            .Replace('\\', '/')
            .Replace(',', '_');
    }
}
