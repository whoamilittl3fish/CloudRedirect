// pending_edits.json resets (StatsStore ApplyPendingEdits), end to end through SeedApps:
// reset_stats / reset_playtime objects, the legacy lock array, and the localconfig.vdf
// reconcile that must not refill a reset app's playtime on the next start.
#include "stats_store.h"
#include "metadata_sync.h"
#include "json.h"

#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <chrono>
#include <mutex>
#include <string>
#include <thread>
#include <unordered_map>

namespace fs = std::filesystem;

static int g_failures = 0;

#define CHECK(cond)                                                        \
    do {                                                                   \
        if (!(cond)) {                                                     \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
            ++g_failures;                                                  \
        }                                                                  \
    } while (0)

namespace {

constexpr uint32_t kAccount = 246500119;
constexpr uint32_t kResetBoth = 4115450;    // reset_stats + reset_playtime
constexpr uint32_t kResetPlaytime = 2638890;// reset_playtime only
constexpr uint32_t kLegacyLock = 3751260;   // legacy array: lock stat 1 bit 0
constexpr uint32_t kUntouched = 1245620;    // no edit; reconcile must still work

fs::path g_root;

void WriteFile(const fs::path& p, const std::string& text) {
    fs::create_directories(p.parent_path());
    std::ofstream(p, std::ios::binary) << text;
}

std::string ReadFile(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    return std::string((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}

StatsStore::AppStats MakeStats() {
    StatsStore::AppStats s{};
    s.crcStats = 0;
    StatsStore::AchievementBlock b{};
    b.statId = 1;
    b.bits = 0b101;
    b.unlockTimes[0] = 1794692862;
    b.unlockTimes[2] = 1794714651;
    s.achievements.push_back(b);
    s.stats.push_back({1, 0b101});
    s.stats.push_back({9, 42});
    s.playtime.perDevice["Z0SK"].windows = 300;
    s.playtime.minutesForever = 300;
    s.playtime.playtimeWindows = 300;
    s.playtime.lastPlayedTime = 1794774061;
    return s;
}

// Fresh process state, as on a Steam restart.
void Restart() {
    StatsStore::ResetForTesting();
    StatsStore::Init((g_root / "cloud_redirect").string(), (g_root / "steam").string());
    StatsStore::SetAccountIdProvider([] { return kAccount; });
    StatsStore::SetNamespacePredicate([](uint32_t) { return true; });
    StatsStore::ResetForAccountSwitch(kAccount);
}

void Seed() {
    StatsStore::SeedApps({kResetBoth, kResetPlaytime, kLegacyLock, kUntouched});
}

void WriteLocalConfig() {
    // Steam's own playtime for each app; the reconcile copies any shortfall into CR.
    std::string vdf =
        "\"UserLocalConfigStore\"\n{\n \"Software\"\n {\n  \"Valve\"\n  {\n   \"Steam\"\n   {\n"
        "    \"apps\"\n    {\n";
    for (uint32_t app : {kResetBoth, kResetPlaytime, kLegacyLock, kUntouched}) {
        vdf += "     \"" + std::to_string(app) + "\"\n     {\n"
               "      \"LastPlayed\"\t\t\"1794774061\"\n"
               "      \"Playtime\"\t\t\"577\"\n     }\n";
    }
    vdf += "    }\n   }\n  }\n }\n}\n";
    WriteFile(g_root / "steam" / "userdata" / std::to_string(kAccount) / "config" / "localconfig.vdf", vdf);
}

uint32_t UnlockedBits(const StatsStore::AppStats& s) {
    uint32_t bits = 0;
    for (const auto& a : s.achievements) bits |= a.bits;
    return bits;
}

uint32_t StatValue(const StatsStore::AppStats& s, uint32_t id) {
    for (const auto& st : s.stats)
        if (st.statId == id) return st.value;
    return 0xFFFFFFFFu;
}

void TestResets() {
    // Initial state: every app has two unlocks, stat 9 = 42 and 300 minutes.
    Restart();
    for (uint32_t app : {kResetBoth, kResetPlaytime, kLegacyLock, kUntouched})
        StatsStore::SaveAppStats(app, MakeStats());

    const std::string acct = std::to_string(kAccount);
    WriteFile(g_root / "cloud_redirect" / "pending_edits.json",
              "{\"" + acct + "\":{"
              "\"" + std::to_string(kResetBoth) + "\":{\"reset_stats\":true,\"reset_playtime\":true},"
              "\"" + std::to_string(kResetPlaytime) + "\":{\"reset_playtime\":true},"
              "\"" + std::to_string(kLegacyLock) + "\":[{\"name\":\"A\",\"stat\":1,\"bit\":0}]"
              "},\"999\":{\"1\":[]}}");
    WriteLocalConfig();

    // First start after the edits.
    Restart();
    Seed();

    auto both = StatsStore::Snapshot(kResetBoth);
    CHECK(UnlockedBits(both) == 0);
    CHECK(both.achievements.size() == 1);            // zeroed, not removed
    CHECK(both.achievements[0].unlockTimes[0] == 0);
    CHECK(StatValue(both, 9) == 0);
    CHECK(StatValue(both, 1) == 0);
    CHECK(both.playtime.minutesForever == 0);
    CHECK(both.playtime.lastPlayedTime == 0);

    auto pt = StatsStore::Snapshot(kResetPlaytime);
    CHECK(UnlockedBits(pt) == 0b101);                // achievements kept
    CHECK(StatValue(pt, 9) == 42);
    CHECK(pt.playtime.minutesForever == 0);

    auto legacy = StatsStore::Snapshot(kLegacyLock);
    CHECK(UnlockedBits(legacy) == 0b100);            // only bit 0 locked
    CHECK(StatValue(legacy, 9) == 42);
    CHECK(legacy.playtime.minutesForever >= 300);    // playtime untouched

    auto untouched = StatsStore::Snapshot(kUntouched);
    CHECK(UnlockedBits(untouched) == 0b101);
    CHECK(untouched.playtime.minutesForever == 577); // reconcile topped it up from Steam's 577

    // Our account's entry is consumed; another account's entry stays for them.
    auto note = Json::Parse(ReadFile(g_root / "cloud_redirect" / "pending_edits.json"));
    CHECK(!note.has(acct));
    CHECK(note.has("999"));

    auto marker = Json::Parse(ReadFile(g_root / "cloud_redirect" / "stats" / acct / "playtime_reset.json"));
    CHECK(marker["apps"].size() == 2);

    // Second start: Steam's localconfig.vdf still says 577 minutes. The reset apps
    // must stay at 0; the untouched app keeps its reconciled total.
    Restart();
    Seed();
    CHECK(StatsStore::Snapshot(kResetBoth).playtime.minutesForever == 0);
    CHECK(StatsStore::Snapshot(kResetPlaytime).playtime.minutesForever == 0);
    CHECK(UnlockedBits(StatsStore::Snapshot(kResetBoth)) == 0);
    CHECK(StatsStore::Snapshot(kUntouched).playtime.minutesForever == 577);
}

void TestIgnoredWhenSyncOff() {
    fs::remove_all(g_root / "cloud_redirect");
    Restart();
    StatsStore::SaveAppStats(kResetBoth, MakeStats());
    WriteFile(g_root / "cloud_redirect" / "pending_edits.json",
              "{\"" + std::to_string(kAccount) + "\":{\"" + std::to_string(kResetBoth) +
              "\":{\"reset_stats\":true}}}");
    MetadataSync::syncAchievements = false;
    MetadataSync::syncPlaytime = false;
    Restart();
    Seed();
    CHECK(!fs::exists(g_root / "cloud_redirect" / "pending_edits.json"));
    MetadataSync::syncAchievements = true;
    MetadataSync::syncPlaytime = true;
}


// Mirrors the platform pushAll (cloud_intercept.cpp): reset apps replace the cloud
// entry, others are merged with StatsStore::MergeAppStatsJson.
std::mutex g_cloudMutex;
std::unordered_map<uint32_t, std::string> g_cloud;
int g_pushes = 0;

void InstallFakeCloud() {
    StatsStore::SetCloudProvider(
        [](std::unordered_map<uint32_t, std::string>& out) {
            std::lock_guard<std::mutex> lock(g_cloudMutex);
            out = g_cloud;
            return true;
        },
        [](const std::unordered_map<uint32_t, std::string>& all) {
            auto reset = StatsStore::ConsumeResetApps();
            std::lock_guard<std::mutex> lock(g_cloudMutex);
            for (const auto& [app, json] : all) {
                auto it = g_cloud.find(app);
                g_cloud[app] = (reset.count(app) || it == g_cloud.end())
                    ? json : StatsStore::MergeAppStatsJson(it->second, json);
            }
            ++g_pushes;
        });
}

void WaitForPush(int before) {
    for (int i = 0; i < 200; i++) {
        {
            std::lock_guard<std::mutex> lock(g_cloudMutex);
            if (g_pushes > before) return;
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
}

void TestCloudRoundTrip() {
    fs::remove_all(g_root / "cloud_redirect");
    fs::remove_all(g_root / "steam" / "userdata");
    // The cloud already holds the old totals from another session.
    {
        StatsStore::AppStats old = MakeStats();
        Restart();
        StatsStore::SaveAppStats(kResetBoth, old);
        auto json = StatsStore::MergeAppStatsJson("", ReadFile(
            g_root / "cloud_redirect" / "stats" / std::to_string(kAccount) /
            (std::to_string(kResetBoth) + ".json")));
        std::lock_guard<std::mutex> lock(g_cloudMutex);
        g_cloud.clear();
        g_cloud[kResetBoth] = json;
    }
    // Local copy gone (e.g. a fresh install): only the cloud has the old values.
    fs::remove_all(g_root / "cloud_redirect" / "stats");
    WriteFile(g_root / "cloud_redirect" / "pending_edits.json",
              "{\"" + std::to_string(kAccount) + "\":{\"" + std::to_string(kResetBoth) +
              "\":{\"reset_stats\":true,\"reset_playtime\":true}}}");

    InstallFakeCloud();
    int before = g_pushes;
    Restart();
    InstallFakeCloud();
    StatsStore::SeedApps({kResetBoth});
    WaitForPush(before);

    StatsStore::AppStats cloudNow{};
    {
        std::lock_guard<std::mutex> lock(g_cloudMutex);
        CHECK(g_pushes > before);
        auto v = Json::Parse(g_cloud[kResetBoth]);
        CHECK(v["playtime"]["minutes_forever"].integer() == 0);
        bool anyBits = false;
        const auto& ach = v["achievements"];
        for (size_t i = 0; i < ach.size(); i++)
            if (ach[i]["bits"].integer() != 0) anyBits = true;
        CHECK(!anyBits);
    }

    // Next start pulls that cloud entry: still zero.
    Restart();
    InstallFakeCloud();
    StatsStore::SeedApps({kResetBoth});
    auto again = StatsStore::Snapshot(kResetBoth);
    CHECK(again.playtime.minutesForever == 0);
    CHECK(UnlockedBits(again) == 0);
}

}  // namespace

int main() {
    std::error_code ec;
    g_root = fs::temp_directory_path() / "cr_stats_reset_tests";
    fs::remove_all(g_root, ec);
    fs::create_directories(g_root / "steam" / "appcache" / "stats", ec);
    MetadataSync::syncAchievements = true;
    MetadataSync::syncPlaytime = true;

    TestResets();
    TestIgnoredWhenSyncOff();
    TestCloudRoundTrip();

    fs::remove_all(g_root, ec);
    if (g_failures == 0) {
        std::printf("stats_reset_tests: all tests passed\n");
        return 0;
    }
    std::printf("stats_reset_tests: %d failure(s)\n", g_failures);
    return 1;
}
