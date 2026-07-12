#include <windows.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <psapi.h>

#include <algorithm>
#include <iomanip>
#include <iostream>
#include <optional>
#include <sstream>
#include <string>
#include <string_view>
#include <vector>

namespace {

constexpr wchar_t kMode[] = L"fluidruntime-native-probe-v0.2";

class UniqueHandle {
public:
    explicit UniqueHandle(HANDLE handle) : handle_(handle) {}
    ~UniqueHandle() {
        if (handle_ != nullptr) {
            CloseHandle(handle_);
        }
    }

    UniqueHandle(const UniqueHandle&) = delete;
    UniqueHandle& operator=(const UniqueHandle&) = delete;

    [[nodiscard]] HANDLE get() const { return handle_; }

private:
    HANDLE handle_;
};

struct ProcessSnapshot {
    std::wstring image_path;
    unsigned long priority_class{};
    unsigned long page_fault_count{};
    unsigned long long working_set_bytes{};
    unsigned long long private_bytes{};
};

struct CounterHandle {
    std::string name;
    PDH_HCOUNTER handle{};
    PDH_STATUS status{static_cast<PDH_STATUS>(PDH_CSTATUS_NO_COUNTER)};
};

struct CounterResult {
    std::string name;
    std::optional<double> sum;
    std::optional<double> peak;
    unsigned long instance_count{};
    PDH_STATUS status{static_cast<PDH_STATUS>(PDH_NO_DATA)};
};

unsigned long long unix_time_ms() {
    FILETIME file_time{};
    GetSystemTimePreciseAsFileTime(&file_time);
    ULARGE_INTEGER ticks{};
    ticks.LowPart = file_time.dwLowDateTime;
    ticks.HighPart = file_time.dwHighDateTime;
    constexpr unsigned long long windows_to_unix_epoch_ticks = 116444736000000000ULL;
    return (ticks.QuadPart - windows_to_unix_epoch_ticks) / 10000ULL;
}

std::string wide_to_utf8(std::wstring_view value) {
    if (value.empty()) {
        return {};
    }

    const auto required = WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        nullptr,
        0,
        nullptr,
        nullptr);
    if (required <= 0) {
        return {};
    }

    std::string result(static_cast<size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8,
        0,
        value.data(),
        static_cast<int>(value.size()),
        result.data(),
        required,
        nullptr,
        nullptr);
    return result;
}

std::string json_escape(std::string_view value) {
    std::ostringstream output;
    for (const auto character : value) {
        switch (character) {
        case '"': output << "\\\""; break;
        case '\\': output << "\\\\"; break;
        case '\b': output << "\\b"; break;
        case '\f': output << "\\f"; break;
        case '\n': output << "\\n"; break;
        case '\r': output << "\\r"; break;
        case '\t': output << "\\t"; break;
        default:
            if (static_cast<unsigned char>(character) < 0x20) {
                output << "\\u"
                       << std::hex << std::setw(4) << std::setfill('0')
                       << static_cast<int>(static_cast<unsigned char>(character))
                       << std::dec;
            } else {
                output << character;
            }
        }
    }
    return output.str();
}

std::string status_hex(PDH_STATUS status) {
    std::ostringstream output;
    output << "0x" << std::uppercase << std::hex << std::setw(8)
           << std::setfill('0') << static_cast<unsigned long>(status);
    return output.str();
}

std::optional<ProcessSnapshot> query_process(unsigned long process_id) {
    UniqueHandle process(OpenProcess(
        PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ,
        FALSE,
        process_id));
    if (process.get() == nullptr) {
        return std::nullopt;
    }

    std::wstring image_path(32768, L'\0');
    unsigned long image_path_size = static_cast<unsigned long>(image_path.size());
    if (!QueryFullProcessImageNameW(
            process.get(), 0, image_path.data(), &image_path_size)) {
        return std::nullopt;
    }
    image_path.resize(image_path_size);

    PROCESS_MEMORY_COUNTERS_EX memory{};
    memory.cb = sizeof(memory);
    if (!GetProcessMemoryInfo(
            process.get(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&memory),
            sizeof(memory))) {
        return std::nullopt;
    }

    return ProcessSnapshot{
        .image_path = std::move(image_path),
        .priority_class = GetPriorityClass(process.get()),
        .page_fault_count = memory.PageFaultCount,
        .working_set_bytes = memory.WorkingSetSize,
        .private_bytes = memory.PrivateUsage,
    };
}

std::wstring gpu_memory_path(unsigned long process_id, std::wstring_view counter) {
    return std::wstring(L"\\GPU Process Memory(pid_")
        + std::to_wstring(process_id)
        + L"_*)\\"
        + std::wstring(counter);
}

std::wstring gpu_engine_path(unsigned long process_id) {
    return std::wstring(L"\\GPU Engine(pid_")
        + std::to_wstring(process_id)
        + L"_*)\\Utilization Percentage";
}

