#!/usr/bin/env python3
"""Local static file server for testing WebAssembly multi-threading.

Serves a published/built WASM wwwroot with the COOP/COEP headers that make the
page cross-origin isolated, which browsers require before exposing
SharedArrayBuffer (and therefore WASM threads).

Usage:
    python eng/serve-isolated.py [directory] [port]

Defaults: directory = artifacts/demo-threads-test/wwwroot
          port      = 8080
"""

import http.server
import os
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_DIRECTORY = os.path.join(ROOT, "artifacts", "demo-threads-test", "wwwroot")


class IsolatedHandler(http.server.SimpleHTTPRequestHandler):
    def end_headers(self):
        self.send_header("Cross-Origin-Opener-Policy", "same-origin")
        self.send_header("Cross-Origin-Embedder-Policy", "require-corp")
        super().end_headers()

    def log_message(self, format, *args):
        sys.stderr.write("%s - %s\n" % (self.address_string(), format % args))


def main():
    directory = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_DIRECTORY
    port = int(sys.argv[2]) if len(sys.argv) > 2 else 8080

    if not os.path.isdir(directory):
        sys.exit(f"Directory not found: {directory}\nBuild the demo first.")

    handler = lambda *args, **kwargs: IsolatedHandler(*args, directory=directory, **kwargs)
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), handler)
    print(f"Serving {directory}")
    print(f"URL: http://localhost:{port}/  (cross-origin isolated: COOP/COEP headers set)")
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
