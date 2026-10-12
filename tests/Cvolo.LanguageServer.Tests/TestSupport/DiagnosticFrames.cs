using Newtonsoft.Json.Linq;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>Published diagnostics parsed out of server frames.</summary>
internal sealed record PublishedDiagnostics(string Uri, JArray Diagnostics, int? Version)
{
    public int Count => Diagnostics.Count;
}

internal static class DiagnosticFrames
{
    private const string PublishMethod = "textDocument/publishDiagnostics";

    public static IReadOnlyList<PublishedDiagnostics> Extract(IEnumerable<string> frames)
    {
        var published = new List<PublishedDiagnostics>();
        foreach (var frame in frames)
        {
            JObject json;
            try
            {
                json = JObject.Parse(frame);
            }
            catch
            {
                continue;
            }

            if (json["method"]?.Value<string>() != PublishMethod)
            {
                continue;
            }

            JToken? parameters = json["params"];
            if (parameters is null)
            {
                continue;
            }

            published.Add(new PublishedDiagnostics(
                parameters["uri"]?.Value<string>() ?? string.Empty,
                parameters["diagnostics"] as JArray ?? [],
                parameters["version"]?.Value<int?>()));
        }

        return published;
    }

    public static IReadOnlyList<PublishedDiagnostics> ForUri(ServerHost server, Uri uri)
    {
        return ForUri(server.GetServerFrames(), uri);
    }

    public static IReadOnlyList<PublishedDiagnostics> ForUri(IEnumerable<string> frames, Uri uri)
    {
        return [.. Extract(frames).Where(p => SameUri(p.Uri, uri))];
    }

    public static bool SameUri(string left, Uri right)
    {
        return Uri.TryCreate(left, UriKind.Absolute, out Uri? parsed)
            && string.Equals(parsed.AbsoluteUri, right.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }
}
