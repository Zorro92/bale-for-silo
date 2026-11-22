#!/usr/bin/env python3
"""Read-only, threaded file server with single-byte-range support for FFmpeg seeks."""

import argparse
import contextlib
import http.client
import http.server
import mimetypes
import pathlib
import re
import socket
import tempfile
import threading
import urllib.parse


class MediaServer(http.server.ThreadingHTTPServer):
    daemon_threads = True

    def __init__(self, address, root):
        self.root = root.resolve(strict=True)
        if ":" in address[0]:
            self.address_family = socket.AF_INET6
        super().__init__(address, MediaHandler)


class MediaHandler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *_args):
        pass  # No per-request URLs, query strings or file names in logs.

    def do_HEAD(self):
        self.serve_file(head=True)

    def do_GET(self):
        self.serve_file(head=False)

    def serve_file(self, head):
        try:
            path = urllib.parse.unquote(urllib.parse.urlsplit(self.path).path, errors="strict")
            parts = path.split("/")
            if "\\" in path or "\x00" in path or any(part in (".", "..") for part in parts):
                self.send_error(403, "Path refused")
                return
            target = self.server.root.joinpath(*parts).resolve(strict=True)
            if not target.is_relative_to(self.server.root):
                self.send_error(403, "Path refused")
                return
            if not target.is_file():
                self.send_error(404, "File not found")
                return
            with target.open("rb") as source:
                size = source.seek(0, 2)
                source.seek(0)
                start, end = 0, size - 1
                requested = self.headers.get_all("Range", [])
                # RFC 9110 Range applies to GET; HEAD describes the complete GET representation.
                if requested and not head:
                    try:
                        if len(requested) != 1:
                            raise ValueError
                        start, end = byte_range(requested[0], size)
                    except ValueError:
                        self.send_response(416)
                        self.send_header("Content-Range", f"bytes */{size}")
                        self.send_header("Accept-Ranges", "bytes")
                        self.send_header("Content-Length", "0")
                        self.end_headers()
                        return
                partial = bool(requested) and not head
                self.send_response(206 if partial else 200)
                content_type = {".mp4": "video/mp4", ".mkv": "video/x-matroska", ".webm": "video/webm"}.get(
                    target.suffix.lower(), mimetypes.guess_type(target.name)[0] or "application/octet-stream"
                )
                self.send_header("Content-Type", content_type)
                self.send_header("Accept-Ranges", "bytes")
                self.send_header("Content-Length", str(end - start + 1))
                if partial:
                    self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
                self.end_headers()
                if head:
                    return
                source.seek(start)
                remaining = end - start + 1
                while remaining:
                    chunk = source.read(min(64 * 1024, remaining))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    remaining -= len(chunk)
        except (ValueError, UnicodeError):
            self.send_error(400, "Invalid path")
        except (FileNotFoundError, NotADirectoryError):
            self.send_error(404, "File not found")
        except PermissionError:
            self.send_error(403, "Path refused")
        except (BrokenPipeError, ConnectionResetError):
            pass  # Seeks close the old response; this is normal for FFmpeg.


def byte_range(value, size):
    """One RFC 9110 inclusive byte range (open-ended and suffix forms included)."""
    match = re.fullmatch(r"bytes=(\d*)-(\d*)", value.strip(), re.ASCII)
    if match is None or size == 0 or not any(match.groups()):
        raise ValueError("Unsatisfiable range")
    first, last = match.groups()
    if not first:
        suffix = int(last)
        if suffix <= 0:
            raise ValueError("Empty suffix")
        return max(0, size - suffix), size - 1
    start, end = int(first), min(int(last), size - 1) if last else size - 1
    if start >= size or end < start:
        raise ValueError("Unsatisfiable range")
    return start, end


