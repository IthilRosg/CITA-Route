import socket,time,threading,json,struct,sys,os,hmac,hashlib
key=sys.stdin.readline().strip().encode();assert len(key)==64
rate=int(sys.argv[1]) if len(sys.argv)>1 else 50
port=int(sys.argv[2]) if len(sys.argv)>2 else 49214
target=sys.argv[3] if len(sys.argv)>3 else '143.20.37.241'
size=int(sys.argv[4]) if len(sys.argv)>4 else 224
mode=sys.argv[5] if len(sys.argv)>5 else 'original'
assert mode in ('original','random-auth')
assert rate in (4,8,16,50,100) and port in (49214,49215)
assert target in ('143.20.37.241','138.124.250.203')
assert size in (96,224,1024)
count=20*rate
u=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);u.connect((target,port));u.settimeout(.5);seen=set();bad=0;done=threading.Event()
def receive():
 global bad
 while not done.is_set():
  try:
   b=u.recv(2048)
   valid=(b[:64]==key) if mode=='original' else hmac.compare_digest(b[-32:],hmac.new(key,b[:-32],hashlib.sha256).digest())
   if len(b)==size and valid:seen.add(struct.unpack('<I',b[64:68] if mode=='original' else b[:4])[0])
   else:bad+=1
  except socket.timeout:continue
  except OSError:return
th=threading.Thread(target=receive);th.start();start=time.monotonic();delays=[]
for i in range(count):
 due=start+i/rate;time.sleep(max(0,due-time.monotonic()));delays.append(time.monotonic()-due)
 if mode=='original':packet=key+struct.pack('<I',400000+i)+bytes(size-68)
 else:
  body=struct.pack('<I',400000+i)+os.urandom(size-36);packet=body+hmac.new(key,body,hashlib.sha256).digest()
 u.send(packet)
time.sleep(2);done.set();th.join();u.close();print(json.dumps({'scope':'local OS UDP between controlled servers, no CITA, 20s','target':target,'rate':rate,'port':port,'payloadBytes':size,'mode':mode,'sent':count,'receivedUnique':len(seen),'invalid':bad,'maxPacingDelayMs':round(max(delays)*1000,3)}),flush=True)
