# STARK-PRODUCTIONS Researcher Trace: v0.8.0

## Assignment

Audit the v0.8.0 technical claims against source, traces, and primary Microsoft
documentation.

## Output

- Confirmed a real but narrow managed-to-native policy path.
- Distinguished Win32 pagefile-backed IPC from D3D shared resources or physical
  unified memory.
- Confirmed that 4,096 bytes is a logical descriptor estimate, not measured
  physical VRAM traffic.
- Required the public performance claim to remain blocked.
- Removed an unsupported attribution of the AMD regression to startup overhead.

## Sources

The audit used Microsoft documentation for `CopyResource`, named shared memory,
kernel-object namespaces, file-mapping security, DXGI adapter memory fields, and
D3D12 UMA capability reporting.

## Risks

Owned-target trust is a laboratory boundary, not a hostile-process security
boundary. No files were edited by the researcher.
