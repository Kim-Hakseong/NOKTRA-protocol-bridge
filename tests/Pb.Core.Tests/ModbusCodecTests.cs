using Pb.Core.Modbus;
using Xunit;

namespace Pb.Core.Tests;

/// <summary>
/// Codec tests, including the pinned Modbus golden vectors, recorded with
/// their source in spec/modbus-tcp-subset.md §5.
/// </summary>
public sealed class ModbusCodecTests
{
    [Fact]
    public void Crc16_GoldenVector_AppendsSevenSixEightSeven()
    {
        byte[] request = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x03];
        byte[] crc = new byte[2];

        ModbusCrc16.Append(request, crc);

        Assert.Equal([0x76, 0x87], crc);
        Assert.Equal(0x8776, ModbusCrc16.Compute(request));
    }

    [Fact]
    public void Crc16_OverAFrameIncludingItsOwnCrc_IsZero()
    {
        byte[] frame = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x03, 0x76, 0x87];

        Assert.Equal(0, ModbusCrc16.Compute(frame));
        Assert.True(ModbusCrc16.Check(frame));
    }

    [Fact]
    public void Crc16_DetectsASingleFlippedBit()
    {
        byte[] frame = [0x11, 0x03, 0x00, 0x6B, 0x00, 0x03, 0x76, 0x87];
        frame[3] ^= 0x01;

        Assert.False(ModbusCrc16.Check(frame));
    }

    [Fact]
    public void Crc16_OfNothingIsTheSeed()
    {
        Assert.Equal(ModbusCrc16.Seed, ModbusCrc16.Compute([]));
    }

    [Fact]
    public void Crc16_TooSmallDestination_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
        {
            byte[] destination = new byte[1];
            ModbusCrc16.Append([0x01], destination);
        });
    }

    [Fact]
    public void RtuFrame_GoldenVector_RoundTrips()
    {
        byte[] pdu = [0x03, 0x00, 0x6B, 0x00, 0x03];

        byte[] frame = ModbusRtuFrame.Wrap(0x11, pdu);

        Assert.Equal([0x11, 0x03, 0x00, 0x6B, 0x00, 0x03, 0x76, 0x87], frame);

        (byte address, ReadOnlyMemory<byte> decoded) = ModbusRtuFrame.Unwrap(frame);

        Assert.Equal(0x11, address);
        Assert.Equal(pdu, decoded.ToArray());
    }

    [Fact]
    public void RtuFrame_CorruptedFrame_IsRejected()
    {
        byte[] frame = ModbusRtuFrame.Wrap(0x11, [0x03, 0x00, 0x6B, 0x00, 0x03]);
        frame[2] ^= 0xFF;

        Assert.Throws<ModbusProtocolException>(() => ModbusRtuFrame.Unwrap(frame));
    }

    [Fact]
    public void RtuFrame_TooShortFrame_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusRtuFrame.Unwrap(new byte[] { 0x11, 0x03, 0x00 }));
    }

    [Fact]
    public void RtuFrame_OversizedFrame_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusRtuFrame.Unwrap(new byte[ModbusRtuFrame.MaxAduSize + 1]));
    }

    [Fact]
    public void RtuFrame_EmptyPdu_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusRtuFrame.Wrap(0x11, []));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(247, true)]
    [InlineData(248, false)]
    [InlineData(255, false)]
    public void RtuFrame_ClassifiesSlaveAddresses(int address, bool unicast)
    {
        Assert.Equal(unicast, ModbusRtuFrame.IsUnicastAddress((byte)address));
    }

    [Fact]
    public void ReadResponse_GoldenVector_DecodesThreeRegisters()
    {
        byte[] responsePdu = [0x03, 0x06, 0x02, 0x2B, 0x00, 0x00, 0x00, 0x64];

        ushort[] registers = DecodeRegisterResponse(responsePdu, ModbusFunction.ReadHoldingRegisters, 3);

        Assert.Equal<ushort[]>([0x022B, 0x0000, 0x0064], registers);
    }

    [Fact]
    public void ReadRequest_BuildsFunctionStartAndQuantity()
    {
        byte[] pdu = ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, 0x006B, 3);

        Assert.Equal([0x03, 0x00, 0x6B, 0x00, 0x03], pdu);
    }

    [Fact]
    public void ReadRequest_RoundTripsThroughTheServerParser()
    {
        byte[] pdu = ModbusPdu.BuildReadRequest(ModbusFunction.ReadInputRegisters, 1234, 7);

        (ModbusFunction function, int start, int quantity) = ModbusPdu.ParseReadRequest(pdu);

        Assert.Equal(ModbusFunction.ReadInputRegisters, function);
        Assert.Equal(1234, start);
        Assert.Equal(7, quantity);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x06)]
    [InlineData(0x10)]
    [InlineData(0x17)]
    public void ReadRequest_UnimplementedFunctionCode_CannotBeEncoded(byte code)
    {
        Assert.False(ModbusFunctions.IsSupported(code));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildReadRequest((ModbusFunction)code, 0, 1));
    }

    [Theory]
    [InlineData(ModbusFunction.ReadHoldingRegisters, 0)]
    [InlineData(ModbusFunction.ReadHoldingRegisters, 126)]
    [InlineData(ModbusFunction.ReadInputRegisters, 126)]
    [InlineData(ModbusFunction.ReadCoils, 0)]
    [InlineData(ModbusFunction.ReadCoils, 2001)]
    [InlineData(ModbusFunction.ReadDiscreteInputs, 2001)]
    public void ReadRequest_QuantityOutsideTheSpecLimits_IsRejected(ModbusFunction function, int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusPdu.BuildReadRequest(function, 0, quantity));
    }

    [Theory]
    [InlineData(ModbusFunction.ReadHoldingRegisters, 125)]
    [InlineData(ModbusFunction.ReadCoils, 2000)]
    public void ReadRequest_QuantityAtTheSpecLimit_IsAccepted(ModbusFunction function, int quantity)
    {
        Assert.Equal(ModbusPdu.ReadRequestSize, ModbusPdu.BuildReadRequest(function, 0, quantity).Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0x1_0000)]
    public void ReadRequest_AddressOutsideTheWireRange_IsRejected(int address)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, address, 1));
    }

    [Fact]
    public void ReadRequest_RunningPastTheEndOfTheAddressSpace_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, 0xFFFF, 2));

        Assert.Equal(
            ModbusPdu.ReadRequestSize,
            ModbusPdu.BuildReadRequest(ModbusFunction.ReadHoldingRegisters, 0xFFFF, 1).Length);
    }

    [Fact]
    public void ParseReadRequest_WrongLength_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusPdu.ParseReadRequest([0x03, 0x00]));
    }

    [Fact]
    public void ParseReadRequest_UnimplementedFunction_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusPdu.ParseReadRequest([0x10, 0x00, 0x00, 0x00, 0x01]));
    }

    [Fact]
    public void BitResponse_PacksTheFirstElementInTheLeastSignificantBit()
    {
        bool[] bits = [true, false, false, false, false, false, false, false, true];

        byte[] pdu = ModbusPdu.BuildBitResponse(ModbusFunction.ReadCoils, bits);

        Assert.Equal([0x01, 0x02, 0x01, 0x01], pdu);
    }

    [Fact]
    public void BitResponse_RoundTripsAnArbitraryPattern()
    {
        bool[] bits = Enumerable.Range(0, 19).Select(i => i % 3 == 0).ToArray();

        byte[] pdu = ModbusPdu.BuildBitResponse(ModbusFunction.ReadDiscreteInputs, bits);
        bool[] decoded = ModbusPdu.DecodeBits(
            ModbusPdu.ParseReadResponse(pdu, ModbusFunction.ReadDiscreteInputs, bits.Length),
            bits.Length);

        Assert.Equal(bits, decoded);
        Assert.Equal(3, pdu[1]);
    }

    [Fact]
    public void RegisterResponse_RoundTrips()
    {
        ushort[] registers = [0x0001, 0xFFFF, 0x8000, 0x1234];

        byte[] pdu = ModbusPdu.BuildRegisterResponse(ModbusFunction.ReadHoldingRegisters, registers);

        Assert.Equal(8, pdu[1]);
        Assert.Equal(registers, DecodeRegisterResponse(pdu, ModbusFunction.ReadHoldingRegisters, registers.Length));
    }

    [Fact]
    public void ResponseBuilders_RejectTheWrongFunctionKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildRegisterResponse(ModbusFunction.ReadCoils, new ushort[] { 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildBitResponse(ModbusFunction.ReadHoldingRegisters, new bool[] { true }));
    }

    [Fact]
    public void ResponseBuilders_RejectEmptyAndOversizedPayloads()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildRegisterResponse(ModbusFunction.ReadHoldingRegisters, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildRegisterResponse(ModbusFunction.ReadHoldingRegisters, new ushort[126]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildBitResponse(ModbusFunction.ReadCoils, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusPdu.BuildBitResponse(ModbusFunction.ReadCoils, new bool[2001]));
    }

    [Fact]
    public void ExceptionResponse_SetsTheHighBitAndCarriesTheCode()
    {
        byte[] pdu = ModbusPdu.BuildExceptionResponse(0x03, ModbusExceptionCode.IllegalDataAddress);

        Assert.Equal([0x83, 0x02], pdu);
    }

    [Fact]
    public void ParseReadResponse_ExceptionResponse_ThrowsWithTheDecodedCode()
    {
        byte[] pdu = ModbusPdu.BuildExceptionResponse(0x03, ModbusExceptionCode.ServerDeviceBusy);

        ModbusExceptionResponseException ex = Assert.Throws<ModbusExceptionResponseException>(() =>
            ModbusPdu.ParseReadResponse(pdu, ModbusFunction.ReadHoldingRegisters, 1));

        Assert.Equal(0x03, ex.Function);
        Assert.Equal(ModbusExceptionCode.ServerDeviceBusy, ex.KnownCode);
        Assert.Contains("server device busy", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReadResponse_UnassignedExceptionCode_IsSurfacedVerbatim()
    {
        byte[] pdu = [0x83, 0x09];

        ModbusExceptionResponseException ex = Assert.Throws<ModbusExceptionResponseException>(() =>
            ModbusPdu.ParseReadResponse(pdu, ModbusFunction.ReadHoldingRegisters, 1));

        Assert.Equal(0x09, ex.ExceptionCode);
        Assert.Null(ex.KnownCode);
        Assert.Contains("unassigned", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReadResponse_ExceptionForADifferentFunction_IsAProtocolError()
    {
        byte[] pdu = [0x84, 0x02];

        Assert.Throws<ModbusProtocolException>(() =>
            ModbusPdu.ParseReadResponse(pdu, ModbusFunction.ReadHoldingRegisters, 1));
    }

    [Theory]
    [InlineData(new byte[] { 0x04, 0x02, 0x00, 0x01 }, "function code")]
    [InlineData(new byte[] { 0x03 }, "at least 2 bytes")]
    [InlineData(new byte[] { 0x03, 0x04, 0x00, 0x01 }, "data byte")]
    [InlineData(new byte[] { 0x03, 0x02, 0x00 }, "data byte")]
    [InlineData(new byte[] { 0x03, 0x02, 0x00, 0x01, 0x02 }, "data byte")]
    public void ParseReadResponse_MalformedResponse_IsRejected(byte[] pdu, string fragment)
    {
        ModbusProtocolException ex = Assert.Throws<ModbusProtocolException>(() =>
            ModbusPdu.ParseReadResponse(pdu, ModbusFunction.ReadHoldingRegisters, 1));

        Assert.Contains(fragment, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeRegisters_OddLengthPayload_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusPdu.DecodeRegisters([0x00, 0x01, 0x02]));
    }

    [Fact]
    public void DecodeBits_PayloadTooSmall_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusPdu.DecodeBits([0xFF], 9));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(8, 1)]
    [InlineData(9, 2)]
    [InlineData(2000, 250)]
    public void BitByteCount_RoundsUpToWholeBytes(int bits, int bytes)
    {
        Assert.Equal(bytes, ModbusPdu.BitByteCount(bits));
    }

    [Fact]
    public void TcpFrame_WrapsPduWithAnMbapHeader()
    {
        byte[] pdu = [0x03, 0x00, 0x6B, 0x00, 0x03];

        byte[] adu = ModbusTcpFrame.Wrap(0x1234, 0x11, pdu);

        // Transaction 0x1234, protocol 0x0000, length 6 (unit id + 5 PDU bytes), unit 0x11.
        Assert.Equal([0x12, 0x34, 0x00, 0x00, 0x00, 0x06, 0x11, 0x03, 0x00, 0x6B, 0x00, 0x03], adu);
    }

    [Fact]
    public void TcpFrame_UnwrapRecoversTheHeaderFieldsAndPdu()
    {
        byte[] pdu = [0x03, 0x06, 0x02, 0x2B, 0x00, 0x00, 0x00, 0x64];

        (ushort transactionId, byte unitId, ReadOnlyMemory<byte> decoded) =
            ModbusTcpFrame.Unwrap(ModbusTcpFrame.Wrap(0xFFFF, 0x01, pdu));

        Assert.Equal(0xFFFF, transactionId);
        Assert.Equal(1, unitId);
        Assert.Equal(pdu, decoded.ToArray());
    }

    [Fact]
    public void TcpFrame_ParseHeaderReportsHowManyMoreBytesToRead()
    {
        byte[] adu = ModbusTcpFrame.Wrap(7, 1, new byte[] { 0x03, 0x02, 0x00, 0x01 });

        (ushort transactionId, byte unitId, int pduLength) = ModbusTcpFrame.ParseHeader(adu);

        Assert.Equal(7, transactionId);
        Assert.Equal(1, unitId);
        Assert.Equal(4, pduLength);
    }

    [Fact]
    public void TcpFrame_NonZeroProtocolIdentifier_IsRejected()
    {
        byte[] adu = [0x00, 0x01, 0x00, 0x01, 0x00, 0x02, 0x01, 0x03];

        ModbusProtocolException ex = Assert.Throws<ModbusProtocolException>(() => ModbusTcpFrame.ParseHeader(adu));

        Assert.Contains("protocol identifier", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    public void TcpFrame_LengthFieldOutsideTheSpecRange_IsRejected(int length)
    {
        byte[] adu = [0x00, 0x01, 0x00, 0x00, (byte)(length >> 8), (byte)(length & 0xFF), 0x01, 0x03];

        Assert.Throws<ModbusProtocolException>(() => ModbusTcpFrame.ParseHeader(adu));
    }

    [Fact]
    public void TcpFrame_ShortHeader_IsRejected()
    {
        Assert.Throws<ModbusProtocolException>(() => ModbusTcpFrame.ParseHeader(new byte[6]));
    }

    [Fact]
    public void TcpFrame_LengthDisagreeingWithTheFrameSize_IsRejected()
    {
        byte[] adu = ModbusTcpFrame.Wrap(1, 1, new byte[] { 0x03, 0x02, 0x00, 0x01 });

        Assert.Throws<ModbusProtocolException>(() => ModbusTcpFrame.Unwrap(adu.AsMemory(0, adu.Length - 1)));
    }

    [Fact]
    public void TcpFrame_RejectsEmptyAndOversizedPdus()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModbusTcpFrame.Wrap(1, 1, []));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ModbusTcpFrame.Wrap(1, 1, new byte[ModbusFunctions.MaxPduSize + 1]));
    }

    [Fact]
    public void TcpFrame_MaximumSizedPduIsAccepted()
    {
        byte[] adu = ModbusTcpFrame.Wrap(1, 1, new byte[ModbusFunctions.MaxPduSize]);

        Assert.Equal(ModbusTcpFrame.MaxAduSize, adu.Length);
    }

    private static ushort[] DecodeRegisterResponse(byte[] pdu, ModbusFunction function, int quantity) =>
        ModbusPdu.DecodeRegisters(ModbusPdu.ParseReadResponse(pdu, function, quantity));
}
