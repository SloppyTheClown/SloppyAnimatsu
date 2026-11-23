using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace SloppyAnimatsu.Core.Updates;

/// <summary>One downloadable file of a release.</summary>
public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

/// <summary>A published release: its tag, version, notes, page and files.</summary>
public sealed record ReleaseInfo(string Tag, string Version, string Name, string Notes, string PageUrl,
    IReadOnlyList<ReleaseAsset> Assets);

/// <summary>How an update is put in place on this machine.</summary>
public enum UpdateMethod
{
    /// <summary>Nothing can be installed automatically; the release page is opened instead.</summary>
    ReleasePage,
    /// <summary>Run the Windows setup program over the installed folder (a setup-made install).</summary>
    Setup,
    /// <summary>Unpack the archive and copy it over the program folder (an unpacked zip or tarball).</summary>
    Archive,
}

/// <summary>
/// Looks up the newest release on the project's GitHub page and decides whether it is newer than
/// this build. The release files are named <c>SloppyAnimatsu-&lt;version&gt;-&lt;runtime&gt;.zip</c> /
/// <c>.tar.gz</c> and <c>-&lt;runtime&gt;-setup.exe</c> (packaging\package.ps1), with a
/// <c>SHA256SUMS</c> beside them.
/// </summary>
public static class UpdateCheck
{
    /// <summary>The GitHub repository releases are published to.</summary>
    public const string Repository = "SloppyTheClown/SloppyAnimatsu";

    /// <summary>The API address of the newest non-draft, non-prerelease release.</summary>
    public const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";

    /// <summary>The human page listing every release.</summary>
    public const string ReleasesPage = "https://github.com/" + Repository + "/releases";

    /// <summary>The checksum list published with every release since 0.2.0-alpha.</summary>
    public const string ChecksumAssetName = "SHA256SUMS";

    /// <summary>An HTTP client set up the way GitHub's API wants it (a User-Agent is required).</summary>
    public static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SloppyAnimatsu", BuildInfo.Version));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>Fetches and parses the newest release.</summary>
    public static async Task<ReleaseInfo> FetchLatestAsync(HttpClient client, CancellationToken cancel = default)
    {
        using HttpResponseMessage response = await client.GetAsync(LatestReleaseApi, cancel).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        string json = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return ParseRelease(json);
    }

