#ifndef FLUID_MEMORY_ARENA_H
#define FLUID_MEMORY_ARENA_H

#include <stdint.h>

#if defined(FLUID_MEMORY_ARENA_EXPORTS)
#define FMA_API __declspec(dllexport)
#else
#define FMA_API __declspec(dllimport)
#endif
#define FMA_CALL __cdecl
#ifdef __cplusplus
#define FMA_NOEXCEPT noexcept
extern "C" {
#else
#define FMA_NOEXCEPT
#endif

#define FMA_ABI_VERSION 1u
#define FMA_MAX_BYTES (64ull * 1024 * 1024)
#define FMA_MAX_BUFFERS 64u
#define FMA_LOCAL_MAGIC 0x31414D46u
#define FMA_READ_CAPABILITY 1u

typedef uint64_t fma_arena;
typedef uint64_t fma_buffer;
typedef uint64_t fma_lease;
typedef uint32_t fma_status;
enum {
    FMA_OK = 0,
    FMA_INVALID_ARGUMENT = 1,
    FMA_INVALID_HANDLE = 2,
    FMA_ABI_MISMATCH = 3,
    FMA_BUSY = 4,
    FMA_LIMIT = 5,
    FMA_STALE = 6,
    FMA_CLOSED = 7,
    FMA_TIMEOUT = 8,
    FMA_DEVICE_ERROR = 9,
    FMA_IDENTITY_MISMATCH = 10,
    FMA_OUT_OF_MEMORY = 11,
    FMA_INTERNAL_ERROR = 12
};
enum { FMA_WARP = 0, FMA_HARDWARE = 1 };
enum { FMA_HOST_READ = 1, FMA_HOST_WRITE = 2 };
enum { FMA_UPLOAD = 1, FMA_READBACK = 2 };

typedef struct fma_options {
    uint32_t size, abi_version, adapter, timeout_ms;
    uint64_t budget_bytes;
} fma_options;

/* A borrowed local span, never an IPC descriptor. Valid until fma_end only.
   Do not retain pointers, access outside bytes, or use the span concurrently
   with end. Read spans are OS read-only; writes require explicit publication. */
typedef struct fma_span {
    fma_lease lease;
    void *data;
    uint64_t bytes;
} fma_span;

typedef struct fma_peer {
    uint64_t process_handle, creation_time;
    uint32_t pid, reserved;
    uint8_t image_sha256[32];
} fma_peer;

/* Owned local-reader protocol, little endian Windows x64, not FluidLink.
   Child first sends this exact hello; parent verifies it before exporting. */
typedef struct fma_reader_hello {
    uint32_t magic, version, opcode, capabilities;
} fma_reader_hello;
typedef struct fma_shared_read {
    uint32_t magic, version, opcode, size;
    uint64_t arena, buffer, lease, mapping_handle, bytes, content_version;
} fma_shared_read;

typedef struct fma_metrics {
    uint32_t size, abi_version, closed, hardware;
    uint32_t uma, cache_coherent_uma, debug_layer_enabled, reserved;
    uint64_t budget_bytes, committed_bytes, peak_bytes, active_buffers, active_leases;
    uint64_t backing_allocations, cpu_copy_bytes, gpu_copy_bytes, transfers;
    uint64_t timeouts, device_errors, debug_errors;
} fma_metrics;

/* One live arena per loaded DLL instance; load only one instance per process.
   DLL must stay loaded until destroy succeeds.
   Valid caller-owned pointers are required; this ABI is not a memory sandbox.
   Calls serialize with try-lock and return BUSY instead of waiting for other callers. */
FMA_API fma_status FMA_CALL fma_create(const fma_options *options, fma_arena *output) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_close(fma_arena arena) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_destroy(fma_arena arena) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_allocate(fma_arena arena, uint64_t bytes, fma_buffer *output) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_release(fma_arena arena, fma_buffer buffer) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_acquire(fma_arena arena, fma_buffer buffer, uint64_t offset, uint64_t bytes,
                                        uint32_t access, fma_span *output) FMA_NOEXCEPT;
/* publish=0 abandons the lease and invalidates content. Closed leases can still
   be ended to unmap safely; CLOSED then means no content was published. */
FMA_API fma_status FMA_CALL fma_end(fma_arena arena, fma_lease lease, uint32_t publish) FMA_NOEXCEPT;
/* Synchronous original-copy paths; never skip a transfer based on freshness alone. */
FMA_API fma_status FMA_CALL fma_transfer(fma_arena arena, fma_buffer buffer, uint32_t direction) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_export_read(fma_arena arena, fma_buffer buffer, const fma_peer *peer,
                                            const fma_reader_hello *hello,
                                            fma_shared_read *output) FMA_NOEXCEPT;
/* Read exports are whole-buffer leases and cannot be reclaimed until peer exit.
   Never close a remote numeric handle: it might have been recycled by the peer. */
FMA_API fma_status FMA_CALL fma_end_read(fma_arena arena, fma_lease lease) FMA_NOEXCEPT;
FMA_API fma_status FMA_CALL fma_get_metrics(fma_arena arena, fma_metrics *output, uint32_t size) FMA_NOEXCEPT;

#ifdef __cplusplus
}
#endif
#endif
