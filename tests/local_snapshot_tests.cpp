#include "local_snapshot.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <chrono>
#include <string>
#include <unordered_map>

namespace fs = std::filesystem;
namespace LS = LocalSnapshot;

static int g_failures = 0;

#define CHECK(cond)                                                        \
    do {                                                                   \
        if (!(cond)) {                                                     \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
            ++g_failures;                                                  \
        }                                                                  \
    } while (0)

static fs::path g_root;

static std::vector<uint8_t> Sha(uint8_t b) { return std::vector<uint8_t>(20, b); }

static LS::LocalFile MakeFile(const std::string& name, const std::string& content, uint8_t sha) {
    fs::path p = g_root / "game" / name;
    fs::create_directories(p.parent_path());
    std::ofstream(p, std::ios::binary) << content;
    return {name, p.string(), Sha(sha)};
}

static std::string ReadFile(const fs::path& p) {
    std::ifstream f(p, std::ios::binary);
    return std::string((std::istreambuf_iterator<char>(f)), std::istreambuf_iterator<char>());
}

static size_t CountSnapshots() {
    size_t n = 0;
    std::error_code ec;
    for (auto& e : fs::directory_iterator(g_root / "cr" / "local_backups" / "7" / "42", ec))
        if (e.is_directory()) ++n;
    return n;
}

static void TestDecision() {
    std::vector<LS::LocalFile> local = {{"a.sav", "", Sha(1)}, {"b.sav", "", Sha(2)}};
    CHECK(!LS::CloudWouldChangeLocal(local, {{"a.sav", Sha(1)}, {"b.sav", Sha(2)}}));
    CHECK(!LS::CloudWouldChangeLocal(local, {{"a.sav", Sha(1)}, {"b.sav", Sha(2)}, {"new.sav", Sha(9)}}));
    CHECK(LS::CloudWouldChangeLocal(local, {{"a.sav", Sha(1)}, {"b.sav", Sha(3)}}));  // overwrite
    CHECK(LS::CloudWouldChangeLocal(local, {{"a.sav", Sha(1)}}));                     // b would go
    CHECK(!LS::CloudWouldChangeLocal({}, {{"a.sav", Sha(1)}}));                       // nothing local
}

static void TestTakeCopiesAndDedupes() {
    LS::SetKeep(5);
    std::vector<LS::LocalFile> files = {
        MakeFile("Saved/PBZSaveGame.sav", "slot", 1),
        MakeFile("Saved/1RealTime/x.sav", "rt", 2),
    };
    auto r = LS::Take((g_root / "cr").string(), 7, 42, files, "20261116-210000");
    CHECK(r.taken);
    CHECK(r.files == 2);
    CHECK(ReadFile(fs::path(r.dir) / "Saved" / "PBZSaveGame.sav") == "slot");
    CHECK(ReadFile(fs::path(r.dir) / "Saved" / "1RealTime" / "x.sav") == "rt");

    // Same names and content again: skipped.
    auto again = LS::Take((g_root / "cr").string(), 7, 42, files, "20261116-210100");
    CHECK(!again.taken);
    CHECK(again.skippedIdentical);
    CHECK(CountSnapshots() == 1);

    // Content changed: new snapshot; same second gets a unique folder.
    files[0] = MakeFile("Saved/PBZSaveGame.sav", "slot2", 3);
    auto third = LS::Take((g_root / "cr").string(), 7, 42, files, "20261116-210000");
    CHECK(third.taken);
    CHECK(third.dir != r.dir);
    CHECK(CountSnapshots() == 2);
}

static void TestPruneKeepsNewest() {
    LS::SetKeep(2);
    for (int i = 0; i < 4; i++) {
        std::vector<LS::LocalFile> files = {MakeFile("s.sav", "v" + std::to_string(i), (uint8_t)(10 + i))};
        LS::Take((g_root / "cr").string(), 7, 42, files, "20261117-00000" + std::to_string(i));
    }
    CHECK(CountSnapshots() == 2);
    CHECK(fs::exists(g_root / "cr" / "local_backups" / "7" / "42" / "20261117-000003"));
    CHECK(fs::exists(g_root / "cr" / "local_backups" / "7" / "42" / "20261117-000002"));
}

