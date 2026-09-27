#include "fluid_memory_arena.h"
#include "memory_arena_windows.h"
#include <d3d12.h>
#include <wrl/client.h>
#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstring>
#include <iostream>
#include <stdexcept>
#include <string_view>
#include <thread>
#include <vector>

extern "C" int fluid_memory_c_smoke(void);
namespace {
using fluid::memory::windows::creation_time;
using fluid::memory::windows::file_hash;
using fluid::memory::windows::Handle;
using fluid::memory::windows::image_path;
constexpr std::uint64_t payload = 16384;
unsigned checks{};
void check(bool valid) {
    ++checks;
    if (!valid) {
        throw std::runtime_error("Arena check " + std::to_string(checks) + " failed");
    }
}
void ok(fma_status status) { check(status == FMA_OK); }
_Ret_notnull_ unsigned char *span_bytes(const fma_span &span) {
    if (!span.data) {
        throw std::runtime_error("Successful acquisition returned a null span");
    }
    return static_cast<unsigned char *>(span.data);
}
fma_metrics metrics(fma_arena arena) {
    fma_metrics result{};
    ok(fma_get_metrics(arena, &result, sizeof(result)));
    return result;
}
fma_arena create(bool hardware, uint64_t budget = FMA_MAX_BYTES, uint32_t timeout = 5000) {
    const fma_options options{sizeof(fma_options), FMA_ABI_VERSION,
                              static_cast<uint32_t>(hardware ? FMA_HARDWARE : FMA_WARP), timeout, budget};
    fma_arena result{};
    ok(fma_create(&options, &result));
    return result;
}
unsigned char pattern(std::uint64_t offset, unsigned seed) {
    return static_cast<unsigned char>((offset * 131 + (offset >> 7) + seed) & 255);
}
void write(fma_arena arena, fma_buffer buffer, unsigned seed) {
    fma_span span{};
    ok(fma_acquire(arena, buffer, 0, payload, FMA_HOST_WRITE, &span));
    auto *data = span_bytes(span);
    for (std::uint64_t i = 0; i < payload; ++i) {
        data[i] = pattern(i, seed);
    }
    ok(fma_end(arena, span.lease, 1));
}
void verify(fma_arena arena, fma_buffer buffer, unsigned seed) {
    fma_span span{};
    ok(fma_acquire(arena, buffer, 0, payload, FMA_HOST_READ, &span));
    const auto *data = span_bytes(span);
    bool equal = true;
    for (std::uint64_t i = 0; i < payload; ++i) {
        if (data[i] != pattern(i, seed)) {
            equal = false;
            break;
        }
    }
    ok(fma_end(arena, span.lease, 1));
    check(equal);
}

class HandleList {
    std::vector<unsigned char> storage_;

