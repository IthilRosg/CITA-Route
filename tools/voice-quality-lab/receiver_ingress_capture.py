import subprocess,json,sys,pathlib,time,ipaddress,os
peer=str(ipaddress.IPv4Address(sys.argv[1]));route=json.loads(subprocess.check_output(['ip','-j','route','get',peer],text=True))[0];dev=route['dev'];base='/tmp/cita-de-ingress-'+str(int(time.time()))
worker=r'''
import subprocess,json,sys,time,pathlib
base,dev,peer=sys.argv[1:]
def counters():
 lines=pathlib.Path('/proc/net/snmp').read_text().splitlines()
 for i,l in enumerate(lines):
  if l.startswith('Udp:'):return dict(zip(l.split()[1:],map(int,lines[i+1].split()[1:])))
before=counters()
with open(base+'.txt','wb') as out,open(base+'.err','wb') as err:
 p=subprocess.run(['timeout','105','tcpdump','-n','-tt','-l','-vv','-i',dev,'udp and host '+peer+' and port 49214'],stdout=out,stderr=err)
after=counters();pathlib.Path(base+'.json').write_text(json.dumps({'before':before,'after':after,'delta':{k:after[k]-before[k] for k in before},'captureExit':p.returncode}))
'''
subprocess.Popen([sys.executable,'-c',worker,base,dev,peer],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
print(json.dumps({'base':base,'interface':dev,'durationSeconds':105}))
