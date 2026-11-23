using System.IO;
using System.Text.Json;

namespace CloudRedirect.Services.Providers;

/// <summary>
/// Folder / network-drive provider. Operations are direct filesystem calls
/// against <c>{syncPath}/{accountId}/{appId}/...</c>; no token, no HTTP.
/// </summary>
internal sealed class FolderUiCloudProvider : IUiCloudProvider
{
    private readonly Action<string>? _log;
    private readonly string _syncPath;

    public FolderUiCloudProvider(Action<string>? log, string syncPath)
    {
        _log = log;
        _syncPath = syncPath;
    }

    public Task<CloudProviderClient.DeleteResult> DeleteAppDataAsync(
        string accountId, string appId, CancellationToken cancel)
    {
        var folderPath = Path.Combine(_syncPath, accountId, appId);
        if (!Directory.Exists(folderPath))
        {
            _log?.Invoke($"No folder provider data found at '{folderPath}'.");
            return Task.FromResult(new CloudProviderClient.DeleteResult(true, 0, null));
        }

        var files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
        int count = files.Length;

        _log?.Invoke($"Deleting {count} files from folder provider...");
        Directory.Delete(folderPath, true);
        _log?.Invoke($"Deleted {count} files from folder provider.");

        return Task.FromResult(new CloudProviderClient.DeleteResult(true, count, null));
    }

    public Task<CloudProviderClient.ListBlobsResult> ListAppBlobsAsync(
        string accountId, string appId, CancellationToken cancel)
    {
        var blobsDir = Path.Combine(_syncPath, accountId, appId, "blobs");
        if (!Directory.Exists(blobsDir))
            return Task.FromResult(new CloudProviderClient.ListBlobsResult(Array.Empty<string>(), true, null));

        // TopDirectoryOnly: the native layout does not create subfolders
        // under blobs/, so anything deeper is foreign content we refuse to
        // touch from the prune path.
        var files = Directory.GetFiles(blobsDir, "*", SearchOption.TopDirectoryOnly);
        var names = new List<string>(files.Length);
        foreach (var f in files)
        {
            var n = Path.GetFileName(f);
            if (!string.IsNullOrEmpty(n)) names.Add(n);
        }
        return Task.FromResult(new CloudProviderClient.ListBlobsResult(names, true, null));
    }

