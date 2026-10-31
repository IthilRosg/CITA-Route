"""Bounded SE/DE UDP A-B-B-A test; credentials/config arrive only on stdin.

No VPN or routes are changed. Only two approved measurement ports are used.
Both roles remove their narrowly scoped INPUT rules in finally (also on alarm).
"""
import datetime
import json
import select
import signal
import socket
import struct
import subprocess
import sys
import time

cfg = json.loads(sys.stdin.readline())
role, start, token, tag = cfg['role'], cfg['start'], cfg['token'].encode(), cfg['tag']
assert role in ('client', 'server') and len(token) == 64
assert tag.startswith('cita-state-') and tag[11:].isalnum()
assert time.time() < start < time.time() + 60
se, de = '138.124.250.203', '143.20.37.241'
local, peer = (se, de) if role == 'client' else (de, se)
phases = [(49214, 49214, False), (49215, 49214, True),
          (49214, 49215, True), (49215, 49215, False)]
rules, sockets, results = [], {}, []


def stop(*_):
    raise SystemExit('bounded test stopped')


signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGALRM, stop)
signal.alarm(125)
try:
    for port in (49214, 49215):
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.bind((local, port))
        sock.setblocking(False)
        sockets[port] = sock
        rule = ['INPUT', '-s', peer + '/32', '-d', local + '/32', '-p', 'udp',
                '--sport', '49214:49215', '--dport', str(port),
                '-m', 'comment', '--comment', tag, '-j', 'ACCEPT']
        subprocess.run(['iptables', '-I', 'INPUT', '1', *rule[1:]], check=True)
        rules.append(rule)
    print(json.dumps({'role': role, 'ready': True}), flush=True)
    for phase, (source, destination, primed) in enumerate(phases):
        begin = start + phase * 23
        sock = sockets[source if role == 'client' else destination]
        address = (peer, destination if role == 'client' else source)
        seen, invalid, sent, errors, prime_sent, prime_seen = set(), 0, 0, 0, 0, 0
        prime = token + b'PRIME' + bytes(155)
        while time.time() < begin - 1:
            time.sleep(.02)
        # One outbound packet creates the reverse tuple before client traffic.
        if role == 'server' and primed:
            sock.sendto(prime, address)
            prime_sent += 1
        while time.time() < begin + 22:
            now = time.time()
            if role == 'client' and sent < 1000 and now >= begin + sent / 50:
                packet = token + struct.pack('!II', phase, sent) + bytes(152)
                try:
                    sock.sendto(packet, address)
                except OSError:
                    errors += 1
                sent += 1
            readable, _, _ = select.select([sock], [], [], .002)
            if not readable:
                continue
            data, addr = sock.recvfrom(2048)
            if addr != address:
                invalid += 1
            elif data == prime:
                prime_seen += 1
            elif len(data) == 224 and data[:64] == token:
                packet_phase, seq = struct.unpack('!II', data[64:72])
                if packet_phase != phase or seq >= 1000:
                    invalid += 1
                    continue
                seen.add(seq)
                if role == 'server':
                    try:
                        sock.sendto(data, address)
                        sent += 1
                    except OSError:
                        errors += 1
            else:
                invalid += 1
        result = {'phase': phase, 'primed': primed, 'sourcePort': source,
                  'destinationPort': destination, 'sent': sent,
                  'receivedUnique': len(seen), 'invalid': invalid,
                  'sendErrors': errors, 'primeSent': prime_sent,
                  'primeSeen': prime_seen,
                  'startUtc': datetime.datetime.fromtimestamp(
                      begin, datetime.timezone.utc).isoformat()}
        results.append(result)
        print(json.dumps({'role': role, **result}), flush=True)
finally:
    for sock in sockets.values():
        sock.close()
    for rule in reversed(rules):
        subprocess.run(['iptables', '-D', *rule], check=True)
    remaining = sum(tag in line for line in subprocess.check_output(
        ['iptables-save'], text=True).splitlines() if line.startswith('-A '))
    print(json.dumps({'role': role, 'cleanupRulesRemaining': remaining,
                      'phasesCompleted': len(results)}), flush=True)
