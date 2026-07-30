# mqtt-subset.md — MQTT 3.1.1 implementation subset

Only what is written down here may be implemented. Anything absent is UNSPECIFIED and the
corresponding code path must return an error instead of guessing.

## Sources

| # | Document | Publisher / date |
|---|---|---|
| S1 | *MQTT Version 3.1.1* — OASIS Standard | OASIS, 2014-10-29 (public: docs.oasis-open.org/mqtt/mqtt/v3.1.1/os/) |
| S2 | Pinned golden vector | this repository |

Written from the public specification only; no broker or client source was consulted.

## 1. Fixed header — S1 §2.2

Every control packet starts with:

```
byte 1:  bits 7..4 = control packet type
         bits 3..0 = flags specific to the packet type
byte 2+: Remaining Length (1..4 bytes)
```

Remaining Length counts the variable header plus the payload — it excludes the fixed header
itself (S1 §2.2.3).

### Control packet types used (S1 §2.2.1, Table 2.1)

| Value | Name | Direction | Fixed-header byte 1 |
|---|---|---|---|
| 1 | CONNECT | client → server | `0x10` |
| 2 | CONNACK | server → client | `0x20` |
| 3 | PUBLISH | either | `0x30` for DUP=0, QoS=0, RETAIN=0 |
| 12 | PINGREQ | client → server | `0xC0` |
| 13 | PINGRESP | server → client | `0xD0` |
| 14 | DISCONNECT | client → server | `0xE0` |

Types 4–11 (PUBACK, PUBREC, PUBREL, PUBCOMP, SUBSCRIBE, SUBACK, UNSUBSCRIBE, UNSUBACK) are
**not implemented**: this project is a publisher only, at QoS 0. Encoding one must fail with an
explicit error, and receiving one is a protocol error.

### PUBLISH flags — S1 §2.2.2

`byte 1 = 0x30 | (DUP << 3) | (QoS << 1) | RETAIN`. Only `DUP = 0`, `QoS = 0` are implemented;
`RETAIN` is configurable because it is a single documented bit.

### Remaining Length encoding — S1 §2.2.3

Seven bits per byte, least significant group first; the high bit of a byte means "another byte
follows". At most four bytes, so the largest encodable length is 268 435 455.

| Digits | Range |
|---|---|
| 1 | 0 … 127 |
| 2 | 128 … 16 383 |
| 3 | 16 384 … 2 097 151 |
| 4 | 2 097 152 … 268 435 455 |

A fifth continuation byte is malformed (S1 §2.2.3).

## 2. UTF-8 encoded strings — S1 §1.5.3

A string is a two-byte big-endian byte count followed by that many UTF-8 bytes, so the longest
string is 65 535 bytes. The character U+0000 must not appear, and the encoding must be
well-formed UTF-8 (no surrogate halves).

## 3. CONNECT — S1 §3.1

```
fixed header:     0x10, Remaining Length
variable header:  protocol name  "MQTT"  (UTF-8 string → 00 04 4D 51 54 54)
                  protocol level 0x04    (= 3.1.1)
                  connect flags  1 byte
                  keep alive     2 bytes, big-endian, seconds
payload:          client identifier (UTF-8 string, always present)
                  [will topic] [will message] [user name] [password], in this order,
                  each present only if its flag is set
```

### Connect flags — S1 §3.1.2.3

| Bit | Meaning |
|---|---|
| 7 | User Name Flag |
| 6 | Password Flag |
| 5 | Will Retain |
| 4–3 | Will QoS |
| 2 | Will Flag |
| 1 | Clean Session |
| 0 | Reserved — must be 0 |

**Will messages are not implemented** (the Will Flag is always 0), so bits 5, 4, 3 and 2 are
always 0. User name and password are implemented, because their payload position and flag bits
are fully specified above.

### Keep alive — S1 §3.1.2.10

Seconds until the client must send some packet; `0` disables the mechanism. The client is
expected to send a packet within 1.5 × keep alive.

### Client identifier — S1 §3.1.3.1

A server must accept 1–23 bytes of `0-9a-zA-Z` and may accept more. A zero-length identifier is
only allowed together with Clean Session = 1.

## 4. CONNACK — S1 §3.2

```
fixed header:     0x20, Remaining Length = 2
variable header:  connect acknowledge flags (bit 0 = Session Present, bits 7..1 = 0)
                  connect return code (1 byte)
```

| Return code | Meaning |
|---|---|
| `0` | Connection accepted |
| `1` | Unacceptable protocol version |
| `2` | Identifier rejected |
| `3` | Server unavailable |
| `4` | Bad user name or password |
| `5` | Not authorized |

Codes 6–255 are reserved (S1 §3.2.2.3), so an unknown code is surfaced verbatim rather than
renamed.

## 5. PUBLISH (QoS 0) — S1 §3.3

```
fixed header:     0x30 | RETAIN, Remaining Length
variable header:  topic name (UTF-8 string)
                  packet identifier — PRESENT ONLY FOR QoS > 0, therefore never here
payload:          the remaining bytes, with no length prefix
```

Topic name rules (S1 §4.7): at least one character, no U+0000, and **no wildcard characters
`+` or `#`** — those are only valid in subscriptions.

## 6. PINGREQ / PINGRESP / DISCONNECT — S1 §3.12, §3.13, §3.14

Each has no variable header and no payload, so each is exactly two bytes:

| Packet | Bytes |
|---|---|
| PINGREQ | `C0 00` |
| PINGRESP | `D0 00` |
| DISCONNECT | `E0 00` |

After sending DISCONNECT the client must close the network connection and must not send
anything further (S1 §3.14.4).

## 7. Transport — S1 §4.2, §5.1

MQTT runs over TCP. The registered ports are **1883** for plain TCP and 8883 for TLS.
**TLS is not implemented**: the target environment is an isolated network and this project is
deliberately local-first, so only plain TCP on 1883 is offered.

## 8. Golden vector (S2 — do not modify)

PUBLISH, QoS 0, topic `t/a`, payload `21`:

- fixed header byte 1 = `0x30`
- Remaining Length = 2 (topic length field) + 3 (`t/a`) + 2 (`21`) = **7**
- complete packet = `30 07 00 03 74 2F 61 32 31`

## 9. Topic naming used by this project

MQTT channels are addressed by name, not by a wire address, so their channel address must be
`topic:0`. The published topic is assembled as:

```
topic = [topic_prefix "/"] channel-name with '.' replaced by '/'
```

so a channel `tank1.level` on an endpoint with `topic_prefix: plant` publishes to
`plant/tank1/level`. This is a Protocol Bridge configuration convention, not part of S1; it
exists because channel names are single identifiers while topics are multi-level.
