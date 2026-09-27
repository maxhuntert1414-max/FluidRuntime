#pragma once

#include <windows.h>
#include <bcrypt.h>
#include <array>
#include <cstdint>
#include <stdexcept>
#include <string>
#include <utility>

namespace fluid::memory::windows {
class Handle {
    HANDLE value_{};

  public:
    explicit Handle(HANDLE value = nullptr) : value_(value) {}
    ~Handle() { reset(); }
    Handle(const Handle &) = delete;
    Handle &operator=(const Handle &) = delete;
    Handle(Handle &&other) noexcept : value_(std::exchange(other.value_, nullptr)) {}
    Handle &operator=(Handle &&other) noexcept {
        if (this != &other) {
            reset();
            value_ = std::exchange(other.value_, nullptr);
        }
        return *this;
    }
    void reset() noexcept {
        if (value_ && value_ != INVALID_HANDLE_VALUE) {
            CloseHandle(value_);
        }
        value_ = nullptr;
    }
    HANDLE get() const { return value_; }
};

inline std::uint64_t creation_time(HANDLE process) {
    FILETIME created{}, exited{}, kernel{}, user{};
    if (!GetProcessTimes(process, &created, &exited, &kernel, &user)) {
        throw std::runtime_error("Cannot read process creation time");
    }
    return (std::uint64_t(created.dwHighDateTime) << 32) | created.dwLowDateTime;
}

inline std::wstring image_path(HANDLE process) {
    std::wstring path(32768, L'\0');
    DWORD length = static_cast<DWORD>(path.size());
    if (!QueryFullProcessImageNameW(process, 0, path.data(), &length)) {
        throw std::runtime_error("Cannot read process image path");
    }
    path.resize(length);
    return path;
}

inline std::array<unsigned char, 32> file_hash(HANDLE file) {
    LARGE_INTEGER size{}, zero{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart <= 0 || size.QuadPart > 256LL * 1024 * 1024 ||
        !SetFilePointerEx(file, zero, nullptr, FILE_BEGIN)) {
        throw std::runtime_error("Image hash size/seek rejected");
    }
    BCRYPT_HASH_HANDLE hash{};
    if (BCryptCreateHash(BCRYPT_SHA256_ALG_HANDLE, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
        throw std::runtime_error("Cannot initialize SHA256");
    }
    struct Cleanup {
        BCRYPT_HASH_HANDLE value;
        ~Cleanup() { BCryptDestroyHash(value); }
    } cleanup{hash};
    std::array<unsigned char, 4096> chunk{};
    std::uint64_t total{};
    while (total < static_cast<std::uint64_t>(size.QuadPart)) {
        DWORD count{};
        if (!ReadFile(file, chunk.data(), static_cast<DWORD>(chunk.size()), &count, nullptr) || !count ||
            count > static_cast<std::uint64_t>(size.QuadPart) - total ||
            BCryptHashData(hash, chunk.data(), count, 0) < 0) {
            throw std::runtime_error("Image hash read failed");
        }
        total += count;
    }
    std::array<unsigned char, 32> result{};
    if (BCryptFinishHash(hash, result.data(), static_cast<ULONG>(result.size()), 0) < 0) {
        throw std::runtime_error("Cannot finalize SHA256");
    }
    return result;
}
} // namespace fluid::memory::windows
