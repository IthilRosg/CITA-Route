"""Small IPv4 Binding codec for a bounded lab, not a public STUN service.

RFC 8489: header, SOFTWARE, XOR-MAPPED-ADDRESS, FINGERPRINT.
Does not implement ICE, retransmission, or MESSAGE-INTEGRITY.
"""
import socket
import struct
import zlib

COOKIE = 0x2112A442


def stun_attr(kind, value):
    return struct.pack('!HH', kind, len(value)) + value + bytes((-len(value)) % 4)


def stun_build(kind, transaction, software, mapped=None):
    assert len(transaction) == 12 and len(software.decode('utf8')) < 128
    attrs = stun_attr(0x8022, software)
    if mapped:
        ip, port = mapped
        attrs += stun_attr(0x0020, struct.pack(
            '!BBHI', 0, 1, port ^ (COOKIE >> 16),
            struct.unpack('!I', socket.inet_aton(ip))[0] ^ COOKIE))
    head = struct.pack('!HHI', kind, len(attrs) + 8, COOKIE) + transaction
    packet = head + attrs
    return packet + stun_attr(0x8028, struct.pack('!I', zlib.crc32(packet) ^ 0x5354554e))


def stun_parse(packet):
    if len(packet) < 28:
        raise ValueError('short STUN')
    kind, length, cookie = struct.unpack('!HHI', packet[:8])
    if kind & 0xc000 or cookie != COOKIE or length % 4 or length != len(packet) - 20:
        raise ValueError('invalid STUN header')
    attrs, offset = {}, 20
    while offset < len(packet):
        if offset + 4 > len(packet):
            raise ValueError('truncated attribute')
        code, size = struct.unpack('!HH', packet[offset:offset+4])
        end = offset + 4 + size
        if end > len(packet) or code in attrs:
            raise ValueError('invalid attribute size/duplicate')
        attrs[code] = packet[offset+4:end]
        if code == 0x8028:
            if size != 4 or end != len(packet):
                raise ValueError('FINGERPRINT must be last')
            expected = struct.pack('!I', zlib.crc32(packet[:offset]) ^ 0x5354554e)
            if attrs[code] != expected:
                raise ValueError('invalid FINGERPRINT')
        offset = end + (-size) % 4
    if offset != len(packet) or 0x8028 not in attrs:
        raise ValueError('missing FINGERPRINT or padding')
    return kind, packet[8:20], attrs


def stun_mapped(attrs):
    reserved, family, port, ip = struct.unpack('!BBHI', attrs[0x0020])
    if reserved or family != 1:
        raise ValueError('not IPv4 XOR-MAPPED-ADDRESS')
    return socket.inet_ntoa(struct.pack('!I', ip ^ COOKIE)), port ^ (COOKIE >> 16)
