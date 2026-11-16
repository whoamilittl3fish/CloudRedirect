#include "blob_retention.h"

#include <cstdio>

static int g_failures = 0;

#define CHECK(cond)                                                        \
    do {                                                                   \
        if (!(cond)) {                                                     \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
            ++g_failures;                                                  \
        }                                                                  \
    } while (0)

namespace BR = BlobRetention;

static const std::string kSave = "1/2/blobs/Saved/PBZSaveGame.sav";
static const std::string kRotated = "1/2/blobs/Saved/1RealTime/20261116-030924-717.sav";

static std::string Blob(const std::string& folder, char c) {
    return folder + "/" + std::string(40, c);
}

static void TestKeepsNewestPerLiveFile() {
    std::vector<BR::Candidate> orphans = {
        {Blob(kSave, 'a'), 100},
        {Blob(kSave, 'b'), 300},
        {Blob(kSave, 'c'), 200},
        {Blob(kSave, 'd'), 50},
    };
    auto keep = BR::SelectHistoryToKeep(orphans, {kSave}, 2);
    CHECK(keep.size() == 2);
    CHECK(keep.count(Blob(kSave, 'b')) == 1);  // newest
    CHECK(keep.count(Blob(kSave, 'c')) == 1);  // second newest
    CHECK(keep.count(Blob(kSave, 'a')) == 0);
    CHECK(keep.count(Blob(kSave, 'd')) == 0);
}

static void TestDeletedFilesKeepNothing() {
    std::vector<BR::Candidate> orphans = {
        {Blob(kRotated, 'a'), 100},
        {Blob(kSave, 'b'), 100},
    };
    auto keep = BR::SelectHistoryToKeep(orphans, {kSave}, 2);
    CHECK(keep.count(Blob(kRotated, 'a')) == 0);  // file no longer exists
    CHECK(keep.count(Blob(kSave, 'b')) == 1);
}

static void TestZeroDisables() {
    std::vector<BR::Candidate> orphans = {{Blob(kSave, 'a'), 1}};
    CHECK(BR::SelectHistoryToKeep(orphans, {kSave}, 0).empty());
}

static void TestFewerThanLimit() {
    std::vector<BR::Candidate> orphans = {{Blob(kSave, 'a'), 1}};
    CHECK(BR::SelectHistoryToKeep(orphans, {kSave}, 5).size() == 1);
}

static void TestTiesAreDeterministic() {
    std::vector<BR::Candidate> orphans = {
        {Blob(kSave, 'a'), 7}, {Blob(kSave, 'b'), 7}, {Blob(kSave, 'c'), 7}};
    auto k1 = BR::SelectHistoryToKeep(orphans, {kSave}, 1);
    std::vector<BR::Candidate> reversed(orphans.rbegin(), orphans.rend());
    auto k2 = BR::SelectHistoryToKeep(reversed, {kSave}, 1);
    CHECK(k1 == k2);
    CHECK(k1.count(Blob(kSave, 'c')) == 1);
}

static void TestFolderOf() {
    CHECK(BR::BlobFolder(Blob(kSave, 'a')) == kSave);
    CHECK(BR::BlobFolder("noslash").empty());
}

int main() {
    TestKeepsNewestPerLiveFile();
    TestDeletedFilesKeepNothing();
    TestZeroDisables();
    TestFewerThanLimit();
    TestTiesAreDeterministic();
    TestFolderOf();
    if (g_failures == 0) {
        std::printf("blob_retention_tests: all tests passed\n");
        return 0;
    }
    std::printf("blob_retention_tests: %d failure(s)\n", g_failures);
    return 1;
}