    public Task<CloudProviderClient.DownloadBlobResult> DownloadAppBlobAsync(
        string accountId, string appId, string filename, CancellationToken cancel)
    {
        // Metadata-text blobs (stats.json) live at {accountId}/{appId}/{name},
        // not under blobs/ (those are SHA content blobs).
        var appDir = Path.Combine(_syncPath, accountId, appId);
        var appDirFull = Path.GetFullPath(appDir);
        if (!appDirFull.EndsWith(Path.DirectorySeparatorChar) &&
            !appDirFull.EndsWith(Path.AltDirectorySeparatorChar))
        {
            appDirFull += Path.DirectorySeparatorChar;
        }

        string path;
        try { path = Path.GetFullPath(Path.Combine(appDir, filename)); }
        catch { return Task.FromResult(new CloudProviderClient.DownloadBlobResult(false, null, "Invalid path")); }

        if (!path.StartsWith(appDirFull, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new CloudProviderClient.DownloadBlobResult(false, null, "Path traversal rejected"));

        if (!File.Exists(path))
            return Task.FromResult(new CloudProviderClient.DownloadBlobResult(false, null, null));

        try
        {
            var content = File.ReadAllText(path);
            return Task.FromResult(new CloudProviderClient.DownloadBlobResult(true, content, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CloudProviderClient.DownloadBlobResult(false, null, ex.Message));
        }
    }

    public Task<CloudProviderClient.ListAllStatsResult> ListAllStatsAsync(CancellationToken cancel)
    {
        // Scan stats.json files; account-scope blob (appId=0) wins over legacy per-app files.
        var entries = new List<CloudProviderClient.CloudStatsEntry>();
        var seen = new HashSet<string>();
        try
        {
            if (!Directory.Exists(_syncPath))
                return Task.FromResult(new CloudProviderClient.ListAllStatsResult(entries, null));

            foreach (var acctDir in Directory.GetDirectories(_syncPath))
            {
                var accountId = Path.GetFileName(acctDir);
                if (string.IsNullOrEmpty(accountId) || !ulong.TryParse(accountId, out _)) continue;

                // Pass 1: account-scope blob ({accountId}/0/stats.json)
                var accountBlobPath = Path.Combine(acctDir, "0", "stats.json");
                if (File.Exists(accountBlobPath))
                {
                    try
                    {
                        var raw = File.ReadAllText(accountBlobPath);
                        using var doc = JsonDocument.Parse(raw);
                        foreach (var prop in doc.RootElement.EnumerateObject())
                        {
                            if (prop.Name == "0") continue;
                            if (!uint.TryParse(prop.Name, out _)) continue;
                            var key = accountId + "/" + prop.Name;
                            if (!seen.Add(key)) continue;
                            entries.Add(new CloudProviderClient.CloudStatsEntry(
                                accountId, prop.Name, prop.Value.GetRawText()));
                        }
                    }
                    catch { }
                }

                // Pass 2: legacy per-app blobs (account blob wins on conflict)
                foreach (var appDir in Directory.GetDirectories(acctDir))
                {
                    var appId = Path.GetFileName(appDir);
                    if (string.IsNullOrEmpty(appId) || !uint.TryParse(appId, out _)) continue;
                    if (appId == "0") continue;

                    var key = accountId + "/" + appId;
                    if (seen.Contains(key)) continue;

                    var statsPath = Path.Combine(appDir, "stats.json");
                    if (!File.Exists(statsPath)) continue;

                    try
                    {
                        var content = File.ReadAllText(statsPath);
                        if (!string.IsNullOrEmpty(content))
                        {
                            seen.Add(key);
                            entries.Add(new CloudProviderClient.CloudStatsEntry(accountId, appId, content));
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(new CloudProviderClient.ListAllStatsResult(entries, ex.Message));
        }
        return Task.FromResult(new CloudProviderClient.ListAllStatsResult(entries, null));
    }

    public Task<CloudProviderClient.DeleteBlobsResult> DeleteAppBlobsAsync(
        string accountId, string appId,
        IReadOnlyCollection<string> blobFilenames, CancellationToken cancel)
    {
        var blobsDir = Path.Combine(_syncPath, accountId, appId, "blobs");
        // Missing blobs dir after scan = sync target offline; surface as failure.
        if (!Directory.Exists(blobsDir))
        {
            return Task.FromResult(new CloudProviderClient.DeleteBlobsResult(
                0, blobFilenames.Count, blobFilenames.ToList(),
                $"Blobs directory not found: {blobsDir}. Sync target may be offline or misconfigured."));
        }

        // Normalize WITH trailing separator so the StartsWith check cannot
        // match a sibling prefix ("blobsDir" would spuriously match
        // "blobsDir_evil\x" via string prefix). Path.GetFullPath does not
        // append a trailing separator, so add one explicitly.
        var blobsDirFull = Path.GetFullPath(blobsDir);
        if (!blobsDirFull.EndsWith(Path.DirectorySeparatorChar) &&
            !blobsDirFull.EndsWith(Path.AltDirectorySeparatorChar))
        {
            blobsDirFull += Path.DirectorySeparatorChar;
        }
        int deleted = 0, failed = 0;
        var failedNames = new List<string>();

        foreach (var filename in blobFilenames)
        {
            // Defense-in-depth: re-verify resolved path is under blobsDir.
            string path;
            try
            {
                path = Path.GetFullPath(Path.Combine(blobsDir, filename));
            }
            catch
            {
                failed++; failedNames.Add(filename); continue;
            }
            if (!path.StartsWith(blobsDirFull, StringComparison.OrdinalIgnoreCase))
            {
                failed++; failedNames.Add(filename); continue;
            }

            try
            {
                if (File.Exists(path)) File.Delete(path);
                deleted++;
            }
            catch
            {
                failed++; failedNames.Add(filename);
            }
        }

        string? err = failed > 0 ? $"{failed} of {blobFilenames.Count} file(s) could not be deleted." : null;
        return Task.FromResult(new CloudProviderClient.DeleteBlobsResult(deleted, failed, failedNames, err));
    }

    public Task<CloudProviderClient.ListVersionsResult> ListVersionsAsync(
        string accountId, string appId, CancellationToken cancel)
    {
        var appDir = Path.Combine(_syncPath, accountId, appId);
        var current = new List<CloudProviderClient.CloudVersionFile>();
        var blobs = new List<CloudProviderClient.CloudBlobVersion>();
        try
        {
            var statePath = Path.Combine(appDir, "state.cloudredirect");
            if (File.Exists(statePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(statePath));
                if (doc.RootElement.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
                {
                    foreach (var f in files.EnumerateObject())
                    {
                        var v = f.Value;
                        if (v.TryGetProperty("ps", out var ps) && ps.GetDouble() != 0) continue; // deleted/forgotten
                        current.Add(new CloudProviderClient.CloudVersionFile(f.Name,
                            v.TryGetProperty("sha", out var sha) ? sha.GetString() ?? "" : "",
                            v.TryGetProperty("size", out var size) ? (long)size.GetDouble() : 0,
                            v.TryGetProperty("ts", out var ts) ? (long)ts.GetDouble() : 0));
                    }
                }
            }

            var blobsDir = Path.Combine(appDir, "blobs");
            if (Directory.Exists(blobsDir))
            {
                foreach (var file in Directory.EnumerateFiles(blobsDir, "*", SearchOption.AllDirectories))
                {
                    var sha = Path.GetFileName(file);
                    if (sha.Length != 40 || !sha.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f')) continue;
                    var nameDir = Path.GetDirectoryName(file)!;
                    if (nameDir.Length <= blobsDir.Length) continue; // legacy flat blob
                    var name = Path.GetRelativePath(blobsDir, nameDir).Replace(Path.DirectorySeparatorChar, '/');
                    var info = new FileInfo(file);
                    blobs.Add(new CloudProviderClient.CloudBlobVersion(name, sha, info.Length,
                        new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds()));
                }
            }
            return Task.FromResult(new CloudProviderClient.ListVersionsResult(true, current, blobs, true, null));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return Task.FromResult(new CloudProviderClient.ListVersionsResult(false, current, blobs, false, ex.Message));
        }
    }

    public Task<CloudProviderClient.DownloadVersionsResult> DownloadVersionsAsync(
        string accountId, string appId,
        IReadOnlyList<CloudProviderClient.VersionDownload> items, CancellationToken cancel)
    {
        var blobsDir = Path.GetFullPath(Path.Combine(_syncPath, accountId, appId, "blobs")) + Path.DirectorySeparatorChar;
        int written = 0;
        foreach (var item in items)
        {
            var src = Path.GetFullPath(Path.Combine(blobsDir, item.Name.Replace('/', Path.DirectorySeparatorChar), item.Sha));
            if (!src.StartsWith(blobsDir, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new CloudProviderClient.DownloadVersionsResult(false, written, $"{item.Name}: invalid name"));
            try
            {
                var bytes = File.ReadAllBytes(src);
                if (Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)).ToLowerInvariant() != item.Sha)
                    return Task.FromResult(new CloudProviderClient.DownloadVersionsResult(false, written, $"{item.Name}: content does not match its SHA1"));
                Directory.CreateDirectory(Path.GetDirectoryName(item.OutPath)!);
                File.WriteAllBytes(item.OutPath, bytes);
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(new CloudProviderClient.DownloadVersionsResult(false, written, $"{item.Name}: {ex.Message}"));
            }
        }
        return Task.FromResult(new CloudProviderClient.DownloadVersionsResult(true, written, null));
    }
}
