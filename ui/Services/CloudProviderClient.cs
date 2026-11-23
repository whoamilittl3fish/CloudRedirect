namespace CloudRedirect.Services;

/// <summary>
/// UI-side facade over IUiCloudProvider. Reads CloudConfig, resolves the
/// provider via UiCloudProviderFactory, forwards calls. Per-provider logic
/// lives in CloudRedirect.Services.Providers.
/// </summary>
internal sealed class CloudProviderClient
{
    private readonly Action<string>? _log;

    public CloudProviderClient(Action<string>? log = null)
    {
        _log = log;
    }

    public record DeleteResult(bool Success, int FilesDeleted, string? Error);

    /// <summary>Delete all cloud data for one app via the active provider.</summary>
    public async Task<DeleteResult> DeleteAppDataAsync(string accountId, string appId, CancellationToken cancel = default)
    {
        var config = SteamDetector.ReadConfig();
        var provider = UiCloudProviderFactory.TryResolve(config, _log);
        if (provider == null)
            return new DeleteResult(true, 0, null); // no config / local-only / unrecognized -> nothing to do

        try
        {
            return await provider.DeleteAppDataAsync(accountId, appId, cancel);
        }
        catch (Exception ex)
        {
            return new DeleteResult(false, 0, ex.Message);
        }
    }

    /// <summary>
    /// Returns the display name of the configured cloud provider, or null if local-only.
    /// </summary>
    public static string? GetProviderDisplayName()
    {
        var config = SteamDetector.ReadConfig();
        if (config == null || config.IsLocal) return null;
        return config.DisplayName;
    }

    // Cloud blobs live under {provider}/CloudRedirect/{accountId}/{appId}/blobs/.
    // Complete=false means callers must not prune (mirrors native ListChecked).

    /// <summary>
    /// Listing of blob filenames; subfolders excluded; case-sensitive; unordered.
    /// Complete=false on pagination/HTTP/provider error.
    /// </summary>
    public record ListBlobsResult(IReadOnlyList<string> BlobFilenames, bool Complete, string? Error);

    public record DeleteBlobsResult(int Deleted, int Failed, IReadOnlyList<string> FailedFilenames, string? Error);

    /// <summary>
    /// Result of downloading a single blob's content. Found=false when the blob
    /// does not exist; Content is the raw (decoded) text on success.
    /// </summary>
    public record DownloadBlobResult(bool Found, string? Content, string? Error);

    public record CloudStatsEntry(string AccountId, string AppId, string Content);

    /// <summary>One file of the current cloud state (Time: the save's own timestamp).</summary>
    public record CloudVersionFile(string Name, string Sha, long Size, long Time);

    /// <summary>One stored blob: a version of file Name with content Sha.</summary>
    public record CloudBlobVersion(string Name, string Sha, long Size, long Modified);

    public record ListVersionsResult(bool Success, IReadOnlyList<CloudVersionFile> Current,
                                     IReadOnlyList<CloudBlobVersion> Blobs, bool Complete, string? Error);

    public record VersionDownload(string Name, string Sha, string OutPath);

    public record DownloadVersionsResult(bool Success, int Written, string? Error);

    /// <summary>Versions of one app's saves. Success=false with Error=null: no cloud configured.</summary>
    public async Task<ListVersionsResult> ListVersionsAsync(string accountId, string appId, CancellationToken cancel = default)
    {
        var provider = UiCloudProviderFactory.TryResolve(SteamDetector.ReadConfig(), _log);
        if (provider == null)
            return new ListVersionsResult(false, Array.Empty<CloudVersionFile>(), Array.Empty<CloudBlobVersion>(), true, null);
        try
        {
            return await provider.ListVersionsAsync(accountId, appId, cancel);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ListVersionsResult(false, Array.Empty<CloudVersionFile>(), Array.Empty<CloudBlobVersion>(), false, ex.Message);
        }
    }

    public async Task<DownloadVersionsResult> DownloadVersionsAsync(string accountId, string appId,
        IReadOnlyList<VersionDownload> items, CancellationToken cancel = default)
    {
        var provider = UiCloudProviderFactory.TryResolve(SteamDetector.ReadConfig(), _log);
        if (provider == null) return new DownloadVersionsResult(false, 0, "No cloud provider configured");
        try
        {
            return await provider.DownloadVersionsAsync(accountId, appId, items, cancel);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DownloadVersionsResult(false, 0, ex.Message);
        }
    }

    /// <summary>Parses the JSON printed by the CLI's list-versions command.</summary>
    internal static ListVersionsResult ParseListVersions(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        string? error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
        bool success = root.TryGetProperty("success", out var s) && s.GetBoolean();
        bool complete = root.TryGetProperty("complete", out var c) && c.GetBoolean();
        var current = new List<CloudVersionFile>();
        if (root.TryGetProperty("current", out var cur) && cur.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var f in cur.EnumerateArray())
                current.Add(new CloudVersionFile(f.GetProperty("name").GetString() ?? "", f.GetProperty("sha").GetString() ?? "",
                    f.GetProperty("size").GetInt64(), f.GetProperty("time").GetInt64()));
        var blobs = new List<CloudBlobVersion>();
        if (root.TryGetProperty("blobs", out var bl) && bl.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var b in bl.EnumerateArray())
                blobs.Add(new CloudBlobVersion(b.GetProperty("name").GetString() ?? "", b.GetProperty("sha").GetString() ?? "",
                    b.GetProperty("size").GetInt64(), b.GetProperty("modified").GetInt64()));
        return new ListVersionsResult(success, current, blobs, complete, error);
    }

