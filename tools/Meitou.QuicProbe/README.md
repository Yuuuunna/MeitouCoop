# Native QUIC platform probe

Isolated Windows x64 executable; no game code/assets, .NET runtime, Schannel or
TCP fallback. Uses official MsQuic v2 headers and the pinned OpenSSL package
2.6.3. A platform failure blocks transport acceptance. Server 2016 remains
**Unknown** until the same ZIP runs there; a Windows 10 result is not evidence
that Server 2016 works.

**Observed (Windows 10 19045 x64, local native probe):** two-round loopback and
independent-process client/server exchanged encrypted streams/DATAGRAMs with
application ACKs and zero remaining handles/buffers. Wrong CA, wrong hostname,
unreachable-peer timeout and altered DLL were rejected. Server 2016 and public
VPS UDP reachability have not been tested.

Build from the repository (MSVC C++ x64 tools and Windows SDK required):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Meitou.QuicProbe/build.ps1
```

Output: `artifacts/network-vnext-platform/probe-win-x64.zip`. Build checks the
package/DLL hashes; the executable checks the DLL again before loading it from
its own directory. Manifest records package, DLL, executable and source hashes.
The ZIP contains licenses and no private key, certificate, game data or account
credentials. Preserve it unchanged when copying to the VPS and client.

Repository-only acceptance runner (creates local test keys under artifacts):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/Meitou.QuicProbe/test-probe.ps1
```

Run in an extracted directory, using a new, dedicated UDP port (47421 here).
No command edits the firewall. The operator must provide UDP reachability for
the chosen port. Do not use the production game's port.

```powershell
.\Meitou.QuicProbe.exe --create-certificate .\probe-certificate
.\Meitou.QuicProbe.exe --loopback --certificate .\probe-certificate\server.pfx --ca .\probe-certificate\server.pem --rounds 2 --output .\loopback.json
```

Create the certificate on the **VPS**, then listen:

```powershell
.\Meitou.QuicProbe.exe --create-certificate .\probe-certificate
.\Meitou.QuicProbe.exe --listen 47421 --certificate .\probe-certificate\server.pfx --rounds 2 --timeout-seconds 120 --output .\server.json
```

Copy **only** `probe-certificate/server.pem` to the client via a trusted channel.
Check its SHA256 against the certificate-creation JSON on the VPS, then run:

```powershell
Get-FileHash .\server.pem -Algorithm SHA256
.\Meitou.QuicProbe.exe --connect VPS_NUMERIC_IP --port 47421 --ca .\server.pem --rounds 2 --timeout-seconds 30 --output .\client.json
```

The PFX has an empty password and contains a private key: keep it on the VPS,
protect it with filesystem access controls, and do not put it in a report/ZIP.
Certificate generation uses Windows CryptoAPI; it neither installs a trust root
nor leaves the temporary provider key behind. It refuses to overwrite outputs.
The client explicitly trusts the supplied PEM and validates hostname `localhost`;
the numeric destination is set separately. This is a dedicated probe identity,
not production server identity or mutual application authentication. There is no
certificate-verification bypass. Never trust an unauthenticated downloaded PEM.
`--server-name` changes the hostname to validate and exists for negative checks;
the generated probe certificate requires the default `localhost`.

Exit 0 and `passed:true` require both directions to exchange a payload-checked
TLS 1.3 stream message and three native unreliable DATAGRAMs at 32 bytes,
`min(1150, negotiated limit)` and the negotiated limit (capped at 4096), receive
explicit **application** ACKs over the reliable stream, finish shutdown and
reconnect with fresh handles for every round. No datagram retransmission is
implemented. Loss can fail this capability probe; it is not a loss benchmark.
Two rounds are the default; `--rounds` permits 1–16. Timeout is per round.

Callbacks copy into a fixed 32-entry/4096-byte receive queue and update native
lifecycle counters. Main-thread work parses frames, sends ACKs and closes
handles. Stream send storage is retained through SEND_COMPLETE; DATAGRAM
storage/context is conservatively retained through its terminal native event.
Native completion is never counted as an application receipt. JSON records OS
build/product type, loaded version/hash/provider, TLS cipher, DATAGRAM limits,
path MTU, ACK masks, UDP-byte counters and zero-live-resource checks. It records
no addresses, payload dumps, names, keys or game credentials. Reports describe
this short capability check only; they do not establish game latency, load,
long-run memory, impaired-link reliability or production acceptance.

Official contracts: [DatagramSend](https://github.com/microsoft/msquic/blob/v2.6.3/docs/api/DatagramSend.md),
[connection events](https://github.com/microsoft/msquic/blob/v2.6.3/docs/api/QUIC_CONNECTION_EVENT.md),
[stream events](https://github.com/microsoft/msquic/blob/v2.6.3/docs/api/QUIC_STREAM_EVENT.md),
[credentials](https://github.com/microsoft/msquic/blob/v2.6.3/docs/api/QUIC_CREDENTIAL_CONFIG.md).
