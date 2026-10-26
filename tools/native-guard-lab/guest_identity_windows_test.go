//go:build windows

package main

import("testing";"encoding/binary")
func firmwareFixture()[]byte{
 raw:=make([]byte,8+26);raw[1]=3;binary.LittleEndian.PutUint32(raw[4:8],26);raw[8]=1;raw[9]=24
 copy(raw[16:32],[]byte{0x56,0x4d,0x4a,0xe2,0xd9,0xc1,0xaa,0x21,0xde,0x2e,0x85,0x72,0xc9,0xf7,0xfe,0x83});return raw
}
func TestFirmwareUUIDMatchesCimByteOrder(t *testing.T){
 id,e:=smbiosUUID(firmwareFixture());if e!=nil||id!="e24a4d56-c1d9-21aa-de2e-8572c9f7fe83"{t.Fatalf("UUID differs from CIM: %q %v",id,e)}
}
func TestFirmwareRefusesMalformedIdentity(t *testing.T){
 for _,change:=range []func([]byte){func(b []byte){b[1]=1},func(b []byte){b[4]=255},func(b []byte){b[9]=3},func(b []byte){b[9]=255},func(b []byte){b[len(b)-1]=1},func(b []byte){clear(b[16:32])},func(b []byte){for i:=16;i<32;i++{b[i]=255}},func(b []byte){b[8]=127}}{
  raw:=firmwareFixture();change(raw);if _,e:=smbiosUUID(raw);e==nil{t.Fatal("malformed or unset identity accepted")}
 }
 for n:=0;n<34;n++{if _,e:=smbiosUUID(firmwareFixture()[:n]);e==nil{t.Fatal("truncated table accepted")}}
}
