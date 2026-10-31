"""Temporary source-pinned alias to the existing DE test VPN listener."""
import ipaddress
import json
import pathlib
import re
import subprocess
import sys
import time

tag = sys.argv[1]
assert re.fullmatch(r'cita-voice-[a-f0-9]{24}', tag)
assert pathlib.Path('/tmp/' + tag).exists()
source = None
for line in subprocess.check_output(['awg', 'show', 'all', 'dump'], text=True).splitlines():
    fields = line.split('\t')
    if len(fields) >= 9 and '10.77.20.5/32' in fields[4].split(','):
        source = str(ipaddress.IPv4Address(fields[3].rsplit(':', 1)[0]))
        break
assert source and ipaddress.ip_address(source).is_global
worker = r'''
import sys,pathlib,signal,subprocess,time
tag,source=sys.argv[1:];marker=pathlib.Path('/tmp/'+tag);ready=pathlib.Path('/tmp/'+tag+'.alias')
rule=['PREROUTING','-i','ens3','-s',source+'/32','-d','143.20.37.241/32','-p','udp','--dport','49214','-m','comment','--comment',tag,'-j','REDIRECT','--to-ports','3081']
def stop(*_):raise SystemExit()
signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGALRM,stop);signal.alarm(150)
added=False
try:
 subprocess.run(['iptables','-t','nat','-I',rule[0],'1',*rule[1:]],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);added=True;ready.write_text('ready')
 while marker.exists():time.sleep(.25)
finally:
 if added:subprocess.run(['iptables','-t','nat','-D',*rule],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 ready.unlink(missing_ok=True)
'''
subprocess.Popen([sys.executable, '-c', worker, tag, source],
                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)
for _ in range(40):
    if pathlib.Path('/tmp/' + tag + '.alias').exists():
        print(json.dumps({'ready': True, 'source': source, 'publicPort': 49214,
                          'existingVpnPort': 3081, 'lifetimeSeconds': 150}))
        break
    time.sleep(.1)
else:
    raise SystemExit('VPN port alias did not become ready')
