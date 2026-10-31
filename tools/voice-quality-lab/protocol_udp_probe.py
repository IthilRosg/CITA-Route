"""Executed after stun_codec.py by the SSH runner. Same tuple, A-B-B-A.

96-byte plain authenticated echo versus STUN Binding; 1000 requests per phase.
No retransmissions: this measures individual datagram delivery, not ICE setup.
"""
import datetime
import hashlib
import hmac
import json
import select
import signal
import subprocess
import sys
import time

cfg = json.loads(sys.stdin.readline())
role, start, token, tag = cfg['role'], cfg['start'], cfg['token'].encode(), cfg['tag']
assert role in ('client', 'server') and len(token) == 64
assert tag.startswith('cita-proto-') and tag[11:].isalnum()
assert time.time() < start < time.time() + 60
se, de = '138.124.250.203', '143.20.37.241'
local, peer = (se, de) if role == 'client' else (de, se)
local_port, peer_port = (49215, 49214) if role == 'client' else (49214, 49215)
address = (peer, peer_port)
rules, results, cleanup_errors = [], [], []
sock = None


def stop(*_):
    raise SystemExit('bounded test stopped')


signal.signal(signal.SIGTERM, stop)
signal.signal(signal.SIGALRM, stop)
signal.alarm(125)
try:
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((local, local_port))
    sock.setblocking(False)
    rule = ['INPUT', '-s', peer + '/32', '-d', local + '/32', '-p', 'udp',
            '--sport', str(peer_port), '--dport', str(local_port),
            '-m', 'comment', '--comment', tag, '-j', 'ACCEPT']
    subprocess.run(['iptables', '-I', 'INPUT', '1', *rule[1:]], check=True)
    rules.append(rule)
    start_mono = time.monotonic() + start - time.time()
    print(json.dumps({'role': role, 'ready': True,
                      'readyUtc': datetime.datetime.now(datetime.timezone.utc).isoformat()}), flush=True)
    for phase, mode in enumerate(('plain', 'stun', 'stun', 'plain')):
        packets, transactions = [], {}
        for seq in range(1000):
            nonce = hmac.new(token, struct.pack('!II', phase, seq), hashlib.sha256).digest()[:12]
            transactions[nonce] = seq
            packet = (stun_build(1, nonce, token) if mode == 'stun' else
                      token + struct.pack('!II', phase, seq) + bytes(24))
            assert len(packet) == 96
            packets.append(packet)
        begin = start_mono + phase * 23
        seen, invalid, sent, errors, duplicates = set(), 0, 0, 0, 0
        pacing, send_times = [], []
        while time.monotonic() < begin:
            time.sleep(min(.01, max(0, begin - time.monotonic())))
        actual_begin = time.time()
        while time.monotonic() < begin + 22:
            now = time.monotonic()
            if role == 'client' and sent < 1000 and now >= begin + sent / 50:
                pacing.append(now - (begin + sent / 50))
                send_times.append(now)
                try:
                    sock.sendto(packets[sent], address)
                except OSError:
                    errors += 1
                sent += 1
            readable, _, _ = select.select([sock], [], [], .001)
            if not readable:
                continue
            data, addr = sock.recvfrom(2048)
            try:
                if addr != address or len(data) != 96:
                    raise ValueError('wrong peer or length')
                if mode == 'stun':
                    kind, nonce, attrs = stun_parse(data)
                    seq = transactions[nonce]
                    if kind != (1 if role == 'server' else 0x101):
                        raise ValueError('wrong STUN type')
                    if role == 'server':
                        if not hmac.compare_digest(attrs[0x8022], token):
                            raise ValueError('wrong lab session')
                        reply = stun_build(0x101, nonce, token[:52], addr)
                    elif stun_mapped(attrs) != (local, local_port):
                        raise ValueError('wrong mapped address')
                else:
                    if not hmac.compare_digest(data[:64], token):
                        raise ValueError('wrong lab session')
                    packet_phase, seq = struct.unpack('!II', data[64:72])
                    if packet_phase != phase or seq >= 1000:
                        raise ValueError('wrong sequence')
                    reply = data
                if seq in seen:
                    duplicates += 1
                seen.add(seq)
                if role == 'server':
                    assert len(reply) == len(data)
                    try:
                        sock.sendto(reply, address)
                        sent += 1
                    except OSError:
                        errors += 1
            except (ValueError, KeyError, struct.error):
                invalid += 1
        result = {'phase': phase, 'mode': mode, 'sent': sent,
                  'receivedUnique': len(seen), 'invalid': invalid,
                  'sendErrors': errors, 'duplicates': duplicates,
                  'maxPacingDelayMs': round(max(pacing, default=0) * 1000, 3),
                  'sendSpanSeconds': round(send_times[-1] - send_times[0], 6) if send_times else None,
                  'actualStartUtc': datetime.datetime.fromtimestamp(
                      actual_begin, datetime.timezone.utc).isoformat()}
        results.append(result)
        print(json.dumps({'role': role, **result}), flush=True)
finally:
    if sock:
        sock.close()
    for rule in reversed(rules):
        removed = subprocess.run(['iptables', '-D', *rule], capture_output=True)
        if removed.returncode:
            cleanup_errors.append(removed.returncode)
    remaining = sum(tag in line for line in subprocess.check_output(
        ['iptables-save'], text=True).splitlines() if line.startswith('-A '))
    print(json.dumps({'role': role, 'cleanupRulesRemaining': remaining,
                      'cleanupErrors': cleanup_errors,
                      'phasesCompleted': len(results)}), flush=True)
