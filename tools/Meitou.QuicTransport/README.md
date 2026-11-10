# Native QUIC adapter

Build/test on Windows x64 with MSVC and .NET 10:

```powershell
./tools/Meitou.QuicProbe/build.ps1 # prepares the pinned NuGet archive
./tools/Meitou.QuicTransport/test-transport.ps1
```

Artifacts stay in `artifacts/network-vnext-transport/`. Build verifies the 2.6.3
OpenSSL package hash, freshly extracts its official headers and verifies the DLL
hash. `manifest.json` records the shim hash required by `NativeQuicEndpoint`.
Only use a manifest obtained from a trusted build/deployment channel.

`Listen(IPAddress, port)` binds the requested address; port zero allocates an ephemeral port reported by
`LocalPort`. The default overload binds IPv4 wildcard. `TryAccept` transfers independently disposable peers (32
live peers maximum). Unclaimed peers belong to the listener. Claimed peers
share the native registration/configuration and survive listener disposal.
Client streams 0/4/8 map to Control/Lifecycle/Bulk; State is a DATAGRAM.
The enum numeric values differ from the scheduler: map by named lane.
`OpenReliableLanes` follows Connected. Stream bytes have no frame boundaries;
feed them to the bounded incremental application framer. TLS authenticates the
server, not a player/account: application admission is required before gameplay.

Native callbacks copy into at most 128 events/256 KiB per peer; overflow is
immediately terminal on poll. Each reliable lane independently bounds native
send ownership to 256 KiB/64 submissions; datagrams to 64 KiB/64 submissions.
Application scheduling, deadlines, pacing and ACK-based bulk credit remain above
this layer. Send completion releases storage; it is not an application receipt.
Shutdown aborts streams, waits for SHUTDOWN_COMPLETE, closes streams/connections,
then reclaims any remaining send contexts after the callback barrier.

**Observed:** local Windows smoke tests cover two peers, all lanes, continued
traffic after listener disposal, pending-send destruction and live-resource
counts returning to baseline, wrong hostname/CA with explicit TLS alert status,
and bounded terminal queue overflow. Tests skip without the native artifact env.
The game sessions use this adapter. It has not itself run on Server 2016/public UDP; the separate pinned
platform probe passed there. Sustained load, impairment, IPv6 end-to-end validation and production
acceptance remain open. Native keepalive runs every 5 s, with a 180 s idle timeout for cold world preparation;
application handshake and preparation deadlines remain separately enforced.

Statistics include UDP payload bytes in both directions and QUIC packet counts.
Coalescing means these are not exact UDP datagram counts or full wire usage;
packet capture/datapath accounting is still required for the bandwidth gate.
