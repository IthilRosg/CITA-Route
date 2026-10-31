"""Temporary restricted TCP/UDP measurement server; stdout contains a test token."""
import ipaddress
import os
import secrets
import subprocess
import sys
import time

direct, vpn = (str(ipaddress.IPv4Address(x)) for x in sys.argv[1:3])
if direct == vpn:
    raise SystemExit("Paths must have distinct source addresses")
port = int(sys.argv[3]) if len(sys.argv) > 3 else 49214
if port not in (49214, 49215):
    raise SystemExit("Unapproved measurement port")
bind_address = str(ipaddress.IPv4Address(sys.argv[4])) if len(sys.argv) > 4 else "0.0.0.0"
if bind_address not in ("0.0.0.0", "138.124.250.203", "143.20.37.241"):
    raise SystemExit("Unapproved bind address")
mode = sys.argv[5] if len(sys.argv) > 5 else "both"
if mode not in ("both", "udp-only"):
    raise SystemExit("Unapproved receiver mode")
token = secrets.token_hex(32)
tag = "cita-voice-" + secrets.token_hex(12)
marker = "/tmp/" + tag
worker = r'''
import os, signal, socket, struct, subprocess, sys, threading, time, hmac, hashlib
direct,vpn,tag,marker,port,bind_address,mode=sys.argv[1:];port=int(port)
token=sys.stdin.readline(65).strip()
if len(token)!=64 or any(c not in '0123456789abcdef' for c in token):raise SystemExit('Invalid session token')
peers={direct,vpn};rules=[];sockets=[]
udp_counts={direct:[0,0,0],vpn:[0,0,0]};last_report=time.monotonic()
udp_rejects={'source':0,'length':0,'auth':0};udp_delivered=0
def record(stage,count=0):
 try:
  fd=os.open(marker+'.diag',os.O_WRONLY|os.O_CREAT|os.O_APPEND,0o600)
  with os.fdopen(fd,'a') as f:f.write(stage+' '+str(count)+'\n')
 except OSError:pass
def stop(*_): raise SystemExit()
signal.signal(signal.SIGALRM,stop);signal.signal(signal.SIGTERM,stop);signal.alarm(550)
try:
 udp=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);udp.bind((bind_address,port));udp.settimeout(1);sockets.append(udp)
 protocols=('udp',) if mode=='udp-only' else ('udp','tcp')
 if mode=='both':
  tcp=socket.socket(socket.AF_INET,socket.SOCK_STREAM);tcp.setsockopt(socket.SOL_SOCKET,socket.SO_REUSEADDR,1);tcp.bind((bind_address,port));tcp.settimeout(1);sockets.append(tcp)
 for proto in protocols:
  rule=['INPUT','-p',proto,'--dport',str(port),'-m','comment','--comment',tag,'-j','DROP']
  subprocess.run(['iptables','-I',rule[0],'1',*rule[1:]],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);rules.append(rule)
 for peer in peers:
  for proto in protocols:
   rule=['INPUT','-s',peer+'/32','-p',proto,'--dport',str(port),'-m','comment','--comment',tag,'-j','ACCEPT']
   subprocess.run(['iptables','-I',rule[0],'1',*rule[1:]],check=True,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL);rules.append(rule)
 if mode=='both':tcp.listen(4)
 def handle(c,addr):
  with c:
   count=0;label='unknown';phase='unknown'
   def event(stage):record('tcp_'+label+'_p'+phase+'_'+stage,count)
   try:
    if addr[0] not in peers:record('peer_refused');return
    label='direct' if addr[0]==direct else 'vpn'
    c.settimeout(5);auth=b''
    while len(auth)<66:
     data=c.recv(66-len(auth))
     if not data:record('auth_eof',len(auth));return
     auth+=data
    if not secrets_compare(auth[:65],token.encode()+b'U') or auth[65:] not in (b'0',b'1',b'2',b'3'):record('auth_refused');return
    phase=auth[65:].decode('ascii');event('authenticated')
    def exact(n):
     data=b''
     while len(data)<n:
      part=c.recv(n-len(data))
      if not part:raise EOFError()
      data+=part
     return data
    while True:
     size=struct.unpack('>I',exact(4))[0]
     if size==0:c.sendall(struct.pack('>Q',count));event('ack_sent');return
     if size>262144:event('frame_limit');return
     count+=len(exact(size))
     if count>100_000_000:event('limit');return
   except (OSError,EOFError) as e:event(type(e).__name__)
 def secrets_compare(a,b):
  import hmac
  return hmac.compare_digest(a,b)
 def accept():
  while True:
   try:c,a=tcp.accept();threading.Thread(target=handle,args=(c,a),daemon=True).start()
   except socket.timeout:continue
   except OSError:return
 if mode=='both':threading.Thread(target=accept,daemon=True).start()
 with open(marker,'x') as f:f.write('ready')
 def valid_udp(data):
  return secrets_compare(data[:64],token.encode()) or hmac.compare_digest(data[-32:],hmac.new(token.encode(),data[:-32],hashlib.sha256).digest())
 while True:
  try:
   data,addr=udp.recvfrom(2048)
   udp_delivered+=1
   if addr[0] not in peers:udp_rejects['source']+=1
   elif len(data) not in (96,224,1024):udp_rejects['length']+=1
   elif not valid_udp(data):udp_rejects['auth']+=1
   if addr[0] in peers and len(data) in (96,224,1024) and valid_udp(data):
    counts=udp_counts[addr[0]];counts[0]+=1
    try:
     udp.sendto(data,addr);counts[1]+=1
    except OSError:counts[2]+=1
  except socket.timeout:pass
  if time.monotonic()-last_report>=10:
   record('udp_delivered',udp_delivered)
   for reason,value in udp_rejects.items():record('udp_reject_'+reason,value)
   for label,peer in (('direct',direct),('vpn',vpn)):
    for metric,value in zip(('received','sent','send_error'),udp_counts[peer]):record('udp_'+label+'_'+metric,value)
   last_report=time.monotonic()
finally:
 record('udp_delivered',udp_delivered)
 for reason,value in udp_rejects.items():record('udp_reject_'+reason,value)
 for label,peer in (('direct',direct),('vpn',vpn)):
  for metric,value in zip(('received','sent','send_error'),udp_counts[peer]):record('udp_'+label+'_'+metric,value)
 for s in sockets:s.close()
 for rule in reversed(rules):subprocess.run(['iptables','-D',*rule],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
 try:os.unlink(marker)
 except OSError:pass
 record('receiver_finalized',1)
'''
child=subprocess.Popen([sys.executable,'-c',worker,direct,vpn,tag,marker,str(port),bind_address,mode],stdin=subprocess.PIPE,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
child.stdin.write((token+'\n').encode('ascii'));child.stdin.close()
for _ in range(50):
    if os.path.exists(marker):
        print(token + "|" + tag)
        break
    time.sleep(.1)
else:
    raise SystemExit("Restricted server did not become ready")
