using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudRedirect.Services;

/// <summary>
/// Writes Steam\cloud_redirect\pending_edits.json, which the DLL applies on the next
/// Steam start (StatsStore ApplyPendingEdits): each listed achievement is locked and the
/// app's cloud stats entry is replaced, so the bit does not merge back in.
///
///   { "&lt;accountId&gt;": { "&lt;appId&gt;": [ { "name": "...", "stat": 1, "bit": 0 }, ... ] } }
///
/// The DLL drops the file when achievement sync is off, so callers check
/// <see cref="IsAchievementSyncEnabled"/> first.
/// </summary>
internal static class PendingAchievementEdits
{
    internal readonly record struct Lock(string Name, uint StatId, int Bit);

    public static string? GetPath()
    {
        var steam = SteamDetector.FindSteamPath();
        return steam == null ? null : Path.Combine(steam, "cloud_redirect", "pending_edits.json");
    }

    public static bool IsAchievementSyncEnabled()
    {
        try
        {
            var path = SteamDetector.GetConfigFilePath();
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("sync_achievements", out var v) &&
                   v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>(stat, bit) pairs already waiting for this account and app.</summary>
    public static HashSet<(uint stat, int bit)> GetPending(string accountId, string appId)
    {
        var result = new HashSet<(uint, int)>();
        var root = Load();
        if (root[accountId]?[appId] is not JsonArray locks) return result;
        foreach (var node in locks)
        {
            if (node is not JsonObject o) continue;
            if (o["stat"] is JsonValue s && s.TryGetValue(out uint stat) &&
                o["bit"] is JsonValue b && b.TryGetValue(out int bit))
                result.Add((stat, bit));
        }
        return result;
    }

    /// <summary>Total number of pending locks across all accounts and apps.</summary>
    public static int CountAll()
    {
        int n = 0;
        foreach (var (_, apps) in Load())
            if (apps is JsonObject appObj)
                foreach (var (_, locks) in appObj)
                    if (locks is JsonArray arr) n += arr.Count;
        return n;
    }

    /// <summary>Adds locks, skipping ones already pending. Returns how many were added.</summary>
    public static int Add(string accountId, string appId, IEnumerable<Lock> locks)
    {
        var path = GetPath() ?? throw new InvalidOperationException("Steam folder not found");
        var root = Load();
        if (root[accountId] is not JsonObject apps)
        {
            apps = new JsonObject();
            root[accountId] = apps;
        }
        if (apps[appId] is not JsonArray list)
        {
            list = new JsonArray();
            apps[appId] = list;
        }

        var existing = GetPendingFrom(list);
        int added = 0;
        foreach (var l in locks)
        {
            if (l.Bit < 0 || l.Bit > 31 || !existing.Add((l.StatId, l.Bit))) continue;
            list.Add(new JsonObject { ["name"] = l.Name, ["stat"] = l.StatId, ["bit"] = l.Bit });
            added++;
        }
        if (added > 0) Save(path, root);
        return added;
    }

    /// <summary>Drops this app's pending locks (the user changed their mind before Steam started).</summary>
    public static void Cancel(string accountId, string appId)
    {
        var path = GetPath();
        if (path == null) return;
        var root = Load();
        if (root[accountId] is not JsonObject apps || !apps.Remove(appId)) return;
        if (apps.Count == 0) root.Remove(accountId);
        if (root.Count == 0)
        {
            try { File.Delete(path); } catch { }
            return;
        }
        Save(path, root);
    }

    private static HashSet<(uint, int)> GetPendingFrom(JsonArray list)
    {
        var set = new HashSet<(uint, int)>();
        foreach (var node in list)
            if (node is JsonObject o && o["stat"] is JsonValue s && s.TryGetValue(out uint stat) &&
                o["bit"] is JsonValue b && b.TryGetValue(out int bit))
                set.Add((stat, bit));
        return set;
    }

    private static JsonObject Load()
    {
        var path = GetPath();
        try
        {
            if (path != null && File.Exists(path) &&
                JsonNode.Parse(File.ReadAllText(path)) is JsonObject o)
                return o;
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new JsonObject();
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString());
        File.Move(tmp, path, overwrite: true);
    }
}
