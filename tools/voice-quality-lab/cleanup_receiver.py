import pathlib,os,signal,sys,subprocess,shlex,time,json,re
x=sys.argv[1]
if not re.fullmatch(r'cita-voice-[a-f0-9]{24}',x):raise SystemExit('Invalid tag')
stopping=[]
for p in pathlib.Path('/proc').iterdir():
 if not p.name.isdigit():continue
 try:a=(p/'cmdline').read_bytes().split(b'\0')
 except OSError:continue
 if x.encode() in a and any(b'direct,vpn,tag,marker' in z or b"rule=['PREROUTING'" in z for z in a):
  stopping.append(p)
  try:os.kill(int(p.name),signal.SIGTERM)
  except ProcessLookupError:pass
def live(p):
 try:return (p/'stat').read_text().rsplit(')',1)[1].split()[0] != 'Z'
 except FileNotFoundError:return False
deadline=time.monotonic()+5
while any(live(p) for p in stopping) and time.monotonic()<deadline:time.sleep(.05)
receiver_stopped=not any(live(p) for p in stopping)
# Read AFTER termination/finally has flushed the final counters, not the last
# periodic snapshot. Reading before SIGTERM undercounts up to ten seconds.
p=pathlib.Path('/tmp/'+x+'.diag');lines=p.read_text().splitlines() if p.exists() else [];counts={};finalized=False
for l in lines:
 a=l.split()
 if len(a)==2 and a[0].startswith('udp_'):counts[a[0]]=int(a[1])
 if a==['receiver_finalized','1']:finalized=True
for l in subprocess.check_output(['iptables-save'],text=True).splitlines():
 if l.startswith('-A INPUT ') and x in shlex.split(l):
  a=shlex.split(l);a[0]='-D';subprocess.run(['iptables',*a],check=True)
remaining=sum(x in shlex.split(l) for l in subprocess.check_output(['iptables-save'],text=True).splitlines() if l.startswith('-A '))
for l in subprocess.check_output(['iptables-save','-t','nat'],text=True).splitlines():
 if l.startswith('-A PREROUTING ') and x in shlex.split(l):
  a=shlex.split(l);a[0]='-D';subprocess.run(['iptables','-t','nat',*a],check=True)
remaining_nat=sum(x in shlex.split(l) for l in subprocess.check_output(['iptables-save','-t','nat'],text=True).splitlines() if l.startswith('-A '))
print(json.dumps({'counters':counts,'receiverStopped':receiver_stopped,'finalCountersConfirmed':receiver_stopped and finalized,'remainingRules':remaining,'remainingNatRules':remaining_nat,'markerExists':pathlib.Path('/tmp/'+x).exists(),'aliasMarkerExists':pathlib.Path('/tmp/'+x+'.alias').exists()}))
