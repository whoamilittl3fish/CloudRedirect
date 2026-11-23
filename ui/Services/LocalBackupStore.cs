using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace CloudRedirect.Services;

/// <summary>
/// Reads and restores the local save snapshots the DLL takes before a cloud sync
/// (src/common/local_snapshot.cpp):
///   Steam\cloud_redirect\local_backups\&lt;account&gt;\&lt;app&gt;\&lt;yyyyMMdd-HHmmss&gt;\
/// with .snapshot.json = {"files":[{"name","sha","path"}]}. Folders ending in
/// "-relink" (optionally "-relink-N") hold the copy the DLL set aside when it merged a
/// ticket-account save folder into the synced one; they are listed separately.
/// </summary>
internal static class LocalBackupStore
{
    internal sealed record SnapshotFile(string Name, string Sha, string Path);

    internal sealed record Snapshot(string Dir, DateTime Taken, IReadOnlyList<SnapshotFile> Files)
    {
        public bool IsRelink => IsRelinkDir(Dir);
    }

    internal sealed record RestoreResult(int Restored, int MovedAside, string ReplacedDir);

    private static bool IsRelinkDir(string dir) =>
        Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar)).Contains("-relink", StringComparison.Ordinal);

    /// <summary>The newest regular snapshot (relink: false) or relink backup (relink: true)
    /// for this account and app, or null.</summary>
    public static Snapshot? FindLatest(uint accountId, uint appId, bool relink = false)
    {
        var root = SyncHistoryReader.GetLocalBackupsDir();
        if (root == null) return null;
        var appDir = Path.Combine(root, accountId.ToString(CultureInfo.InvariantCulture),
            appId.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(appDir)) return null;

        foreach (var dir in Directory.GetDirectories(appDir)
                     .Where(d => IsRelinkDir(d) == relink)
                     .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
        {
            var snap = Read(dir);
            if (snap != null) return snap;
        }
        return null;
    }

    /// <summary>Every readable snapshot (regular and relink) for this account and app, newest first.</summary>
    public static List<Snapshot> FindAll(uint accountId, uint appId)
    {
        var list = new List<Snapshot>();
        var root = SyncHistoryReader.GetLocalBackupsDir();
        if (root == null) return list;
        var appDir = Path.Combine(root, accountId.ToString(CultureInfo.InvariantCulture),
            appId.ToString(CultureInfo.InvariantCulture));
        if (!Directory.Exists(appDir)) return list;
        foreach (var dir in Directory.GetDirectories(appDir))
        {
            var snap = Read(dir);
            if (snap != null) list.Add(snap);
        }
        return list.OrderByDescending(s => s.Taken).ToList();
    }

    private static Snapshot? Read(string dir)
    {
        try
        {
            var metaPath = Path.Combine(dir, ".snapshot.json");
            if (!File.Exists(metaPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (!doc.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                return null;
            var list = new List<SnapshotFile>();
            foreach (var f in files.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                var sha = f.TryGetProperty("sha", out var s) ? s.GetString() ?? "" : "";
                var path = f.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                // Snapshots from before paths were recorded cannot be restored automatically.
                if (name.Length == 0 || path.Length == 0 || !Path.IsPathFullyQualified(path)) return null;
                list.Add(new SnapshotFile(name, sha, path));
            }
            if (list.Count == 0) return null;
            var taken = DateTime.TryParseExact(Path.GetFileName(dir)[..Math.Min(15, Path.GetFileName(dir).Length)],
                "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var t)
                ? t : File.GetLastWriteTime(metaPath);
            return new Snapshot(dir, taken, list);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Puts the snapshot back. Nothing is deleted: every file it replaces, and every
    /// other file in the same save folders that the snapshot does not have, is moved
    /// into &lt;snapshot&gt;\.replaced\&lt;now&gt;\ first. Files are verified by SHA1.
    /// The game must not be running.
    /// </summary>
    public static RestoreResult Restore(Snapshot snap)
    {
        var replacedDir = Path.Combine(snap.Dir, ".replaced", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        var srcByPath = new Dictionary<string, SnapshotFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snap.Files) srcByPath[Path.GetFullPath(f.Path)] = f;

        // Save folders touched by the snapshot -> their cloud-relative folder name.
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in snap.Files)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(f.Path))!;
            var cloudDir = f.Name.Contains('/') ? f.Name[..f.Name.LastIndexOf('/')] : "";
            folders.TryAdd(dir, cloudDir);
        }

        int moved = 0;
        foreach (var (dir, cloudDir) in folders)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var current in Directory.GetFiles(dir))
            {
                var full = Path.GetFullPath(current);
                // Steam's own marker file, not part of the saves.
                if (Path.GetFileName(full).Equals("steam_autocloud.vdf", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (srcByPath.TryGetValue(full, out var want) && Sha1(full) == want.Sha)
                    continue; // already identical
                var rel = cloudDir.Length == 0 ? Path.GetFileName(full) : cloudDir + "/" + Path.GetFileName(full);
                var dst = Path.Combine(replacedDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(full, dst, overwrite: true);
                moved++;
            }
        }

        int restored = 0;
        foreach (var f in snap.Files)
        {
            var target = Path.GetFullPath(f.Path);
            if (File.Exists(target)) continue; // identical file left in place above
            var source = Path.Combine(snap.Dir, f.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            if (f.Sha.Length > 0 && Sha1(target) != f.Sha)
                throw new IOException($"Restored file does not match the backup: {target}");
            restored++;
        }
        return new RestoreResult(restored, moved, moved > 0 ? replacedDir : "");
    }

    /// <summary>Deletes the snapshot folder (and the app folder once empty).</summary>
    public static void Delete(Snapshot snap)
    {
        var root = SyncHistoryReader.GetLocalBackupsDir();
        var full = Path.GetFullPath(snap.Dir);
        // Only ever delete inside local_backups.
        if (root == null || !full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException($"Not a backup folder: {snap.Dir}");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        var appDir = Path.GetDirectoryName(full);
        if (appDir != null && Directory.Exists(appDir) && !Directory.EnumerateFileSystemEntries(appDir).Any())
            Directory.Delete(appDir);
    }

    private static string Sha1(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(fs)).ToLowerInvariant();
    }
}
