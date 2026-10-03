#!/usr/bin/env python3
"""
Lightweight SPA static server for Blazor WebAssembly.
Serves static assets with appropriate MIME types and redirects non-file routes to index.html.
"""

import http.server
import os
import socketserver
import sys

DIRECTORY = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "dist", "wwwroot")
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 5171
HOST = sys.argv[3] if len(sys.argv) > 3 else "0.0.0.0"

class SpaHandler(http.server.SimpleHTTPRequestHandler):
    def __init__(self, *args, **kwargs):
        super().__init__(*args, directory=DIRECTORY, **kwargs)

    def send_head(self):
        # Translate requested URL path to local file path
        path = self.translate_path(self.path)
        # If requested path does not exist on disk, serve index.html for SPA routing
        if not os.path.exists(path):
            self.path = "/index.html"
        return super().send_head()

    def end_headers(self):
        # Ensure CORS friendly for local dev/testing
        self.send_header("Access-Control-Allow-Origin", "*")
        super().end_headers()

SpaHandler.extensions_map.update({
    ".wasm": "application/wasm",
    ".webmanifest": "application/manifest+json",
    ".json": "application/json",
    ".js": "text/javascript",
    ".css": "text/css",
    ".html": "text/html",
    ".svg": "image/svg+xml",
    ".png": "image/png",
})

class ThreadingTCPServer(socketserver.ThreadingMixIn, socketserver.TCPServer):
    allow_reuse_address = True

if __name__ == "__main__":
    DIRECTORY = os.path.abspath(DIRECTORY)
    if not os.path.isdir(DIRECTORY):
        print(f"Error: Directory '{DIRECTORY}' not found. Please publish or build the project first.", file=sys.stderr)
        sys.exit(1)

    print(f"Serving Blazor WebAssembly app from: {DIRECTORY}")
    print(f"Listening on: http://{HOST}:{PORT}")

    with ThreadingTCPServer((HOST, PORT), SpaHandler) as httpd:
        try:
            httpd.serve_forever()
        except KeyboardInterrupt:
            print("\nShutting down server...")
            httpd.shutdown()
