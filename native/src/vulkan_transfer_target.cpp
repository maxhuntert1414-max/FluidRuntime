#include "fluidruntime_vulkan_api.h"
#include "fluidruntime_hook_api.h"
#include "fluidruntime_transfer_api.h"

#include <array>
#include <charconv>
#include <cstring>
#include <filesystem>
#include <iomanip>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace {
struct Options {
    std::filesystem::path library;
    std::string mode{"baseline"};
    std::uint32_t candidates{128};
    std::uint32_t timeout_ms{10000};
    std::uint32_t hold_ms{50};
    bool validation{};
    bool hardware{true};
};
_Post_satisfies_(condition) void check(bool condition, const char* message) {
    if (!condition) throw std::runtime_error(message);
}
std::uint32_t number(const std::string& value, std::uint32_t low, std::uint32_t high) {
    std::uint32_t result{};
    const auto parsed = std::from_chars(value.data(), value.data() + value.size(), result);
    check(parsed.ec == std::errc{} && parsed.ptr == value.data() + value.size() &&
        result >= low && result <= high, "Numeric argument out of range");
    return result;
}
Options parse(int argc, char** argv) {
    Options result;
    for (int i = 1; i < argc; i += 2) {
        check(i + 1 < argc, "Every option requires a value");
        const std::string name = argv[i];
        const std::string value = argv[i + 1];
        if (name == "--library") result.library = value;
        else if (name == "--mode") result.mode = value;
        else if (name == "--candidate-count") result.candidates = number(value, 1, 128);
        else if (name == "--gpu-timeout-ms") result.timeout_ms = number(value, 1, 30000);
        else if (name == "--hold-ms") result.hold_ms = number(value, 1, 5000);
        else if (name == "--validation" || name == "--hardware") {
            check(value == "true" || value == "false", "Boolean argument must be true or false");
            if (name == "--validation") result.validation = value == "true";
            else result.hardware = value == "true";
        } else throw std::runtime_error("Unknown Vulkan target argument: " + name);
    }
    check(!result.library.empty(), "--library is required");
    check(result.mode == "baseline" || result.mode == "controlled" ||
        result.mode == "managed" || result.mode == "guards", "Unknown Vulkan mode");
    result.library = std::filesystem::absolute(result.library);
    return result;
}

struct Runtime {
    HMODULE module{};
    FluidVulkanApiV1 api{sizeof(FluidVulkanApiV1), fluid_vulkan_abi};
    FluidVulkanContext* context{};
    ~Runtime() {
        // If a fence timed out, do not unload code owning pending resources.
        // This owned child exits; the manager has a separate bounded timeout.
        if (context && FAILED(api.destroy(context, nullptr))) return;
        if (module) FreeLibrary(module);
    }
    void call(HRESULT result) {
        if (FAILED(result)) throw std::runtime_error(api.last_error());
    }
    FluidVulkanSnapshotV1 snapshot() {
        FluidVulkanSnapshotV1 snapshot{sizeof(FluidVulkanSnapshotV1), fluid_vulkan_abi};
        call(api.snapshot(context, &snapshot));
        return snapshot;
    }
};

