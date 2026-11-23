using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FH6Telemetry;

internal sealed record TelemetryRelease(string Tag, Version Version, long? PackageSize, string? Digest);

internal sealed class GitHubUpdates
{
    private const string PackageName = "FH6Telemetry.exe";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly HttpClient _http;
    private readonly Uri? _latestApi;

    public GitHubUpdates(string? repository, HttpClient? http = null)
    {
        _http = http ?? SharedClient;
        var path = repository?.Trim();
        if (path?.Split('/') is not [var owner, var name] || !IsRepositoryPart(owner) || !IsRepositoryPart(name)) return;

        ProjectPage = new Uri($"https://github.com/{path}");
        ReleasesPage = new Uri($"{ProjectPage}/releases");
        LatestReleasePage = new Uri($"{ReleasesPage}/latest");
        _latestApi = new Uri($"https://api.github.com/repos/{path}/releases/latest");
    }

    public Uri? ProjectPage { get; }
    public Uri? ReleasesPage { get; }
    public Uri? LatestReleasePage { get; }

    public async Task<TelemetryRelease?> FindLatestAsync(CancellationToken cancellationToken)
    {
        if (_latestApi is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, _latestApi);
        request.Headers.UserAgent.ParseAdd("FH6Telemetry");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var tag = ReadText(root, "tag_name");
        if (tag is null || !Version.TryParse(tag.TrimStart('v', 'V').Split(['-', '+'], 2)[0], out var version))
            throw new JsonException("Invalid release tag.");

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                if (ReadText(asset, "name") == PackageName && asset.TryGetProperty("size", out var size) &&
                    size.ValueKind == JsonValueKind.Number && size.TryGetInt64(out var length) && length > 0)
                    return new TelemetryRelease(tag, version, length, ReadText(asset, "digest"));
            }
        }

        return new TelemetryRelease(tag, version, null, null);
    }

    public async Task DownloadAsync(TelemetryRelease release, string directory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, $"FH6Telemetry-{release.Version}.exe");
        var temporary = destination + $".{Guid.NewGuid():N}.part";
        var downloadUrl = $"{ReleasesPage}/download/{Uri.EscapeDataString(release.Tag)}/{PackageName}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));

        if (File.Exists(destination) && await MatchesReleaseAsync(destination, release, timeout.Token).ConfigureAwait(false))
            return;

        try
        {
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await response.Content.CopyToAsync(file, timeout.Token).ConfigureAwait(false);

            if (!await MatchesReleaseAsync(temporary, release, timeout.Token).ConfigureAwait(false))
                throw new IOException("The downloaded package failed verification.");

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<bool> MatchesReleaseAsync(string path, TelemetryRelease release, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != release.PackageSize) return false;
        if (release.Digest is null) return true;

        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals($"sha256:{Convert.ToHexString(hash)}", release.Digest, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRepositoryPart(string value) => value.Length > 0 && value is not ("." or "..") &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static string? ReadText(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
