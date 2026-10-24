package main

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"io"
	"net/netip"
	"os"
)

type captureSpec struct {
	File, Local, Endpoint string
	Port                  uint16
	Protected             []string
}
type captureResult struct {
	Success        bool `json:"success"`
	Packets        int  `json:"packets"`
	TunnelUDP      int  `json:"tunnelUdp"`
	OtherTCP       int  `json:"otherTcp"`
	OtherUDP       int  `json:"otherUdp"`
	Unsupported    int  `json:"unsupported"`
	IPv4           int  `json:"ipv4"`
	AllUDP         int  `json:"allUdp"`
	OtherSourceUDP int  `json:"otherSourceUdp"`
	ProtectedTCP   int  `json:"protectedTcp"`
	ProtectedUDP   int  `json:"protectedUdp"`
}

var errCapture = errors.New("unsupported or malformed capture")

func analyzeCapture(data []byte, s captureSpec) (r captureResult, e error) {
	local, e := netip.ParseAddr(s.Local)
	if e != nil || !local.Is4() {
		return r, errCapture
	}
	endpoint, e := netip.ParseAddr(s.Endpoint)
	if e != nil || !endpoint.Is4() || s.Port == 0 {
		return r, errCapture
	}
	if len(data) > 32*1024*1024 {
		return r, errCapture
	}
	protected := map[netip.Addr]bool{}
	if len(s.Protected) > 8 {
		return r, errCapture
	}
	for _, value := range s.Protected {
		ip, err := netip.ParseAddr(value)
		if err != nil || !publicTarget(ip) || ip == endpoint || protected[ip] {
			return r, errCapture
		}
		protected[ip] = true
	}
	var links []uint16
	section := false
	for len(data) > 0 {
		if len(data) < 12 {
			return r, errCapture
		}
		kind := binary.LittleEndian.Uint32(data)
		length := int(binary.LittleEndian.Uint32(data[4:]))
		if length < 12 || length%4 != 0 || length > len(data) || length > 1024*1024 || binary.LittleEndian.Uint32(data[length-4:]) != uint32(length) {
			return r, errCapture
		}
		block := data[:length]
		data = data[length:]
		switch kind {
		case 0x0a0d0d0a:
			if length < 28 || binary.LittleEndian.Uint32(block[8:]) != 0x1a2b3c4d {
				return r, errCapture
			}
			section = true
			links = nil
		case 1:
			if !section || length < 20 {
				return r, errCapture
			}
			links = append(links, binary.LittleEndian.Uint16(block[8:]))
		case 6:
			if !section || length < 32 {
				return r, errCapture
			}
			id := binary.LittleEndian.Uint32(block[8:])
			captured := int(binary.LittleEndian.Uint32(block[20:]))
			original := int(binary.LittleEndian.Uint32(block[24:]))
			if id >= uint32(len(links)) || captured < 0 || captured > length-32 || captured > original {
				return r, errCapture
			}
			packet := block[28 : 28+captured]
			r.Packets++
			if links[id] != 1 {
				r.Unsupported++
				continue
			}
			if len(packet) < 14 {
				r.Unsupported++
				continue
			}
			protocol := binary.BigEndian.Uint16(packet[12:])
			packet = packet[14:]
			if protocol == 0x8100 || protocol == 0x88a8 {
				if len(packet) < 4 {
					r.Unsupported++
					continue
				}
				protocol = binary.BigEndian.Uint16(packet[2:])
				packet = packet[4:]
			}
			if protocol != 0x0800 {
				continue
			}
			r.IPv4++
			if len(packet) < 20 || packet[0]>>4 != 4 {
				r.Unsupported++
				continue
			}
			header := int(packet[0]&15) * 4
			if header < 20 || len(packet) < header+4 {
				r.Unsupported++
				continue
			}
			var source, dest [4]byte
			copy(source[:], packet[12:16])
			copy(dest[:], packet[16:20])
			if packet[9] == 17 {
				r.AllUDP++
			}
			if netip.AddrFrom4(source) != local {
				if packet[9] == 17 {
					r.OtherSourceUDP++
				}
				continue
			}
			// Noninitial fragments lack ports and cannot certify the boundary.
			if binary.BigEndian.Uint16(packet[6:8])&0x1fff != 0 {
				r.Unsupported++
				continue
			}
			port := binary.BigEndian.Uint16(packet[header+2 : header+4])
			switch packet[9] {
			case 6:
				r.OtherTCP++
				if protected[netip.AddrFrom4(dest)] {
					r.ProtectedTCP++
				}
			case 17:
				if protected[netip.AddrFrom4(dest)] {
					r.ProtectedUDP++
				}
				if netip.AddrFrom4(dest) == endpoint && port == s.Port {
					r.TunnelUDP++
				} else {
					r.OtherUDP++
				}
			}
		case 2, 3:
			r.Unsupported++ // Do not silently ignore packet block formats.
		}
	}
	if !section || len(links) == 0 {
		return r, errCapture
	}
	r.Success = r.Unsupported == 0
	return r, nil
}
func captureMain() int {
	var s captureSpec
	if json.NewDecoder(io.LimitReader(os.Stdin, 4096)).Decode(&s) != nil {
		return 2
	}
	f, e := os.Open(s.File)
	if e != nil {
		return 2
	}
	defer f.Close()
	data, e := io.ReadAll(io.LimitReader(f, 32*1024*1024+1))
	if e != nil {
		return 2
	}
	r, e := analyzeCapture(data, s)
	if e != nil {
		r.Success = false
	}
	_ = json.NewEncoder(os.Stdout).Encode(r)
	if !r.Success {
		return 1
	}
	return 0
}