CounterHandle add_counter(
    PDH_HQUERY query,
    std::string name,
    const std::wstring& path) {
    CounterHandle counter{.name = std::move(name)};
    counter.status = PdhAddEnglishCounterW(
        query,
        path.c_str(),
        0,
        &counter.handle);
    return counter;
}

CounterResult read_counter(const CounterHandle& counter) {
    CounterResult result{.name = counter.name, .status = counter.status};
    if (counter.status != ERROR_SUCCESS) {
        return result;
    }

    unsigned long buffer_size = 0;
    unsigned long item_count = 0;
    auto status = PdhGetFormattedCounterArrayW(
        counter.handle,
        PDH_FMT_DOUBLE | PDH_FMT_NOCAP100,
        &buffer_size,
        &item_count,
        nullptr);
    if (status != static_cast<PDH_STATUS>(PDH_MORE_DATA) || buffer_size == 0) {
        result.status = status;
        return result;
    }

    std::vector<unsigned char> buffer(buffer_size);
    auto* items = reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(buffer.data());
    status = PdhGetFormattedCounterArrayW(
        counter.handle,
        PDH_FMT_DOUBLE | PDH_FMT_NOCAP100,
        &buffer_size,
        &item_count,
        items);
    if (status != ERROR_SUCCESS) {
        result.status = status;
        return result;
    }

    double sum = 0;
    double peak = 0;
    unsigned long valid_count = 0;
    for (unsigned long index = 0; index < item_count; ++index) {
        const auto value_status = items[index].FmtValue.CStatus;
        if (value_status != PDH_CSTATUS_VALID_DATA &&
            value_status != PDH_CSTATUS_NEW_DATA) {
            continue;
        }

        const auto value = std::max(0.0, items[index].FmtValue.doubleValue);
        sum += value;
        peak = std::max(peak, value);
        ++valid_count;
    }

    if (valid_count == 0) {
        result.status = static_cast<PDH_STATUS>(PDH_CSTATUS_NO_INSTANCE);
        return result;
    }

    result.sum = sum;
    result.peak = peak;
    result.instance_count = valid_count;
    result.status = ERROR_SUCCESS;
    return result;
}

std::vector<CounterResult> query_gpu_counters(
    unsigned long process_id,
    unsigned long interval_ms) {
    PDH_HQUERY query{};
    const auto open_status = PdhOpenQueryW(nullptr, 0, &query);
    if (open_status != ERROR_SUCCESS) {
        return {
            CounterResult{.name = "pdh-query", .status = open_status},
        };
    }

    const std::vector<CounterHandle> counters{
        add_counter(query, "local_usage_bytes", gpu_memory_path(process_id, L"Local Usage")),
        add_counter(query, "dedicated_usage_bytes", gpu_memory_path(process_id, L"Dedicated Usage")),
        add_counter(query, "shared_usage_bytes", gpu_memory_path(process_id, L"Shared Usage")),
        add_counter(query, "non_local_usage_bytes", gpu_memory_path(process_id, L"Non Local Usage")),
        add_counter(query, "engine_utilization_percent", gpu_engine_path(process_id)),
    };

    auto collect_status = PdhCollectQueryData(query);
    if (collect_status == ERROR_SUCCESS) {
        Sleep(interval_ms);
        collect_status = PdhCollectQueryData(query);
    }

    std::vector<CounterResult> results;
    results.reserve(counters.size());
    for (const auto& counter : counters) {
        if (collect_status != ERROR_SUCCESS && counter.status == ERROR_SUCCESS) {
            results.push_back(CounterResult{
                .name = counter.name,
                .status = collect_status,
            });
        } else {
            results.push_back(read_counter(counter));
        }
    }

    PdhCloseQuery(query);
    return results;
}

const CounterResult& find_counter(
    const std::vector<CounterResult>& counters,
    std::string_view name) {
    const auto item = std::find_if(
        counters.begin(),
        counters.end(),
        [name](const CounterResult& counter) { return counter.name == name; });
    if (item == counters.end()) {
        static const CounterResult missing{.name = "missing"};
        return missing;
    }
    return *item;
}

void write_optional_number(
    std::ostream& output,
    const std::optional<double>& value,
    int precision) {
    if (!value.has_value()) {
        output << "null";
        return;
    }
    output << std::fixed << std::setprecision(precision) << *value;
}

