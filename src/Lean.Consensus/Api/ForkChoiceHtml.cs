using System.Reflection;

namespace Lean.Consensus.Api;

public static class ForkChoiceHtml
{
    private static readonly Lazy<string> _html = new(() =>
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Lean.Consensus.Api.fork_choice.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    public static string Content => _html.Value;
}