static void TestDisabledAndUnsafeNames() {
    LS::SetKeep(0);
    auto off = LS::Take((g_root / "cr").string(), 7, 99, {MakeFile("a.sav", "x", 1)}, "s");
    CHECK(!off.taken);
    LS::SetKeep(5);
    // A traversal name is skipped, never written outside the snapshot folder.
    LS::LocalFile evil{"../../escape.sav", MakeFile("ok.sav", "x", 4).fullPath, Sha(4)};
    auto r = LS::Take((g_root / "cr").string(), 7, 99, {evil}, "20261118-000000");
    CHECK(!fs::exists(g_root / "cr" / "local_backups" / "escape.sav"));
    CHECK(!fs::exists(g_root / "escape.sav"));
    (void)r;
}

static void TestMissingSourceLeavesNoPartialSnapshot() {
    LS::SetKeep(5);
    std::vector<LS::LocalFile> files = {MakeFile("a.sav", "x", 1),
                                        {"gone.sav", (g_root / "game" / "gone.sav").string(), Sha(5)}};
    auto r = LS::Take((g_root / "cr").string(), 7, 77, files, "20261119-000000");
    CHECK(!r.taken);
    CHECK(!r.error.empty());
    CHECK(!fs::exists(g_root / "cr" / "local_backups" / "7" / "77" / "20261119-000000"));
}


static fs::path AppDir(uint32_t app) { return g_root / "cr" / "local_backups" / "7" / std::to_string(app); }

static void TestDefaultKeepsOne() {
    LS::SetKeep(1);
    LS::Take((g_root / "cr").string(), 7, 50, {MakeFile("one.sav", "a", 21)}, "20261120-000000");
    LS::Take((g_root / "cr").string(), 7, 50, {MakeFile("one.sav", "b", 22)}, "20261120-000001");
    size_t n = 0;
    for (auto& e : fs::directory_iterator(AppDir(50))) if (e.is_directory()) ++n;
    CHECK(n == 1);
    CHECK(fs::exists(AppDir(50) / "20261120-000001"));
}

static void TestMetadataHasPaths() {
    LS::SetKeep(1);
    auto f = MakeFile("meta.sav", "m", 31);
    auto r = LS::Take((g_root / "cr").string(), 7, 51, {f}, "20261120-010000");
    std::string meta = ReadFile(fs::path(r.dir) / ".snapshot.json");
    CHECK(meta.find("\"name\":\"meta.sav\"") != std::string::npos);
    CHECK(meta.find("\"path\"") != std::string::npos);
}

static void TestRedundantDeleted() {
    LS::SetKeep(1);
    std::vector<LS::LocalFile> files = {MakeFile("r1.sav", "x", 41), MakeFile("r2.sav", "y", 42)};
    LS::Take((g_root / "cr").string(), 7, 52, files, "20261120-020000");
    // Cloud has only one of them with the same content: keep the snapshot.
    std::unordered_map<std::string, std::vector<uint8_t>> partial = {{"r1.sav", Sha(41)}};
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 52, partial) == 0);
    CHECK(fs::exists(AppDir(52) / "20261120-020000"));
    // Cloud has both, but one with other content (cloud overwrote it): keep.
    std::unordered_map<std::string, std::vector<uint8_t>> changed = {{"r1.sav", Sha(41)}, {"r2.sav", Sha(99)}};
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 52, changed) == 0);
    // Cloud now holds exactly what the snapshot has (local was uploaded): delete.
    std::unordered_map<std::string, std::vector<uint8_t>> same = {{"r1.sav", Sha(41)}, {"r2.sav", Sha(42)}, {"extra", Sha(1)}};
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 52, same) == 1);
    CHECK(!fs::exists(AppDir(52)));
}

static void TestExpired() {
    LS::SetKeep(1);
    LS::SetMaxAgeDays(30);
    auto r = LS::Take((g_root / "cr").string(), 7, 53, {MakeFile("old.sav", "o", 51)}, "20261001-000000");
    CHECK(r.taken);
    std::unordered_map<std::string, std::vector<uint8_t>> none;
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 53, none) == 0);  // fresh
    auto meta = fs::path(r.dir) / ".snapshot.json";
    fs::last_write_time(meta, fs::file_time_type::clock::now() - std::chrono::hours(24 * 31));
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 53, none) == 1);
    // 0 disables expiry.
    auto r2 = LS::Take((g_root / "cr").string(), 7, 54, {MakeFile("old2.sav", "o", 52)}, "20261001-000000");
    fs::last_write_time(fs::path(r2.dir) / ".snapshot.json",
                        fs::file_time_type::clock::now() - std::chrono::hours(24 * 400));
    LS::SetMaxAgeDays(0);
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 54, none) == 0);
    LS::SetMaxAgeDays(30);
}

