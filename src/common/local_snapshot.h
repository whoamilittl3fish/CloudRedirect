#pragma once
// Copies of an app's local save files taken right before a cloud sync could
// overwrite or delete them (the first full changelist Steam gets in a session).
//
//   <cloudRoot>/local_backups/<accountId>/<appId>/<yyyyMMdd-HHmmss>/<cloud filename>
//   <cloudRoot>/local_backups/<accountId>/<appId>/<stamp>/.snapshot.json
//
// Only the newest `keep` snapshots per app are kept, and a snapshot identical to
// the latest one is skipped. Best-effort: failures are logged, never thrown.
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

// Default 5; config "local_backups_kept". 0 turns snapshots off.
void SetKeep(uint32_t n);
uint32_t GetKeep();

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

// Local time as yyyyMMdd-HHmmss.
std::string NowStamp();

}  // namespace LocalSnapshot
