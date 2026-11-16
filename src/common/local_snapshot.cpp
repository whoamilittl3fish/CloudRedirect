#include "local_snapshot.h"
#include "file_util.h"
#include "json.h"

#include <algorithm>
#include <atomic>
#include <ctime>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <sstream>

namespace fs = std::filesystem;

namespace LocalSnapshot {

namespace {
std::atomic<uint32_t> g_keep{5};
std::mutex g_mutex;

std::string ShaHex(const std::vector<uint8_t>& sha) {
    static const char* hex = "0123456789abcdef";
    std::string out;
    out.reserve(sha.size() * 2);
    for (uint8_t b : sha) {
        out.push_back(hex[b >> 4]);
        out.push_back(hex[b & 15]);
    }
    return out;
}

// name -> sha hex, the identity of a snapshot.
std::string Fingerprint(const std::vector<LocalFile>& files) {
    std::vector<std::pair<std::string, std::string>> pairs;
    pairs.reserve(files.size());
    for (const auto& f : files) pairs.emplace_back(f.cloudName, ShaHex(f.sha));
    std::sort(pairs.begin(), pairs.end());
    Json::Value arr = Json::Array();
    for (const auto& [name, sha] : pairs) {
        Json::Value o = Json::Object();
        o.objVal["name"] = Json::String(name);
        o.objVal["sha"] = Json::String(sha);
        arr.arrVal.push_back(std::move(o));
    }
    Json::Value root = Json::Object();
    root.objVal["files"] = std::move(arr);
    return Json::Stringify(root);
}

// Snapshot folders, oldest first (names are sortable stamps).
std::vector<fs::path> ListSnapshots(const fs::path& appDir) {
    std::vector<fs::path> dirs;
    std::error_code ec;
    for (fs::directory_iterator it(appDir, ec), end; !ec && it != end; it.increment(ec)) {
        std::error_code dec;
        if (it->is_directory(dec) && !dec) dirs.push_back(it->path());
    }
    std::sort(dirs.begin(), dirs.end());
    return dirs;
}

std::string ReadText(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    return std::string((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}

bool SafeRelative(const std::string& name) {
    if (name.empty() || name.front() == '/' || name.front() == '\\') return false;
    if (name.find(':') != std::string::npos) return false;
    std::stringstream ss(name);
    std::string part;
    while (std::getline(ss, part, '/'))
        if (part == ".." || part.find('\\') != std::string::npos) return false;
    return true;
}
}  // namespace

void SetKeep(uint32_t n) { g_keep.store((std::min)(n, 50u)); }
uint32_t GetKeep() { return g_keep.load(); }

bool CloudWouldChangeLocal(const std::vector<LocalFile>& local,
                           const std::unordered_map<std::string, std::vector<uint8_t>>& cloudShas) {
    for (const auto& f : local) {
        auto it = cloudShas.find(f.cloudName);
        if (it == cloudShas.end() || it->second != f.sha) return true;
    }
    return false;
}

std::string NowStamp() {
    std::time_t t = std::time(nullptr);
    std::tm tm{};
#ifdef _WIN32
    localtime_s(&tm, &t);
#else
    localtime_r(&t, &tm);
#endif
    char buf[32];
    std::strftime(buf, sizeof(buf), "%Y%m%d-%H%M%S", &tm);
    return buf;
}

Result Take(const std::string& cloudRoot, uint32_t accountId, uint32_t appId,
            const std::vector<LocalFile>& files, const std::string& stamp) {
    Result r;
    const uint32_t keep = GetKeep();
    if (keep == 0 || files.empty() || cloudRoot.empty() || accountId == 0 || appId == 0)
        return r;

    std::lock_guard<std::mutex> lock(g_mutex);
    try {
        const fs::path appDir = FileUtil::Utf8ToPath(cloudRoot) / "local_backups" /
                                std::to_string(accountId) / std::to_string(appId);
        const std::string fingerprint = Fingerprint(files);

        auto existing = ListSnapshots(appDir);
        if (!existing.empty() &&
            ReadText(existing.back() / ".snapshot.json") == fingerprint) {
            r.skippedIdentical = true;
            return r;
        }

        // Unique folder even if two snapshots land in the same second.
        fs::path dir = appDir / stamp;
        for (int i = 2; fs::exists(dir); i++)
            dir = appDir / (stamp + "-" + std::to_string(i));

        std::error_code ec;
        fs::create_directories(dir, ec);
        if (ec) {
            r.error = "cannot create " + FileUtil::PathToUtf8(dir) + ": " + ec.message();
            return r;
        }
        size_t copied = 0;
        for (const auto& f : files) {
            if (!SafeRelative(f.cloudName)) continue;
            fs::path dst = dir;
            std::stringstream ss(f.cloudName);
            std::string part;
            while (std::getline(ss, part, '/'))
                if (!part.empty()) dst /= FileUtil::Utf8ToPath(part);
            fs::create_directories(dst.parent_path(), ec);
            ec.clear();
            fs::copy_file(FileUtil::Utf8ToPath(f.fullPath), dst,
                          fs::copy_options::overwrite_existing, ec);
            if (ec) {
                // A partial snapshot is worse than none: it would look complete.
                fs::remove_all(dir, ec);
                r.error = "cannot copy " + f.fullPath;
                return r;
            }
            ++copied;
        }
        {
            std::ofstream meta(dir / ".snapshot.json", std::ios::binary);
            meta << fingerprint;
        }

        // Prune: keep the newest `keep` snapshot folders.
        auto all = ListSnapshots(appDir);
        while (all.size() > keep) {
            fs::remove_all(all.front(), ec);
            all.erase(all.begin());
        }

        r.taken = true;
        r.files = copied;
        r.dir = FileUtil::PathToUtf8(dir);
    } catch (const std::exception& e) {
        r.error = e.what();
    }
    return r;
}

}  // namespace LocalSnapshot
