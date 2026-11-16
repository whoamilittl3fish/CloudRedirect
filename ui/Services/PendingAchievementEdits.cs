using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CloudRedirect.Services;

/// <summary>
/// Writes Steam\cloud_redirect\pending_edits.json, which the DLL applies on the next
/// Steam start (StatsStore ApplyPendingEdits) and then removes. Per app the value is
/// either a bare array of achievement locks (older tools) or an object:
///
///   { "&lt;accountId&gt;": { "&lt;appId&gt;": {
///       "locks": [ { "name": "...", "stat": 1, "bit": 0 } ],
///       "reset_stats": true,      // zero every stat and achievement
///       "reset_playtime": true    // clear playtime and last played
///   } } }
///
/// The DLL replaces the app's cloud entry for any of these, so values do not merge back.
/// It drops the whole file when both achievement and playtime sync are off.
/// </summary>
internal static class PendingAchievementEdits
{
    internal readonly record struct Lock(string Name, uint StatId, int Bit);

    internal readonly record struct AppEdits(HashSet<(uint stat, int bit)> Locks, bool ResetStats, bool ResetPlaytime)
    {
        public bool Any => Locks.Count > 0 || ResetStats || ResetPlaytime;
    }

    public static string? GetPath()
    {
        var steam = SteamDetector.FindSteamPath();
        return steam == null ? null : Path.Combine(steam, "cloud_redirect", "pending_edits.json");
    }

    public static bool IsAchievementSyncEnabled() => ConfigFlag("sync_achievements");
    public static bool IsPlaytimeSyncEnabled() => ConfigFlag("sync_playtime");

    private static bool ConfigFlag(string name)
    {
        try
        {
            var path = SteamDetector.GetConfigFilePath();
            if (!File.Exists(path)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public static AppEdits Get(string accountId, string appId)
    {
        var node = Load()[accountId]?[appId];
        if (node is JsonArray arr) return new AppEdits(ReadLocks(arr), false, false);
        if (node is JsonObject obj)
            return new AppEdits(ReadLocks(obj["locks"] as JsonArray), Flag(obj, "reset_stats"), Flag(obj, "reset_playtime"));
        return new AppEdits(new HashSet<(uint, int)>(), false, false);
    }

    /// <summary>Number of apps with anything pending, across all accounts.</summary>
    public static int CountApps()
    {
        int n = 0;
        foreach (var (_, apps) in Load())
            if (apps is JsonObject appObj) n += appObj.Count;
        return n;
    }

    /// <summary>Adds achievement locks, skipping ones already pending. Returns how many were added.</summary>
    public static int AddLocks(string accountId, string appId, IEnumerable<Lock> locks)
    {
        int added = 0;
        Update(accountId, appId, entry =>
        {
            var list = entry["locks"] as JsonArray;
            if (list == null) { list = new JsonArray(); entry["locks"] = list; }
            var existing = ReadLocks(list);
            foreach (var l in locks)
            {
                if (l.Bit < 0 || l.Bit > 31 || !existing.Add((l.StatId, l.Bit))) continue;
                list.Add(new JsonObject { ["name"] = l.Name, ["stat"] = l.StatId, ["bit"] = l.Bit });
                added++;
            }
            return added > 0;
        });
        return added;
    }

    /// <summary>Queues a full stats reset (all stats and achievements), a playtime reset, or both.</summary>
    public static void SetReset(string accountId, string appId, bool resetStats, bool resetPlaytime)
    {
        Update(accountId, appId, entry =>
        {
            bool changed = false;
            if (resetStats && !Flag(entry, "reset_stats")) { entry["reset_stats"] = true; changed = true; }
            if (resetPlaytime && !Flag(entry, "reset_playtime")) { entry["reset_playtime"] = true; changed = true; }
            // A full reset already clears every achievement; individual locks are redundant.
            if (resetStats && entry.Remove("locks")) changed = true;
            return changed;
        });
    }

    /// <summary>Drops everything pending for this app (before Steam starts).</summary>
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

    // Loads the file, gives the app's entry (always in object form) to `edit`, saves if it changed.
    private static void Update(string accountId, string appId, Func<JsonObject, bool> edit)
    {
        var path = GetPath() ?? throw new InvalidOperationException("Steam folder not found");
        var root = Load();
        if (root[accountId] is not JsonObject apps)
        {
            apps = new JsonObject();
            root[accountId] = apps;
        }
        JsonObject entry;
        switch (apps[appId])
        {
            case JsonObject o:
                entry = o;
                break;
            case JsonArray legacy:
                // Keep locks queued by another tool when switching to the object form.
                apps.Remove(appId);
                entry = new JsonObject { ["locks"] = legacy };
                apps[appId] = entry;
                break;
            default:
                entry = new JsonObject();
                apps[appId] = entry;
                break;
        }
        if (edit(entry)) Save(path, root);
    }

    private static bool Flag(JsonObject o, string name) =>
        o[name] is JsonValue v && v.TryGetValue(out bool b) && b;

    private static HashSet<(uint, int)> ReadLocks(JsonArray? list)
    {
        var set = new HashSet<(uint, int)>();
        if (list == null) return set;
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
