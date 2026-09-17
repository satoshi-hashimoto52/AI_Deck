# AI Deck — Network Protocol

Covers FR-060 to FR-068 and §4.4 of [Issue #1](https://github.com/satoshi-hashimoto52/AI_Deck/issues/1).

Scope: **same LAN only**. Operation across the internet is out of scope for V1 and the
protocol has no authentication, so it must not be exposed beyond a trusted network.

## 1. Transports

| Channel | Transport | Port | Carries |
| --- | --- | --- | --- |
| Discovery | UDP broadcast | 47812 | Host beacon, 1 Hz |
| Reliable | TCP | 47810 | Session, transport commands, loading, recording, queries |
| Fast | UDP | 47811 | Continuous controls, state snapshots, heartbeats |

The split follows §4.4 directly. Anything that changes *what is loaded* or *whether audio
is running* goes over TCP, because losing it would leave the two ends describing different
worlds. Anything that is a sample of a continuously moving control goes over UDP, because
a late value is worse than no value — by the time it arrives the finger has already moved.

`MessageType.Channel()` is the single source of truth for that classification and is
covered by a test. `AllStop` is reliable on purpose: a safety command must never be
droppable.

## 2. Frame format

Every frame is a fixed 22-byte header followed by an opaque payload. All integers are
**little-endian**.

```
offset size field
 0     2    magic            'A' (0x41), 'D' (0x44)
 2     2    protocolVersion  uint16
 4     1    messageType      uint8   (MessageType)
 5     1    deck             uint8   0 = A, 1 = B, 0xFF = not deck scoped
 6     2    flags            uint16  reserved, must be 0
 8     4    sequence         uint32
12     8    timestampMs      int64   sender clock, Unix ms UTC
20     2    payloadLength    uint16  ≤ 60000
22     …    payload
```

Notes:

* **Magic** lets a TCP stream resynchronise after damage instead of wedging.
* **`timestampMs`** is diagnostic only. It is never used to drive audio: the two clocks are
  not synchronised and trusting them would make playback depend on clock skew.
* **`flags`** must be zero. A non-zero value means the sender used a feature this build does
  not know about, and the frame is rejected rather than half-interpreted.
* **`payloadLength`** caps a frame at 60 kB, comfortably inside a jumbo-free path once
  fragmented, and bounds the memory a malicious or broken sender can make the peer allocate.

### Decode outcomes

| Result | Meaning | Recovery |
| --- | --- | --- |
| `None` | Frame decoded | — |
| `Incomplete` | Header or payload not fully arrived | Wait for more bytes |
| `BadMagic` | Not an AI Deck frame | Scan forward to the next magic pair |
| `UnsupportedVersion` | Peer speaks another protocol version | Consume the frame, refuse the session |
| `PayloadTooLarge` | Declared length over the cap | Consume the header, resynchronise |
| `BadFlags` | Reserved bits set | Consume the frame, count it |

`Incomplete` and `BadMagic` consume nothing, so the caller can retry once more data lands.
Every other error consumes the frame it identified, so a single bad packet cannot stall the
connection.

## 3. Versioning (FR-068)

`ProtocolInfo.Version` is currently **1**, and `MinimumSupportedVersion` is **1**.

A frame whose version falls outside `[MinimumSupportedVersion, Version]` is rejected with
`DecodeError.UnsupportedVersion`, and the host answers the handshake with an `Error`
message naming both versions before closing. There is no silent downgrade and no partial
connection: a mismatched peer is told what is wrong.

Bump `Version` for **any** incompatible change — a header field, a payload layout, or the
numeric value of an existing enum member. Adding a new `MessageType` value is compatible,
because an unknown type decodes to `MessageType.Unknown` and is ignored.

## 4. Message catalogue

`C` = controller → host, `H` = host → controller.

### Session (1–9)

| # | Name | Dir | Channel | Payload |
| --- | --- | --- | --- | --- |
| 1 | `Hello` | C | TCP | device name, app version, controller UDP port |
| 2 | `Ping` | both | UDP | — |
| 3 | `Bye` | both | TCP | — |
| 100 | `HelloAck` | H | TCP | host name, app version, host UDP port |
| 101 | `Pong` | both | UDP | — |

### Deck transport (10–23, reliable)

| # | Name | Payload |
| --- | --- | --- |
| 10 | `LoadTrack` | string track id |
| 11 | `Play` | — |
| 12 | `Pause` | — |
| 13 | `TogglePlay` | — |
| 14 | `CueSet` | — |
| 15 | `CueReturn` | — |
| 16 | `Seek` | double seconds |
| 19 | `SyncToggle` | bool enable |
| 20 | `LoopSetRegion` | double in, double out |
| 21 | `LoopToggle` | — |
| 22 | `LoopBeats` | float beats |
| 23 | `Eject` | — |

### Platter and tempo (30–37)

| # | Name | Channel | Payload |
| --- | --- | --- | --- |
| 30 | `TempoFader` | UDP | float −1…+1 |
| 31 | `TempoRange` | TCP | float percent |
| 32 | `JogNudge` | UDP | float delta |
| 33 | `ScratchBegin` | TCP | — |
| 34 | `ScratchUpdate` | UDP | float rate |
| 35 | `ScratchEnd` | TCP | — |
| 36 | `Brake` | TCP | — |
| 37 | `Backspin` | TCP | — |

`ScratchBegin` and `ScratchEnd` are reliable while `ScratchUpdate` is not. Losing a mid-
gesture sample is inaudible; losing the *end* of a gesture would leave the platter held at
a scratch rate forever, which is exactly the failure §9 tells us to design out.

### Mixer and effects (40–47)

| # | Name | Channel | Payload |
| --- | --- | --- | --- |
| 40 | `ChannelGain` | UDP | float 0…1 |
| 41 | `Crossfader` | UDP | float −1…+1 |
| 42 | `MasterGain` | UDP | float 0…1 |
| 43 | `Filter` | UDP | float −1…+1 |
| 44 | `Mute` | TCP | bool |
| 45 | `Echo` | TCP | bool |
| 46 | `CueMonitor` | TCP | bool |
| 47 | `CrossfaderCurve` | TCP | byte curve |

### Recording, queries, safety (50–70)

| # | Name | Channel | Payload |
| --- | --- | --- | --- |
| 50 | `RecordStart` | TCP | — |
| 51 | `RecordStop` | TCP | — |
| 60 | `RequestLibrary` | TCP | — |
| 61 | `RequestWaveform` | TCP | string track id |
| 62 | `RequestSnapshot` | TCP | — |
| 70 | `AllStop` | TCP | — |

### Host to controller (110–130)

| # | Name | Channel | Payload |
| --- | --- | --- | --- |
| 110 | `StateSnapshot` | UDP | full state, see §6 |
| 111 | `LibraryChunk` | TCP | revision, index, count, up to 64 tracks |
| 112 | `WaveformChunk` | TCP | track id, index, count, up to 8192 envelope buckets |
| 120 | `Error` | TCP | string, user-facing |
| 121 | `Notice` | TCP | string, user-facing |
| 130 | `Discovery` | UDP broadcast | service name, host name, TCP port, UDP port |

## 5. Session lifecycle

### Discovery (FR-060)

The host broadcasts a `Discovery` frame to `255.255.255.255:47812` once per second. The
controller listens, collects beacons, and offers the hosts it heard from. A beacon whose
`ServiceName` is not `"AIDeck"` is ignored, so an unrelated broadcast on the same port
cannot be mistaken for a host.

### Manual connection (FR-061)

Discovery fails on networks with broadcast isolation — guest Wi-Fi, some mesh systems,
client isolation on a corporate AP. The controller therefore always offers direct entry of
an IP address, and the last successful address is stored in `AppSettings.LastHostAddress`
(FR-080) and offered first on the next launch.

### Handshake

```
controller ──► Hello(device, version, udpPort)      TCP
host       ──► HelloAck(host, version, udpPort)     TCP   … or Error + close
host       ──► LibraryChunk × n                     TCP
host       ──► StateSnapshot                        UDP, then every 50 ms
```

The host rejects a second controller while one is connected, with an `Error` explaining
why. Two controllers driving one deck would produce a control-fight with no arbitration
rule, and V1 has no reason to solve that.

### Heartbeat (FR-062, FR-065)

`Ping`/`Pong` every second on the fast channel. Three seconds of silence marks the peer
gone. The controller then shows `Reconnecting`, keeps its UI responsive but visibly
inactive, and retries. On reconnection it re-runs the handshake and takes the host's state
wholesale — there is no attempt to replay buffered intents, because a command composed
before the drop is almost certainly no longer what the user wants.

### Disconnect (FR-066, §9)

On the host, losing the controller:

1. releases every continuous control on both decks — scratch, nudge, in-flight brake or
   backspin (`DeckModel.ReleaseContinuousControls`);
2. then applies `AppSettings.OnDisconnect`, whose default is **`StopPlayback`**: both decks
   fade out over ~12 ms and pause.

The fade is not cosmetic. Cutting a playing buffer to zero in one sample is a click at full
output level, which is exactly the "sudden loud noise" §9 rules out.

## 6. State snapshot (FR-064)

A full snapshot, not a delta, broadcast at 20 Hz. Contents:

* host clock, library revision;
* per deck: transport state, motion mode, track id, position, length, base BPM, tempo fader
  and range, sync flag, effective rate, cue, loop, peak level, error reason;
* mixer: both channel gains, master gain, crossfader and curve, both filters, mutes, echoes,
  cue routing;
* master: peak, RMS, clipping flag;
* recording: active flag, elapsed seconds, file name;
* a short user-facing notice.

Sending the whole state every 50 ms costs about 5 kB/s. In exchange, a controller that
missed packets, was backgrounded, or has just reconnected is fully corrected by the next
snapshot with no catch-up protocol, no sequence reconciliation and no way to accumulate
drift. For a state this small that trade is not close.

`LibraryRevision` increments on every library mutation. The controller compares it against
what it holds and issues `RequestLibrary` when it differs, so the large payload is sent only
when it actually changed.

The file **name** is sent, never the full path (NFR-006).

## 7. Sequence numbers and flow control

### Stale rejection (FR-067)

`SequenceSource` allocates a counter per `(MessageType, DeckId?)` pair. `SequenceGate` on
the receiving side keeps the highest number seen per pair and rejects anything not strictly
newer.

Scoping per pair rather than globally matters: a jog gesture on deck A produces a torrent of
messages, and a single global counter would make an interleaved deck B message look stale.

Comparison is wrap-safe — `(int)(sequence - last) > 0` — so the 32-bit counter rolling over
is a non-event. A counter that jumps backwards by more than 2²⁰ is read as a peer that
restarted rather than as a stale packet, and the gate adopts the new baseline; otherwise a
reconnected controller starting again at 1 would be ignored forever.

### Bounded queue (NFR-004)

`OutboundQueue` holds at most 512 messages.

* A **fast** message replaces any queued message with the same key. Dragging the crossfader
  for ten seconds leaves exactly one message queued, not six hundred.
* A **reliable** message is never coalesced and never silently dropped. If the reliable
  backlog fills the queue, `Enqueue` returns `false` and the caller treats the link as dead
  and disconnects — which is the honest response, rather than growing memory while latency
  climbs.

## 8. Privacy (NFR-006)

Over the wire: device names, track titles and artists, and file names. Never full paths,
never file contents, never account information, never anything derived from the Apple ID.
`DiagnosticLog` truncates entries to 512 characters and strips newlines so a stray
exception message cannot smuggle a large payload into the log.
