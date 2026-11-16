#pragma once
// Which unreferenced cloud blobs GarbageCollectBlobs keeps as save history.
//
// Content-addressed blobs live at "<acct>/<app>/blobs/<filename>/<sha1>": every
// version of a file is a sibling under the same <filename> folder. The cloud state
// references one of them (the current version); the rest used to be deleted by GC.
// Here the newest `keepPrevious` unreferenced versions of each file that still
// exists are spared, so a bad save or a wrong conflict choice can be undone.
//
// Files that no longer exist in the cloud state keep no history: games such as
// PBZ create and delete rotating backup files every session, and keeping versions
// of every deleted name would grow without bound.
#include <algorithm>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace BlobRetention {

struct Candidate {
    std::string path;           // full blob path ".../blobs/<filename>/<sha>"
    uint64_t modifiedTime = 0;  // provider timestamp; only the order matters
};

// Folder of a canonical blob path: everything before the last '/'.
inline std::string BlobFolder(const std::string& path) {
    size_t slash = path.rfind('/');
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

// Returns the subset of `unreferenced` to keep. `liveFolders` holds the folders
// (BlobFolder of the referenced blob) of files present in the current state.
inline std::unordered_set<std::string> SelectHistoryToKeep(
        const std::vector<Candidate>& unreferenced,
        const std::unordered_set<std::string>& liveFolders,
        uint32_t keepPrevious) {
    std::unordered_set<std::string> keep;
    if (keepPrevious == 0) return keep;

    std::unordered_map<std::string, std::vector<const Candidate*>> byFolder;
    for (const auto& c : unreferenced) {
        std::string folder = BlobFolder(c.path);
        if (folder.empty() || liveFolders.count(folder) == 0) continue;
        byFolder[folder].push_back(&c);
    }
    for (auto& [folder, list] : byFolder) {
        // Newest first; path breaks ties so the choice is deterministic.
        std::sort(list.begin(), list.end(), [](const Candidate* a, const Candidate* b) {
            if (a->modifiedTime != b->modifiedTime) return a->modifiedTime > b->modifiedTime;
            return a->path > b->path;
        });
        for (size_t i = 0; i < list.size() && i < keepPrevious; i++)
            keep.insert(list[i]->path);
    }
    return keep;
}

}  // namespace BlobRetention
