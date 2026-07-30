namespace Pb.Core.Modbus;

/// <summary>A frame or PDU that does not conform to spec/modbus-tcp-subset.md.</summary>
public sealed class ModbusProtocolException : Exception
{
    public ModbusProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// The server answered with a MODBUS exception response (spec §4). This is a protocol-level
/// negative acknowledgement, not a transport failure: the link is healthy and the request
/// was rejected.
/// </summary>
public sealed class ModbusExceptionResponseException : Exception
{
    public ModbusExceptionResponseException(byte function, byte exceptionCode)
        : base($"Modbus function 0x{function:X2} was rejected: {ModbusFunctions.DescribeExceptionCode(exceptionCode)} (0x{exceptionCode:X2}).")
    {
        Function = function;
        ExceptionCode = exceptionCode;
    }

    /// <summary>The function code that was rejected, with the exception flag already cleared.</summary>
    public byte Function { get; }

    /// <summary>The raw exception code, preserved even when it is unassigned.</summary>
    public byte ExceptionCode { get; }

    /// <summary>The exception code as an enum, or null when the code is not one the spec assigns.</summary>
    public ModbusExceptionCode? KnownCode => Enum.IsDefined(typeof(ModbusExceptionCode), ExceptionCode)
        ? (ModbusExceptionCode)ExceptionCode
        : null;
}