  public:
    explicit HandleList(std::array<HANDLE, 2> &handles) {
        SIZE_T bytes{};
        InitializeProcThreadAttributeList(nullptr, 1, 0, &bytes);
        check(bytes && bytes <= 65536);
        storage_.resize(bytes);
        check(InitializeProcThreadAttributeList(get(), 1, 0, &bytes) != FALSE);
        if (!UpdateProcThreadAttribute(get(), 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles.data(),
                                       sizeof(handles), nullptr, nullptr)) {
            DeleteProcThreadAttributeList(get());
            throw std::runtime_error("Handle allowlist failed");
        }
    }
    ~HandleList() { DeleteProcThreadAttributeList(get()); }
    LPPROC_THREAD_ATTRIBUTE_LIST get() {
        return reinterpret_cast<LPPROC_THREAD_ATTRIBUTE_LIST>(storage_.data());
    }
};
class Child {
  public:
    Handle process, input, output;
    Child() {
        SECURITY_ATTRIBUTES security{sizeof(security), nullptr, TRUE};
        HANDLE read{}, write{};
        check(CreatePipe(&read, &write, &security, 4096) != FALSE);
        Handle child_input(read);
        input = Handle(write);
        check(SetHandleInformation(input.get(), HANDLE_FLAG_INHERIT, 0) != FALSE);
        check(CreatePipe(&read, &write, &security, 4096) != FALSE);
        output = Handle(read);
        Handle child_output(write);
        check(SetHandleInformation(output.get(), HANDLE_FLAG_INHERIT, 0) != FALSE);
        std::array<HANDLE, 2> inherited{child_input.get(), child_output.get()};
        HandleList attributes(inherited);
        const auto executable = image_path(GetCurrentProcess());
        auto command = L"\"" + executable + L"\" --reader";
        STARTUPINFOEXW startup{};
        startup.StartupInfo.cb = sizeof(startup);
        startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES;
        startup.StartupInfo.hStdInput = child_input.get();
        startup.StartupInfo.hStdOutput = startup.StartupInfo.hStdError = child_output.get();
        startup.lpAttributeList = attributes.get();
        PROCESS_INFORMATION information{};
        check(CreateProcessW(executable.c_str(), command.data(), nullptr, nullptr, TRUE,
                             CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, nullptr, nullptr,
                             &startup.StartupInfo, &information) != FALSE);
        process = Handle(information.hProcess);
        Handle thread(information.hThread);
    }
    ~Child() {
        if (WaitForSingleObject(process.get(), 0) == WAIT_TIMEOUT) {
            TerminateProcess(process.get(), 90); // Only this lab's directly-created child.
            WaitForSingleObject(process.get(), 5000);
        }
    }
    fma_reader_hello hello() {
        const auto deadline = GetTickCount64() + 5000;
        DWORD available{};
        while (GetTickCount64() < deadline) {
            check(PeekNamedPipe(output.get(), nullptr, 0, nullptr, &available, nullptr) != FALSE);
            if (available >= sizeof(fma_reader_hello)) {
                break;
            }
            Sleep(1);
        }
        check(available >= sizeof(fma_reader_hello));
        fma_reader_hello result{};
        DWORD read{};
        check(ReadFile(output.get(), &result, sizeof(result), &read, nullptr) && read == sizeof(result));
        return result;
    }
    fma_peer identity() {
        fma_peer result{};
        result.process_handle = reinterpret_cast<std::uintptr_t>(process.get());
        result.creation_time = creation_time(process.get());
        result.pid = GetProcessId(process.get());
        Handle file(CreateFileW(image_path(process.get()).c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr,
                                OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr));
        const auto hash = file_hash(file.get());
        std::memcpy(result.image_sha256, hash.data(), hash.size());
        return result;
    }
    void send(const fma_shared_read &descriptor) {
        DWORD written{};
        check(WriteFile(input.get(), &descriptor, sizeof(descriptor), &written, nullptr) &&
              written == sizeof(descriptor));
    }
    void wait(DWORD expected) {
        check(WaitForSingleObject(process.get(), 5000) == WAIT_OBJECT_0);
        DWORD code{};
        check(GetExitCodeProcess(process.get(), &code) && code == expected);
    }
};

int reader() {
    const fma_reader_hello hello{FMA_LOCAL_MAGIC, FMA_ABI_VERSION, 0, FMA_READ_CAPABILITY};
    DWORD count{};
    check(WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), &hello, sizeof(hello), &count, nullptr) &&
          count == sizeof(hello));
    fma_shared_read descriptor{};
    check(ReadFile(GetStdHandle(STD_INPUT_HANDLE), &descriptor, sizeof(descriptor), &count, nullptr) &&
          count == sizeof(descriptor));
    check(descriptor.magic == FMA_LOCAL_MAGIC && descriptor.version == 1 && descriptor.opcode == 1 &&
          descriptor.size == sizeof(descriptor) && descriptor.arena && descriptor.buffer &&
          descriptor.lease && descriptor.content_version && descriptor.bytes == payload &&
          descriptor.mapping_handle && descriptor.mapping_handle < UINTPTR_MAX);
    Handle mapping(reinterpret_cast<HANDLE>(descriptor.mapping_handle));
    auto *writable = MapViewOfFile(mapping.get(), FILE_MAP_WRITE, 0, 0, payload);
    if (writable) {
        UnmapViewOfFile(writable);
        throw std::runtime_error("Reader received writable mapping");
    }
    check(GetLastError() == ERROR_ACCESS_DENIED);
    auto *data =
        static_cast<const unsigned char *>(MapViewOfFile(mapping.get(), FILE_MAP_READ, 0, 0, payload));
    if (!data) {
        throw std::runtime_error("Read-only peer map failed");
    }
    bool equal = true;
    for (std::uint64_t i = 0; i < payload; ++i) {
        if (data[i] != pattern(i, 42)) {
            equal = false;
            break;
        }
    }
    UnmapViewOfFile(data);
    check(equal);
    return 0;
}