def self_test():
    with tempfile.TemporaryDirectory(prefix="bale-serve-media-") as temporary:
        root = pathlib.Path(temporary) / "root"
        root.mkdir()
        (root / "clip.mp4").write_bytes(bytes(range(256)))
        (root / "empty.webm").touch()
        (root / "a clip.mp4").write_bytes(b"spaced")
        (root / "clip.mkv").write_bytes(b"mkv")
        (root / "clip.webm").write_bytes(b"webm")
        (pathlib.Path(temporary) / "outside").write_bytes(b"outside")
        (root / "escape").symlink_to(pathlib.Path(temporary) / "outside")
        with MediaServer(("localhost", 0), root) as server:
            worker = threading.Thread(target=server.serve_forever)
            worker.start()
            try:
                def request(method, path, headers=None):
                    with contextlib.closing(http.client.HTTPConnection("localhost", server.server_port, timeout=5)) as connection:
                        connection.request(method, path, headers=headers or {})
                        response = connection.getresponse()
                        return response.status, dict(response.getheaders()), response.read()

                status, headers, data = request("GET", "/clip.mp4", {"Range": "bytes=10-19"})
                assert status == 206 and data == bytes(range(10, 20))
                assert headers["Content-Range"] == "bytes 10-19/256" and headers["Accept-Ranges"] == "bytes"
                status, headers, data = request("HEAD", "/clip.mp4")
                assert status == 200 and not data and headers["Content-Length"] == "256"
                assert headers["Content-Type"] == "video/mp4"
                for extension, mime in (("mkv", "video/x-matroska"), ("webm", "video/webm")):
                    assert request("HEAD", f"/clip.{extension}")[1]["Content-Type"] == mime
                for path in ("/../outside", "/%2e%2e/outside", "/..%2foutside", "/escape", "/%5c..%5coutside"):
                    assert request("GET", path)[0] == 403, path
                assert request("GET", "/a%20clip.mp4?ignored=1")[2] == b"spaced"
                assert request("GET", "/")[0] == 404
                assert request("POST", "/clip.mp4")[0] == 501
                assert request("GET", "/clip.mp4", {"Range": "bytes=250-"})[2] == bytes(range(250, 256))
                assert request("GET", "/clip.mp4", {"Range": "bytes=-6"})[2] == bytes(range(250, 256))
                assert request("GET", "/clip.mp4", {"Range": "bytes=250-999"})[1]["Content-Range"] == "bytes 250-255/256"
                for value in ("bytes=999-", "bytes=20-10", "bytes=-0", "bytes=0-1,3-4", "bytes=-"):
                    status, headers, data = request("GET", "/clip.mp4", {"Range": value})
                    assert status == 416 and headers["Content-Range"] == "bytes */256" and not data
                assert request("GET", "/empty.webm", {"Range": "bytes=0-"})[0] == 416
                assert request("GET", "/empty.webm")[2] == b""
            finally:
                server.shutdown()
                worker.join()
    print("serve-media: self-test passed (ranges, HEAD, types, traversal, read-only methods)")


def main():
    parser = argparse.ArgumentParser(
        description=__doc__,
        epilog="Run with python3 -I scripts/serve-media.py or uv run scripts/serve-media.py. "
        "For LAN playback on Fedora, allow the chosen TCP port in the active firewall zone yourself; "
        "this script never changes firewall settings. Serve only a directory you intend to share.",
    )
    parser.add_argument("directory", type=pathlib.Path, nargs="?", help="directory of files to share (no directory listing)")
    parser.add_argument("--bind", default="", help="bind address (default: all IPv4 interfaces)")
    parser.add_argument("--port", type=int, default=8765, help="TCP port (default: 8765; 0 selects a free port)")
    parser.add_argument("--self-test", action="store_true", help="test on an ephemeral local port and exit")
    args = parser.parse_args()
    if args.self_test:
        self_test()
        return
    if args.directory is None or not args.directory.is_dir():
        parser.error("directory must name an existing directory")
    if not 0 <= args.port <= 65535:
        parser.error("port must be 0..65535")
    with MediaServer((args.bind, args.port), args.directory) as server:
        host = args.bind or socket.gethostname()
        if ":" in host:
            host = f"[{host}]"
        print(f"Serving read-only files on TCP port {server.server_port}; Ctrl-C stops.", flush=True)
        for path in sorted(server.root.rglob("*")):
            if path.is_file() and path.resolve().is_relative_to(server.root):
                relative = path.relative_to(server.root).as_posix()
                print(f"http://{host}:{server.server_port}/{urllib.parse.quote(relative)}", flush=True)
        try:
            server.serve_forever()
        except KeyboardInterrupt:
            pass


if __name__ == "__main__":
    main()
