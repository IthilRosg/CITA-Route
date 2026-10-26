//go:build windows

package main

import (
 "encoding/binary"
 "errors"
 "fmt"
 "unsafe"
 "golang.org/x/sys/windows"
)

// Read the same SMBIOS Type 1 UUID used by Win32_ComputerSystemProduct,
// without starting WMI/PowerShell on every lab-only guard inspection.
func smbiosUUID(raw []byte)(string,error){
 if len(raw)<8||raw[1]<2||raw[1]==2&&raw[2]<6{return "",errors.New("unsupported SMBIOS header")}
 size:=int(binary.LittleEndian.Uint32(raw[4:8]));if size<4||size>len(raw)-8{return "",errors.New("SMBIOS length")};table:=raw[8:8+size]
 for at:=0;at+4<=len(table);{
  kind,n:=table[at],int(table[at+1]);if n<4||n>len(table)-at{return "",errors.New("SMBIOS structure length")}
  end:=at+n;for end+1<len(table)&&(table[end]!=0||table[end+1]!=0){end++};if end+1>=len(table){return "",errors.New("SMBIOS string termination")}
  if kind==1{
   if n<24{return "",errors.New("SMBIOS system UUID absent")};id:=table[at+8:at+24];zero,ones:=true,true;for _,b:=range id{zero=zero&&b==0;ones=ones&&b==255};if zero||ones{return "",errors.New("SMBIOS UUID unset")}
   return fmt.Sprintf("%08x-%04x-%04x-%x-%x",binary.LittleEndian.Uint32(id[:4]),binary.LittleEndian.Uint16(id[4:6]),binary.LittleEndian.Uint16(id[6:8]),id[8:10],id[10:]),nil
  }
  if kind==127{break};at=end+2
 }
 return "",errors.New("SMBIOS system information absent")
}
func nativeGuestUUID()(string,error){
 get:=windows.NewLazySystemDLL("kernel32.dll").NewProc("GetSystemFirmwareTable")
 size,_,e:=get.Call(0x52534d42,0,0,0);if size<8||size>1048576{return "",fmt.Errorf("firmware query: %v",e)}
 raw:=make([]byte,int(size));read,_,e:=get.Call(0x52534d42,0,uintptr(unsafe.Pointer(&raw[0])),size);if read<8||read>size{return "",fmt.Errorf("firmware read: %v",e)}
 return smbiosUUID(raw[:int(read)])
}
