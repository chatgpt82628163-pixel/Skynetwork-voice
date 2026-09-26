// SkyNetwork voice server: radio relay over UDP.
//
// The server never decodes audio. Clients send Opus frames tagged with the transceivers they
// transmit on; the server forwards each frame to every client that has a transceiver tuned to
// the same frequency within VHF line-of-sight range, together with a signal strength (0..1)
// that the receiving client uses to apply static, squelch and radio band-pass effects.
//
// Wire format: every datagram starts with 'S' 'K' <version=1> <type>, integers big-endian,
// strings are <u8 length><bytes>, f64/f32 are IEEE-754 big-endian.
//
//   AUTH        c->s  u32 cid, str callsign, str password
//   AUTH_OK     s->c  u32 token
//   AUTH_FAIL   s->c  str reason
//   TRANSCEIVERS c->s u32 token, u8 n, n * { u8 id, u32 freq_hz, f64 lat, f64 lon, f64 alt_ft }
//                     [, n * f32 range_nm]   optional: a controller's coverage beyond the radio horizon
//   AUDIO       c->s  u32 token, u32 seq, u8 last, u8 n, n * u8 tx_id, opus payload...
//   AUDIO_RX    s->c  u32 seq, u8 last, str callsign, u8 n, n * { u32 freq_hz, f32 strength }, opus...
//   KEEPALIVE   c->s  u32 token            -> KEEPALIVE_ACK s->c (empty)
//   BYE         c->s  u32 token
//   KICK        s->c  str reason       (the session is closed: account suspended or deleted)
#pragma once
#include <netinet/in.h>

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

#include "accounts.h"

namespace skynet {

namespace voice {
enum Type : uint8_t {
    AUTH = 1, AUTH_OK = 2, AUTH_FAIL = 3, TRANSCEIVERS = 4, AUDIO = 5, AUDIO_RX = 6,
    KEEPALIVE = 7, KEEPALIVE_ACK = 8, BYE = 9, KICK = 10,
};
constexpr uint8_t kVersion = 1;
constexpr size_t kMaxTransceivers = 8;
constexpr size_t kMaxDatagram = 1500;
}  // namespace voice

struct Transceiver {
    uint8_t id;
    uint32_t freq_hz;
    double lat, lon, alt_ft;
    double range_nm = 0;  // a controller's coverage (from its sector border); 0: the radio horizon only
};

struct VoiceSession {
    uint32_t token;
    int cid;
    std::string callsign;
    sockaddr_in addr;
    int64_t last_rx_ms;
    std::vector<Transceiver> transceivers;
    // A transmission has been described in the log (once per transmission, reset by its last packet).
    bool tx_logged = false;
};

class VoiceServer {
public:
    // account_check_ms: how often connected members are re-checked against the database, so a
    // member suspended on the website loses the radio within that time.
    VoiceServer(Accounts& accounts, std::string host, uint16_t port, int account_check_ms = 10000);
    ~VoiceServer();
    void bind();
    void run();
    uint16_t port() const;

private:
    void on_datagram(const uint8_t* data, size_t len, const sockaddr_in& from);
    void on_auth(const uint8_t* p, size_t len, const sockaddr_in& from);
    void on_audio(VoiceSession& s, const uint8_t* p, size_t len);
    void log_transmission(const VoiceSession& s, const std::vector<const Transceiver*>& tx) const;
    VoiceSession* session_for(uint32_t token, const sockaddr_in& from);
    void send_to(const sockaddr_in& to, const std::vector<uint8_t>& pkt);
    void check_accounts();

    Accounts& accounts_;
    std::string host_;
    uint16_t port_;
    int account_check_ms_;
    int fd_ = -1;
    std::unordered_map<uint32_t, VoiceSession> sessions_;
};

}  // namespace skynet