    public record ListAllStatsResult(IReadOnlyList<CloudStatsEntry> Entries, string? Error);

    /// <summary>Enumerate every app's stats.json in the cloud. Returns empty when no cloud is configured.</summary>
    public async Task<ListAllStatsResult> ListAllStatsAsync(CancellationToken cancel = default)
    {
        var config = SteamDetector.ReadConfig();
        var provider = UiCloudProviderFactory.TryResolve(config, _log);
        if (provider == null)
            return new ListAllStatsResult(Array.Empty<CloudStatsEntry>(), null);

        try
        {
            return await provider.ListAllStatsAsync(cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ListAllStatsResult(Array.Empty<CloudStatsEntry>(), ex.Message);
        }
    }

    /// <summary>
    /// Download a single metadata blob (e.g. stats.json) from the cloud provider.
    /// Returns Found=false when there is no cloud configured or the blob is absent.
    /// </summary>
    public async Task<DownloadBlobResult> DownloadAppBlobAsync(
        string accountId, string appId, string filename, CancellationToken cancel = default)
    {
        if (IsUnsafeBlobName(filename))
            return new DownloadBlobResult(false, null, "Unsafe blob name");

        var config = SteamDetector.ReadConfig();
        var provider = UiCloudProviderFactory.TryResolve(config, _log);
        if (provider == null)
            return new DownloadBlobResult(false, null, null); // local-only / no cloud

        try
        {
            return await provider.DownloadAppBlobAsync(accountId, appId, filename, cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadBlobResult(false, null, ex.Message);
        }
    }

    /// <summary>List blob filenames under {accountId}/{appId}/blobs/. Local/no-cloud always Complete=true.</summary>
    public async Task<ListBlobsResult> ListAppBlobsAsync(string accountId, string appId, CancellationToken cancel = default)
    {
        var config = SteamDetector.ReadConfig();
        var provider = UiCloudProviderFactory.TryResolve(config, _log);
        if (provider == null)
            return new ListBlobsResult(Array.Empty<string>(), true, null);

        try
        {
            return await provider.ListAppBlobsAsync(accountId, appId, cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ListBlobsResult(Array.Empty<string>(), false, ex.Message);
        }
    }

    /// <summary>
    /// Delete given blob filenames; absent → counted as Deleted (idempotent).
    /// Path-like names are rejected up front.
    /// </summary>
    public async Task<DeleteBlobsResult> DeleteAppBlobsAsync(
        string accountId, string appId, IReadOnlyCollection<string> blobFilenames, CancellationToken cancel = default)
    {
        if (blobFilenames.Count == 0)
            return new DeleteBlobsResult(0, 0, Array.Empty<string>(), null);

        // Reject path-like filenames; per-provider impls rely on this filter.
        var safe = new List<string>(blobFilenames.Count);
        var rejected = new List<string>();
        foreach (var name in blobFilenames)
        {
            if (IsUnsafeBlobName(name)) rejected.Add(name);
            else safe.Add(name);
        }

        var config = SteamDetector.ReadConfig();
        var provider = UiCloudProviderFactory.TryResolve(config, _log);
        if (provider == null)
        {
            // No cloud configured; rejected names still surface as failed.
            string? err = rejected.Count > 0 ? $"{rejected.Count} filename(s) rejected as unsafe." : null;
            return new DeleteBlobsResult(0, rejected.Count, rejected, err);
        }

        DeleteBlobsResult inner;
        try
        {
            inner = await provider.DeleteAppBlobsAsync(accountId, appId, safe, cancel);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var all = new List<string>(safe);
            all.AddRange(rejected);
            return new DeleteBlobsResult(0, all.Count, all, ex.Message);
        }

        if (rejected.Count == 0) return inner;

        // Fold rejected names into the failure tally.
        var mergedFailed = new List<string>(inner.FailedFilenames);
        mergedFailed.AddRange(rejected);
        var mergedErr = inner.Error;
        if (mergedErr == null) mergedErr = $"{rejected.Count} filename(s) rejected as unsafe.";
        else mergedErr += $" {rejected.Count} filename(s) also rejected as unsafe.";
        return new DeleteBlobsResult(inner.Deleted, inner.Failed + rejected.Count, mergedFailed, mergedErr);
    }

    /// <summary>
    /// Reject filenames Windows would canonicalize (trailing dot/space,
    /// path separators, traversal, reserved DOS device names).
    /// </summary>
    private static bool IsUnsafeBlobName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.Contains('/') || name.Contains('\\')) return true;
        if (name == "." || name == "..") return true;
        if (name.Contains(":")) return true; // drive-letter / NTFS stream
        // Trailing '.' or ' ' is silently stripped by Windows canonicalization.
        if (name.EndsWith('.') || name.EndsWith(' ')) return true;
        foreach (var c in name)
            if (c < 0x20) return true;
        // Reserved DOS device names map to the device, not a file.
        var baseName = name;
        var dot = name.IndexOf('.');
        if (dot >= 0) baseName = name.Substring(0, dot);
        if (s_reservedDeviceNames.Contains(baseName)) return true;
        return false;
    }

    private static readonly HashSet<string> s_reservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