void guards(bool hardware) {
    check(fluid_memory_c_smoke() != 0);
    const fma_reader_hello wire_hello{FMA_LOCAL_MAGIC, 1, 0, 1};
    const unsigned char hello_golden[]{0x46, 0x4d, 0x41, 0x31, 1, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0};
    check(std::memcmp(&wire_hello, hello_golden, sizeof(hello_golden)) == 0);
    const fma_shared_read wire_descriptor{FMA_LOCAL_MAGIC, 1, 1, 64, 1, 2, 3, 4, 16, 5};
    const unsigned char descriptor_golden[]{0x46, 0x4d, 0x41, 0x31, 1, 0, 0, 0, 1, 0, 0, 0, 64, 0, 0, 0,
                                            1,    0,    0,    0,    0, 0, 0, 0, 2, 0, 0, 0, 0,  0, 0, 0,
                                            3,    0,    0,    0,    0, 0, 0, 0, 4, 0, 0, 0, 0,  0, 0, 0,
                                            16,   0,    0,    0,    0, 0, 0, 0, 5, 0, 0, 0, 0,  0, 0, 0};
    check(std::memcmp(&wire_descriptor, descriptor_golden, sizeof(descriptor_golden)) == 0);
    fma_options bad{sizeof(fma_options), 99, FMA_WARP, 5000, FMA_MAX_BYTES};
    fma_arena rejected = 99;
    check(fma_create(&bad, &rejected) == FMA_ABI_MISMATCH && !rejected);
    auto arena = create(hardware);
    fma_buffer buffer{};
    check(fma_allocate(arena, UINT64_MAX, &buffer) == FMA_INVALID_ARGUMENT && !buffer);
    check(fma_allocate(arena, 0, &buffer) == FMA_INVALID_ARGUMENT && !buffer);
    check(fma_create(nullptr, &rejected) == FMA_INVALID_ARGUMENT && !rejected);
    ok(fma_allocate(arena, payload, &buffer));
    const auto charge = metrics(arena).committed_bytes;
    check(charge >= payload * 4 && charge <= FMA_MAX_BYTES);
    fma_span span{};
    check(fma_acquire(arena, buffer, 0, payload, FMA_HOST_READ, &span) == FMA_STALE);
    check(fma_transfer(arena, buffer, FMA_READBACK) == FMA_STALE);
    write(arena, buffer, 42);
    ok(fma_acquire(arena, buffer, 16, 32, FMA_HOST_WRITE, &span));
    const auto lease = span.lease;
    check(fma_release(arena, buffer) == FMA_BUSY);
    check(fma_destroy(arena) == FMA_BUSY);
    check(fma_transfer(arena, buffer, FMA_UPLOAD) == FMA_BUSY);
    ok(fma_end(arena, lease, 0));
    check(fma_end(arena, lease, 1) == FMA_INVALID_HANDLE);
    check(fma_acquire(arena, buffer, 16, 32, FMA_HOST_WRITE, &span) == FMA_STALE);
    write(arena, buffer, 42);
    ok(fma_transfer(arena, buffer, FMA_UPLOAD));
    ok(fma_transfer(arena, buffer, FMA_READBACK));
    verify(arena, buffer, 42);
    // Alias write invalidates the device; stale readback must not overwrite newer CPU bytes.
    ok(fma_acquire(arena, buffer, 16, 32, FMA_HOST_WRITE, &span));
    std::memset(span_bytes(span), 5, 32);
    ok(fma_end(arena, span.lease, 1));
    check(fma_transfer(arena, buffer, FMA_READBACK) == FMA_STALE);
    ok(fma_transfer(arena, buffer, FMA_UPLOAD));
    ok(fma_transfer(arena, buffer, FMA_READBACK));
    ok(fma_acquire(arena, buffer, 0, payload, FMA_HOST_READ, &span));
    const auto *data = span_bytes(span);
    MEMORY_BASIC_INFORMATION protection{};
    check(VirtualQuery(data, &protection, sizeof(protection)) == sizeof(protection) &&
          (protection.Protect & 255) == PAGE_READONLY);
    for (std::uint64_t i = 0; i < payload; ++i) {
        check(data[i] == (i >= 16 && i < 48 ? 5 : pattern(i, 42)));
    }
    ok(fma_end(arena, span.lease, 1));
    write(arena, buffer, 42);
    {
        Child child;
        auto hello = child.hello();
        const auto peer = child.identity();
        fma_shared_read descriptor{};
        auto invalid_hello = hello;
        invalid_hello.capabilities = 2;
        check(fma_export_read(arena, buffer, &peer, &invalid_hello, &descriptor) == FMA_ABI_MISMATCH);
        auto invalid_peer = peer;
        ++invalid_peer.pid;
        check(fma_export_read(arena, buffer, &invalid_peer, &hello, &descriptor) == FMA_IDENTITY_MISMATCH);
        invalid_peer = peer;
        ++invalid_peer.creation_time;
        check(fma_export_read(arena, buffer, &invalid_peer, &hello, &descriptor) == FMA_IDENTITY_MISMATCH);
        invalid_peer = peer;
        invalid_peer.image_sha256[0] ^= 1;
        check(fma_export_read(arena, buffer, &invalid_peer, &hello, &descriptor) == FMA_IDENTITY_MISMATCH);
        check(descriptor.mapping_handle == 0 && metrics(arena).active_leases == 0);
        ok(fma_export_read(arena, buffer, &peer, &hello, &descriptor));
        check(fma_end_read(arena, descriptor.lease) == FMA_BUSY);
        check(fma_acquire(arena, buffer, 0, payload, FMA_HOST_WRITE, &span) == FMA_BUSY);
        check(fma_release(arena, buffer) == FMA_BUSY);
        child.send(descriptor);
        child.wait(0);
        ok(fma_end_read(arena, descriptor.lease));
        check(fma_end_read(arena, descriptor.lease) == FMA_INVALID_HANDLE);
    }
    const auto old_buffer = buffer;
    ok(fma_release(arena, buffer));
    ok(fma_allocate(arena, payload, &buffer));
    check(buffer != old_buffer && fma_transfer(arena, old_buffer, FMA_UPLOAD) == FMA_INVALID_HANDLE);
    ok(fma_release(arena, buffer));
    check(metrics(arena).committed_bytes == 0);
    std::array<fma_buffer, FMA_MAX_BUFFERS> slots{};
    for (auto &slot : slots) {
        ok(fma_allocate(arena, 1, &slot));
    }
    check(fma_allocate(arena, 1, &buffer) == FMA_LIMIT && !buffer);
    for (auto slot : slots) {
        ok(fma_release(arena, slot));
    }
    check(metrics(arena).active_buffers == 0 && metrics(arena).debug_errors == 0);
    const auto old_arena = arena;
    ok(fma_destroy(arena));
    check(fma_destroy(old_arena) == FMA_INVALID_HANDLE);
    arena = create(hardware, charge - 1);
    check(fma_allocate(arena, payload, &buffer) == FMA_LIMIT && !buffer);
    check(metrics(arena).committed_bytes == 0 && metrics(arena).backing_allocations == 0);
    ok(fma_destroy(arena));
    arena = create(hardware, 2 * charge - 1);
    ok(fma_allocate(arena, payload, &buffer));
    fma_buffer excess{};
    check(fma_allocate(arena, payload, &excess) == FMA_LIMIT && !excess);
    check(metrics(arena).committed_bytes == charge);
    ok(fma_release(arena, buffer));
    for (unsigned cycle = 0; cycle < 100; ++cycle) {
        ok(fma_allocate(arena, payload, &buffer));
        ok(fma_release(arena, buffer));
    }
    check(metrics(arena).committed_bytes == 0 && metrics(arena).peak_bytes == charge);
    ok(fma_destroy(arena));

    arena = create(hardware);
    ok(fma_allocate(arena, payload, &buffer));
    check(fma_acquire(arena, old_buffer, 0, payload, FMA_HOST_READ, &span) == FMA_INVALID_HANDLE);
    check(fma_acquire(arena, buffer, UINT64_MAX, 8, FMA_HOST_WRITE, &span) == FMA_INVALID_ARGUMENT);
    check(metrics(arena).closed == 1 && !span.data);
    ok(fma_destroy(arena));

    arena = create(hardware, FMA_MAX_BYTES, 100);
    ok(fma_allocate(arena, payload, &buffer));
    ok(fma_acquire(arena, buffer, 0, payload, FMA_HOST_WRITE, &span));
    Sleep(150);
    check(metrics(arena).closed == 1);
    check(fma_destroy(arena) == FMA_BUSY);
    check(fma_end(arena, span.lease, 1) == FMA_CLOSED);
    ok(fma_destroy(arena));

    arena = create(hardware);
    ok(fma_allocate(arena, payload, &buffer));
    write(arena, buffer, 42);
    {
        Child child;
        const auto hello = child.hello();
        const auto peer = child.identity();
        fma_shared_read descriptor{};
        ok(fma_export_read(arena, buffer, &peer, &hello, &descriptor));
        check(TerminateProcess(child.process.get(), 9) != FALSE);
        child.wait(9);
        check(fma_end_read(arena, descriptor.lease) == FMA_CLOSED);
        check(metrics(arena).closed == 1 && metrics(arena).active_leases == 0);
    }
    ok(fma_destroy(arena));

    arena = create(hardware, FMA_MAX_BYTES, 100);
    ok(fma_allocate(arena, payload, &buffer));
    write(arena, buffer, 42);
    {
        Child child;
        const auto hello = child.hello();
        const auto peer = child.identity();
        fma_shared_read descriptor{};
        ok(fma_export_read(arena, buffer, &peer, &hello, &descriptor));
        Sleep(150);
        check(metrics(arena).closed == 1);
        check(fma_end_read(arena, descriptor.lease) == FMA_BUSY);
        check(fma_destroy(arena) == FMA_BUSY);
        child.send(descriptor);
        child.wait(0);
        check(fma_end_read(arena, descriptor.lease) == FMA_CLOSED);
    }
    ok(fma_destroy(arena));

    arena = create(hardware);
    ok(fma_allocate(arena, payload, &buffer));
    std::atomic<unsigned> errors{}, published{};
    std::vector<std::thread> threads;
    for (unsigned thread = 0; thread < 4; ++thread) {
        threads.emplace_back([&] {
            for (unsigned i = 0; i < 100;) {
                fma_span local{};
                auto result = fma_acquire(arena, buffer, 0, payload, FMA_HOST_WRITE, &local);
                if (result == FMA_BUSY) {
                    std::this_thread::yield();
                    continue;
                }
                if (result != FMA_OK || !local.data) {
                    ++errors;
                    break;
                }
                std::memset(local.data, 37, payload);
                do {
                    result = fma_end(arena, local.lease, 1);
                } while (result == FMA_BUSY);
                if (result != FMA_OK) {
                    ++errors;
                    break;
                }
                ++published;
                ++i;
            }
        });
    }
    for (auto &thread : threads) {
        thread.join();
    }
    check(errors == 0 && published == 400);
    ok(fma_destroy(arena));
}

