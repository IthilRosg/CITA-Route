package main
import("sort";"strings";"unsafe";"golang.org/x/sys/windows")
func dnsNetworkIdentity()string{
 size:=uint32(32768)
 for attempt:=0;attempt<3;attempt++{
  if size>1048576{return "unavailable"};buffer:=make([]byte,size);first:=(*windows.IpAdapterAddresses)(unsafe.Pointer(&buffer[0]))
  e:=windows.GetAdaptersAddresses(windows.AF_UNSPEC,windows.GAA_FLAG_INCLUDE_GATEWAYS,0,first,&size)
  if e==windows.ERROR_BUFFER_OVERFLOW{continue};if e!=nil{return "unavailable"};values:=[]string{dnsInterfaceIdentity()}
  for nic:=first;nic!=nil;nic=nic.Next{if nic.OperStatus!=1||nic.IfType==24{continue};key:=windows.BytePtrToString(nic.AdapterName)
   for address:=nic.FirstDnsServerAddress;address!=nil;address=address.Next{values=append(values,key+"|dns|"+address.Address.IP().String())}
   for address:=nic.FirstGatewayAddress;address!=nil;address=address.Next{values=append(values,key+"|gateway|"+address.Address.IP().String())}
  };sort.Strings(values);return strings.Join(values,";")
 };return "unavailable"
}
