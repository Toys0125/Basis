# Participant-bound P2P discovery v2

A main server connection over native IPv6 no longer limits a dual-stack client's
DIRECT socket to IPv6 NAT discovery. The client resolves all addresses of its
configured server host and the broker's optional IPv4 discovery host, sends NAT
requests from the direct manager's existing socket, and retries those requests.
LiteNetLib chooses its send socket from each destination family. The authenticated
main connection stays in place; no socket-family switch or IP translation occurs.

For servers joined by IPv6 literal, or whose connection hostname has no A record,
set `BASIS_P2P_IPV4_HOST` in the **C# server process environment** to a public IPv4
address or stable DNS name for the same broker, on the same UDP port. This value is
sent only on authenticated signaling. DNS is resolved at discovery time; do not
copy a rotating client IPv6 address into configuration. Without an IPv4 broker
address, or without usable client IPv4 routing, IPv4 discovery cannot succeed.
Relay remains available.

## Wire and identity

The existing signal prefix is unchanged: other-player ushort, session-token
string (maximum 64 characters), has-key byte, and optional 32-byte ephemeral key.
An optional trailing extension contains:

1. Byte `2` (discovery version).
2. Discovery-credential string, maximum 64 characters; empty for client signals.
3. IPv4-discovery-host string, maximum 255 characters; empty if not configured.

Strings use the existing Basis NetDataWriter encoding. No extension means legacy
version 0. Unsupported versions do not enable v2.

The initiator advertises v2 in Request. The broker allocates two independent
unguessable credentials, binds them to the authenticated initiator and target,
and sends only the recipient's credential in ServerArmed. Its forwarded Request
also carries the **target's** credential, proving broker support (an old broker
might simply forward the initiator's empty capability extension). The accepting
client waits for ServerArmed before discovery. Accept advertises v2; the broker
arms the target before forwarding Accept to the initiator. Keys are forwarded
unchanged, without forwarding either participant's credential to the other.

NatIntroduceRequest's existing token field carries the private credential, not
the shared session token. The broker rejects unknown credentials and shared-token
requests, associates both address families with the credential's participant,
and pins one local/observed-external endpoint pair per family per punch cycle.
Duplicate requests repeat introductions without occupying the other participant's
slot. A changed endpoint cannot replace a pinned candidate. An authenticated,
pair-validated LinkLost clears candidates/selection for a fresh mapping cycle.
Session teardown invalidates both credentials. Token collisions cannot overwrite
an existing session. Cancel/Decline/LinkLost must match the authenticated pair.
Credentials are bearer secrets on NAT UDP traffic; this does not add protection
against an on-path observer stealing those datagrams.

The first common family is selected for a punch cycle and used by both sides.
Later candidates cannot race the participants onto different IPv4/IPv6 connections.
Introductions and port prediction repeat on retries, rather than predicting only
on the first packet. Native IPv6 remains a valid selected family. If the selected
direct path cannot connect, normal bounded retries and relay fallback apply;
this change does not add automatic family racing after a selected path fails.
No common family means no introduction and no new Offloaded state.

Introduction responses, punch packets, and direct connect data retain the shared
session token. Simultaneous-open and ephemeral-key derivation remain in use.
IPv4-mapped IPv6 is canonicalized to IPv4 on candidate collection, endpoint wire
serialization and crypto lookup; genuine IPv6 is preserved. Direct endpoint key
associations survive overlapping inbound/outbound opens, with distinct nonce
ranges for new associations and cleanup on retry/teardown. Both LinkUp reports
are still needed for Offloaded confirmation; LinkLost resumes server relay.

## Compatibility and Rust

- New client + old C# broker: no private ServerArmed credential, so the client
  retains legacy single-address discovery. The mixed-family fix is unavailable.
- Old client + new C# broker: Request without v2 is canceled. An old target's
  Accept cancels both sides and removes the session. Ordinary server relay works;
  direct links require both clients to upgrade. This deliberate policy avoids
  pretending a legacy shared token securely identifies a participant.
- New clients + new C# broker: v2 discovery as described above.
- The unmodified Rust broker does **not** implement v2. It must parse/negotiate
  the extension, privately issue participant credentials in Request/ServerArmed,
  validate Accept capability, collect candidates by credential and family,
  reject shared-token/unknown discovery requests, select one common family,
  repeat prediction on retries, and clear credentials/candidates/offload safely.
  It must advertise an IPv4 discovery host when clients cannot resolve one from
  their connection hostname. Keep the fixed mapped-IPv4 endpoint serialization
  and IPv6 configuration aliases. Legacy fallback with that Rust version needs
  separate wire verification; this C# change does not assert it was tested.

No BasisRust checkout was changed. Canonical C# sources under `Basis Server` and
required Unity mirrors under `Basis/Packages` are synchronized.

## Validation and remaining end-to-end work

Validated: 117 focused tests passed; the netstandard2.1 server build passed
with zero errors (existing warnings remain).

Focused commands:

```sh
dotnet test 'Basis Server/BasisServerTests/BasisServerTests.csproj' \
  --filter 'FullyQualifiedName~P2P|FullyQualifiedName~CryptoLayer'
dotnet build 'Basis Server/BasisNetworkServer/BasisNetworkServer.csproj' -f netstandard2.1
```

Tests cover both initiating roles, participant binding across families,
duplicate/reordered/lost requests, third endpoints, pinning/rearming, consistent
family selection, legacy negotiation, private credential delivery, unchanged
key forwarding, mapped IPv4/native IPv6, and offload lifecycle/signals.
Real **local UDP** tests use the production direct-socket adapter and C# candidate
collector, an IPv4-only LiteNetLib manager, simultaneous-open and derived X25519
keys. They verify 100 known encrypted payloads each direction for both IPv4
initiating roles and a native IPv6 control. They verify the broker offload gate;
separate signal-routing tests verify Offloaded messages. They do not execute
Unity, DID login, a native-IPv6 authenticated main connection, or WAN routing.

WAN validation remains required with both upgraded clients and a v2 broker:
keep one authenticated main connection native IPv6, verify its discovered direct
endpoint is IPv4 against an IPv4-only peer in each initiating role, send known
payloads both ways, and verify receipt of Offloaded on both clients. Repeat a
native IPv6 control and a truly no-common-family case that continues carrying
traffic over the normal relay. Do not disable auth on an existing deployment.

In this workspace, public-host SSH failed with `Permission denied (publickey)`;
no remote process or deployment was changed. Unity is not installed/on PATH.
These local checks are not evidence of a successful cross-family WAN run or
of Unity/DID end-to-end behavior.
