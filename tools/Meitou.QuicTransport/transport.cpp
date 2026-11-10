// SPDX-License-Identifier: GPL-3.0-or-later
// C ABI boundary compiled against the pinned MsQuic headers, never a copied ABI union.
#include <msquic.h>
#include <array>
#include <atomic>
#include <cstring>
#include <deque>
#include <fstream>
#include <mutex>
#include <memory>
#include <condition_variable>
#include <filesystem>
#include <new>
#include <unordered_set>
#include <vector>

#define EXPORT extern "C" __declspec(dllexport)
constexpr uint32_t MaxChunk = 65536, MaxQueuedBytes = 262144, MaxEvents = 128;
constexpr char Alpn[] = "meitou/21";
std::atomic<uint64_t> LiveEndpoints{}, LiveRuntimes{}, LiveConnections{}, LiveStreams{}, LiveSends{};
struct Endpoint;
struct Runtime {
    const QUIC_API_TABLE* api{}; HMODULE module{}; HQUIC registration{}, configuration{};
    std::atomic<uint32_t> peers{};
    Runtime() { ++LiveRuntimes; }
    ~Runtime() {
        --LiveRuntimes;
        if(configuration) api->ConfigurationClose(configuration);
        if(registration) api->RegistrationClose(registration);
        if(api) { auto close=reinterpret_cast<void(QUIC_API*)(const QUIC_API_TABLE*)>(GetProcAddress(module,"MsQuicClose")); close(api); }
        if(module) FreeLibrary(module);
    }
};
struct Lane { Endpoint* owner{}; std::atomic<HQUIC> handle{}; uint32_t index{}; std::atomic<bool> started{}, shutdownComplete{}; };
struct Event { uint32_t kind{}, lane{}, status{}, length{}; uint64_t token{}; std::vector<uint8_t> bytes; };
struct Send {
    Endpoint* owner; uint64_t token; uint32_t lane; bool datagram; QUIC_BUFFER buffer{}; std::vector<uint8_t> bytes;
    Send(Endpoint* e,uint64_t t,uint32_t l,const uint8_t* data,uint32_t size)
        :owner(e),token(t),lane(l),datagram(l==3),bytes(data,data+size) { buffer={size,bytes.data()}; ++LiveSends; }
    ~Send() { --LiveSends; }
};
struct Endpoint {
    std::shared_ptr<Runtime> runtime;
    const QUIC_API_TABLE* api{};
    HQUIC listener{}; std::atomic<HQUIC> connection{};
    std::array<Lane, 3> lanes{};
    std::mutex queueMutex, sendMutex, acceptMutex, shutdownMutex;
    std::condition_variable shutdownCondition;
    std::deque<Event> events;
    std::unordered_set<Send*> sends;
    uint32_t queuedBytes{}, submittedBytes{};
    std::array<uint32_t,4> laneBytes{}, laneSends{};
    std::deque<Endpoint*> accepted;
    std::atomic<bool> overflow{}, connected{}, closed{}, stopping{};
    std::atomic<uint16_t> datagramLimit{};
    bool server{}, acceptedPeer{};
    ~Endpoint() { --LiveEndpoints; }
    Endpoint() { ++LiveEndpoints; for (uint32_t i=0;i<3;++i) { lanes[i].owner=this; lanes[i].index=i; } }
    void enqueue(Event event) {
        std::lock_guard lock(queueMutex);
        if (events.size() == MaxEvents || event.length > MaxQueuedBytes - queuedBytes) { overflow = true; return; }
        queuedBytes += event.length; events.push_back(std::move(event));
    }
    void receive(uint32_t lane, const QUIC_BUFFER* buffers, uint32_t count) {
        std::lock_guard lock(queueMutex);
        uint64_t length=0, needed=0;
        for(uint32_t i=0;i<count;++i) { length+=buffers[i].Length; needed+=(buffers[i].Length+MaxChunk-1)/MaxChunk; }
        if(length>MaxQueuedBytes-queuedBytes || needed>MaxEvents-events.size() || (lane==3 && length>1150)) { overflow=true; return; }
        for(uint32_t i=0;i<count;++i) for(uint32_t offset=0;offset<buffers[i].Length;) {
            uint32_t n=(buffers[i].Length-offset)>MaxChunk ? MaxChunk : buffers[i].Length-offset;
            Event event{lane==3 ? 3u : 2u,lane,0,n,0,{}};
            event.bytes.assign(buffers[i].Buffer+offset,buffers[i].Buffer+offset+n);
            events.push_back(std::move(event)); queuedBytes+=n; offset+=n;
        }
    }
    void release(Send* raw, uint32_t state) noexcept {
        std::unique_ptr<Send> send(raw);
        { std::lock_guard lock(sendMutex); sends.erase(raw); submittedBytes-=send->buffer.Length;
          laneBytes[send->lane]-=send->buffer.Length; --laneSends[send->lane]; }
        try { enqueue({send->datagram ? 5u : 4u,send->lane,state,0,send->token,{}}); } catch(...) { overflow=true; }
    }

};
// All callback allocation failures become a fail-closed overflow; no exception crosses MsQuic.
QUIC_STATUS QUIC_API StreamCallback(HQUIC, void* context, QUIC_STREAM_EVENT* event) noexcept {
    auto& lane=*static_cast<Lane*>(context); auto& e=*lane.owner;
    try {
        switch(event->Type) {
        case QUIC_STREAM_EVENT_RECEIVE: e.receive(lane.index,event->RECEIVE.Buffers,event->RECEIVE.BufferCount); break;
        case QUIC_STREAM_EVENT_SEND_COMPLETE: e.release(static_cast<Send*>(event->SEND_COMPLETE.ClientContext),event->SEND_COMPLETE.Canceled ? 1u : 0u); break;
        case QUIC_STREAM_EVENT_PEER_SEND_SHUTDOWN: e.enqueue({7,lane.index,0,0,0,{}}); break;
        case QUIC_STREAM_EVENT_PEER_SEND_ABORTED: e.overflow=true; break;
        case QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE: { std::lock_guard lock(e.shutdownMutex); lane.shutdownComplete=true; e.shutdownCondition.notify_all(); break; }
        default: break;
        }
    } catch(...) { e.overflow=true; }
    return QUIC_STATUS_SUCCESS;
}
// Rejected streams have no application sends/context storage and close only after shutdown.
QUIC_STATUS QUIC_API RejectedStreamCallback(HQUIC stream, void* context, QUIC_STREAM_EVENT* event) noexcept {
    if(event->Type==QUIC_STREAM_EVENT_SHUTDOWN_COMPLETE) { static_cast<Endpoint*>(context)->api->StreamClose(stream); --LiveStreams; }
    return QUIC_STATUS_SUCCESS;
}
void RejectStream(Endpoint& e,HQUIC stream) noexcept {
    ++LiveStreams;
    e.api->SetCallbackHandler(stream,reinterpret_cast<void*>(RejectedStreamCallback),&e);
    e.api->StreamShutdown(stream,QUIC_STREAM_SHUTDOWN_FLAG_ABORT|QUIC_STREAM_SHUTDOWN_FLAG_IMMEDIATE,1);
    e.overflow=true;
}
QUIC_STATUS QUIC_API ConnectionCallback(HQUIC handle, void* context, QUIC_CONNECTION_EVENT* event) noexcept {
    auto& e=*static_cast<Endpoint*>(context);
    try {
        switch(event->Type) {
        case QUIC_CONNECTION_EVENT_CONNECTED: e.connected=true; e.enqueue({1,0,0,0,0,{}}); break;
        case QUIC_CONNECTION_EVENT_SHUTDOWN_COMPLETE: e.closed=true; e.enqueue({6,0,0,0,0,{}}); break;
        case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_TRANSPORT: e.enqueue({8,0,static_cast<uint32_t>(event->SHUTDOWN_INITIATED_BY_TRANSPORT.Status),0,0,{}}); break;
        case QUIC_CONNECTION_EVENT_SHUTDOWN_INITIATED_BY_PEER: e.enqueue({8,0,0,0,event->SHUTDOWN_INITIATED_BY_PEER.ErrorCode,{}}); break;
        case QUIC_CONNECTION_EVENT_PEER_STREAM_STARTED: {
            std::lock_guard lock(e.acceptMutex);
            uint64_t id=0; uint32_t size=sizeof(id);
            auto stream=event->PEER_STREAM_STARTED.Stream;
            auto status=e.api->GetParam(stream,QUIC_PARAM_STREAM_ID,&size,&id);
            // Only the client opens the three fixed bidirectional lanes (IDs 0,4,8).
            if(e.stopping || !e.server || QUIC_FAILED(status) || id>8 || id%4 || event->PEER_STREAM_STARTED.Flags!=QUIC_STREAM_OPEN_FLAG_NONE) {
                RejectStream(e,stream); break;
            }
            auto& lane=e.lanes[id/4];
            if(lane.handle) { RejectStream(e,stream); break; }
            ++LiveStreams; lane.handle=stream; lane.started=true; e.api->SetCallbackHandler(stream,reinterpret_cast<void*>(StreamCallback),&lane); break;
        }
        case QUIC_CONNECTION_EVENT_DATAGRAM_STATE_CHANGED: e.datagramLimit=event->DATAGRAM_STATE_CHANGED.SendEnabled ? event->DATAGRAM_STATE_CHANGED.MaxSendLength : 0; break;
        case QUIC_CONNECTION_EVENT_DATAGRAM_RECEIVED: e.receive(3,event->DATAGRAM_RECEIVED.Buffer,1); break;
        case QUIC_CONNECTION_EVENT_DATAGRAM_SEND_STATE_CHANGED:
            if(QUIC_DATAGRAM_SEND_STATE_IS_FINAL(event->DATAGRAM_SEND_STATE_CHANGED.State)) {
                e.release(static_cast<Send*>(event->DATAGRAM_SEND_STATE_CHANGED.ClientContext),static_cast<uint32_t>(event->DATAGRAM_SEND_STATE_CHANGED.State));
                event->DATAGRAM_SEND_STATE_CHANGED.ClientContext=nullptr;
            } break;
        default: break;
        }
    } catch(...) { e.overflow=true; }
    (void)handle; return QUIC_STATUS_SUCCESS;
}
QUIC_STATUS QUIC_API ListenerCallback(HQUIC,void* context,QUIC_LISTENER_EVENT* event) noexcept {
    auto& e=*static_cast<Endpoint*>(context);
    if(event->Type!=QUIC_LISTENER_EVENT_NEW_CONNECTION) return QUIC_STATUS_SUCCESS;
    std::lock_guard lock(e.acceptMutex);
    if(e.stopping || e.runtime->peers>=32) return QUIC_STATUS_CONNECTION_REFUSED;
    Endpoint* peer=nullptr;
    try {
        peer=new Endpoint; peer->runtime=e.runtime; peer->api=e.api; peer->server=true; peer->acceptedPeer=true;
        ++e.runtime->peers;
        peer->connection=event->NEW_CONNECTION.Connection;
        // Ownership passes to the application when this callback returns success.
        // Configure only after accept, avoiding callback-context lifetime on a rejected configuration.
        e.accepted.push_back(peer);
        e.api->SetCallbackHandler(peer->connection,reinterpret_cast<void*>(ConnectionCallback),peer);
        ++LiveConnections;
        return QUIC_STATUS_SUCCESS;
    } catch(...) {
        if(peer) { --e.runtime->peers; delete peer; }
        return QUIC_STATUS_OUT_OF_MEMORY;
    }
}
// Destroy is called only by the serialized managed owner, never in a callback.
EXPORT void mtq_destroy(Endpoint* e) noexcept {
    if(!e) return;
    e->stopping=true;
    if(e->listener) { e->api->ListenerClose(e->listener); e->listener=nullptr; }
    // ListenerClose is a barrier for accepts. Unclaimed peers belong to the listener.
    for(auto peer:e->accepted) mtq_destroy(peer);
    if(e->connection) {
        e->api->ConnectionShutdown(e->connection,QUIC_CONNECTION_SHUTDOWN_FLAG_NONE,0);
        // Close APIs are callback barriers; contexts remain alive through the final close.
        std::array<HQUIC,3> streams{};
        { std::lock_guard lock(e->acceptMutex); for(size_t i=0;i<3;++i) streams[i]=e->lanes[i].handle; }
        for(size_t i=0;i<3;++i) if(streams[i] && e->lanes[i].started && !e->lanes[i].shutdownComplete)
            e->api->StreamShutdown(streams[i],QUIC_STREAM_SHUTDOWN_FLAG_ABORT|QUIC_STREAM_SHUTDOWN_FLAG_IMMEDIATE,0);
        {
            std::unique_lock lock(e->shutdownMutex);
            e->shutdownCondition.wait(lock,[&] {
                for(size_t i=0;i<3;++i) if(streams[i] && e->lanes[i].started && !e->lanes[i].shutdownComplete) return false;
                return true;
            });
        }
        for(auto stream:streams) if(stream) { e->api->StreamClose(stream); --LiveStreams; }
        e->api->ConnectionClose(e->connection); --LiveConnections;
    }
    // Any contexts not terminally reported during abort are reclaimed only after callbacks stop.
    for(auto send:e->sends) delete send;
    if(e->acceptedPeer) --e->runtime->peers;
    delete e;
}
// Absolute DLL path is hash-validated by the managed owner before entry.
EXPORT uint32_t mtq_create(const wchar_t* dllPath,int server,const char* credentialPath,const char* password,Endpoint** result) noexcept {
    if(!dllPath || !credentialPath || !password || !result) return 1; *result=nullptr;
    auto e=new(std::nothrow) Endpoint; if(!e) return 1; e->server=server!=0;
    try {
        e->runtime=std::make_shared<Runtime>();
        e->runtime->module=LoadLibraryExW(dllPath,nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
        if(!e->runtime->module) throw uint32_t(HRESULT_FROM_WIN32(GetLastError()));
        auto open=reinterpret_cast<QUIC_STATUS(QUIC_API*)(uint32_t,const void**)>(GetProcAddress(e->runtime->module,"MsQuicOpenVersion"));
        if(!open || !GetProcAddress(e->runtime->module,"MsQuicClose")) throw uint32_t(1);
        auto status=open(2,reinterpret_cast<const void**>(&e->runtime->api)); if(QUIC_FAILED(status)) throw uint32_t(status); e->api=e->runtime->api;
        QUIC_REGISTRATION_CONFIG registration{"Meitou.v21",QUIC_EXECUTION_PROFILE_LOW_LATENCY};
        status=e->api->RegistrationOpen(&registration,&e->runtime->registration); if(QUIC_FAILED(status)) throw uint32_t(status);
        QUIC_SETTINGS settings{}; settings.IdleTimeoutMs=180000; settings.IsSet.IdleTimeoutMs=TRUE;
        settings.KeepAliveIntervalMs=5000; settings.IsSet.KeepAliveIntervalMs=TRUE;
        settings.PeerBidiStreamCount=e->server ? 3 : 0; settings.IsSet.PeerBidiStreamCount=TRUE;
        settings.DatagramReceiveEnabled=TRUE; settings.IsSet.DatagramReceiveEnabled=TRUE;
        QUIC_BUFFER alpn{sizeof(Alpn)-1,reinterpret_cast<uint8_t*>(const_cast<char*>(Alpn))};
        status=e->api->ConfigurationOpen(e->runtime->registration,&alpn,1,&settings,sizeof(settings),nullptr,&e->runtime->configuration); if(QUIC_FAILED(status)) throw uint32_t(status);
        QUIC_CREDENTIAL_CONFIG credentials{}; QUIC_CERTIFICATE_PKCS12 pfx{}; std::vector<uint8_t> bytes;
        if(e->server) {
            std::ifstream file(std::filesystem::path(reinterpret_cast<const char8_t*>(credentialPath)),std::ios::binary|std::ios::ate);
            if(!file || file.tellg()<=0 || file.tellg()>1024*1024) throw uint32_t(1);
            bytes.resize(static_cast<size_t>(file.tellg())); file.seekg(0); file.read(reinterpret_cast<char*>(bytes.data()),static_cast<std::streamsize>(bytes.size()));
            if(!file) throw uint32_t(1);
            pfx.Asn1Blob=bytes.data(); pfx.Asn1BlobLength=static_cast<uint32_t>(bytes.size()); pfx.PrivateKeyPassword=password;
            credentials.Type=QUIC_CREDENTIAL_TYPE_CERTIFICATE_PKCS12; credentials.CertificatePkcs12=&pfx;
        } else {
            credentials.Type=QUIC_CREDENTIAL_TYPE_NONE;
            credentials.Flags=QUIC_CREDENTIAL_FLAG_CLIENT|QUIC_CREDENTIAL_FLAG_SET_CA_CERTIFICATE_FILE|QUIC_CREDENTIAL_FLAG_USE_TLS_BUILTIN_CERTIFICATE_VALIDATION;
            credentials.CaCertificateFile=credentialPath;
        }
        status=e->api->ConfigurationLoadCredential(e->runtime->configuration,&credentials);
        if(!bytes.empty()) SecureZeroMemory(bytes.data(),bytes.size());
        if(QUIC_FAILED(status)) throw uint32_t(status);
        *result=e; return 0;
    } catch(uint32_t status) { mtq_destroy(e); return status ? status : 1; } catch(...) { mtq_destroy(e); return 1; }
}
EXPORT uint32_t mtq_start(Endpoint* e,const char* host,const char* serverName,uint16_t port) noexcept {
    if(!e || e->connection || e->listener) return 1;
    if(e->server) {
        auto status=e->api->ListenerOpen(e->runtime->registration,ListenerCallback,e,&e->listener); if(QUIC_FAILED(status)) return uint32_t(status);
        QUIC_ADDR address{}; if(!host || !QuicAddrFromString(host,port,&address)) return 1;
        QUIC_BUFFER alpn{sizeof(Alpn)-1,reinterpret_cast<uint8_t*>(const_cast<char*>(Alpn))};
        return uint32_t(e->api->ListenerStart(e->listener,&alpn,1,&address));
    }
    if(!host || !serverName || !port) return 1;
    HQUIC connection{};
    auto status=e->api->ConnectionOpen(e->runtime->registration,ConnectionCallback,e,&connection); if(QUIC_FAILED(status)) return uint32_t(status); e->connection=connection; ++LiveConnections;
    QUIC_ADDR address{}; if(!QuicAddrFromString(host,port,&address)) return 1;
    status=e->api->SetParam(e->connection,QUIC_PARAM_CONN_REMOTE_ADDRESS,sizeof(address),&address); if(QUIC_FAILED(status)) return uint32_t(status);
    return uint32_t(e->api->ConnectionStart(e->connection,e->runtime->configuration,QuicAddrGetFamily(&address),serverName,port));
}
EXPORT uint32_t mtq_listener_port(Endpoint* e,uint16_t* port) noexcept {
    if(!e || !e->listener || !port) return 1;
    QUIC_ADDR address{}; uint32_t size=sizeof(address);
    auto status=e->api->GetParam(e->listener,QUIC_PARAM_LISTENER_LOCAL_ADDRESS,&size,&address);
    if(QUIC_FAILED(status)) return uint32_t(status);
    *port=QuicAddrGetPort(&address); return 0;
}
EXPORT uint32_t mtq_open_lanes(Endpoint* e) noexcept {
    if(!e || e->server || !e->connected || e->stopping || e->lanes[0].handle) return 1;
    for(auto& lane:e->lanes) {
        HQUIC stream{};
        auto status=e->api->StreamOpen(e->connection,QUIC_STREAM_OPEN_FLAG_NONE,StreamCallback,&lane,&stream); if(QUIC_FAILED(status)) return uint32_t(status); lane.handle=stream; ++LiveStreams;
        status=e->api->StreamStart(lane.handle,QUIC_STREAM_START_FLAG_IMMEDIATE); if(QUIC_FAILED(status)) return uint32_t(status); lane.started=true;
    }
    return 0;
}
EXPORT uint32_t mtq_send(Endpoint* e,uint32_t lane,const uint8_t* bytes,uint32_t length,uint64_t token) noexcept {
    if(!e || !bytes || !length || lane>3 || !e->connected || e->closed || e->stopping || e->overflow) return 1;
    if(length>MaxChunk || (lane==3 && (length>1150 || length>e->datagramLimit))) return 1;
    if(lane<3 && !e->lanes[lane].handle) return 2;
    Send* send=nullptr;
    try {
        std::lock_guard lock(e->sendMutex);
        if(length>(lane==3 ? 65536u : MaxQueuedBytes)-e->laneBytes[lane] || e->laneSends[lane]==64) return 2;
        send=new Send(e,token,lane,bytes,length);
        send->buffer={length,send->bytes.data()}; e->sends.insert(send); e->submittedBytes+=length; e->laneBytes[lane]+=length; ++e->laneSends[lane];
    } catch(...) { delete send; return 1; }
    auto status=lane==3 ? e->api->DatagramSend(e->connection,&send->buffer,1,QUIC_SEND_FLAG_NONE,send)
        : e->api->StreamSend(e->lanes[lane].handle,&send->buffer,1,QUIC_SEND_FLAG_NONE,send);
    if(QUIC_FAILED(status)) { e->release(send,1); return uint32_t(status); } return 0;
}
// The front event is retained on insufficient destination capacity. Metadata is fixed-width.
EXPORT int mtq_poll(Endpoint* e,uint32_t* metadata,uint64_t* token,uint8_t* bytes,uint32_t capacity) noexcept {
    if(!e || !metadata || !token || !bytes) return -1;
    if(e->overflow) {
        if(e->connection && !e->stopping.exchange(true)) e->api->ConnectionShutdown(e->connection,QUIC_CONNECTION_SHUTDOWN_FLAG_NONE,1);
        return -2;
    }
    std::lock_guard lock(e->queueMutex);
    if(e->overflow) return -2;
    if(e->events.empty()) return e->overflow ? -2 : 0;
    auto& event=e->events.front(); metadata[0]=event.kind; metadata[1]=event.lane; metadata[2]=event.status; metadata[3]=event.length; *token=event.token;
    if(capacity<event.length) return -3;
    if(event.length) std::memcpy(bytes,event.bytes.data(),event.length);
    e->queuedBytes-=event.length; e->events.pop_front(); return 1;
}
EXPORT uint32_t mtq_statistics(Endpoint* e,uint64_t* values) noexcept {
    if(!e || !e->connection || !values) return 1;
    QUIC_STATISTICS_V2 stats{}; uint32_t size=sizeof(stats);
    auto status=e->api->GetParam(e->connection,QUIC_PARAM_CONN_STATISTICS_V2,&size,&stats); if(QUIC_FAILED(status)) return uint32_t(status);
    values[0]=stats.SendTotalBytes; values[1]=stats.RecvTotalBytes; values[2]=stats.SendTotalPackets; values[3]=stats.RecvTotalPackets;
    values[4]=stats.Rtt; values[5]=e->datagramLimit;
    { std::lock_guard lock(e->sendMutex); values[6]=e->submittedBytes; values[7]=e->sends.size(); }
    { std::lock_guard lock(e->queueMutex); values[8]=e->queuedBytes; values[9]=e->events.size(); }
    return 0;
}

EXPORT Endpoint* mtq_accept(Endpoint* listener) noexcept {
    if(!listener || !listener->listener || listener->stopping) return nullptr;
    std::lock_guard lock(listener->acceptMutex);
    if(listener->accepted.empty()) return nullptr;
    auto peer=listener->accepted.front(); listener->accepted.pop_front();
    auto status=peer->api->ConnectionSetConfiguration(peer->connection,peer->runtime->configuration);
    if(QUIC_FAILED(status)) {
        try { peer->enqueue({8,0,static_cast<uint32_t>(status),0,0,{}}); } catch(...) { peer->overflow=true; }
        peer->api->ConnectionShutdown(peer->connection,QUIC_CONNECTION_SHUTDOWN_FLAG_NONE,1);
    }
    return peer;
}



EXPORT void mtq_resources(uint64_t* counts) noexcept {
    counts[0]=LiveEndpoints; counts[1]=LiveRuntimes; counts[2]=LiveConnections; counts[3]=LiveStreams; counts[4]=LiveSends;
}
