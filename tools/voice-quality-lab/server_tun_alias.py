"""Bounded test-only DNAT alias for one existing authenticated receiver."""
import sys,re,subprocess,pathlib,time,json
tag=sys.argv[1]
if not re.fullmatch(r'cita-voice-[a-f0-9]{24}',tag):raise SystemExit('Invalid test tag')
if not pathlib.Path('/tmp/'+tag).exists():raise SystemExit('Receiver not active')
worker=r'''
import sys,pathlib,signal,subprocess,time
tag=sys.argv[1];marker=pathlib.Path('/tmp/'+tag);ready=pathlib.Path('/tmp/'+tag+'.alias')
rule=['PREROUTING','-i','awg-cita2','-s','10.79.32.13/32','-d','1.1.1.1/32','-p','udp','--dport','49214','-m','comment','--comment',tag,'-j','DNAT','--to-destination','138.124.250.203:49214']
def stop(*_):raise SystemExit()
signal.signal(signal.SIGTERM,stop);signal.signal(signal.SIGALRM,stop);signal.alarm(500)
added=False
try:
 subprocess.run(['iptables','-t','nat','-I',rule[0],'1',*rule[1:]],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);added=True;ready.write_text('ready')
 while marker.exists():time.sleep(1)
finally:
 if added:subprocess.run(['iptables','-t','nat','-D',*rule],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 ready.unlink(missing_ok=True)
'''
subprocess.Popen([sys.executable,'-c',worker,tag],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
for _ in range(40):
 if pathlib.Path('/tmp/'+tag+'.alias').exists():print(json.dumps({'aliasReady':True,'lifetimeSeconds':500,'scope':'one peer/interface/destination/UDP port'}));break
 time.sleep(.1)
else:raise SystemExit('Alias not ready')
