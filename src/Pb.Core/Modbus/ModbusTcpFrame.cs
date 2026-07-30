using System.Buffers.Binary;

namespace Pb.Core.Modbus;

/// <summary>
/// MBAP header codec for MODBUS TCP, per spec/modbus-tcp-subset.md §2.1:
/// transaction id (2), protocol id (2, always 0), length (2, counts the unit id plus the
/// PDU), unit id (1). MODBUS TCP carries no checksum.
/// </summary>
public static class ModbusTcpFrame
{
    /// <summary>Size of the MBAP header in bytes.</summary>
    public const int HeaderSize = 7;

    /// <summary>Protocol identifier reserved for MODBUS.</summary>
    public const ushort ProtocolId = 0x0000;

    /// <summary>Registered system port for MODBUS TCP.</summary>
    public const int DefaultPort = 502;

    /// <summary>Largest complete TCP ADU: MBAP header plus the largest PDU.</summary>
    public const int MaxAduSize = HeaderSize + ModbusFunctions.MaxPduSize;

    /// <summary>Offset of the length field inside the header, useful when reading incrementally.</summary>
    public const int LengthFieldOffset = 4;

    /// <summary>Wraps <paramref name="pdu"/> in an MBAP header.</summary>
    public static byte[] Wrap(ushort transactionId, byte unitId, ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length is 0 or > ModbusFunctions.MaxPduSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pdu),
                pdu.Length,
                $"A PDU is 1..{ModbusFunctions.MaxPduSize} bytes (spec §1).");
        }

        byte[] adu = new byte[HeaderSize + pdu.Length];
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(0), transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(2), ProtocolId);
        BinaryPrimitives.WriteUInt16BigEndian(adu.AsSpan(4), (ushort)(pdu.Length + 1));
        adu[6] = unitId;
        pdu.CopyTo(adu.AsSpan(HeaderSize));
        return adu;
    }

    /// <summary>
    /// Reads the fields of an MBAP header and reports how many further bytes belong to this
    /// ADU, so a stream reader knows exactly how much to await.
    /// </summary>
    /// <exception cref="ModbusProtocolException">The header is too short, or its fields are out of spec.</exception>
    public static (ushort TransactionId, byte UnitId, int PduLength) ParseHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            throw new ModbusProtocolException(
                $"An MBAP header is {HeaderSize} bytes but only {header.Length} were received.");
        }

        ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(header);
        ushort protocolId = BinaryPrimitives.ReadUInt16BigEndian(header[2..]);
        ushort length = BinaryPrimitives.ReadUInt16BigEndian(header[4..]);

        if (protocolId != ProtocolId)
        {
            throw new ModbusProtocolException(
                $"MBAP protocol identifier is 0x{protocolId:X4} but MODBUS requires 0x0000 (spec §2.1).");
        }

        // Length counts the unit identifier plus the PDU, so a PDU of at least one byte
        // means a length of at least two.
        if (length < 2 || length > ModbusFunctions.MaxPduSize + 1)
        {
            throw new ModbusProtocolException(
                $"MBAP length field is {length}, outside 2..{ModbusFunctions.MaxPduSize + 1} (spec §2.1).");
        }

        return (transactionId, header[6], length - 1);
    }

    /// <summary>Splits a complete ADU into its header fields and its PDU.</summary>
    public static (ushort TransactionId, byte UnitId, ReadOnlyMemory<byte> Pdu) Unwrap(ReadOnlyMemory<byte> adu)
    {
        (ushort transactionId, byte unitId, int pduLength) = ParseHeader(adu.Span);

        if (adu.Length != HeaderSize + pduLength)
        {
            throw new ModbusProtocolException(
                $"MBAP length field announces a {pduLength}-byte PDU but the frame carries {adu.Length - HeaderSize}.");
        }

        return (transactionId, unitId, adu[HeaderSize..]);
    }
}
