import json,subprocess,ipaddress,sys,time,os
inner=str(ipaddress.IPv4Address(sys.argv[1]));port=int(sys.argv[2]);target='143.20.37.241'
rows=subprocess.check_output(['awg','show','all','dump'],text=True).splitlines();peer=None;iface=None
for row in rows:
 a=row.split('\t')
 if len(a)>=9 and any(x.split('/')[0]==inner for x in a[4].split(',')):peer=a;iface=a[0];break
if peer is None:raise SystemExit('Test peer not found')
endpoint=peer[3];host,sport=endpoint.rsplit(':',1);host=str(ipaddress.IPv4Address(host));sport=int(sport)
# Metadata only: no -w, -X, -A, or payload output. Narrow tuple and inner test destination.
stamp=str(int(time.time()));base='/tmp/cita-localize-'+stamp
specs={'egress':('ens3',f'udp and host {target} and port 49214'),'inner':(iface,f'udp and host {inner} and host {target} and port 49214')}
for name,(dev,flt) in specs.items():
 out=open(base+'-'+name+'.txt','wb');err=open(base+'-'+name+'.err','wb');subprocess.Popen(['timeout','100','tcpdump','-n','-tt','-l','-q','-i',dev,flt],stdout=out,stderr=err,start_new_session=True);out.close();err.close()
print(json.dumps({'base':base,'interface':iface,'startedUtc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime()),'durationSeconds':100}))