std::string hash(const std::vector<unsigned char>& bytes) {
    std::uint64_t value = 14695981039346656037ULL;
    for (auto byte : bytes) { value ^= byte; value *= 1099511628211ULL; }
    std::ostringstream stream;
    stream << std::hex << std::setfill('0') << std::setw(16) << value;
    return stream.str();
}
std::string quote(const std::string& value) {
    std::ostringstream stream;
    stream << '"';
    for (unsigned char character : value) {
        if (character == '"' || character == '\\') stream << '\\' << character;
        else if (character < 32) stream << "\\u" << std::hex << std::setw(4) <<
            std::setfill('0') << static_cast<unsigned>(character) << std::dec;
        else stream << character;
    }
    stream << '"';
    return stream.str();
}
void publish_test_control(std::uint32_t count, bool expired) {
    const auto name = std::wstring(fluid_transfer_ring_name_prefix) + L"3-" +
        std::to_wstring(GetCurrentProcessId());
    const auto mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name.c_str());
    check(mapping != nullptr, "Cannot open test control mapping");
    auto* header = static_cast<FluidHookRingHeaderV1*>(MapViewOfFile(mapping,
        FILE_MAP_ALL_ACCESS, 0, 0, fluid_hook_ring_mapping_size));
    if (!header) { CloseHandle(mapping); throw std::runtime_error("Cannot map test control"); }
    auto* control = reinterpret_cast<FluidHookControlBlockV1*>(header + 1);
    LARGE_INTEGER now{};
    QueryPerformanceCounter(&now);
    control->expires_at_qpc = expired ? now.QuadPart - 1 : now.QuadPart +
        static_cast<LONG64>(header->qpc_frequency * 4);
    control->action_mask = fluid_hook_control_action_skip_redundant_transfer_buffer_copy;
    control->action_budget = count;
    MemoryBarrier();
    InterlockedExchange64(&control->published_epoch, 1);
    UnmapViewOfFile(header);
    CloseHandle(mapping);
}
}

