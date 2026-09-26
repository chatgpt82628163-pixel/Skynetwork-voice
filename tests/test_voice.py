"""Integration tests: start the real skynet-voice binary and talk to them.

Run after building:  cmake -B build && make -C build && python3 -m unittest discover -s tests
"""
import os
import socket
import struct
import subprocess
import tempfile
import time
import unittest

BUILD = os.environ.get("SKYNET_BUILD", os.path.join(os.path.dirname(__file__), "..", "build"))


def free_port(kind=socket.SOCK_STREAM):
    s = socket.socket(socket.AF_INET, kind)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class Network(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = tempfile.TemporaryDirectory()
        cls.db = os.path.join(cls.tmp.name, "test.db")
        admin = os.path.join(BUILD, "skynet-admin")
        for args in (["1000001", "Pilot One", "pw1"], ["1000002", "Pilot Two", "pw2"],
                     ["1000003", "Controller", "pw3", "S3"], ["1000004", "Supervisor", "pw4", "SUP"],
                     ["1000005", "Rule Breaker", "pw5"]):
            subprocess.run([admin, "--db", cls.db, "adduser", *args], check=True, capture_output=True)
        cls.voice_port = free_port(socket.SOCK_DGRAM)
        cls.procs = [
            subprocess.Popen([os.path.join(BUILD, "skynet-voice"), "--db", cls.db, "--host", "127.0.0.1",
                              "--port", str(cls.voice_port), "--account-check", "1"], stderr=subprocess.DEVNULL),
        ]
        time.sleep(0.5)

    @classmethod
    def tearDownClass(cls):
        for p in cls.procs:
            p.terminate()
            p.wait()
        cls.tmp.cleanup()


def vpkt(t, body=b""):
    return b"SK\x01" + bytes([t]) + body


def vstr(s):
    return bytes([len(s)]) + s.encode()


class VoiceClient:
    def __init__(self, port, cid, callsign, password):
        self.addr = ("127.0.0.1", port)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.settimeout(1)
        self.sock.sendto(vpkt(1, struct.pack(">I", cid) + vstr(callsign) + vstr(password)), self.addr)
        data = self.sock.recv(1500)
        self.ok = data[3] == 2
        self.token = struct.unpack(">I", data[4:8])[0] if self.ok else None
        self.reason = data[5:5 + data[4]].decode() if not self.ok else None

    def tune(self, *xcvrs, ranges=None):
        body = struct.pack(">IB", self.token, len(xcvrs))
        for i, (freq, lat, lon, alt) in enumerate(xcvrs):
            body += struct.pack(">BIddd", i, freq, lat, lon, alt)
        for r in ranges or []:
            body += struct.pack(">f", r)
        self.sock.sendto(vpkt(4, body), self.addr)

    def talk(self, seq, payload, tx_ids=(0,)):
        body = struct.pack(">IIBB", self.token, seq, 0, len(tx_ids)) + bytes(tx_ids) + payload
        self.sock.sendto(vpkt(5, body), self.addr)

    def recv_audio(self):
        data = self.sock.recv(1500)
        assert data[3] == 6, "expected AUDIO_RX"
        seq, last = struct.unpack(">IB", data[4:9])
        n = data[9]
        callsign = data[10:10 + n].decode()
        pos = 10 + n
        count = data[pos]
        pos += 1
        rx = [struct.unpack(">If", data[pos + 8 * i:pos + 8 * i + 8]) for i in range(count)]
        return seq, callsign, rx, data[pos + 8 * count:]


class VoiceTest(Network):
    def test_radio_relay(self):
        bad = VoiceClient(self.voice_port, 1000001, "AFL1", "wrong")
        self.assertFalse(bad.ok)
        twr = VoiceClient(self.voice_port, 1000003, "UUEE_TWR", "pw3")
        near = VoiceClient(self.voice_port, 1000001, "AFL1", "pw1")
        far = VoiceClient(self.voice_port, 1000002, "SBI2", "pw2")
        other_freq = VoiceClient(self.voice_port, 1000004, "UTA3", "pw4")
        twr.tune((118100000, 55.97, 37.41, 200))
        near.tune((118100000, 56.20, 37.41, 3000))      # ~14 nm, within line of sight
        far.tune((118100000, 59.80, 30.26, 3000))       # ~330 nm, beyond the horizon
        other_freq.tune((121500000, 55.98, 37.41, 3000))
        time.sleep(0.2)
        twr.talk(7, b"OPUSFRAME")
        seq, callsign, rx, payload = near.recv_audio()
        self.assertEqual((seq, callsign, payload), (7, "UUEE_TWR", b"OPUSFRAME"))
        self.assertEqual(rx[0][0], 118100000)
        self.assertTrue(0 < rx[0][1] < 1)
        for c in (far, other_freq):
            with self.assertRaises(socket.timeout):
                c.sock.recv(1500)


    def test_controller_coverage_beyond_the_horizon(self):
        # A centre with 1000 nm of coverage is heard by an aircraft far beyond the radio horizon,
        # and hears it; without the coverage field the horizon still decides.
        ctr = VoiceClient(self.voice_port, 1000003, "UUWV_CTR", "pw3")
        low = VoiceClient(self.voice_port, 1000001, "AFL5", "pw1")
        ctr.tune((127500000, 55.97, 37.41, 100), ranges=[1000])
        low.tune((127500000, 59.80, 30.26, 1000))        # ~330 nm away at 1000 ft
        time.sleep(0.2)
        ctr.talk(1, b"HELLO")
        seq, callsign, rx, payload = low.recv_audio()
        self.assertEqual((callsign, payload), ("UUWV_CTR", b"HELLO"))
        self.assertTrue(0.5 < rx[0][1] < 0.8)
        low.talk(2, b"ROGER")
        self.assertEqual(ctr.recv_audio()[1], "AFL5")
        ctr.tune((127500000, 55.97, 37.41, 100))           # an old client: no coverage
        time.sleep(0.2)
        ctr.talk(3, b"AGAIN")
        with self.assertRaises(socket.timeout):
            low.sock.recv(1500)

    def test_suspension_kicks_and_blocks(self):
        admin = os.path.join(BUILD, "skynet-admin")
        c = VoiceClient(self.voice_port, 1000005, "BAD1", "pw5")
        self.assertTrue(c.ok)
        subprocess.run([admin, "--db", self.db, "suspend", "1000005"], check=True, capture_output=True)
        c.sock.settimeout(3)
        data = c.sock.recv(1500)
        self.assertEqual(data[3], 10)  # KICK
        self.assertEqual(data[5:5 + data[4]].decode(), "CID suspended")
        again = VoiceClient(self.voice_port, 1000005, "BAD1", "pw5")
        self.assertFalse(again.ok)
        self.assertEqual(again.reason, "CID suspended")
        # The kicked token is dead: keepalives get no answer.
        c.sock.settimeout(0.5)
        c.sock.sendto(vpkt(7, struct.pack(">I", c.token)), c.addr)
        with self.assertRaises(socket.timeout):
            c.sock.recv(1500)
        subprocess.run([admin, "--db", self.db, "unsuspend", "1000005"], check=True, capture_output=True)
        self.assertTrue(VoiceClient(self.voice_port, 1000005, "BAD1", "pw5").ok)


if __name__ == "__main__":
    unittest.main()
