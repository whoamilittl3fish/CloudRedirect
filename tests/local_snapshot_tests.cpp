#include "local_snapshot.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>

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

    fs::remove_all(g_root, ec);
    if (g_failures == 0) {
        std::printf("local_snapshot_tests: all tests passed\n");
        return 0;
    }
    std::printf("local_snapshot_tests: %d failure(s)\n", g_failures);
    return 1;
}
