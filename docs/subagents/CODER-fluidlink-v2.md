# CODER - FluidLink v2

- Agent: `019fb41a-f86b-7af0-aaa8-303d12ffdde0`
- Assignment: implement the .NET v2 package, typed client, probe, and tests.
- Owned files: `src/FluidLink/V2/`, v2 .NET tests, probe/report, package metadata,
  and the integration script.
- Edited files: yes.

## Output

Added the no-dependency `FluidLink` 0.2.0 API, strict little-endian codec,
loopback-only serialized client, exact handshake/correlation validation, real
v1/v2 probe, and NuGet contract/golden packaging.

The measured same-flow result is 3,189 v1 frame bytes versus 1,880 v2 frame
bytes. This is control-frame evidence only.
