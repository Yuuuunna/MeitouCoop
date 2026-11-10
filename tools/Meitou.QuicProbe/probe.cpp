// SPDX-License-Identifier: GPL-3.0-or-later
// Isolated interoperability probe. No game assemblies or content are loaded.
#include <msquic.h>
#include <wincrypt.h>
#include <bcrypt.h>
#include <array>
#include <atomic>
#include <chrono>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <memory>
#include <mutex>
#include <sstream>
#include <stdexcept>
#include <thread>
#include <vector>

namespace fs = std::filesystem;
using Clock = std::chrono::steady_clock;
constexpr size_t MaxPacket = 4096, QueueCapacity = 32, MaxConnections = 16;
constexpr char Alpn[] = "meitou-platform-probe/1";
const QUIC_API_TABLE* Api;
std::atomic<int> Buffers{0}, StreamHandles{0}, ConnectionHandles{0};
std::atomic<unsigned> BuffersAllocated{0}, BuffersReleased{0}, QueueDrops{0};

void Check(QUIC_STATUS status, const char* op) {
    if (QUIC_FAILED(status)) {
        std::ostringstream s; s << op << " failed: 0x" << std::hex << static_cast<unsigned>(status);
        throw std::runtime_error(s.str());
    }
}
void WinCheck(BOOL value, const char* op) {
    if (!value) throw std::runtime_error(std::string(op) + " failed: " + std::to_string(GetLastError()));
}
std::vector<uint8_t> Read(const fs::path& path) {
    std::ifstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("Cannot open input file");
    return {std::istreambuf_iterator<char>(f), {}};
}
std::string Hash(const fs::path& path) {
    auto bytes = Read(path); BCRYPT_ALG_HANDLE alg{}; BCRYPT_HASH_HANDLE h{};
    if (BCryptOpenAlgorithmProvider(&alg, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0)
        throw std::runtime_error("SHA256 provider failed");
    std::array<uint8_t, 32> digest{};
    NTSTATUS status = BCryptCreateHash(alg, &h, nullptr, 0, nullptr, 0, 0);
    if (status >= 0) status = BCryptHashData(h, bytes.data(), static_cast<ULONG>(bytes.size()), 0);
    if (status >= 0) status = BCryptFinishHash(h, digest.data(), static_cast<ULONG>(digest.size()), 0);
    if (h) BCryptDestroyHash(h);
    BCryptCloseAlgorithmProvider(alg, 0);
    if (status < 0) throw std::runtime_error("SHA256 failed");
    std::ostringstream s; for (auto b : digest) s << std::hex << std::setw(2) << std::setfill('0') << unsigned(b);
    return s.str();
}
std::string JsonString(const std::string& value) {
    std::ostringstream s; s << '"';
    for (unsigned char c : value) {
        if (c == '"' || c == '\\') s << '\\' << c;
        else if (c >= 32 && c < 127) s << c;
        else s << "\\u" << std::hex << std::setw(4) << std::setfill('0') << unsigned(c);
    }
    s << '"'; return s.str();
}

// Uses only OS cryptography, not the machine certificate store. The temporary
// provider key is deleted after exporting a local-only PFX and public PEM.
void CreateCertificate(const fs::path& directory) {
    fs::create_directories(directory);
    if (fs::exists(directory / "server.pfx") || fs::exists(directory / "server.pem"))
        throw std::runtime_error("Certificate output already exists");
    HCRYPTPROV provider{}; HCRYPTKEY key{}; PCCERT_CONTEXT cert{}; HCERTSTORE store{};
    std::wstring container = L"MeitouQuicProbe-" + std::to_wstring(GetCurrentProcessId()) + L"-" + std::to_wstring(GetTickCount64());
    try {
        WinCheck(CryptAcquireContextW(&provider, container.c_str(), MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_NEWKEYSET), "CryptAcquireContext");
        WinCheck(CryptGenKey(provider, AT_KEYEXCHANGE, (2048u << 16) | CRYPT_EXPORTABLE, &key), "CryptGenKey");
        DWORD nameLength = 0;
        WinCheck(CertStrToNameW(X509_ASN_ENCODING, L"CN=localhost", CERT_X500_NAME_STR, nullptr, nullptr, &nameLength, nullptr), "CertStrToName");
        std::vector<uint8_t> name(nameLength);
        WinCheck(CertStrToNameW(X509_ASN_ENCODING, L"CN=localhost", CERT_X500_NAME_STR, nullptr, name.data(), &nameLength, nullptr), "CertStrToName");
        CERT_NAME_BLOB subject{ nameLength, name.data() };
        CRYPT_KEY_PROV_INFO info{}; info.pwszContainerName = container.data();
        info.pwszProvName = const_cast<wchar_t*>(MS_ENH_RSA_AES_PROV_W); info.dwProvType = PROV_RSA_AES; info.dwKeySpec = AT_KEYEXCHANGE;
        CRYPT_ALGORITHM_IDENTIFIER signature{}; signature.pszObjId = const_cast<char*>(szOID_RSA_SHA256RSA);
        SYSTEMTIME end{}; GetSystemTime(&end); end.wYear += 1;
        // Explicit localhost DNS SAN. Remote destination is selected separately from SNI.
        CERT_ALT_NAME_ENTRY sanEntry{}; sanEntry.dwAltNameChoice = CERT_ALT_NAME_DNS_NAME;
        sanEntry.pwszDNSName = const_cast<wchar_t*>(L"localhost");
        CERT_ALT_NAME_INFO san{1, &sanEntry}; DWORD sanLength = 0;
        WinCheck(CryptEncodeObjectEx(X509_ASN_ENCODING, X509_ALTERNATE_NAME, &san, 0, nullptr, nullptr, &sanLength), "Encode SAN");
        std::vector<uint8_t> encodedSan(sanLength);
        WinCheck(CryptEncodeObjectEx(X509_ASN_ENCODING, X509_ALTERNATE_NAME, &san, 0, nullptr, encodedSan.data(), &sanLength), "Encode SAN");
        CERT_EXTENSION extension{}; extension.pszObjId = const_cast<char*>(szOID_SUBJECT_ALT_NAME2);
        extension.Value = {sanLength, encodedSan.data()}; CERT_EXTENSIONS extensions{1, &extension};
        cert = CertCreateSelfSignCertificate(provider, &subject, 0, &info, &signature, nullptr, &end, &extensions);
        WinCheck(cert != nullptr, "CertCreateSelfSignCertificate");
        store = CertOpenStore(CERT_STORE_PROV_MEMORY, 0, 0, 0, nullptr); WinCheck(store != nullptr, "CertOpenStore");
        WinCheck(CertAddCertificateContextToStore(store, cert, CERT_STORE_ADD_ALWAYS, nullptr), "CertAddCertificate");
        CRYPT_DATA_BLOB pfx{};
        WinCheck(PFXExportCertStoreEx(store, &pfx, L"", nullptr, EXPORT_PRIVATE_KEYS | REPORT_NOT_ABLE_TO_EXPORT_PRIVATE_KEY), "PFX size");
        std::vector<uint8_t> pfxBytes(pfx.cbData); pfx.pbData = pfxBytes.data();
        WinCheck(PFXExportCertStoreEx(store, &pfx, L"", nullptr, EXPORT_PRIVATE_KEYS | REPORT_NOT_ABLE_TO_EXPORT_PRIVATE_KEY), "PFX export");
        DWORD pemLength = 0;
        WinCheck(CryptBinaryToStringA(cert->pbCertEncoded, cert->cbCertEncoded, CRYPT_STRING_BASE64HEADER, nullptr, &pemLength), "PEM size");
        std::string pem(pemLength, '\0');
        WinCheck(CryptBinaryToStringA(cert->pbCertEncoded, cert->cbCertEncoded, CRYPT_STRING_BASE64HEADER, pem.data(), &pemLength), "PEM export");
        std::ofstream pfxFile(directory / "server.pfx", std::ios::binary);
        pfxFile.write(reinterpret_cast<char*>(pfxBytes.data()), pfx.cbData); pfxFile.close();
        std::ofstream pemFile(directory / "server.pem", std::ios::binary); pemFile << pem.c_str(); pemFile.close();
        SecureZeroMemory(pfxBytes.data(), pfxBytes.size());
        WinCheck(bool(pfxFile) && bool(pemFile), "Certificate file write");
    } catch (...) {
        if (store) CertCloseStore(store, 0); if (cert) CertFreeCertificateContext(cert);
        if (key) CryptDestroyKey(key); if (provider) CryptReleaseContext(provider, 0);
        HCRYPTPROV unused{}; CryptAcquireContextW(&unused, container.c_str(), MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_DELETEKEYSET);
        throw;
    }
    CertCloseStore(store, 0); CertFreeCertificateContext(cert); CryptDestroyKey(key); CryptReleaseContext(provider, 0);
    HCRYPTPROV unused{}; WinCheck(CryptAcquireContextW(&unused, container.c_str(), MS_ENH_RSA_AES_PROV_W, PROV_RSA_AES, CRYPT_DELETEKEYSET), "Delete temporary key");
    std::cout << "{\"certificateCreated\":true,\"publicCertificateSha256\":" << JsonString(Hash(directory / "server.pem")) << "}\n";
}

struct Packet { bool datagram{}; uint32_t length{}; std::array<uint8_t, MaxPacket> bytes{}; };
struct Send {
    QUIC_BUFFER buffer{};
    std::array<uint8_t, MaxPacket> data{};
    Send(uint8_t kind, uint32_t seq, uint32_t size) {
        buffer = {size, data.data()}; std::memcpy(data.data(), "QPRB", 4);
        data[4] = 1; data[5] = kind;
        std::memcpy(data.data() + 8, &seq, 4); std::memcpy(data.data() + 12, &size, 4);
        for (uint32_t i = 16; i < size; ++i) data[i] = static_cast<uint8_t>((i * 31 + seq) & 255);
        ++Buffers; ++BuffersAllocated;
    }
    ~Send() { --Buffers; ++BuffersReleased; }
};
struct App;
struct Connection {
    App* app{};
    std::atomic<HQUIC> handle{}, stream{};
    std::atomic<bool> connected{}, closed{}, streamClosed{}, overflow{};
    std::atomic<uint16_t> maxDatagram{};
    std::atomic<unsigned> sendCompletions{}, datagramTerminal{}, datagramLost{}, datagramCanceled{};
    std::atomic<unsigned> transportStatus{};
    std::atomic<unsigned> tlsStatus{};
    std::mutex mutex;
    std::array<Packet, QueueCapacity> queue{};
    size_t head{}, count{};
    std::array<uint8_t, MaxPacket> streamBytes{};
    size_t streamLength{};
    bool streamRequestSent{}, streamReceived{}, streamAck{}, datagramsSent{}, doneSent{}, doneReceived{}, doneAckSent{}, doneAckReceived{};
    unsigned datagramReceivedMask{}, datagramAckMask{}, ackSentMask{};
    unsigned datagramLimitAtSend{};
    std::array<unsigned, 3> sizes{};
    bool shutdownRequested{}, success{};
    QUIC_HANDSHAKE_INFO tls{}; QUIC_STATISTICS_V2 stats{};
    void Enqueue(const uint8_t* data, uint32_t length, bool datagram) {
        std::lock_guard lock(mutex);
        if (length > MaxPacket || count == QueueCapacity) { overflow = true; ++QueueDrops; return; }
        Packet& p = queue[(head + count) % QueueCapacity]; p.datagram = datagram; p.length = length;
        std::memcpy(p.bytes.data(), data, length); ++count;
    }
    bool Pop(Packet& p) {
        std::lock_guard lock(mutex); if (!count) return false;
        p = queue[head]; head = (head + 1) % QueueCapacity; --count; return true;
    }
};
struct App {
    bool server{};
    HQUIC registration{}, configuration{}, listener{};
    std::array<Connection, MaxConnections> connections{};
    std::atomic<unsigned> accepted{};
    std::mutex acceptMutex;
    unsigned rounds{2};
    App() { for (auto& c : connections) c.app = this; }
};

QUIC_STATUS QUIC_API StreamCallback(HQUIC, void* context, QUIC_STREAM_EVENT* event) {
    auto& c = *static_cast<Connection*>(context);
    switch (event->Type) {
    case QUIC_STREAM_EVENT_RECEIVE:
        for (uint32_t i = 0; i < event->RECEIVE.BufferCount; ++i)
            c.Enqueue(event->RECEIVE.Buffers[i].Buffer, event->RECEIVE.Buffers[i].Length, false);
        break;
    case QUIC_STREAM_EVENT_SEND_COMPLETE:
        delete static_cast<Send*>(event->SEND_COMPLETE.ClientContext); ++c.sendCompletions; break;
    case QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE: c.streamClosed = true; break;
    default: break;
    }
    return QUIC_STATUS_SUCCESS;
}
QUIC_STATUS QUIC_API ConnectionCallback(HQUIC handle, void* context, QUIC_CONNECTION_EVENT* event) {
    auto& c = *static_cast<Connection*>(context);
    switch (event->Type) {
    case QUIC_CONNECTION_EVENT_CONNECTED: {
        // TLS context may be released after this callback on the server.
        uint32_t n = sizeof(c.tls);
        c.tlsStatus = static_cast<unsigned>(Api->GetParam(handle, QUIC_PARAM_TLS_HANDSHAKE_INFO, &n, &c.tls));
        c.connected = true; break;
    }
    case QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE: c.closed = true; break;
    case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT:
        c.transportStatus = static_cast<unsigned>(event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status); break;
    case QUIC_CONNECTION_EVENT_PEER_STREAM_STARTED:
        // Probe has exactly one bidirectional stream and rejects all extra streams.
        if (c.stream.load() || event->PEER_STREAM_STARTED.Flags != QUIC_STREAM_OPEN_FLAG_NONE) {
            Api->StreamClose(event->PEER_STREAM_STARTED.Stream); c.overflow = true;
        } else {
            Api->SetCallbackHandler(event->PEER_STREAM_STARTED.Stream, reinterpret_cast<void*>(StreamCallback), &c);
            ++StreamHandles; c.stream = event->PEER_STREAM_STARTED.Stream;
        }
        break;
    case QUIC_CONNECTION_EVENT_DATAGRAM_STATE_CHANGED:
        c.maxDatagram = event->DATAGRAM_STATE_CHANGED.SendEnabled ? event->DATAGRAM_STATE_CHANGED.MaxSendLength : 0;
        break;
    case QUIC_CONNECTION_EVENT_DATAGRAM_RECEIVED:
        c.Enqueue(event->DATAGRAM_RECEIVED.Buffer->Buffer, event->DATAGRAM_RECEIVED.Buffer->Length, true); break;
    case QUIC_CONNECTION_EVENT_DATAGRAM_SEND_STATE_CHANGED:
        if (QUIC_DATAGRAM_SEND_STATE_IS_FINAL(event->DATAGRAM_SEND_STATE_CHANGED.State)) {
            delete static_cast<Send*>(event->DATAGRAM_SEND_STATE_CHANGED.ClientContext);
            event->DATAGRAM_SEND_STATE_CHANGED.ClientContext = nullptr;
            ++c.datagramTerminal;
            if (event->DATAGRAM_SEND_STATE_CHANGED.State == QUIC_DATAGRAM_SEND_LOST_DISCARDED) ++c.datagramLost;
            if (event->DATAGRAM_SEND_STATE_CHANGED.State == QUIC_DATAGRAM_SEND_CANCELED) ++c.datagramCanceled;
        }
        break;
    default: break;
    }
    return QUIC_STATUS_SUCCESS;
}
QUIC_STATUS QUIC_API ListenerCallback(HQUIC, void* context, QUIC_LISTENER_EVENT* event) {
    auto& app = *static_cast<App*>(context);
    if (event->Type != QUIC_LISTENER_EVENT_NEW_CONNECTION) return QUIC_STATUS_SUCCESS;
    std::lock_guard lock(app.acceptMutex);
    unsigned i = app.accepted.load();
    if (i >= app.rounds) return QUIC_STATUS_CONNECTION_REFUSED;
    auto& c = app.connections[i]; c.handle = event->NEW_CONNECTION.Connection; ++ConnectionHandles;
    Api->SetCallbackHandler(c.handle, reinterpret_cast<void*>(ConnectionCallback), &c);
    QUIC_STATUS status = Api->ConnectionSetConfiguration(c.handle, app.configuration);
    if (QUIC_FAILED(status)) { c.handle = nullptr; --ConnectionHandles; return status; }
    app.accepted.store(i + 1);
    return QUIC_STATUS_SUCCESS;
}
void SendPacket(Connection& c, uint8_t kind, uint32_t seq, uint32_t length, bool datagram = false) {
    if (Buffers.load() >= 64) throw std::runtime_error("Native send buffer bound exceeded");
    auto* send = new Send(kind, seq, length);
    QUIC_STATUS status = datagram
        ? Api->DatagramSend(c.handle, &send->buffer, 1, QUIC_SEND_FLAG_NONE, send)
        : Api->StreamSend(c.stream, &send->buffer, 1, QUIC_SEND_FLAG_NONE, send);
    if (QUIC_FAILED(status)) { delete send; Check(status, datagram ? "DatagramSend" : "StreamSend"); }
    // Ownership passed to callback; it may already have completed inline.
}
void ProcessFrame(Connection& c, const uint8_t* bytes, size_t size, bool datagram) {
    if (size < 16 || std::memcmp(bytes, "QPRB", 4) || bytes[4] != 1 || bytes[6] || bytes[7])
        throw std::runtime_error("Invalid probe frame");
    uint32_t seq{}, length{}; std::memcpy(&seq, bytes + 8, 4); std::memcpy(&length, bytes + 12, 4);
    if (length != size) throw std::runtime_error("Invalid probe length");
    for (uint32_t i = 16; i < size; ++i)
        if (bytes[i] != static_cast<uint8_t>((i * 31 + seq) & 255)) throw std::runtime_error("Payload mismatch");
    switch (bytes[5]) {
    case 1:
        if (datagram || size != 64 || seq != 1 || c.streamReceived) throw std::runtime_error("Invalid stream request");
        c.streamReceived = true; SendPacket(c, 2, 1, 16); break;
    case 2:
        if (datagram || size != 16 || seq != 1) throw std::runtime_error("Invalid stream ACK");
        c.streamAck = true; break;
    case 3:
        if (!datagram || seq >= 3 || size < 32) throw std::runtime_error("Invalid datagram request");
        c.datagramReceivedMask |= 1u << seq; break;
    case 4:
        if (datagram || seq >= 3 || size != 16) throw std::runtime_error("Invalid datagram ACK");
        c.datagramAckMask |= 1u << seq; break;
    case 5:
        if (datagram || seq || size != 16 || !c.app->server) throw std::runtime_error("Invalid DONE");
        c.doneReceived = true; break;
    case 6:
        if (datagram || seq || size != 16 || c.app->server) throw std::runtime_error("Invalid DONE ACK");
        c.doneAckReceived = true; break;
    default: throw std::runtime_error("Unknown probe kind");
    }
}
void Pump(Connection& c) {
    if (c.overflow) throw std::runtime_error("Bounded receive queue overflow / extra stream");
    if (c.connected && c.stream && !c.streamRequestSent && !c.closed) {
        SendPacket(c, 1, 1, 64); c.streamRequestSent = true;
        Check(static_cast<QUIC_STATUS>(c.tlsStatus.load()), "TLS handshake info");
        if (c.tls.TlsProtocolVersion != QUIC_TLS_PROTOCOL_1_3 || c.tls.CipherStrength < 128)
            throw std::runtime_error("Expected encrypted TLS 1.3");
    }
    Packet p;
    while (c.Pop(p)) {
        if (p.datagram) ProcessFrame(c, p.bytes.data(), p.length, true);
        else {
            if (c.streamLength + p.length > MaxPacket) throw std::runtime_error("Stream frame cap exceeded");
            std::memcpy(c.streamBytes.data() + c.streamLength, p.bytes.data(), p.length); c.streamLength += p.length;
            while (c.streamLength >= 16) {
                uint32_t n{}; std::memcpy(&n, c.streamBytes.data() + 12, 4);
                if (n < 16 || n > MaxPacket) throw std::runtime_error("Stream length cap exceeded");
                if (c.streamLength < n) break;
                ProcessFrame(c, c.streamBytes.data(), n, false);
                c.streamLength -= n; std::memmove(c.streamBytes.data(), c.streamBytes.data() + n, c.streamLength);
            }
        }
    }
    if (c.closed) return;
    if (c.stream) {
        for (unsigned i = 0; i < 3; ++i)
            if ((c.datagramReceivedMask & (1u << i)) && !(c.ackSentMask & (1u << i))) {
                SendPacket(c, 4, i, 16); c.ackSentMask |= 1u << i;
            }
    }
    if (c.connected && c.streamReceived && c.maxDatagram >= 32 && !c.datagramsSent) {
        c.datagramLimitAtSend = c.maxDatagram.load();
        c.sizes = {32, (std::min)(1150u, c.datagramLimitAtSend), (std::min)(unsigned(MaxPacket), c.datagramLimitAtSend)};
        for (unsigned i = 0; i < 3; ++i) SendPacket(c, 3, i, c.sizes[i], true);
        c.datagramsSent = true;
    }
    bool ready = c.streamReceived && c.streamAck && c.datagramReceivedMask == 7 && c.datagramAckMask == 7;
    if (!c.app->server && ready && !c.doneSent) { SendPacket(c, 5, 0, 16); c.doneSent = true; }
    if (c.app->server && ready && c.doneReceived && !c.doneAckSent) {
        SendPacket(c, 6, 0, 16); c.doneAckSent = true; c.success = true;
    }
    if (!c.app->server && ready && c.doneAckReceived && !c.shutdownRequested) {
        c.success = true; c.shutdownRequested = true;
        Api->ConnectionShutdown(c.handle, QUIC_CONNECTION_SHUTDOWN_FLAG_NONE, 0);
    }
}
void CloseConnection(Connection& c) {
    HQUIC handle = c.handle.load(); if (!handle) return;
    uint32_t n = sizeof(c.stats); Api->GetParam(handle, QUIC_PARAM_CONN_STATISTICS_V2, &n, &c.stats);
    if (c.stream) { Api->StreamClose(c.stream); c.stream = nullptr; --StreamHandles; }
    Api->ConnectionClose(handle); c.handle = nullptr; --ConnectionHandles;
}

struct Options {
    bool server{}, loopback{}, create{}; unsigned port{47421}, rounds{2}, timeout{30};
    std::string host, certificate, ca, certificateDirectory, output, serverName{"localhost"};
};
unsigned Number(const char* s, unsigned max) {
    size_t count{}; unsigned long n = std::stoul(s, &count);
    if (!n || n > max || count != std::strlen(s)) throw std::runtime_error("Invalid numeric argument");
    return static_cast<unsigned>(n);
}
Options Parse(int argc, char** argv) {
    Options o;
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        auto value = [&]() -> const char* { if (++i >= argc) throw std::runtime_error("Missing argument value"); return argv[i]; };
        if (a == "--listen") { o.server = true; o.port = Number(value(), 65535); }
        else if (a == "--connect") o.host = value();
        else if (a == "--loopback") o.loopback = true;
        else if (a == "--port") o.port = Number(value(), 65535);
        else if (a == "--rounds") o.rounds = Number(value(), MaxConnections);
        else if (a == "--timeout-seconds") o.timeout = Number(value(), 3600);
        else if (a == "--certificate") o.certificate = value();
        else if (a == "--ca") o.ca = value();
        else if (a == "--server-name") o.serverName = value();
        else if (a == "--output") o.output = value();
        else if (a == "--create-certificate") { o.create = true; o.certificateDirectory = value(); }
        else throw std::runtime_error("Unknown argument");
    }
    if (!o.create && unsigned(o.server) + unsigned(o.loopback) + unsigned(!o.host.empty()) != 1)
        throw std::runtime_error("Choose --listen PORT, --connect HOST or --loopback");
    if ((o.server || o.loopback) && o.certificate.empty()) throw std::runtime_error("--certificate server.pfx required");
    if ((!o.host.empty() || o.loopback) && o.ca.empty()) throw std::runtime_error("--ca server.pem required");
    return o;
}
void Configure(App& app, const Options& o) {
    QUIC_REGISTRATION_CONFIG registration{"Meitou.QuicProbe", QUIC_EXECUTION_PROFILE_LOW_LATENCY};
    Check(Api->RegistrationOpen(&registration, &app.registration), "RegistrationOpen");
    QUIC_SETTINGS settings{}; settings.IdleTimeoutMs = 15000; settings.IsSet.IdleTimeoutMs = TRUE;
    settings.PeerBidiStreamCount = 1; settings.IsSet.PeerBidiStreamCount = TRUE;
    settings.DatagramReceiveEnabled = TRUE; settings.IsSet.DatagramReceiveEnabled = TRUE;
    QUIC_BUFFER alpn{sizeof(Alpn) - 1, reinterpret_cast<uint8_t*>(const_cast<char*>(Alpn))};
    Check(Api->ConfigurationOpen(app.registration, &alpn, 1, &settings, sizeof(settings), nullptr, &app.configuration), "ConfigurationOpen");
    QUIC_CREDENTIAL_CONFIG credential{}; std::vector<uint8_t> pfx; QUIC_CERTIFICATE_PKCS12 pkcs12{};
    if (app.server) {
        pfx = Read(o.certificate); pkcs12.Asn1Blob = pfx.data(); pkcs12.Asn1BlobLength = static_cast<uint32_t>(pfx.size()); pkcs12.PrivateKeyPassword = "";
        credential.Type = QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12; credential.CertificatePkcs12 = &pkcs12;
    } else {
        credential.Type = QUIC_CREDENTIAL_TYPE_NONE;
        credential.Flags = QUIC_CREDENTIAL_FLAG_CLIENT | QUIC_CREDENTIAL_FLAG_SET_CA_CERTIFICATE_FILE
            | QUIC_CREDENTIAL_FLAG_USE_TLS_BUILTIN_CERTIFICATE_VALIDATION;
        credential.CaCertificateFile = o.ca.c_str();
    }
    QUIC_STATUS status = Api->ConfigurationLoadCredential(app.configuration, &credential);
    if (!pfx.empty()) SecureZeroMemory(pfx.data(), pfx.size());
    Check(status, "ConfigurationLoadCredential");
}
void Listen(App& app, const Options& o, bool loopback) {
    Check(Api->ListenerOpen(app.registration, ListenerCallback, &app, &app.listener), "ListenerOpen");
    QUIC_ADDR address{}; QuicAddrSetFamily(&address, QUIC_ADDRESS_FAMILY_INET); QuicAddrSetPort(&address, static_cast<uint16_t>(o.port));
    if (loopback) QuicAddrSetToLoopback(&address);
    QUIC_BUFFER alpn{sizeof(Alpn) - 1, reinterpret_cast<uint8_t*>(const_cast<char*>(Alpn))};
    Check(Api->ListenerStart(app.listener, &alpn, 1, &address), "ListenerStart");
    std::cerr << "Listening on UDP " << o.port << "; no firewall changes performed\n";
}
void Connect(App& app, unsigned index, const Options& o) {
    Connection& c = app.connections[index]; HQUIC h{};
    Check(Api->ConnectionOpen(app.registration, ConnectionCallback, &c, &h), "ConnectionOpen"); c.handle = h; ++ConnectionHandles;
    // Destination can be a remote IP/DNS name, while the generated certificate is for localhost.
    QUIC_ADDR remote{};
    if (!QuicAddrFromString(o.host.c_str(), static_cast<uint16_t>(o.port), &remote))
        throw std::runtime_error("--connect requires numeric IPv4/IPv6 address");
    Check(Api->SetParam(h, QUIC_PARAM_CONN_REMOTE_ADDRESS, sizeof(remote), &remote), "Remote address");
    Check(Api->ConnectionStart(h, app.configuration, QuicAddrGetFamily(&remote), o.serverName.c_str(), static_cast<uint16_t>(o.port)), "ConnectionStart");
    app.accepted = index + 1;
}
void OpenStream(Connection& c) {
    HQUIC stream{}; Check(Api->StreamOpen(c.handle, QUIC_STREAM_OPEN_FLAG_NONE, StreamCallback, &c, &stream), "StreamOpen");
    c.stream = stream; ++StreamHandles;
    Check(Api->StreamStart(stream, QUIC_STREAM_START_FLAG_IMMEDIATE), "StreamStart");
}
void Cleanup(App& app) {
    if (app.listener) { Api->ListenerClose(app.listener); app.listener = nullptr; }
    for (auto& c : app.connections) if (c.handle) Api->ConnectionShutdown(c.handle, QUIC_CONNECTION_SHUTDOWN_FLAG_SILENT, 1);
    // ConnectionClose synchronizes callbacks, including on failed handshakes.
    for (auto& c : app.connections) CloseConnection(c);
    if (app.configuration) { Api->ConfigurationClose(app.configuration); app.configuration = nullptr; }
    if (app.registration) { Api->RegistrationClose(app.registration); app.registration = nullptr; }
}
std::string ConnectionJson(Connection& c, unsigned round, const char* side) {
    std::ostringstream s;
    s << "{\"side\":\"" << side << "\",\"round\":" << round << ",\"success\":" << (c.success ? "true" : "false")
      << ",\"connected\":" << (c.connected ? "true" : "false") << ",\"shutdownComplete\":" << (c.closed ? "true" : "false")
      << ",\"transportStatus\":" << c.transportStatus << ",\"tlsVersion\":" << c.tls.TlsProtocolVersion
      << ",\"cipherSuite\":" << c.tls.CipherSuite << ",\"cipherStrength\":" << c.tls.CipherStrength
      << ",\"maxDatagram\":" << c.maxDatagram << ",\"datagramLimitAtSend\":" << c.datagramLimitAtSend << ",\"datagramSizes\":[" << c.sizes[0] << ',' << c.sizes[1] << ',' << c.sizes[2]
      << "],\"datagramReceivedMask\":" << c.datagramReceivedMask << ",\"applicationDatagramAckMask\":" << c.datagramAckMask
      << ",\"streamApplicationAck\":" << (c.streamAck ? "true" : "false") << ",\"streamSendCompletions\":" << c.sendCompletions
      << ",\"datagramTerminalEvents\":" << c.datagramTerminal << ",\"datagramLost\":" << c.datagramLost << ",\"datagramCanceled\":" << c.datagramCanceled
      << ",\"pathMtu\":" << c.stats.SendPathMtu << ",\"udpSentBytes\":" << c.stats.SendTotalBytes << ",\"udpReceivedBytes\":" << c.stats.RecvTotalBytes
      << ",\"rttMicroseconds\":" << c.stats.Rtt << '}'; return s.str();
}

