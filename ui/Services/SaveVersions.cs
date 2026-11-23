using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloudRedirect.Services;

/// <summary>
/// Every version of one game's saves that can be put back: the current cloud state,
/// earlier cloud versions (the blobs kept by keep_previous_versions), the local
/// snapshot and the relink backup. Cloud versions are kept per file, not per save,
/// so an earlier cloud version is rebuilt as "each file as it was at time T".
/// </summary>
internal static class SaveVersions
{
    internal enum Kind { CloudCurrent, CloudEarlier, LocalBackup, OtherSaveFolder }

    internal sealed record VersionFile(string Name, string Sha, long Size);

    /// <summary>A restorable version. Changed: files that differ from the current cloud
    /// state (cloud versions only). Snapshot is set for the two local kinds.</summary>
    internal sealed record Version(Kind Kind, DateTimeOffset Time, IReadOnlyList<VersionFile> Files,
                                   int Changed, LocalBackupStore.Snapshot? Snapshot)
    {
        public long Bytes => Files.Sum(f => f.Size);
    }

    internal sealed record Listing(IReadOnlyList<Version> Versions, string? CloudError);

    internal sealed record RestoreOutcome(int Restored, string BackupDir);

    // Files uploaded within this window belong to the same save.
    private const long SameSaveSeconds = 120;