int main(int argc, char** argv) {
    try {
        const auto options = parse(argc, argv);
        Runtime runtime;
        runtime.module = LoadLibraryExW(options.library.c_str(), nullptr,
            LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        check(runtime.module != nullptr, "Cannot load cooperative Vulkan library");
        const auto get_api = reinterpret_cast<FluidVulkanGetApiFunction>(
            GetProcAddress(runtime.module, "FluidVulkanGetApi"));
        check(get_api && SUCCEEDED(get_api(&runtime.api)), "Incompatible Vulkan library ABI");
        FluidVulkanOptionsV1 config{sizeof(FluidVulkanOptionsV1), fluid_vulkan_abi,
            fluid_vulkan_max_bytes, options.candidates, options.mode != "baseline",
            options.validation, options.hardware};
        runtime.call(runtime.api.create(&config, &runtime.context));
        std::array<std::array<std::vector<unsigned char>, 2>, fluid_vulkan_lanes> patterns;
        std::array<std::string, 4> pattern_hashes;
        for (std::uint32_t lane = 0; lane < fluid_vulkan_lanes; ++lane) {
            for (std::uint32_t slot = 0; slot < 2; ++slot) {
                auto& bytes = patterns[lane][slot];
                bytes.resize(fluid_vulkan_max_bytes);
                for (size_t i = 0; i < bytes.size(); ++i) {
                    bytes[i] = static_cast<unsigned char>((i * 31 + (i >> 8) + lane * 71 + slot * 43) & 255);
                }
                runtime.call(runtime.api.set_source(runtime.context, lane, slot, bytes.data(), bytes.size()));
                pattern_hashes[2 * lane + slot] = hash(bytes);
            }
        }
        bool guards_passed = true;
        std::vector<unsigned char> readback(fluid_vulkan_max_bytes);
        if (options.mode == "guards") {
            guards_passed &= FAILED(runtime.api.readback(runtime.context, 0, readback.data(), readback.size()));
            guards_passed &= FAILED(runtime.api.copy(runtime.context, 0, 0));
            guards_passed &= FAILED(runtime.api.set_source(runtime.context, 2, 0, readback.data(), readback.size()));
            guards_passed &= FAILED(runtime.api.set_source(runtime.context, 0, 2, readback.data(), readback.size()));
            guards_passed &= FAILED(runtime.api.set_source(runtime.context, 0, 0, readback.data(), readback.size() - 1));
            bool thread_rejected = false;
            std::thread wrong_thread([&] { thread_rejected = FAILED(runtime.api.begin(runtime.context)); });
            wrong_thread.join();
            guards_passed &= thread_rejected;
        }
        if (options.mode == "controlled" || options.mode == "guards") {
            publish_test_control(options.candidates, options.mode == "guards");
        }
        if (options.mode != "baseline") {
            const auto result = runtime.api.wait_control(runtime.context, 5000);
            if (options.mode == "guards") guards_passed &= FAILED(result);
            else runtime.call(result);
        }
        LARGE_INTEGER start{}, end{}, frequency{};
        QueryPerformanceFrequency(&frequency);
        QueryPerformanceCounter(&start);
        runtime.call(runtime.api.begin(runtime.context));
        if (options.mode == "guards") {
            guards_passed &= FAILED(runtime.api.begin(runtime.context));
            guards_passed &= FAILED(runtime.api.set_source(runtime.context, 0, 0, readback.data(), readback.size()));
            guards_passed &= FAILED(runtime.api.copy(runtime.context, 0, 2));
            guards_passed &= FAILED(runtime.api.copy(runtime.context, 2, 0));
            guards_passed &= FAILED(runtime.api.submit(runtime.context, options.timeout_ms));
        }
        for (std::uint32_t lane = 0; lane < fluid_vulkan_lanes; ++lane) {
            runtime.call(runtime.api.copy(runtime.context, lane, 0));
            const auto candidates = options.candidates / 2 + (lane == 0 ? options.candidates % 2 : 0);
            for (std::uint32_t i = 0; i < candidates; ++i) runtime.call(runtime.api.copy(runtime.context, lane, 0));
            runtime.call(runtime.api.copy(runtime.context, lane, 1));
            runtime.call(runtime.api.fill(runtime.context, lane, 0));
            runtime.call(runtime.api.copy(runtime.context, lane, 1));
            runtime.call(runtime.api.invalidate(runtime.context, lane));
            runtime.call(runtime.api.copy(runtime.context, lane, 1));
        }
        runtime.call(runtime.api.submit(runtime.context, options.timeout_ms));
        std::array<std::string, 2> final_hashes;
        for (std::uint32_t lane = 0; lane < fluid_vulkan_lanes; ++lane) {
            runtime.call(runtime.api.readback(runtime.context, lane, readback.data(), readback.size()));
            check(readback == patterns[lane][1], "Vulkan exact readback differs after mutation/invalidation");
            final_hashes[lane] = hash(readback);
        }
        QueryPerformanceCounter(&end);
        const auto measured = runtime.snapshot();
        const auto expected_skipped = (options.mode == "controlled" || options.mode == "managed") ?
            options.candidates : 0;
        check(measured.skipped_copies == expected_skipped && measured.applied_actions == expected_skipped &&
            measured.tracked_copies == options.candidates + 8ULL && measured.candidates == options.candidates &&
            measured.comparisons == options.candidates + 2ULL && measured.invalidations == 4 &&
            measured.completed_submissions == 1 && measured.submissions == 1,
            "Vulkan action/copy/invalidation counters violated the workload contract");

        runtime.call(runtime.api.revoke(runtime.context));
        runtime.call(runtime.api.begin(runtime.context));
        for (std::uint32_t lane = 0; lane < fluid_vulkan_lanes; ++lane) {
            runtime.call(runtime.api.copy(runtime.context, lane, 0));
            runtime.call(runtime.api.copy(runtime.context, lane, 0));
        }
        runtime.call(runtime.api.submit(runtime.context, options.timeout_ms));
        for (std::uint32_t lane = 0; lane < fluid_vulkan_lanes; ++lane) {
            runtime.call(runtime.api.readback(runtime.context, lane, readback.data(), readback.size()));
            check(readback == patterns[lane][0], "Vulkan rollback/reset readback differs");
        }
        const auto after = runtime.snapshot();
        const auto expected_events = options.candidates + (expected_skipped ? 27ULL : 26ULL);
        check(after.skipped_copies == measured.skipped_copies &&
            after.tracked_copies == measured.tracked_copies + 4 && after.completed_submissions == 2 &&
            after.events == expected_events && after.overruns == 0 && guards_passed &&
            after.validation_errors == 0 && after.validation_warnings == 0, "Vulkan rollback, guards, IPC or validation failed");
        Sleep(options.hold_ms);
        FluidVulkanSnapshotV1 final_snapshot{sizeof(FluidVulkanSnapshotV1), fluid_vulkan_abi};
        runtime.call(runtime.api.destroy(runtime.context, &final_snapshot));
        runtime.context = nullptr;
        check(final_snapshot.validation_errors == 0 && final_snapshot.validation_warnings == 0,
            "Vulkan teardown validation failed");
        std::cout << std::boolalpha << std::setprecision(12)
            << "{\n  \"schema\":\"fluidruntime-vulkan-transfer-v1\",\n"
            << "  \"process_id\":" << GetCurrentProcessId()
            << ",\n  \"backend\":3,\n  \"operation\":2,\n  \"target_owned\":true,\n"
            << "  \"cooperative_library\":true,\n  \"remote_injection\":false,\n"
            << "  \"mode\":" << quote(options.mode)
            << ",\n  \"self_published_control\":" << (options.mode == "controlled" || options.mode == "guards")
            << ",\n  \"actuation_enabled\":" << (expected_skipped != 0)
            << ",\n  \"device_name\":" << quote(measured.device_name)
            << ",\n  \"api_version\":" << measured.api_version
            << ",\n  \"device_type\":" << measured.device_type
            << ",\n  \"validation_enabled\":" << (measured.validation_enabled != 0)
            << ",\n  \"validation_errors\":" << final_snapshot.validation_errors
            << ",\n  \"validation_warnings\":" << final_snapshot.validation_warnings
            << ",\n  \"loader_messages\":" << final_snapshot.loader_messages
            << ",\n  \"upload_memory_flags\":" << measured.upload_memory_flags
            << ",\n  \"device_memory_flags\":" << measured.device_memory_flags
            << ",\n  \"readback_memory_flags\":" << measured.readback_memory_flags
            << ",\n  \"buffer_bytes\":" << fluid_vulkan_max_bytes
            << ",\n  \"lane_count\":2,\n  \"queue_count\":1,\n  \"fence_count\":1,\n"
            << "  \"source_snapshot_bytes\":" << 4 * fluid_vulkan_max_bytes
            << ",\n  \"candidate_count\":" << options.candidates
            << ",\n  \"tracked_copies\":" << measured.tracked_copies
            << ",\n  \"forwarded_copies\":" << measured.tracked_copies - measured.skipped_copies
            << ",\n  \"skipped_copies\":" << measured.skipped_copies
            << ",\n  \"avoided_logical_bytes\":" << measured.skipped_copies * fluid_vulkan_max_bytes
            << ",\n  \"physical_transfer_bytes_measured\":false,\n"
            << "  \"content_equivalent\":true,\n  \"rollback_verified\":true,\n"
            << "  \"source_mutation_guard_verified\":true,\n  \"invalidation_guards_verified\":true,\n"
            << "  \"state_guards_tested\":" << (options.mode == "guards")
            << ",\n  \"comparisons\":" << measured.comparisons
            << ",\n  \"invalidations\":" << measured.invalidations
            << ",\n  \"policy_epoch\":" << measured.policy_epoch
            << ",\n  \"policy_status\":" << measured.policy_status
            << ",\n  \"applied_actions\":" << measured.applied_actions
            << ",\n  \"events\":" << after.events
            << ",\n  \"overruns\":" << after.overruns
            << ",\n  \"completed_submissions\":" << after.completed_submissions
            << ",\n  \"rollback_forwarded_copies\":4,\n"
            << "  \"native_workload_us\":" << static_cast<double>(end.QuadPart - start.QuadPart) * 1e6 /
                static_cast<double>(frequency.QuadPart)
            << ",\n  \"submit_to_fence_us\":" << measured.submit_to_fence_us
            << ",\n  \"gpu_timestamp_valid\":" << (measured.gpu_timestamp_valid != 0)
            << ",\n  \"gpu_timestamp_valid_bits\":" << measured.timestamp_valid_bits
            << ",\n  \"gpu_us\":" << measured.gpu_us
            << ",\n  \"pattern_hashes\":[" << quote(pattern_hashes[0]) << ',' << quote(pattern_hashes[1]) << ','
            << quote(pattern_hashes[2]) << ',' << quote(pattern_hashes[3]) << ']'
            << ",\n  \"final_hashes\":[" << quote(final_hashes[0]) << ',' << quote(final_hashes[1]) << "]\n}\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "Owned Vulkan transfer failed: " << error.what() << '\n';
        return 1;
    }
}