int main(int argc, char** argv) {
    Options options; std::string error, dllHash; bool passed = false; HMODULE module{};
    std::array<uint32_t, 4> version{}; QUIC_TLS_PROVIDER provider{};
    auto server = std::make_unique<App>(), client = std::make_unique<App>();
    try {
        options = Parse(argc, argv);
        if (options.create) { CreateCertificate(options.certificateDirectory); return 0; }
        wchar_t executable[32768]{}; WinCheck(GetModuleFileNameW(nullptr, executable, 32768) != 0, "Executable location");
        fs::path dll = fs::path(executable).parent_path() / "msquic.dll"; dllHash = Hash(dll);
        // Build script pins the exact DLL hash in a generated header.
        if (dllHash != MSQUIC_SHA256) throw std::runtime_error("Pinned msquic.dll SHA256 mismatch");
        module = LoadLibraryExW(dll.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        WinCheck(module != nullptr, "LoadLibraryEx(msquic)");
        auto open = reinterpret_cast<QUIC_STATUS (QUIC_API*)(uint32_t, const void**)>(GetProcAddress(module, "MsQuicOpenVersion"));
        if (!open) throw std::runtime_error("MsQuicOpenVersion export missing");
        Check(open(2, reinterpret_cast<const void**>(&Api)), "MsQuicOpenVersion(2)");
        uint32_t n = sizeof(version); Check(Api->GetParam(nullptr, QUIC_PARAM_GLOBAL_LIBRARY_VERSION, &n, version.data()), "LibraryVersion");
        n = sizeof(provider); Check(Api->GetParam(nullptr, QUIC_PARAM_GLOBAL_TLS_PROVIDER, &n, &provider), "TLS provider");
        if (provider != QUIC_TLS_PROVIDER_OPENSSL) throw std::runtime_error("Expected OpenSSL TLS provider");
        server->server = true; server->rounds = client->rounds = options.rounds;
        if (options.server || options.loopback) { Configure(*server, options); Listen(*server, options, options.loopback); }
        if (!options.server) Configure(*client, options);
        if (options.loopback) options.host = "127.0.0.1";
        auto end = Clock::now() + std::chrono::seconds(options.timeout);
        unsigned next = 0, completed = 0;
        while (completed < options.rounds) {
            if (Clock::now() > end) throw std::runtime_error("Probe timeout (UDP/handshake/DATAGRAM/application ACK/shutdown)");
            if (!options.server && next == completed) { Connect(*client, next, options); ++next; }
            if (!options.server) {
                auto& c = client->connections[completed];
                if (c.connected && !c.stream && !c.closed) OpenStream(c);
                Pump(c);
                if (c.closed) {
                    if (!c.success) throw std::runtime_error("Client connection closed before application acceptance");
                    if (options.loopback) {
                        auto& peer = server->connections[completed]; Pump(peer);
                        if (!peer.closed) { std::this_thread::sleep_for(std::chrono::milliseconds(1)); continue; }
                        if (!peer.success) throw std::runtime_error("Server did not complete application ACK exchange");
                        CloseConnection(peer);
                    }
                    CloseConnection(c); ++completed; end = Clock::now() + std::chrono::seconds(options.timeout);
                }
            }
            if (options.server || options.loopback) {
                unsigned accepted = server->accepted.load();
                for (unsigned i = completed; i < accepted; ++i) Pump(server->connections[i]);
                if (options.server && completed < accepted && server->connections[completed].closed) {
                    auto& c = server->connections[completed];
                    if (!c.success) throw std::runtime_error("Server connection closed before application acceptance");
                    CloseConnection(c); ++completed; end = Clock::now() + std::chrono::seconds(options.timeout);
                }
            }
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
        passed = true;
    } catch (const std::exception& e) { error = e.what(); }
    if (Api) { Cleanup(*client); Cleanup(*server); }
    if (Buffers || StreamHandles || ConnectionHandles || QueueDrops) { passed = false; if (error.empty()) error = "Unreleased buffers/handles or dropped receive data"; }
    OSVERSIONINFOW os{}; os.dwOSVersionInfoSize = sizeof(os);
    auto rtl = reinterpret_cast<LONG (WINAPI*)(OSVERSIONINFOW*)>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion")); if (rtl) rtl(&os);
    std::ostringstream report;
    report << "{\"schema\":1,\"probe\":\"native-msquic-v2\",\"passed\":" << (passed ? "true" : "false")
           << ",\"mode\":\"" << (options.server ? "listen" : options.loopback ? "loopback" : "connect") << "\",\"error\":" << JsonString(error)
           << ",\"os\":{\"major\":" << os.dwMajorVersion << ",\"minor\":" << os.dwMinorVersion << ",\"build\":" << os.dwBuildNumber << ",\"productType\":";
    OSVERSIONINFOEXW osEx{}; osEx.dwOSVersionInfoSize = sizeof(osEx);
    if (rtl) rtl(reinterpret_cast<OSVERSIONINFOW*>(&osEx));
    report << unsigned(osEx.wProductType) << "},\"architecture\":\"x64\",\"package\":\"Microsoft.Native.Quic.MsQuic.OpenSSL\",\"packageVersion\":\"2.6.3\",\"packageSha256\":\"" << MSQUIC_PACKAGE_SHA256
           << "\",\"dllSha256\":" << JsonString(dllHash) << ",\"apiVersion\":2,\"nativeVersion\":[" << version[0] << ',' << version[1] << ',' << version[2] << ',' << version[3]
           << "],\"tlsProvider\":" << JsonString(Api && provider == QUIC_TLS_PROVIDER_OPENSSL ? "OpenSSL" : "unavailable")
           << ",\"certificateValidation\":\"explicit-public-CA-and-localhost-SNI\",\"alpn\":\"" << Alpn
           << "\",\"liveBuffers\":" << Buffers << ",\"buffersAllocated\":" << BuffersAllocated << ",\"buffersReleased\":" << BuffersReleased
           << ",\"liveStreamHandles\":" << StreamHandles << ",\"liveConnectionHandles\":" << ConnectionHandles << ",\"receiveQueueDrops\":" << QueueDrops << ",\"connections\":[";
    bool comma = false;
    for (unsigned i = 0; i < options.rounds; ++i) {
        if (!options.server) { if (comma) report << ','; comma = true; report << ConnectionJson(client->connections[i], i + 1, "client"); }
        if (options.server || options.loopback) { if (comma) report << ','; comma = true; report << ConnectionJson(server->connections[i], i + 1, "server"); }
    }
    report << "]}";
    if (Api) {
        auto close = reinterpret_cast<void (QUIC_API*)(const QUIC_API_TABLE*)>(GetProcAddress(module, "MsQuicClose"));
        close(Api); Api = nullptr;
    }
    if (module) FreeLibrary(module);
    std::cout << report.str() << '\n';
    if (!options.output.empty()) { std::ofstream f(options.output); f << report.str() << '\n'; if (!f) return 2; }
    return passed ? 0 : 1;
}