double cpu_us() {
    FILETIME created{}, exited{}, kernel{}, user{};
    check(GetProcessTimes(GetCurrentProcess(), &created, &exited, &kernel, &user) != FALSE);
    const auto value = [](FILETIME time) {
        return (uint64_t(time.dwHighDateTime) << 32) | time.dwLowDateTime;
    };
    return double(value(kernel) + value(user)) / 10;
}
uint64_t cpu_cycles() {
    ULONG64 cycles{};
    check(QueryProcessCycleTime(GetCurrentProcess(), &cycles) != FALSE);
    return cycles;
}
using Clock = std::chrono::steady_clock;
double elapsed(Clock::time_point start) {
    return std::chrono::duration<double, std::micro>(Clock::now() - start).count();
}
struct Results {
    std::vector<double> latencies, batches;
    double cpu{}, wall{};
    uint64_t allocations{}, cpu_bytes{}, gpu_bytes{}, cycles{};
};
void trial(fma_arena arena, bool reuse, Results &result) {
    const auto before = metrics(arena);
    fma_buffer buffer{};
    if (reuse) {
        ok(fma_allocate(arena, payload, &buffer));
    }
    const auto cpu_start = cpu_us();
    const auto cycles_start = cpu_cycles();
    const auto batch_start = Clock::now();
    for (unsigned sample = 0; sample < 8; ++sample) {
        const auto start = Clock::now();
        if (!reuse) {
            ok(fma_allocate(arena, payload, &buffer));
        }
        write(arena, buffer, sample);
        ok(fma_transfer(arena, buffer, FMA_UPLOAD));
        ok(fma_transfer(arena, buffer, FMA_READBACK));
        verify(arena, buffer, sample);
        if (!reuse) {
            ok(fma_release(arena, buffer));
        }
        result.latencies.push_back(elapsed(start));
    }
    const auto duration = elapsed(batch_start);
    result.batches.push_back(duration);
    result.wall += duration;
    result.cpu += cpu_us() - cpu_start;
    result.cycles += cpu_cycles() - cycles_start;
    if (reuse) {
        ok(fma_release(arena, buffer));
    }
    const auto after = metrics(arena);
    result.allocations += after.backing_allocations - before.backing_allocations;
    result.cpu_bytes += after.cpu_copy_bytes - before.cpu_copy_bytes;
    result.gpu_bytes += after.gpu_copy_bytes - before.gpu_copy_bytes;
    check(after.committed_bytes == 0 && after.active_leases == 0 && after.debug_errors == 0);
}
double percentile(std::vector<double> values, double percentile) {
    std::sort(values.begin(), values.end());
    const auto position = percentile * double(values.size() - 1);
    const auto index = static_cast<std::size_t>(position);
    const auto upper = (std::min)(index + 1, values.size() - 1);
    return values[index] + (values[upper] - values[index]) * (position - double(index));
}
void print_results(const Results &result) {
    std::cout << "{\"p50_us\":" << percentile(result.latencies, .50)
              << ",\"p95_us\":" << percentile(result.latencies, .95)
              << ",\"p99_us\":" << percentile(result.latencies, .99)
              << ",\"batch_p50_us\":" << percentile(result.batches, .50) << ",\"cpu_us_per_operation\":";
    if (result.cpu > 0) {
        std::cout << result.cpu / double(result.latencies.size());
    } else {
        std::cout << "null";
    }
    std::cout << ",\"cpu_cycles_per_operation\":" << double(result.cycles) / double(result.latencies.size())
              << ",\"operations_per_second\":" << double(result.latencies.size()) * 1000000 / result.wall
              << ",\"backing_allocations\":" << result.allocations
              << ",\"cpu_copy_bytes\":" << result.cpu_bytes << ",\"gpu_copy_bytes\":" << result.gpu_bytes
              << "}";
}
int run(bool hardware) {
    Microsoft::WRL::ComPtr<ID3D12Debug> debug;
    if (SUCCEEDED(D3D12GetDebugInterface(IID_PPV_ARGS(&debug)))) {
        debug->EnableDebugLayer();
    }
    check(percentile({0, 10, 20}, .95) == 19);
    guards(hardware);
    auto arena = create(hardware);
    Results warmup;
    trial(arena, false, warmup);
    trial(arena, true, warmup);
    Results baseline, reused;
    for (unsigned pair = 0; pair < 30; ++pair) {
        if (pair % 2 == 0) {
            trial(arena, false, baseline);
            trial(arena, true, reused);
        } else {
            trial(arena, true, reused);
            trial(arena, false, baseline);
        }
    }
    const auto final = metrics(arena);
    check(baseline.allocations == 960 && reused.allocations == 120);
    check(baseline.cpu_bytes == 7864320 && reused.cpu_bytes == baseline.cpu_bytes);
    check(baseline.gpu_bytes == 7864320 && reused.gpu_bytes == baseline.gpu_bytes);
    ok(fma_destroy(arena));
    std::cout << "{\"mode\":\"fluidruntime-shared-arena-v1\",\"passed\":true,\"hardware\":"
              << (hardware ? "true" : "false") << ",\"checks\":" << checks
              << ",\"uma\":" << (final.uma ? "true" : "false")
              << ",\"debug_layer_enabled\":" << (final.debug_layer_enabled ? "true" : "false")
              << ",\"debug_errors\":" << final.debug_errors
              << ",\"trial_pairs\":30,\"operations_per_arm\":240,\"payload_bytes\":" << payload
              << ",\"backing_budget_bytes\":" << final.budget_bytes
              << ",\"peak_charged_bytes\":" << final.peak_bytes
              << ",\"content_equivalent\":true,\"read_only_peer_verified\":true,\"gpu_bytes_omitted\":0"
              << ",\"unified_memory_active\":false,\"third_party_authority\":false,\"baseline\":";
    print_results(baseline);
    std::cout << ",\"reused\":";
    print_results(reused);
    std::cout << "}\n";
    return 0;
}
} // namespace

int wmain(int argc, wchar_t **argv) {
    try {
        if (argc == 2 && std::wstring_view(argv[1]) == L"--reader") {
            return reader();
        }
        if (argc == 2 && std::wstring_view(argv[1]) == L"--warp") {
            return run(false);
        }
        if (argc == 2 && std::wstring_view(argv[1]) == L"--hardware") {
            return run(true);
        }
        std::fputs("Usage: fluidruntime-memory-arena-lab --warp | --hardware\n", stderr);
        return 2;
    } catch (const std::exception &error) {
        std::fprintf(stderr, "%s\n", error.what());
        return 1;
    }
}
