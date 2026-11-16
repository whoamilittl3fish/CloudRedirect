#include "sync_history.h"
#include "json.h"

#include <cstdio>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace SH = SyncHistory;

static int g_failures = 0;

#define CHECK(cond)                                                        \
    do {                                                                   \
        if (!(cond)) {                                                     \
            std::printf("FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
            ++g_failures;                                                  \
        }                                                                  \
    } while (0)

namespace {

constexpr uint32_t kAccount = 4242;

std::vector<std::string> ReadLines(const std::filesystem::path& p) {
    std::vector<std::string> lines;
    std::ifstream in(p, std::ios::binary);
    std::string line;
    while (std::getline(in, line))
        if (!line.empty()) lines.push_back(line);
    return lines;
}

void TestFormatLineRoundTrips() {
    SH::Event ev;
    ev.appId = 4115450;
    ev.kind = SH::kUpload;
    ev.ok = true;
    ev.cn = 3;
    ev.files = 16;
    ev.deleted = 15;
    ev.msg = "quote \" backslash \\ newline \n done";
    auto v = Json::Parse(SH::FormatLine(1794815240, ev));
    CHECK(v["t"].integer() == 1794815240);
    CHECK(v["app"].integer() == 4115450);
    CHECK(v["ev"].str() == "upload");
    CHECK(v["ok"].boolean() == true);
    CHECK(v["cn"].integer() == 3);
    CHECK(v["files"].integer() == 16);
    CHECK(v["deleted"].integer() == 15);
    CHECK(v["msg"].str() == ev.msg);
}

void TestLongMessageIsCapped() {
    SH::Event ev;
    ev.kind = SH::kUploadFailed;
    ev.ok = false;
    ev.msg.assign(5000, 'x');
    auto v = Json::Parse(SH::FormatLine(1, ev));
    CHECK(v["msg"].str().size() == 400);
    CHECK(v["ok"].boolean() == false);
}

void TestRecordAppendsOneLinePerEvent(const std::filesystem::path& root) {
    SH::Event a;
    a.appId = 1;
    a.kind = SH::kUpload;
    SH::Event b;
    b.appId = 2;
    b.kind = SH::kConflict;
    b.msg = "keep-local";
    SH::Record(kAccount, a);
    SH::Record(kAccount, b);
    SH::Record(0, a);  // no account: ignored
    auto lines = ReadLines(root / "history" / "4242.jsonl");
    CHECK(lines.size() == 2);
    if (lines.size() == 2) {
        CHECK(Json::Parse(lines[0])["app"].integer() == 1);
        CHECK(Json::Parse(lines[1])["ev"].str() == "conflict");
    }
    CHECK(!std::filesystem::exists(root / "history" / "0.jsonl"));
}

void TestThrottleDropsRepeats(const std::filesystem::path& root) {
    SH::Event ev;
    ev.appId = 77;
    ev.kind = SH::kCloudUnreachable;
    ev.ok = false;
    CHECK(SH::RecordThrottled(kAccount + 1, ev, 600) == true);
    CHECK(SH::RecordThrottled(kAccount + 1, ev, 600) == false);
    SH::Event other = ev;
    other.appId = 78;  // different app is not throttled
    CHECK(SH::RecordThrottled(kAccount + 1, other, 600) == true);
    CHECK(ReadLines(root / "history" / "4243.jsonl").size() == 2);
}

void TestRotationKeepsTwoFiles(const std::filesystem::path& root) {
    const auto cur = root / "history" / "9.jsonl";
    const auto old = root / "history" / "9.1.jsonl";
    {
        std::ofstream big(cur, std::ios::binary);
        big << std::string(SH::kMaxBytes, 'a') << '\n';
    }
    SH::Event ev;
    ev.appId = 5;
    ev.kind = SH::kUpload;
    SH::Record(9, ev);
    CHECK(std::filesystem::exists(old));
    CHECK(std::filesystem::file_size(old) > SH::kMaxBytes);
    auto lines = ReadLines(cur);
    CHECK(lines.size() == 1);

    // Second rotation replaces the old .1 file rather than failing.
    {
        std::ofstream big(cur, std::ios::binary | std::ios::app);
        big << std::string(SH::kMaxBytes, 'b') << '\n';
    }
    SH::Record(9, ev);
    CHECK(ReadLines(cur).size() == 1);
    CHECK(ReadLines(old).size() == 2);  // the one event line + the 'b' filler
}

void TestNoInitIsNoOp() {
    SH::Init("");
    SH::Event ev;
    ev.appId = 1;
    ev.kind = SH::kUpload;
    SH::Record(kAccount, ev);  // must not crash or write anywhere
}

}  // namespace

int main() {
    std::error_code ec;
    auto root = std::filesystem::temp_directory_path() / "cr_sync_history_tests";
    std::filesystem::remove_all(root, ec);
    std::filesystem::create_directories(root, ec);

    TestFormatLineRoundTrips();
    TestLongMessageIsCapped();
    SH::Init(root.string());
    TestRecordAppendsOneLinePerEvent(root);
    TestThrottleDropsRepeats(root);
    TestRotationKeepsTwoFiles(root);
    TestNoInitIsNoOp();

    std::filesystem::remove_all(root, ec);

    if (g_failures == 0) {
        std::printf("sync_history_tests: all tests passed\n");
        return 0;
    }
    std::printf("sync_history_tests: %d failure(s)\n", g_failures);
    return 1;
}