void write_json(
    unsigned long process_id,
    unsigned long interval_ms,
    bool self_test,
    const ProcessSnapshot& process,
    const std::vector<CounterResult>& counters) {
    const auto& local = find_counter(counters, "local_usage_bytes");
    const auto& dedicated = find_counter(counters, "dedicated_usage_bytes");
    const auto& shared = find_counter(counters, "shared_usage_bytes");
    const auto& non_local = find_counter(counters, "non_local_usage_bytes");
    const auto& engine = find_counter(counters, "engine_utilization_percent");

    std::cout << "{\n"
              << "  \"mode\": \"" << wide_to_utf8(kMode) << "\",\n"
              << "  \"read_only\": true,\n"
              << "  \"would_modify_system\": false,\n"
              << "  \"self_test\": " << (self_test ? "true" : "false") << ",\n"
              << "  \"pid\": " << process_id << ",\n"
              << "  \"captured_at_unix_ms\": " << unix_time_ms() << ",\n"
              << "  \"sample_interval_ms\": " << interval_ms << ",\n"
              << "  \"process\": {\n"
              << "    \"image_path\": \""
              << json_escape(wide_to_utf8(process.image_path)) << "\",\n"
              << "    \"priority_class\": " << process.priority_class << ",\n"
              << "    \"page_fault_count\": " << process.page_fault_count << ",\n"
              << "    \"working_set_bytes\": " << process.working_set_bytes << ",\n"
              << "    \"private_bytes\": " << process.private_bytes << "\n"
              << "  },\n"
              << "  \"gpu\": {\n"
              << "    \"source\": \"windows-pdh\",\n"
              << "    \"local_usage_bytes\": ";
    write_optional_number(std::cout, local.sum, 0);
    std::cout << ",\n    \"dedicated_usage_bytes\": ";
    write_optional_number(std::cout, dedicated.sum, 0);
    std::cout << ",\n    \"shared_usage_bytes\": ";
    write_optional_number(std::cout, shared.sum, 0);
    std::cout << ",\n    \"non_local_usage_bytes\": ";
    write_optional_number(std::cout, non_local.sum, 0);
    std::cout << ",\n    \"engine_utilization_sum_percent\": ";
    write_optional_number(std::cout, engine.sum, 3);
    std::cout << ",\n    \"engine_utilization_peak_percent\": ";
    write_optional_number(std::cout, engine.peak, 3);
    std::cout << ",\n    \"memory_instance_count\": "
              << std::max({
                     local.instance_count,
                     dedicated.instance_count,
                     shared.instance_count,
                     non_local.instance_count})
              << ",\n    \"engine_instance_count\": " << engine.instance_count << "\n"
              << "  },\n"
              << "  \"capabilities\": {\n"
              << "    \"process_memory\": true,\n"
              << "    \"gpu_process_memory\": "
              << (local.sum.has_value() || dedicated.sum.has_value() ||
                      shared.sum.has_value() || non_local.sum.has_value()
                      ? "true" : "false")
              << ",\n"
              << "    \"gpu_engine_utilization\": "
              << (engine.sum.has_value() ? "true" : "false") << "\n"
              << "  },\n"
              << "  \"errors\": [";

    bool wrote_error = false;
    for (const auto& counter : counters) {
        if (counter.status == ERROR_SUCCESS) {
            continue;
        }
        if (wrote_error) {
            std::cout << ',';
        }
        std::cout << "\n    {\"counter\": \"" << json_escape(counter.name)
                  << "\", \"status\": \"" << status_hex(counter.status) << "\"}";
        wrote_error = true;
    }
    if (wrote_error) {
        std::cout << '\n';
    }
    std::cout << "  ]\n}\n";
}

void print_usage() {
    std::wcerr << L"Usage: fluidruntime-native-probe --pid <id> "
                  L"[--interval-ms <milliseconds>]\n"
                  L"       fluidruntime-native-probe --self-test\n";
}

std::optional<unsigned long> parse_positive(const wchar_t* value) {
    wchar_t* end{};
    const auto parsed = wcstoul(value, &end, 10);
    if (end == value || *end != L'\0' || parsed == 0) {
        return std::nullopt;
    }
    return parsed;
}

} // namespace

int wmain(int argc, wchar_t* argv[]) {
    unsigned long process_id = 0;
    unsigned long interval_ms = 250;
    bool self_test = false;

    for (int index = 1; index < argc; ++index) {
        const std::wstring_view argument(argv[index]);
        if (argument == L"--self-test") {
            self_test = true;
            process_id = GetCurrentProcessId();
            interval_ms = 50;
        } else if (argument == L"--pid" && index + 1 < argc) {
            const auto parsed = parse_positive(argv[++index]);
            if (!parsed.has_value()) {
                print_usage();
                return 2;
            }
            process_id = *parsed;
        } else if (argument == L"--interval-ms" && index + 1 < argc) {
            const auto parsed = parse_positive(argv[++index]);
            if (!parsed.has_value() || *parsed > 60000) {
                print_usage();
                return 2;
            }
            interval_ms = *parsed;
        } else if (argument == L"--help" || argument == L"-h") {
            print_usage();
            return 0;
        } else {
            print_usage();
            return 2;
        }
    }

    if (process_id == 0) {
        print_usage();
        return 2;
    }

    const auto process = query_process(process_id);
    if (!process.has_value()) {
        std::cerr << "Unable to query process " << process_id
                  << "; Win32 error=" << GetLastError() << "\n";
        return 3;
    }

    const auto counters = query_gpu_counters(process_id, interval_ms);
    write_json(process_id, interval_ms, self_test, *process, counters);
    return 0;
}
