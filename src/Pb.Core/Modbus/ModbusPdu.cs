using System.Buffers.Binary;

namespace Pb.Core.Modbus;

/// <summary>
/// Encoder and decoder for the MODBUS Protocol Data Units recorded in
/// spec/modbus-tcp-subset.md §3. Nothing outside that list is encodable: an unsupported
/// function is an explicit error rather than a best guess.
/// </summary>
public static class ModbusPdu
{
    /// <summary>Size of a read request PDU: function code, start address, quantity.</summary>
    public const int ReadRequestSize = 5;

    /// <summary>
    /// Writes a read request for <paramref name="function"/> into <paramref name="destination"/>.
    /// </summary>
    /// <param name="function">One of the implemented read functions.</param>
    /// <param name="startAddress">Zero-based wire address, 0x0000–0xFFFF (spec §1).</param>
    /// <param name="quantity">Element count; limits are per function (spec §3).</param>
    /// <param name="destination">Buffer of at least <see cref="ReadRequestSize"/> bytes.</param>
    /// <returns>Number of bytes written.</returns>
    public static int WriteReadRequest(ModbusFunction function, int startAddress, int quantity, Span<byte> destination)
    {
        if (!ModbusFunctions.IsSupported((byte)function))
        {
            throw new ArgumentOutOfRangeException(
                nameof(function),
                function,
                "Only the read functions recorded in spec/modbus-tcp-subset.md §3 can be encoded.");
        }

        ValidateRange(function, startAddress, quantity);

        if (destination.Length < ReadRequestSize)
        {
            throw new ArgumentException($"Need {ReadRequestSize} bytes for a read request.", nameof(destination));
        }

        destination[0] = (byte)function;
        BinaryPrimitives.WriteUInt16BigEndian(destination[1..], (ushort)startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(destination[3..], (ushort)quantity);
        return ReadRequestSize;
    }

    /// <summary>Allocates and returns a read request PDU.</summary>
    public static byte[] BuildReadRequest(ModbusFunction function, int startAddress, int quantity)
    {
        byte[] pdu = new byte[ReadRequestSize];
        WriteReadRequest(function, startAddress, quantity, pdu);
        return pdu;
    }

    /// <summary>Parses a read request PDU received as a server.</summary>
    /// <exception cref="ModbusProtocolException">The PDU is malformed.</exception>
    public static (ModbusFunction Function, int StartAddress, int Quantity) ParseReadRequest(ReadOnlySpan<byte> pdu)
    {
        if (pdu.Length != ReadRequestSize)
        {
            throw new ModbusProtocolException(
                $"A read request PDU is {ReadRequestSize} bytes but {pdu.Length} were received.");
        }

        if (!ModbusFunctions.IsSupported(pdu[0]))
        {
            throw new ModbusProtocolException($"Function code 0x{pdu[0]:X2} is not implemented.");
        }

        ModbusFunction function = (ModbusFunction)pdu[0];
        int start = BinaryPrimitives.ReadUInt16BigEndian(pdu[1..]);
        int quantity = BinaryPrimitives.ReadUInt16BigEndian(pdu[3..]);
        return (function, start, quantity);
    }

    /// <summary>
    /// Builds a register read response: function code, byte count, then two big-endian bytes
    /// per register (spec §3, FC 03/04).
    /// </summary>
    public static byte[] BuildRegisterResponse(ModbusFunction function, ReadOnlySpan<ushort> registers)
    {
        if (!function.IsRegisterFunction())
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Not a register read function.");
        }

        if (registers.Length is 0 or > ModbusFunctions.MaxRegistersPerRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registers),
                registers.Length,
                $"A register response carries 1..{ModbusFunctions.MaxRegistersPerRead} registers.");
        }

        byte[] pdu = new byte[2 + (registers.Length * 2)];
        pdu[0] = (byte)function;
        pdu[1] = (byte)(registers.Length * 2);

        for (int i = 0; i < registers.Length; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(2 + (i * 2)), registers[i]);
        }

        return pdu;
    }

    /// <summary>
    /// Builds a bit read response: function code, byte count, then packed bits with the
    /// addressed element in the LSB of the first data byte (spec §3, FC 01/02).
    /// </summary>
    public static byte[] BuildBitResponse(ModbusFunction function, ReadOnlySpan<bool> bits)
    {
        if (!function.IsBitFunction())
        {
            throw new ArgumentOutOfRangeException(nameof(function), function, "Not a bit read function.");
        }

        if (bits.Length is 0 or > ModbusFunctions.MaxBitsPerRead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bits),
                bits.Length,
                $"A bit response carries 1..{ModbusFunctions.MaxBitsPerRead} bits.");
        }

        int byteCount = BitByteCount(bits.Length);
        byte[] pdu = new byte[2 + byteCount];
        pdu[0] = (byte)function;
        pdu[1] = (byte)byteCount;

        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
            {
                pdu[2 + (i / 8)] |= (byte)(1 << (i % 8));
            }
        }

        return pdu;
    }

    /// <summary>Builds an exception response: function code with the exception flag, then the code (spec §4).</summary>
    public static byte[] BuildExceptionResponse(byte function, ModbusExceptionCode code) =>
        [(byte)(function | ModbusFunctions.ExceptionFlag), (byte)code];

    /// <summary>
    /// Validates a response PDU against the request it answers and returns its data payload
    /// (everything after the byte count).
    /// </summary>
    /// <exception cref="ModbusExceptionResponseException">The server rejected the request.</exception>
    /// <exception cref="ModbusProtocolException">The response is malformed or answers a different function.</exception>
    public static ReadOnlySpan<byte> ParseReadResponse(ReadOnlySpan<byte> pdu, ModbusFunction expected, int quantity)
    {
        if (pdu.Length < 2)
        {
            throw new ModbusProtocolException($"A response PDU needs at least 2 bytes but {pdu.Length} were received.");
        }

        byte function = pdu[0];

        if ((function & ModbusFunctions.ExceptionFlag) != 0)
        {
            byte bare = (byte)(function & ~ModbusFunctions.ExceptionFlag);

            if (bare != (byte)expected)
            {
                throw new ModbusProtocolException(
                    $"Received an exception response for function 0x{bare:X2} while function 0x{(byte)expected:X2} was requested.");
            }

            throw new ModbusExceptionResponseException(bare, pdu[1]);
        }

        if (function != (byte)expected)
        {
            throw new ModbusProtocolException(
                $"Response function code is 0x{function:X2} but 0x{(byte)expected:X2} was requested.");
        }

        int expectedByteCount = ExpectedDataByteCount(expected, quantity);
        int declared = pdu[1];

        if (declared != expectedByteCount)
        {
            throw new ModbusProtocolException(
                $"Response declares {declared} data byte(s) but {expectedByteCount} were expected for {quantity} element(s).");
        }

        if (pdu.Length != 2 + declared)
        {
            throw new ModbusProtocolException(
                $"Response declares {declared} data byte(s) but carries {pdu.Length - 2}.");
        }

        return pdu[2..];
    }

    /// <summary>Decodes the register values of a validated FC 03/04 response payload.</summary>
    public static ushort[] DecodeRegisters(ReadOnlySpan<byte> data)
    {
        if (data.Length % 2 != 0)
        {
            throw new ModbusProtocolException($"A register payload must have an even length but is {data.Length}.");
        }

        ushort[] registers = new ushort[data.Length / 2];

        for (int i = 0; i < registers.Length; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(i * 2)..]);
        }

        return registers;
    }

    /// <summary>Decodes <paramref name="quantity"/> bits from a validated FC 01/02 response payload.</summary>
    public static bool[] DecodeBits(ReadOnlySpan<byte> data, int quantity)
    {
        if (data.Length < BitByteCount(quantity))
        {
            throw new ModbusProtocolException(
                $"A payload of {data.Length} byte(s) cannot hold {quantity} bit(s).");
        }

        bool[] bits = new bool[quantity];

        for (int i = 0; i < quantity; i++)
        {
            bits[i] = (data[i / 8] & (1 << (i % 8))) != 0;
        }

        return bits;
    }

    /// <summary>Number of data bytes a response to this request must carry.</summary>
    public static int ExpectedDataByteCount(ModbusFunction function, int quantity) =>
        function.IsRegisterFunction() ? quantity * 2 : BitByteCount(quantity);

    /// <summary>Bytes needed to pack <paramref name="bits"/> bits, eight per byte.</summary>
    public static int BitByteCount(int bits) => (bits + 7) / 8;

    private static void ValidateRange(ModbusFunction function, int startAddress, int quantity)
    {
        if (startAddress is < 0 or > 0xFFFF)
        {
            throw new ArgumentOutOfRangeException(
                nameof(startAddress),
                startAddress,
                "A Modbus data address is 0x0000..0xFFFF (spec §1).");
        }

        int max = function.MaxElementsPerRead();

        if (quantity < 1 || quantity > max)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                quantity,
                $"{function} reads 1..{max} elements per request (spec §3).");
        }

        if (startAddress + quantity > 0x1_0000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(quantity),
                quantity,
                $"Reading {quantity} element(s) from 0x{startAddress:X4} runs past the end of the address space.");
        }
    }
}
