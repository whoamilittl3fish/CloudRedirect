using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace CloudRedirect.Services;

/// <summary>One line of cloud_redirect\history\&lt;account&gt;.jsonl, written by the DLL
/// (src/common/sync_history.cpp).</summary>
internal sealed record SyncHistoryEvent(
    DateTimeOffset Time,
    uint AccountId,
    uint AppId,
    string Kind,
    bool Ok,
    ulong ChangeNumber,
    uint Files,
    uint Deleted,
    string Message);

internal static class SyncHistoryReader
{
    // Event kinds; keep in sync with src/common/sync_history.h.
    public const string Upload = "upload";
    public const string UploadFailed = "upload_failed";
    public const string Conflict = "conflict";
    public const string CloudUnreachable = "cloud_unreachable";
    public const string SaveNotSynced = "save_not_synced";

    public static string? GetHistoryDir()
    {
        var steam = SteamDetector.FindSteamPath();
        return steam == null ? null : Path.Combine(steam, "cloud_redirect", "history");
    }

    /// <summary>All events from every account's current and rotated file, oldest first.
    /// Malformed lines are skipped; the DLL may be appending while we read.</summary>
    public static List<SyncHistoryEvent> ReadAll()
    {
        var result = new List<SyncHistoryEvent>();
        var dir = GetHistoryDir();
        if (dir == null || !Directory.Exists(dir)) return result;

        foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
        {
            var name = Path.GetFileName(path);
            var idPart = name.Split('.')[0];
            if (!uint.TryParse(idPart, NumberStyles.None, CultureInfo.InvariantCulture, out var account))
                continue;
            foreach (var line in ReadLinesShared(path))
            {
                var ev = ParseLine(line, account);
                if (ev != null) result.Add(ev);
            }
        }
        return result.OrderBy(e => e.Time).ToList();
    }

    private static IEnumerable<string> ReadLinesShared(string path)
    {
        var lines = new List<string>();
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null)
                if (line.Length > 0) lines.Add(line);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return lines;
    }

    internal static SyncHistoryEvent? ParseLine(string line, uint account)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return null;
            long t = o.TryGetProperty("t", out var tv) && tv.TryGetInt64(out var tl) ? tl : 0;
            uint app = o.TryGetProperty("app", out var av) && av.TryGetUInt32(out var al) ? al : 0;
            string kind = o.TryGetProperty("ev", out var ev) && ev.ValueKind == JsonValueKind.String
                ? ev.GetString() ?? "" : "";
            if (t <= 0 || app == 0 || kind.Length == 0) return null;
            bool ok = o.TryGetProperty("ok", out var okv) && okv.ValueKind == JsonValueKind.True;
            ulong cn = o.TryGetProperty("cn", out var cv) && cv.TryGetUInt64(out var cl) ? cl : 0;
            uint files = o.TryGetProperty("files", out var fv) && fv.TryGetUInt32(out var fl) ? fl : 0;
            uint deleted = o.TryGetProperty("deleted", out var dv) && dv.TryGetUInt32(out var dl) ? dl : 0;
            string msg = o.TryGetProperty("msg", out var mv) && mv.ValueKind == JsonValueKind.String
                ? mv.GetString() ?? "" : "";
            return new SyncHistoryEvent(DateTimeOffset.FromUnixTimeSeconds(t), account, app, kind,
                ok, cn, files, deleted, msg);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
