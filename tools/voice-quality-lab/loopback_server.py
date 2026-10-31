"""Ephemeral, loopback-only echo sink for validating the voice fixture."""
import secrets
import socket
import struct
import sys
import threading
import time

metadata = sys.argv[1]
token = secrets.token_hex(32).encode("ascii")
udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
tcp = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
udp.bind(("127.0.0.1", 49214))
tcp.bind(("127.0.0.1", 49214))
tcp.listen(4)
udp.settimeout(1)
tcp.settimeout(1)
deadline = time.monotonic() + 190

def receive(conn):
    with conn:
        conn.settimeout(5)
        try:
            auth = b""
            while len(auth) < 66:
                part = conn.recv(66 - len(auth))
                if not part:
                    return
                auth += part
            if auth[:65] != token + b"U" or auth[65:] not in (b"0", b"1", b"2", b"3"):
                return
            total = 0
            def exact(n):
                result = b""
                while len(result) < n:
                    part = conn.recv(n-len(result))
                    if not part: raise EOFError()
                    result += part
                return result
            while time.monotonic() < deadline:
                size = struct.unpack(">I", exact(4))[0]
                if size == 0:
                    conn.sendall(struct.pack(">Q", total)); return
                if size > 262144: return
                total += len(exact(size))
        except OSError:
            pass

def accept():
    while time.monotonic() < deadline:
        try:
            conn, _ = tcp.accept()
            threading.Thread(target=receive, args=(conn,), daemon=True).start()
        except socket.timeout:
            pass

threading.Thread(target=accept, daemon=True).start()
with open(metadata, "x", encoding="ascii") as output:
    output.write("127.0.0.1|" + token.decode("ascii"))
while time.monotonic() < deadline:
    try:
        data, sender = udp.recvfrom(512)
        if len(data) == 224 and data[:64] == token:
            udp.sendto(data, sender)
    except socket.timeout:
        pass
udp.close()
tcp.close()
