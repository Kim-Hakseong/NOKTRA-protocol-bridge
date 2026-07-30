namespace Pb.Core.Modbus;

/// <summary>
/// The function codes this project implements. Only read functions are recorded in
/// spec/modbus-tcp-subset.md §3, so only read functions exist here; anything else must be
/// rejected rather than guessed at.
/// </summary>
public enum ModbusFunction : byte
{
    /// <summary>Read Coils — 1 bit per element (spec §3, FC 01).</summary>
    ReadCoils = 0x01,

    /// <summary>Read Discrete Inputs — 1 bit per element (spec §3, FC 02).</summary>
    ReadDiscreteInputs = 0x02,

    /// <summary>Read Holding Registers — 16 bits per element (spec §3, FC 03).</summary>
    ReadHoldingRegisters = 0x03,

    /// <summary>Read Input Registers — 16 bits per element (spec §3, FC 04).</summary>
    ReadInputRegisters = 0x04,
}

/// <summary>Exception codes assigned by the MODBUS specification (spec §4).</summary>
public enum ModbusExceptionCode : byte
{
    IllegalFunction = 0x01,
    IllegalDataAddress = 0x02,
    IllegalDataValue = 0x03,
    ServerDeviceFailure = 0x04,
    Acknowledge = 0x05,
    ServerDeviceBusy = 0x06,
    MemoryParityError = 0x08,
    GatewayPathUnavailable = 0x0A,
    GatewayTargetDeviceFailedToRespond = 0x0B,
}

/// <summary>Helpers over the function-code enums.</summary>
public static class ModbusFunctions
{
    /// <summary>Bit set on a function code to mark an exception response (spec §4).</summary>
    public const byte ExceptionFlag = 0x80;

    /// <summary>Maximum PDU size in bytes (spec §1).</summary>
    public const int MaxPduSize = 253;

    /// <summary>Largest register count one read request may ask for (spec §3, FC 03/04).</summary>
    public const int MaxRegistersPerRead = 125;

    /// <summary>Largest bit count one read request may ask for (spec §3, FC 01/02).</summary>
    public const int MaxBitsPerRead = 2000;

    /// <summary>True when the function reads 16-bit registers rather than single bits.</summary>
    public static bool IsRegisterFunction(this ModbusFunction function) =>
        function is ModbusFunction.ReadHoldingRegisters or ModbusFunction.ReadInputRegisters;

    /// <summary>True when the function reads single bits rather than 16-bit registers.</summary>
    public static bool IsBitFunction(this ModbusFunction function) =>
        function is ModbusFunction.ReadCoils or ModbusFunction.ReadDiscreteInputs;

    /// <summary>True when <paramref name="code"/> is one of the implemented read functions.</summary>
    public static bool IsSupported(byte code) =>
        code is (byte)ModbusFunction.ReadCoils
            or (byte)ModbusFunction.ReadDiscreteInputs
            or (byte)ModbusFunction.ReadHoldingRegisters
            or (byte)ModbusFunction.ReadInputRegisters;

    /// <summary>Largest element count a single request of <paramref name="function"/> may ask for.</summary>
    public static int MaxElementsPerRead(this ModbusFunction function) =>
        function.IsRegisterFunction() ? MaxRegistersPerRead : MaxBitsPerRead;

    /// <summary>Names an exception code, keeping unassigned codes visible as such (spec §4).</summary>
    public static string DescribeExceptionCode(byte code) => code switch
    {
        (byte)ModbusExceptionCode.IllegalFunction => "illegal function",
        (byte)ModbusExceptionCode.IllegalDataAddress => "illegal data address",
        (byte)ModbusExceptionCode.IllegalDataValue => "illegal data value",
        (byte)ModbusExceptionCode.ServerDeviceFailure => "server device failure",
        (byte)ModbusExceptionCode.Acknowledge => "acknowledge",
        (byte)ModbusExceptionCode.ServerDeviceBusy => "server device busy",
        (byte)ModbusExceptionCode.MemoryParityError => "memory parity error",
        (byte)ModbusExceptionCode.GatewayPathUnavailable => "gateway path unavailable",
        (byte)ModbusExceptionCode.GatewayTargetDeviceFailedToRespond => "gateway target device failed to respond",
        _ => $"unassigned exception code 0x{code:X2}",
    };
}
