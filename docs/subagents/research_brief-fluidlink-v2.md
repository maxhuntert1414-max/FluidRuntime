# FluidLink v2 Research Brief

The feedback contains two immediately valuable changes and two conditional
ones. Positional binary payloads remove repeated JSON keys and parsing; fixed
integer microseconds/bytes remove floating-point wire drift. Both fit the
existing negotiated-version model.

Delta state is premature because no state snapshot body currently crosses the
wire. Generic shared memory is plausible later, but cannot reuse the D3D11 hook
ring without defining record framing, atomics, backpressure, identity/ACL,
timeouts, crash recovery, TCP fallback, and sustained benchmark gates.
