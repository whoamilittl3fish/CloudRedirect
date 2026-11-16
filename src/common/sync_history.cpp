#include "sync_history.h"
#include "file_util.h"
#include "json.h"

#include <ctime>
#include <filesystem>
#include <fstream>
#include <chrono>
#include <mutex>
#include <string>
#include <unordered_map>

namespace SyncHistory {

namespace {
std::mutex g_mutex;
std::filesystem::path g_dir;  // <cloudRoot>/history, empty until Init
}

void Init(const std::string& cloudRoot) {
    std::lock_guard<std::mutex> lock(g_mutex);
    g_dir = cloudRoot.empty() ? std::filesystem::path()
                              : FileUtil::Utf8ToPath(cloudRoot) / "history";
}

std::string FormatLine(uint64_t unixTime, const Event& ev) {
    Json::Value o = Json::Object();
    o.objVal["t"] = Json::Number((double)unixTime);
    o.objVal["app"] = Json::Number((double)ev.appId);
    o.objVal["ev"] = Json::String(ev.kind ? ev.kind : "");
    Json::Value ok;
    ok.type = Json::Type::Bool;
    ok.boolVal = ev.ok;
    o.objVal["ok"] = ok;
    o.objVal["cn"] = Json::Number((double)ev.cn);
    o.objVal["files"] = Json::Number((double)ev.files);
    o.objVal["deleted"] = Json::Number((double)ev.deleted);
    // Cap the detail so a pathological error string can't bloat the file.
    o.objVal["msg"] = Json::String(ev.msg.size() > 400 ? ev.msg.substr(0, 400) : ev.msg);
    return Json::Stringify(o);
}

void Record(uint32_t accountId, const Event& ev) {
    if (accountId == 0) return;
    try {
        const std::string line = FormatLine((uint64_t)std::time(nullptr), ev);

        std::lock_guard<std::mutex> lock(g_mutex);
        if (g_dir.empty()) return;
        std::error_code ec;
        std::filesystem::create_directories(g_dir, ec);
        if (ec) return;

        const std::string id = std::to_string(accountId);
        const auto cur = g_dir / (id + ".jsonl");
        auto size = std::filesystem::file_size(cur, ec);
        if (!ec && size >= kMaxBytes) {
            const auto old = g_dir / (id + ".1.jsonl");
            std::filesystem::remove(old, ec);
            std::filesystem::rename(cur, old, ec);
        }

        std::ofstream out(cur, std::ios::binary | std::ios::app);
        if (!out) return;
        out << line << '\n';
    } catch (...) {
        // History is diagnostics only; never let it break a sync path.
    }
}

bool RecordThrottled(uint32_t accountId, const Event& ev, uint32_t minIntervalSec) {
    if (accountId == 0) return false;
    static std::mutex throttleMutex;
    static std::unordered_map<std::string, std::chrono::steady_clock::time_point> last;
    const auto now = std::chrono::steady_clock::now();
    const std::string key = std::to_string(accountId) + "|" + std::to_string(ev.appId) + "|" +
                            (ev.kind ? ev.kind : "");
    {
        std::lock_guard<std::mutex> lock(throttleMutex);
        auto it = last.find(key);
        if (it != last.end() && now - it->second < std::chrono::seconds(minIntervalSec))
            return false;
        last[key] = now;
    }
    Record(accountId, ev);
    return true;
}

}  // namespace SyncHistory
