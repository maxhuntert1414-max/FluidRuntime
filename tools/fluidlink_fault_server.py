"""Small loopback-only FluidLink fault peer used by the integration gate."""

from __future__ import annotations

import argparse
import socket
import sys
import time
from pathlib import Path


def _serve_slow(connection: socket.socket, gateway_path: Path, delay_ms: int) -> None:
    sys.path.insert(0, str(gateway_path))
    from fluidgateway import __version__
    from fluidgateway.adapter import RuntimeAdapterSession, process_adapter_event_payload
    from fluidgateway.fluidlink_v2 import (
        FluidLinkV2ServerSession,
        encode_fluidlink_v2_frame,
        read_fluidlink_v2_frame,
    )

    adapter = RuntimeAdapterSession()
    protocol = FluidLinkV2ServerSession(
        server_name="fluidgateway",
        server_version=__version__,
    )
    with connection.makefile("rwb", buffering=0) as stream:
        while True:
            request = read_fluidlink_v2_frame(stream)
            if request is None:
                return
            response = protocol.process(
                request,
                lambda event: process_adapter_event_payload(adapter, event),
            )
            time.sleep(delay_ms / 1000)
            stream.write(encode_fluidlink_v2_frame(response))
            stream.flush()
            if protocol.closed:
                return


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument(
        "--mode", choices=("invalid", "stall", "slow"), required=True
    )
    parser.add_argument("--ready", type=Path, required=True)
    parser.add_argument("--gateway-path", type=Path)
    parser.add_argument("--delay-ms", type=int, default=100)
    args = parser.parse_args()
    if args.mode == "slow" and args.gateway_path is None:
        parser.error("--gateway-path is required in slow mode")
    if args.delay_ms < 1:
        parser.error("--delay-ms must be positive")

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as listener:
        listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        listener.bind(("127.0.0.1", args.port))
        listener.listen(1)
        args.ready.write_text("ready\n", encoding="ascii")
        connection, _ = listener.accept()
        with connection:
            if args.mode == "invalid":
                connection.recv(64)
                connection.sendall(bytes(64))
                time.sleep(1)
            elif args.mode == "stall":
                time.sleep(10)
            else:
                _serve_slow(connection, args.gateway_path, args.delay_ms)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
