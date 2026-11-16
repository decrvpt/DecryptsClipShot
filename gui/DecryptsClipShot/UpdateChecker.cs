using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DecryptsClipShot;

/// <summary>A newer release found on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string SetupUrl, string PageUrl);

/// <summary>
/// Checks the GitHub Releases page for a newer ClipShot and downloads its
/// setup. Releases are published by .github/workflows/release.yml, so pushing
/// a new version tag is all it takes for every installed copy to be offered it.
/// </summary>
public static class UpdateChecker
{
    private const string Repository = "decrvpt/DecryptsClipShot";
    private const string SetupAssetName = "DecryptsClipShot-Setup.exe";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API requires a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"DecryptsClipShot/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The installed app's version (set from the release tag at build time).</summary>
    public static Version CurrentVersion
    {
        get
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }
    }

    /// <summary>Returns the newest release if it is newer than this app, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpResponseMessage response = await Http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode) return null; // no releases yet, offline, rate-limited…

        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        JsonElement root = json.RootElement;
        string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest)) return null;
        latest = new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build));
        if (latest <= CurrentVersion) return null;

        string? setupUrl = root.TryGetProperty("assets", out JsonElement assets)
            ? assets.EnumerateArray()
                .Where(asset => asset.TryGetProperty("name", out JsonElement name) &&
                                string.Equals(name.GetString(), SetupAssetName, StringComparison.OrdinalIgnoreCase))
                .Select(asset => asset.GetProperty("browser_download_url").GetString())
                .FirstOrDefault()
            : null;
        // Only accept a download from this repository on GitHub.
        if (setupUrl is null ||
            !setupUrl.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.OrdinalIgnoreCase))
            return null;

        string notes = root.TryGetProperty("body", out JsonElement body) ? body.GetString() ?? "" : "";
        string page = root.TryGetProperty("html_url", out JsonElement html) ? html.GetString() ?? "" : "";
        return new UpdateInfo(latest, CleanNotes(notes), setupUrl, page);
    }

    /// <summary>Downloads the setup to a temporary folder, reporting progress 0–1.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress,
                                                   CancellationToken cancellation)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DecryptsClipShot-Update");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, $"DecryptsClipShot-Setup-{update.Version}.exe");
        string partial = destination + ".download";

        using HttpResponseMessage response = await Http.GetAsync(update.SetupUrl,
            HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using (Stream input = await response.Content.ReadAsStreamAsync(cancellation))
        await using (FileStream output = File.Create(partial))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                received += read;
                if (total is > 0) progress.Report((double)received / total.Value);
            }
        }
        File.Move(partial, destination, overwrite: true);
        return destination;
    }

    // Release notes are Markdown; show them as plain text in the popup.
    private static string CleanNotes(string markdown)
    {
        // Release pages end with download instructions; the popup only needs "What's new".
        int download = markdown.IndexOf("## Download", StringComparison.OrdinalIgnoreCase);
        if (download >= 0) markdown = markdown[..download];
        markdown = markdown.Replace("## What's new", "", StringComparison.OrdinalIgnoreCase);
        var lines = markdown.Replace("\r", "").Split('\n')
            .Select(line => line.Replace("**", "").Replace("`", "").Trim())
            .Where(line => !line.StartsWith("Windows may show", StringComparison.OrdinalIgnoreCase))
            .Select(line => line.StartsWith("#") ? line.TrimStart('#').Trim() : line)
            .Select(line => line.StartsWith("- ") || line.StartsWith("* ") ? "•  " + line[2..] : line)
            .ToList();
        string text = string.Join("\n", lines).Trim();
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        return text.Length > 1500 ? text[..1500] + "…" : text;
    }
}
