import ipaddress, json, socket, struct, sys, time

request=json.load(sys.stdin)
peer=str(ipaddress.IPv4Address(request['peer']))
sock=socket.socket(socket.AF_PACKET,socket.SOCK_DGRAM,socket.htons(0x0003))
sock.bind(('awg-cita2',0));sock.settimeout(.3)
began=time.monotonic();events=[]
print(json.dumps(dict(stage='ObservingOwnedPeerDNS')),flush=True)
while time.monotonic()-began < 55:
    try: packet=sock.recv(65535)
    except socket.timeout: continue
    if len(packet)<40 or packet[0]>>4!=4 or packet[9]!=17: continue
    offset=(packet[0]&15)*4
    if len(packet)<offset+20:continue
    src=socket.inet_ntoa(packet[12:16]);dst=socket.inet_ntoa(packet[16:20]);sp,dp=struct.unpack('!HH',packet[offset:offset+4])
    if src==peer and dp==53: direction='query';port=sp
    elif dst==peer and sp==53: direction='reply';port=dp
    else:continue
    if len(events)<512:events.append(dict(direction=direction,id=struct.unpack('!H',packet[offset+8:offset+10])[0],port=port,ms=round((time.monotonic()-began)*1000)))
sock.close();print(json.dumps(dict(stage='ObservedOwnedPeerDNS',events=events)),flush=True)
