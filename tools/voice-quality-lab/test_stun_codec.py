import unittest
from stun_codec import stun_build, stun_parse, stun_mapped


class StunCodecTests(unittest.TestCase):
    def test_rfc5769_request_fingerprint(self):
        packet = bytes.fromhex('''
            000100582112a442b7e7a701bc34d686fa87dfae
            802200105354554e207465737420636c69656e74
            002400046e0001ff80290008932ff9b151263b36
            000600096576746a3a68367659202020
            000800149aeaa70cbfd8cb56781ef2b5b2d3f249c1b571a2
            80280004e57a3bcf''')
        kind, _, attrs = stun_parse(packet)
        self.assertEqual(kind, 1)
        self.assertEqual(attrs[0x8022], b'STUN test client')

    def test_rfc5769_ipv4_response(self):
        packet = bytes.fromhex('''
            0101003c2112a442b7e7a701bc34d686fa87dfae
            8022000b7465737420766563746f7220
            002000080001a147e112a643
            000800142b91f599fd9e90c38c7489f92af9ba53f06be7d7
            80280004c07d4c96''')
        self.assertEqual(stun_mapped(stun_parse(packet)[2]), ('192.0.2.1', 32853))

    def test_lab_packets_same_size_and_reject_corruption(self):
        txn = bytes(range(12))
        request = stun_build(1, txn, b'a' * 64)
        response = stun_build(0x101, txn, b'a' * 52, ('138.124.250.203', 49215))
        self.assertEqual(len(request), 96)
        self.assertEqual(len(response), 96)
        self.assertEqual(stun_parse(response)[1], txn)
        self.assertEqual(stun_mapped(stun_parse(response)[2]), ('138.124.250.203', 49215))
        for damaged in (request[:-1], request[:25] + b'z' + request[26:]):
            with self.assertRaises(ValueError):
                stun_parse(damaged)


if __name__ == '__main__':
    unittest.main()
