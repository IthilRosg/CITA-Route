"""Bounded, mutually authenticated workload fixture; stdin carries test TLS keys."""
import hashlib, ipaddress, json, os, pathlib, re, shutil, subprocess, sys, time

request = json.load(sys.stdin)
run = request['run']
if not re.fullmatch('[a-f0-9]{32}', run):
    raise RuntimeError('Invalid fixture identity')
root = pathlib.Path('/tmp/cita-native-workload-' + run)
if root.is_symlink() or not root.is_dir() or root.resolve() != root or root.stat().st_uid != os.getuid():
    raise RuntimeError('Fixture directory identity mismatch')
image = root / 'server'
if request['action'] == 'start':
    if image.is_symlink() or hashlib.sha256(image.read_bytes()).hexdigest().upper() != request['sha256']:
        raise RuntimeError('Fixture binary integrity mismatch')
    os.chmod(image, 0o700)
    log = open(root / 'ready.json', 'x', encoding='utf-8')
    os.chmod(root / 'ready.json', 0o600)
    child = subprocess.Popen([str(image), '--serve'], stdin=subprocess.PIPE, stdout=log, stderr=subprocess.DEVNULL, start_new_session=True)
    child.stdin.write(json.dumps(request['spec']).encode())
    child.stdin.close()
    started = pathlib.Path(f'/proc/{child.pid}/stat').read_text().split()[21]
    peer = ipaddress.IPv4Address(request['peer'])
    interface = request.get('interface', 'awg-cita2')
    if interface == 'matching-awg-peer':
        candidates = []
        interfaces = subprocess.run(['/usr/local/bin/awg', 'show', 'interfaces'], capture_output=True, text=True, timeout=5, check=True).stdout.split()
        for candidate in interfaces:
            if not re.fullmatch(r'awg[a-zA-Z0-9_-]{1,24}', candidate):
                continue
            public_key = subprocess.run(['/usr/local/bin/awg', 'show', candidate, 'public-key'], capture_output=True, text=True, timeout=5, check=True).stdout.strip()
            if hashlib.sha256(public_key.encode()).hexdigest().upper() != request['serverPublicKeyHash']:
                continue
            allowed = subprocess.run(['/usr/local/bin/awg', 'show', candidate, 'allowed-ips'], capture_output=True, text=True, timeout=5, check=True).stdout.splitlines()
            if any(str(peer) + '/32' in row.split()[1:] for row in allowed):
                candidates.append(candidate)
        if len(candidates) != 1 or not peer.is_private:
            child.terminate()
            raise RuntimeError('Owned AWG interface and peer must match uniquely')
        interface = candidates[0]
        authorized_owned_peer = True
    else:
        authorized_owned_peer = False
    if not authorized_owned_peer and (interface not in ['awg-cita2', 'ens3'] or (interface == 'awg-cita2' and not peer.is_private) or (interface == 'ens3' and not peer.is_global)):
        child.terminate()
        raise RuntimeError('Private approved peer required')
    rules = [['INPUT', '-i', interface, '-s', str(peer) + '/32', '-p', protocol, '--dport', '49197', '-m', 'comment', '--comment', 'CITA-workload-' + run, '-j', 'ACCEPT'] for protocol in ['tcp', 'udp']]
    ledger = dict(pid=child.pid, start=started, image=str(image), root=str(root), rules=rules)
    (root / 'cleanup.json').write_text(json.dumps(ledger))
    os.chmod(root / 'cleanup.json', 0o600)
    safety = '''import json,os,pathlib,shutil,subprocess,time
root=pathlib.Path(__file__).parent
time.sleep(250)
if root.is_dir() and not root.is_symlink():
 s=json.loads((root/'cleanup.json').read_text())
 for rule in s['rules']:
  if subprocess.run(['iptables','-C']+rule,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0: subprocess.run(['iptables','-D']+rule,check=True)
 p=pathlib.Path('/proc/'+str(s['pid']))
 if p.exists() and os.readlink(p/'exe')==s['image'] and (p/'stat').read_text().split()[21]==s['start']: os.kill(s['pid'],15)
 shutil.rmtree(root)
'''
    (root / 'safety.py').write_text(safety)
    watcher = subprocess.Popen(['/usr/bin/python3', str(root / 'safety.py')], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
    ledger['watcherPid'] = watcher.pid
    ledger['watcherStart'] = pathlib.Path(f'/proc/{watcher.pid}/stat').read_text().split()[21]
    (root / 'cleanup.json').write_text(json.dumps(ledger))
    try:
        for rule in rules:
            subprocess.run(['iptables', '-I', rule[0], '1'] + rule[1:], check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception:
        child.terminate()
        for rule in rules:
            if subprocess.run(['iptables', '-C']+rule, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
                subprocess.run(['iptables', '-D']+rule, check=True)
        raise
    for _ in range(60):
        time.sleep(.1)
        if child.poll() is not None:
            raise RuntimeError('Fixture failed to start')
        if 'Ready' in (root / 'ready.json').read_text():
            print(json.dumps(dict(success=True, pid=child.pid, start=started, maximumLifetimeSeconds=240)))
            break
    else:
        child.terminate()
        raise RuntimeError('Fixture readiness deadline')
elif request['action'] == 'discard':
    if {p.name for p in root.iterdir()} - {'server'}:
        raise RuntimeError('Only an unstarted fixture may be discarded')
    if image.exists() and (image.is_symlink() or hashlib.sha256(image.read_bytes()).hexdigest().upper() != request['sha256']):
        raise RuntimeError('Unstarted fixture binary identity mismatch')
    shutil.rmtree(root)
    print(json.dumps(dict(success=not root.exists(), unstartedFixtureRemoved=True)))
elif request['action'] == 'stop':
    pid = int(request['pid'])
    proc = pathlib.Path(f'/proc/{pid}')
    if proc.exists():
        if os.readlink(proc / 'exe') != str(image) or (proc / 'stat').read_text().split()[21] != request['start']:
            raise RuntimeError('Refusing unrelated process')
        os.kill(pid, 15)
        for _ in range(50):
            time.sleep(.1)
            if not proc.exists() or (proc / 'stat').read_text().split()[2] == 'Z':
                break
        else:
            raise RuntimeError('Fixture process cleanup unconfirmed')
    ledger = json.loads((root / 'cleanup.json').read_text())
    counters = []
    snapshot = subprocess.run(['iptables-save', '-c', '-t', 'filter'], capture_output=True, text=True, timeout=5, check=True).stdout
    for line in snapshot.splitlines():
        if 'CITA-workload-' + run not in line:
            continue
        counts = re.match(r'^\[(\d+):(\d+)\]', line)
        protocol = re.search(r' -p (tcp|udp) ', line)
        if counts and protocol:
            counters.append(dict(protocol=protocol.group(1), packets=int(counts.group(1)), bytes=int(counts.group(2))))
    for rule in ledger['rules']:
        if subprocess.run(['iptables', '-C']+rule, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
            subprocess.run(['iptables', '-D']+rule, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if subprocess.run(['iptables', '-C']+rule, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL).returncode == 0:
            raise RuntimeError('Private test rule removal unconfirmed')
    watcher = pathlib.Path('/proc/' + str(ledger['watcherPid']))
    if watcher.exists() and (watcher / 'stat').read_text().split()[21] == ledger['watcherStart'] and str(root / 'safety.py').encode() in (watcher / 'cmdline').read_bytes().split(b'\0'):
        os.kill(ledger['watcherPid'], 15)
    shutil.rmtree(root)
    print(json.dumps(dict(success=not root.exists(), processStopped=True, directoryRemoved=not root.exists(), privatePeerRulesRemoved=True, testRuleCounters=counters)))
else:
    raise RuntimeError('Unknown fixture action')