    /// <summary>Reads GitHub's release JSON (only the members used here).</summary>
    public static ReleaseInfo ParseRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string tag = Text(root, "tag_name");
        if (tag.Length == 0) throw new FormatException("The release has no tag.");
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in list.EnumerateArray())
            {
                string name = Text(asset, "name"), url = Text(asset, "browser_download_url");
                long size = asset.TryGetProperty("size", out JsonElement s) && s.TryGetInt64(out long n) ? n : 0;
                if (name.Length > 0 && url.Length > 0) assets.Add(new ReleaseAsset(name, url, size));
            }
        }
        string page = Text(root, "html_url");
        return new ReleaseInfo(tag, VersionFromTag(tag), Text(root, "name"), Text(root, "body"),
            page.Length > 0 ? page : ReleasesPage, assets);
    }

    /// <summary>"v0.4.0-alpha" -> "0.4.0-alpha".</summary>
    public static string VersionFromTag(string tag)
    {
        string trimmed = tag.Trim();
        return trimmed.Length > 1 && (trimmed[0] == 'v' || trimmed[0] == 'V') && char.IsDigit(trimmed[1])
            ? trimmed[1..]
            : trimmed;
    }

    /// <summary>True when <paramref name="candidate"/> is a later version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string candidate, string current) => CompareVersions(candidate, current) > 0;

    /// <summary>
    /// Semantic-version order: the numeric core first ("0.10.0" after "0.9.0"), then a release
    /// after any pre-release of the same core ("0.4.0" after "0.4.0-alpha"), then the pre-release
    /// labels part by part, numbers numerically ("beta.2" before "beta.10"). Build metadata
    /// ("+abc") is ignored.
    /// </summary>
    public static int CompareVersions(string a, string b)
    {
        (int[] coreA, string preA) = Split(a);
        (int[] coreB, string preB) = Split(b);
        for (int i = 0; i < Math.Max(coreA.Length, coreB.Length); i++)
        {
            int x = i < coreA.Length ? coreA[i] : 0, y = i < coreB.Length ? coreB[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        if (preA.Length == 0 || preB.Length == 0) return preA.Length == 0 ? (preB.Length == 0 ? 0 : 1) : -1;
        string[] partsA = preA.Split('.'), partsB = preB.Split('.');
        for (int i = 0; i < Math.Min(partsA.Length, partsB.Length); i++)
        {
            bool numA = int.TryParse(partsA[i], NumberStyles.None, CultureInfo.InvariantCulture, out int na);
            bool numB = int.TryParse(partsB[i], NumberStyles.None, CultureInfo.InvariantCulture, out int nb);
            int order = numA && numB ? na.CompareTo(nb)
                : numA ? -1 : numB ? 1
                : string.CompareOrdinal(partsA[i], partsB[i]);
            if (order != 0) return Math.Sign(order);
        }
        return partsA.Length.CompareTo(partsB.Length);
    }

    private static (int[] Core, string PreRelease) Split(string version)
    {
        string v = VersionFromTag(version);
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        int dash = v.IndexOf('-');
        string core = dash >= 0 ? v[..dash] : v, pre = dash >= 0 ? v[(dash + 1)..] : "";
        int[] numbers = core.Split('.')
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0)
            .ToArray();
        return (numbers, pre);
    }

    /// <summary>"win-x64", "linux-x64", ... for the machine this runs on.</summary>
    public static string CurrentRuntime =>
        (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-" +
        RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    /// <summary>The setup program of <paramref name="runtime"/>, or null.</summary>
    public static ReleaseAsset? SetupAsset(ReleaseInfo release, string runtime) =>
        release.Assets.FirstOrDefault(a => a.Name.EndsWith($"-{runtime}-setup.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>The archive (.zip on Windows, .tar.gz elsewhere) of <paramref name="runtime"/>, or null.</summary>
    public static ReleaseAsset? ArchiveAsset(ReleaseInfo release, string runtime) =>
        release.Assets.FirstOrDefault(a => a.Name.EndsWith($"-{runtime}.zip", StringComparison.OrdinalIgnoreCase))
        ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith($"-{runtime}.tar.gz", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Picks how to install: a setup-made install (its folder holds the installer's
    /// <c>uninstall.dat</c>) on Windows runs the new setup; a writable unpacked folder takes the
    /// archive; anything else - a build tree, a read-only folder, a runtime without a package -
    /// opens the release page.
    /// </summary>
    public static (UpdateMethod Method, ReleaseAsset? Asset) ChooseMethod(ReleaseInfo release, string runtime,
        bool installedBySetup, bool folderWritable, bool isBuildTree)
    {
        if (isBuildTree) return (UpdateMethod.ReleasePage, null);
        ReleaseAsset? setup = SetupAsset(release, runtime);
        if (installedBySetup && setup != null) return (UpdateMethod.Setup, setup);
        ReleaseAsset? archive = ArchiveAsset(release, runtime);
        if (folderWritable && archive != null) return (UpdateMethod.Archive, archive);
        if (setup != null && runtime.StartsWith("win", StringComparison.Ordinal)) return (UpdateMethod.Setup, setup);
        return (UpdateMethod.ReleasePage, null);
    }

    /// <summary>True for a folder under a project's <c>bin</c> output (a developer build, never overwritten).</summary>
    public static bool IsBuildTree(string folder)
    {
        string[] parts = folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i + 1 < parts.Length; i++)
        {
            if (parts[i].Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                (parts[i + 1].Equals("Debug", StringComparison.OrdinalIgnoreCase) ||
                 parts[i + 1].Equals("Release", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>The hash a <c>SHA256SUMS</c> file lists for <paramref name="fileName"/> (lower-case hex), or null.</summary>
    public static string? ChecksumFor(string sums, string fileName)
    {
        foreach (string raw in sums.Split('\n'))
        {
            string line = raw.Trim();
            int space = line.IndexOf(' ');
            if (space <= 0) continue;
            // "<hash>  <name>" or "<hash> *<name>" (binary mode).
            string name = line[(space + 1)..].TrimStart(' ', '*');
            if (name.Equals(fileName, StringComparison.Ordinal)) return line[..space].ToLowerInvariant();
        }
        return null;
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> to <paramref name="target"/>, reporting 0 to 1, and checks it
    /// against the release's <c>SHA256SUMS</c> when the release has one.
    /// </summary>
    public static async Task DownloadAsync(HttpClient client, ReleaseInfo release, ReleaseAsset asset, string target,
        IProgress<double>? progress, CancellationToken cancel = default)
    {
        using (HttpResponseMessage response = await client.GetAsync(asset.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            await using FileStream file = File.Create(target);
            byte[] buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1.0, (double)done / total));
            }
        }
        ReleaseAsset? sumsAsset = release.Assets.FirstOrDefault(a => a.Name == ChecksumAssetName);
        if (sumsAsset == null) return;
        string sums = await client.GetStringAsync(sumsAsset.DownloadUrl, cancel).ConfigureAwait(false);
        string? expected = ChecksumFor(sums, asset.Name);
        if (expected == null) return;
        string actual;
        await using (FileStream file = File.OpenRead(target))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, cancel).ConfigureAwait(false)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(target);
            throw new InvalidDataException($"{asset.Name} does not match its published checksum; the download was discarded.");
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