    public static async Task<Listing> LoadAsync(uint accountId, uint appId, CancellationToken cancel = default)
    {
        var versions = new List<Version>();
        string? cloudError = null;

        var cloud = await new CloudProviderClient().ListVersionsAsync(
            accountId.ToString(CultureInfo.InvariantCulture), appId.ToString(CultureInfo.InvariantCulture), cancel);
        if (cloud.Success)
            versions.AddRange(BuildCloudVersions(cloud.Current, cloud.Blobs));
        else
            cloudError = cloud.Error; // null: no cloud configured

        foreach (var snap in await Task.Run(() => LocalBackupStore.FindAll(accountId, appId), cancel))
        {
            var files = snap.Files.Select(f => new VersionFile(f.Name, f.Sha, SafeLength(Path.Combine(snap.Dir,
                f.Name.Replace('/', Path.DirectorySeparatorChar))))).ToList();
            versions.Add(new Version(snap.IsRelink ? Kind.OtherSaveFolder : Kind.LocalBackup,
                new DateTimeOffset(snap.Taken), files, 0, snap));
        }

        return new Listing(versions.OrderBy(v => v.Kind == Kind.CloudCurrent ? 0 : 1)
                                   .ThenByDescending(v => v.Time).ToList(), cloudError);
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; } catch { return 0; }
    }

    /// <summary>
    /// The current cloud state, then one entry per earlier save point (newest first):
    /// for each current file, the newest stored version uploaded at or before that
    /// point; a file with no version that old keeps its current content. Points that
    /// change nothing, or repeat an earlier entry, are dropped.
    /// </summary>
    internal static List<Version> BuildCloudVersions(IReadOnlyList<CloudProviderClient.CloudVersionFile> current,
                                                     IReadOnlyList<CloudProviderClient.CloudBlobVersion> blobs)
    {
        var result = new List<Version>();
        if (current.Count == 0) return result;

        var currentByName = current.ToDictionary(f => f.Name, StringComparer.Ordinal);
        var byName = blobs.Where(b => currentByName.ContainsKey(b.Name))
                          .GroupBy(b => b.Name, StringComparer.Ordinal)
                          .ToDictionary(g => g.Key, g => g.OrderBy(b => b.Modified).ToList(), StringComparer.Ordinal);

        long currentTime = current.Max(f => f.Time);
        foreach (var f in current)
            if (byName.TryGetValue(f.Name, out var list))
                foreach (var b in list)
                    if (b.Sha == f.Sha) currentTime = Math.Max(currentTime, b.Modified);
        result.Add(new Version(Kind.CloudCurrent, DateTimeOffset.FromUnixTimeSeconds(currentTime),
            current.Select(f => new VersionFile(f.Name, f.Sha, f.Size)).ToList(), 0, null));

        // Save points: upload times of versions that are no longer current, clustered.
        var times = byName.Values.SelectMany(l => l)
                          .Where(b => b.Sha != currentByName[b.Name].Sha)
                          .Select(b => b.Modified).Distinct().OrderBy(t => t).ToList();
        var points = new List<long>();
        foreach (var t in times)
        {
            if (points.Count > 0 && t - points[^1] <= SameSaveSeconds) points[^1] = t;
            else points.Add(t);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            string.Join("|", current.OrderBy(f => f.Name, StringComparer.Ordinal).Select(f => f.Sha))
        };
        foreach (var point in points.OrderByDescending(t => t))
        {
            var files = new List<VersionFile>();
            int changed = 0;
            foreach (var cur in current.OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                CloudProviderClient.CloudBlobVersion? asOf = null;
                if (byName.TryGetValue(cur.Name, out var list))
                    asOf = list.LastOrDefault(b => b.Modified <= point);
                if (asOf == null)
                {
                    files.Add(new VersionFile(cur.Name, cur.Sha, cur.Size));
                    continue;
                }
                files.Add(new VersionFile(cur.Name, asOf.Sha, asOf.Size));
                if (asOf.Sha != cur.Sha) changed++;
            }
            if (changed == 0) continue;
            if (!seen.Add(string.Join("|", files.Select(f => f.Sha)))) continue;
            result.Add(new Version(Kind.CloudEarlier, DateTimeOffset.FromUnixTimeSeconds(point), files, changed, null));
        }
        return result;
    }

    // ── Restore ─────────────────────────────────────────────────────────────

    internal sealed class RestoreException : Exception
    {
        public RestoreException(string message) : base(message) { }
    }

    public static bool IsSteamRunning() => Process.GetProcessesByName("steam").Length > 0;

    /// <summary>Where cloud file names of this app live on this PC: the map the DLL
    /// writes on every scan, or userdata\...\remote for Steam Cloud API games.</summary>
    private static List<(string Cloud, string Local)> LoadSaveRoots(uint accountId, uint appId)
    {
        var roots = new List<(string, string)>();
        var steam = SteamDetector.FindSteamPath();
        if (steam == null) return roots;
        var map = Path.Combine(steam, "cloud_redirect", "save_roots",
            accountId.ToString(CultureInfo.InvariantCulture), appId.ToString(CultureInfo.InvariantCulture) + ".json");
        try
        {
            if (File.Exists(map))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(map));
                if (doc.RootElement.TryGetProperty("roots", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var r in arr.EnumerateArray())
                    {
                        var cloud = r.TryGetProperty("cloud", out var c) ? c.GetString() ?? "" : "";
                        var local = r.TryGetProperty("local", out var l) ? l.GetString() ?? "" : "";
                        if (local.Length > 0 && Path.IsPathFullyQualified(local)) roots.Add((cloud, local));
                    }
                return roots;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }

        var remote = Path.Combine(steam, "userdata", accountId.ToString(CultureInfo.InvariantCulture),
            appId.ToString(CultureInfo.InvariantCulture), "remote");
        if (Directory.Exists(remote)) roots.Add(("", remote));
        return roots;
    }

    /// <summary>Local path for a cloud file name (longest matching cloud prefix), or null.</summary>
    internal static string? ResolveLocalPath(IReadOnlyList<(string Cloud, string Local)> roots, string name)
    {
        if (name.Length == 0 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':')) return null;
        if (name.Split('/').Any(p => p.Length == 0 || p == "." || p == "..")) return null;
        foreach (var (cloud, local) in roots.OrderByDescending(r => r.Cloud.Length))
        {
            string rest;
            if (cloud.Length == 0) rest = name;
            else if (name.StartsWith(cloud + "/", StringComparison.Ordinal)) rest = name[(cloud.Length + 1)..];
            else continue;
            var baseDir = Path.GetFullPath(local).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(baseDir, rest.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        return null;
    }

    /// <summary>
    /// Writes a cloud version into the save folder. Only files that differ are
    /// touched; other files are left alone. The files it replaces are first saved as
    /// a local backup (local_backups\...\&lt;stamp&gt;-before-restore), which the Restore
    /// local backup button can put back. Steam must not be running.
    /// </summary>
    public static async Task<RestoreOutcome> RestoreCloudAsync(uint accountId, uint appId, Version version,
                                                               CancellationToken cancel = default)
    {
        if (IsSteamRunning()) throw new RestoreException(Resources.S.Get("History_VersionsSteamRunning"));
        var steam = SteamDetector.FindSteamPath() ?? throw new RestoreException("Steam folder not found");
        var roots = LoadSaveRoots(accountId, appId);

        // 1. Plan: the files whose content on disk differs from the version.
        var plan = new List<(VersionFile File, string Target)>();
        foreach (var f in version.Files)
        {
            var target = ResolveLocalPath(roots, f.Name)
                ?? throw new RestoreException(Resources.S.Format("History_VersionsNoFolder", f.Name));
            if (File.Exists(target) && Sha1(target) == f.Sha) continue;
            plan.Add((f, target));
        }
        if (plan.Count == 0) return new RestoreOutcome(0, "");

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var staging = Path.Combine(steam, "cloud_redirect", "restore_tmp", stamp);
        try
        {
            // 2. Download every file first; nothing on disk changes if any download fails.
            var items = plan.Select((p, i) => new CloudProviderClient.VersionDownload(
                p.File.Name, p.File.Sha, Path.Combine(staging, i.ToString(CultureInfo.InvariantCulture) + ".bin"))).ToList();
            var dl = await new CloudProviderClient().DownloadVersionsAsync(
                accountId.ToString(CultureInfo.InvariantCulture), appId.ToString(CultureInfo.InvariantCulture), items, cancel);
            if (!dl.Success) throw new RestoreException(dl.Error ?? "download failed");

            return await Task.Run(() =>
            {
                // 3. Back up the files about to be replaced, as a restorable snapshot.
                var backupRoot = SyncHistoryReader.GetLocalBackupsDir() ?? throw new RestoreException("Steam folder not found");
                var backupDir = Path.Combine(backupRoot, accountId.ToString(CultureInfo.InvariantCulture),
                    appId.ToString(CultureInfo.InvariantCulture), stamp + "-before-restore");
                var meta = new List<object>();
                foreach (var (file, target) in plan)
                {
                    if (!File.Exists(target)) continue;
                    var copy = Path.Combine(backupDir, file.Name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                    File.Copy(target, copy, overwrite: false);
                    meta.Add(new { name = file.Name, sha = Sha1(copy), path = target });
                }
                if (meta.Count > 0)
                    File.WriteAllText(Path.Combine(backupDir, ".snapshot.json"),
                        JsonSerializer.Serialize(new { files = meta }));

                // 4. Put the downloaded files in place and verify them.
                int restored = 0;
                for (int i = 0; i < plan.Count; i++)
                {
                    var (file, target) = plan[i];
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(items[i].OutPath, target, overwrite: true);
                    // A fresh write time, so Steam sees the file as changed on this PC.
                    File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                    if (Sha1(target) != file.Sha)
                        throw new RestoreException($"Restored file does not match the cloud version: {target}");
                    restored++;
                }
                return new RestoreOutcome(restored, meta.Count > 0 ? backupDir : "");
            }, cancel);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static string Sha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }
}
