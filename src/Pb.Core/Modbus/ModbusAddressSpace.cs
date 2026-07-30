using Pb.Core.Channels;

namespace Pb.Core.Modbus;

/// <summary>
/// Maps a channel's address space token onto a MODBUS function code, per
/// spec/modbus-tcp-subset.md §6, and works out how many elements one channel value spans.
/// </summary>
public static class ModbusAddressSpace
{
    /// <summary>Address space tokens a Modbus endpoint understands, for error messages.</summary>
    public static readonly string[] KnownSpaces = ["holding", "input", "coil", "discrete"];

    /// <summary>Resolves an address space token to its function code.</summary>
    public static bool TryResolve(string space, out ModbusFunction function)
    {
        switch (space)
        {
            case "holding" or "holding_register" or "holding_registers" or "hr":
                function = ModbusFunction.ReadHoldingRegisters;
                return true;
            case "input" or "input_register" or "input_registers" or "ir":
                function = ModbusFunction.ReadInputRegisters;
                return true;
            case "coil" or "coils":
                function = ModbusFunction.ReadCoils;
                return true;
            case "discrete" or "discrete_input" or "discrete_inputs" or "di":
                function = ModbusFunction.ReadDiscreteInputs;
                return true;
            default:
                function = default;
                return false;
        }
    }

    /// <summary>
    /// Validates that <paramref name="channel"/> can be read from a Modbus endpoint and
    /// reports the function code and element count one read needs.
    /// </summary>
    /// <param name="channel">Channel to plan a read for.</param>
    /// <param name="function">Function code to use.</param>
    /// <param name="elements">Registers or bits one channel value spans.</param>
    /// <param name="error">Why the channel cannot be read, when the method returns false.</param>
    public static bool TryPlanRead(
        ChannelSpec channel,
        out ModbusFunction function,
        out int elements,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(channel);

        elements = 0;

        if (!TryResolve(channel.Address.Space, out function))
        {
            error = $"address space '{channel.Address.Space}' is not a Modbus space; use one of {string.Join(", ", KnownSpaces)}.";
            return false;
        }

        if (function.IsBitFunction())
        {
            if (channel.Type != DataType.Bool)
            {
                error = $"a '{channel.Address.Space}' channel reads single bits, so its type must be bool, not {channel.Type}.";
                return false;
            }

            elements = 1;
        }
        else
        {
            // A register holds 16 bits, so a value occupies half as many registers as bytes;
            // a bool packed into a register still costs one whole register.
            elements = Math.Max(1, channel.SizeInBytes / 2);
        }

        int max = function.MaxElementsPerRead();

        if (elements > max)
        {
            error = $"reading {channel.Type} needs {elements} element(s) but {function} allows {max} per request.";
            return false;
        }

        if (channel.Address.Index + elements > 0x1_0000)
        {
            error = $"reading {elements} element(s) from 0x{channel.Address.Index:X4} runs past the end of the Modbus address space.";
            return false;
        }

        error = null;
        return true;
    }
}
