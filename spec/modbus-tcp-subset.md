# modbus-tcp-subset.md — MODBUS implementation subset

Only what is written down here may be implemented. Anything absent is UNSPECIFIED and the
corresponding code path must return an error instead of guessing.

## Sources

| # | Document | Publisher / date |
|---|---|---|
| S1 | *MODBUS Application Protocol Specification V1.1b3* | Modbus Organization, 2012-04-26 (public, modbus.org) |
| S2 | *MODBUS Messaging on TCP/IP Implementation Guide V1.0b* | Modbus Organization, 2006-10-24 (public, modbus.org) |
| S3 | *MODBUS over Serial Line Specification and Implementation Guide V1.02* | Modbus Organization, 2006-12-20 (public, modbus.org) |
| S4 | Pinned golden vectors (standard verification values, quoted from S1 §6.3 worked example) | this repository |

"MODBUS" is a registered trademark of Schneider Electric, licensed to the Modbus
Organization. This is an independent, clean-room implementation written from the public
specification only.

## 1. Protocol Data Unit (PDU) — S1 §4.1, §6

A PDU is `function code (1 byte) || function data`. Maximum PDU size is **253 bytes**
(S1 §4.1). All multi-byte protocol fields are transmitted **most significant byte first**
(S1 §4.2).

Data addresses on the wire are `0x0000`–`0xFFFF` (S1 §4.4). The `4xxxx` / `3xxxx` operator
notation is a numbering convention outside the protocol and is **not** implemented; this
project addresses registers by their zero-based wire address.

## 2. Application Data Unit (ADU)

### 2.1 MODBUS TCP — S2 §3.1.3, §4.1

```
MBAP header (7 bytes)                                   PDU
+--------------------+--------------------+---------+---------+------------------+
| Transaction Id (2) | Protocol Id (2)    | Len (2) | Unit(1) | function code .. |
+--------------------+--------------------+---------+---------+------------------+
```

| Field | Size | Value |
|---|---|---|
| Transaction Identifier | 2, big-endian | Chosen by the client; the server copies it into the response (S2 §4.1) |
| Protocol Identifier | 2, big-endian | `0x0000` for MODBUS (S2 §4.1) |
| Length | 2, big-endian | Byte count of everything that follows the Length field, i.e. `1 (unit id) + PDU size` (S2 §4.1) |
| Unit Identifier | 1 | Slave/unit address; used to route through a bridge to a serial sub-network (S2 §4.1) |

- Maximum TCP ADU = `7 + 253` = **260 bytes** (S2 §4.1).
- Registered system port is **502** (S2 §3.1.1).
- MODBUS TCP carries **no checksum**: TCP already guarantees integrity (S2 §3.1.3).

### 2.2 MODBUS RTU (serial) — S3 §2.5.1, S1 Appendix B

```
+-------------+--------------+-----------+
| Address (1) | PDU (1..253) | CRC (2)   |
+-------------+--------------+-----------+
```

- Maximum RTU ADU = **256 bytes** (S3 §2.5.1).
- Slave addresses: `1`–`247` individually addressable, `0` = broadcast, `248`–`255` reserved (S3 §2.2).
- CRC-16, computed over address + PDU, is appended **low byte first** (S3 §2.5.1.2).

CRC-16 algorithm (S3 §2.5.1.2, S1 Appendix B):

```
crc = 0xFFFF
for each byte b:
    crc ^= b
    repeat 8 times:
        if crc & 1: crc = (crc >> 1) ^ 0xA001
        else:       crc = crc >> 1
```

`0xA001` is the reversed representation of the generator polynomial `x^16 + x^15 + x^2 + 1`.
Running the same routine across a complete frame including its two CRC bytes yields `0x0000`.

**UNSPECIFIED — RTU line timing.** The 3.5-character inter-frame silent interval and the
1.5-character intra-frame timeout (S3 §2.5.1.1) are not implemented, because correct
character-time derivation for arbitrary baud/parity/stop-bit combinations is not recorded
here. Consequence: **no Modbus-RTU endpoint is offered.** The RTU codec exists only as a
pure frame encoder/decoder (it is what the CRC golden vector pins down); any attempt to
create a Modbus endpoint over a serial line must fail with an explicit error.

## 3. Implemented function codes

Only these are implemented. Every other function code — including all write functions,
diagnostics, file record and FIFO access — is UNSPECIFIED: encoding a request for it must fail
with an explicit error, and receiving one as a server must answer exception `01`.

### FC 01 — Read Coils (S1 §6.1)

| | Layout |
|---|---|
| Request | `01` \|\| starting address (2) \|\| quantity of coils (2) |
| Response | `01` \|\| byte count (1) \|\| coil status (`ceil(quantity / 8)` bytes) |

Quantity range `1`–`2000` (`0x07D0`). In the response the LSB of the first data byte holds
the addressed coil; unused high bits of the last byte are zero-padded (S1 §6.1).

### FC 02 — Read Discrete Inputs (S1 §6.2)

Identical layout and limits to FC 01, function code `02`.

### FC 03 — Read Holding Registers (S1 §6.3)

| | Layout |
|---|---|
| Request | `03` \|\| starting address (2) \|\| quantity of registers (2) |
| Response | `03` \|\| byte count (1) = `2 × quantity` \|\| register values (2 bytes each, big-endian) |

Quantity range `1`–`125` (`0x7D`).

### FC 04 — Read Input Registers (S1 §6.4)

Identical layout and limits to FC 03, function code `04`.

## 4. Exception responses — S1 §7

An exception response replaces the PDU with `function code + 0x80` followed by one
exception code byte.

| Code | Name |
|---|---|
| `01` | ILLEGAL FUNCTION |
| `02` | ILLEGAL DATA ADDRESS |
| `03` | ILLEGAL DATA VALUE |
| `04` | SERVER DEVICE FAILURE |
| `05` | ACKNOWLEDGE |
| `06` | SERVER DEVICE BUSY |
| `08` | MEMORY PARITY ERROR |
| `0A` | GATEWAY PATH UNAVAILABLE |
| `0B` | GATEWAY TARGET DEVICE FAILED TO RESPOND |

Codes `07` and `09` are not assigned in S1 §7 and are therefore UNSPECIFIED: an unrecognised
exception code is surfaced verbatim as an unknown-exception error rather than renamed.

## 5. Golden vectors (S4 — do not modify)

| Input | Expected |
|---|---|
| CRC-16 over `11 03 00 6B 00 03` | appended bytes `76 87` (CRC value `0x8776`) |
| Complete RTU frame `11 03 00 6B 00 03 76 87` | CRC self-check = `0x0000` |
| FC 03 response PDU `03 06 02 2B 00 00 00 64` | registers `[0x022B, 0x0000, 0x0064]` |

## 6. Address-space tokens used by this project

`ChannelAddress.Space` selects the function code. These tokens are a Protocol Bridge
configuration convention, not part of S1.

| Token (aliases) | Function code | Element |
|---|---|---|
| `holding` (`holding_register`, `hr`) | FC 03 | 16-bit register |
| `input` (`input_register`, `ir`) | FC 04 | 16-bit register |
| `coil` (`coils`) | FC 01 | 1 bit |
| `discrete` (`discrete_input`, `di`) | FC 02 | 1 bit |

Any other address space on a Modbus endpoint is rejected at configuration time.
