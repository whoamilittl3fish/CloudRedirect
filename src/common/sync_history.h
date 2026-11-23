#pragma once
#include <cstdint>
#include <string>

// Per-account sync history for the companion app: one JSON object per line in
// <cloudRoot>/history/<accountId>.jsonl, e.g.
//   {"t":1794815240,"app":4115450,"ev":"upload","ok":true,"cn":3,"files":16,"deleted":15,"msg":""}
// The file is rotated to <accountId>.1.jsonl past kMaxBytes, so at most two
// files per account exist. Writing is best-effort and never throws: a failed
// history write must not affect sync.
namespace SyncHistory {

// Event kinds (the "ev" field). Keep in sync with ui/Services/SyncHistoryReader.cs.
inline constexpr const char* kUpload           = "upload";            // batch published to the cloud
inline constexpr const char* kUploadFailed     = "upload_failed";     // batch could not be stored or published
inline constexpr const char* kConflict         = "conflict";          // user answered Steam's conflict dialog
inline constexpr const char* kCloudUnreachable = "cloud_unreachable"; // cloud state fetch failed or timed out
inline constexpr const char* kSaveNotSynced    = "save_not_synced";   // saves found where they are not synced
inline constexpr const char* kLocalBackup      = "local_backup";      // local saves copied aside before a cloud sync
inline constexpr const char* kSaveFolderRelinked = "save_folder_relinked"; // ticket-account save folder merged and linked

inline constexpr uint64_t kMaxBytes = 512 * 1024;

struct Event {
    uint32_t appId = 0;
    const char* kind = "";
    bool ok = true;
    uint64_t cn = 0;        // change number, 0 if not applicable
    uint32_t files = 0;     // files uploaded
    uint32_t deleted = 0;   // files deleted
    std::string msg;        // short human-readable detail or error
};

// cloudRoot is the cloud_redirect folder (with or without a trailing separator).
void Init(const std::string& cloudRoot);

// Appends one line for accountId. No-op before Init or when accountId is 0.
void Record(uint32_t accountId, const Event& ev);

// Like Record, but drops the event if the same (account, app, kind) was recorded
// less than minIntervalSec ago. For conditions that repeat on every Steam call,
// such as an unreachable cloud. Returns true if the event was written.
bool RecordThrottled(uint32_t accountId, const Event& ev, uint32_t minIntervalSec);

// Exposed for tests: the JSON line Record would write, without the newline.
std::string FormatLine(uint64_t unixTime, const Event& ev);

}  // namespace SyncHistory