static size_t CountDirs(const fs::path& d) {
    size_t n = 0;
    std::error_code ec;
    for (auto& e : fs::directory_iterator(d, ec)) if (e.is_directory()) ++n;
    return n;
}

static void TestRelinkSurvivesRotation() {
    LS::SetKeep(1);
    auto r = LS::TakeRelinkBackup((g_root / "cr").string(), 7, 60, {MakeFile("t.sav", "ticket", 61)},
                                  "20261123-195300", /*deleteAfterSync=*/false);
    CHECK(r.taken);
    CHECK(fs::path(r.dir).filename() == "20261123-195300-relink");
    CHECK(LS::IsRelinkBackupDir("20261123-195300-relink"));
    CHECK(!LS::IsRelinkBackupDir("20261123-195300"));
    std::string meta = ReadFile(fs::path(r.dir) / ".snapshot.json");
    CHECK(meta.find("\"relink\":true") != std::string::npos);
    CHECK(meta.find("\"deleteAfterSync\":false") != std::string::npos);
    CHECK(ReadFile(fs::path(r.dir) / "t.sav") == "ticket");

    // Regular snapshots rotate among themselves; the relink backup stays.
    LS::Take((g_root / "cr").string(), 7, 60, {MakeFile("t.sav", "a", 62)}, "20261123-200000");
    LS::Take((g_root / "cr").string(), 7, 60, {MakeFile("t.sav", "b", 63)}, "20261123-210000");
    CHECK(CountDirs(AppDir(60)) == 2);
    CHECK(fs::exists(r.dir));
    CHECK(fs::exists(AppDir(60) / "20261123-210000"));

    // Kept across a publish when not marked deleteAfterSync.
    std::unordered_map<std::string, std::vector<uint8_t>> cloud = {{"t.sav", Sha(99)}};
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 60, cloud) == 0);
    CHECK(fs::exists(r.dir));

    // Taken even with regular snapshots switched off.
    LS::SetKeep(0);
    auto off = LS::TakeRelinkBackup((g_root / "cr").string(), 7, 61, {MakeFile("u.sav", "u", 64)},
                                    "20261123-195300", false);
    CHECK(off.taken);
    LS::SetKeep(1);
}

static void TestRelinkDeleteAfterSync() {
    LS::SetKeep(1);
    auto r = LS::TakeRelinkBackup((g_root / "cr").string(), 7, 62, {MakeFile("v.sav", "old", 71)},
                                  "20261123-195300", /*deleteAfterSync=*/true);
    CHECK(r.taken);
    std::unordered_map<std::string, std::vector<uint8_t>> cloud = {{"v.sav", Sha(72)}};
    CHECK(LS::PruneRedundantAndExpired((g_root / "cr").string(), 7, 62, cloud) == 1);
    CHECK(!fs::exists(r.dir));
}

static void TestRelinkFailureLeavesNothing() {
    auto r = LS::TakeRelinkBackup((g_root / "cr").string(), 7, 63,
                                  {{"gone.sav", (g_root / "game" / "nope.sav").string(), Sha(5)}},
                                  "20261123-195300", false);
    CHECK(!r.taken);
    CHECK(!r.error.empty());
    CHECK(CountDirs(AppDir(63)) == 0);
    auto empty = LS::TakeRelinkBackup((g_root / "cr").string(), 7, 63, {}, "s", false);
    CHECK(!empty.taken);
}

int main() {
    std::error_code ec;
    g_root = fs::temp_directory_path() / "cr_local_snapshot_tests";
    fs::remove_all(g_root, ec);
    fs::create_directories(g_root, ec);

    TestDecision();
    TestTakeCopiesAndDedupes();
    TestPruneKeepsNewest();
    TestDisabledAndUnsafeNames();
    TestMissingSourceLeavesNoPartialSnapshot();
    TestDefaultKeepsOne();
    TestMetadataHasPaths();
    TestRedundantDeleted();
    TestExpired();
    TestRelinkSurvivesRotation();
    TestRelinkDeleteAfterSync();
    TestRelinkFailureLeavesNothing();

    fs::remove_all(g_root, ec);
    if (g_failures == 0) {
        std::printf("local_snapshot_tests: all tests passed\n");
        return 0;
    }
    std::printf("local_snapshot_tests: %d failure(s)\n", g_failures);
    return 1;
}
