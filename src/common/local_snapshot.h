#pragma once
// Copies of an app's local save files taken right before a cloud sync could
// overwrite or delete them (the first full changelist Steam gets in a session).
//
//   <cloudRoot>/local_backups/<accountId>/<appId>/<yyyyMMdd-HHmmss>/<cloud filename>
//   <cloudRoot>/local_backups/<accountId>/<appId>/<stamp>/.snapshot.json
//
// Only the newest `keep` snapshots per app are kept (default 1), a snapshot identical
// to the latest one is skipped, a snapshot whose every file the cloud now holds with
// the same content is deleted as redundant, and snapshots older than `maxAgeDays`
// expire. .snapshot.json lists each file's cloud name, SHA1 and original path, which
// the companion app uses to restore. Best-effort: failures are logged, never thrown.
#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

namespace LocalSnapshot {

struct LocalFile {
    std::string cloudName;          // cloud-relative filename ("ProjectPBZ/.../PBZSaveGame.sav")
    std::string fullPath;           // absolute path on disk
    std::vector<uint8_t> sha;       // SHA1 of the local content
};

// Default 1; config "local_backups_kept". 0 turns snapshots off.
void SetKeep(uint32_t n);
uint32_t GetKeep();

// Default 30; config "local_backups_max_days". 0 disables expiry.
void SetMaxAgeDays(uint32_t days);
uint32_t GetMaxAgeDays();

// True if serving `cloudShas` could change something on disk: a local file whose
// cloud copy has different content, or that the cloud does not have at all.
bool CloudWouldChangeLocal(const std::vector<LocalFile>& local,
                           const std::unordered_map<std::string, std::vector<uint8_t>>& cloudShas);

struct Result {
    bool taken = false;
    bool skippedIdentical = false;  // same files and content as the latest snapshot
    std::string dir;                // snapshot folder when taken
    size_t files = 0;
    std::string error;
};

// Copies `files` into a new snapshot folder named `stamp` and prunes old ones.
Result Take(const std::string& cloudRoot, uint32_t accountId, uint32_t appId,
            const std::vector<LocalFile>& files, const std::string& stamp);

// Backup of the losing copy when the DLL merges two save folders of one game
// (TicketLink). Stored as <stamp>-relink with the same layout and .snapshot.json
// (plus "relink":true), so the app can restore or delete it. Taken even when
// local_backups_kept is 0, never skipped as identical, never rotated out by Take and
// never pruned as redundant; it only expires after maxAgeDays or is deleted in the app.
Result TakeRelinkBackup(const std::string& cloudRoot, uint32_t accountId, uint32_t appId,
                        const std::vector<LocalFile>& files, const std::string& stamp);

// True for a folder written by TakeRelinkBackup.
bool IsRelinkBackupDir(const std::string& folderName);

// Deletes snapshots of this app whose files the cloud already holds with identical
// content (cloudShas: cloud filename -> SHA1), then expired ones. Returns how many
// snapshot folders were removed. Call after a successful cloud publish. Relink
// backups are only expired here.
size_t PruneRedundantAndExpired(const std::string& cloudRoot, uint32_t accountId, uint32_t appId,
                                const std::unordered_map<std::string, std::vector<uint8_t>>& cloudShas);

// Local time as yyyyMMdd-HHmmss.
std::string NowStamp();

}  // namespace LocalSnapshot
