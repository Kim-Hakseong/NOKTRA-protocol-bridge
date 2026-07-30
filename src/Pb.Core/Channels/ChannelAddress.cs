using System.Diagnostics.CodeAnalysis;

namespace Pb.Core.Channels;

/// <summary>
/// Endpoint-relative location of a channel, written as <c>space:index</c>
/// (for example <c>holding:107</c>, <c>coil:0</c>, <c>offset:4</c>).
/// The address space token is interpreted by the owning endpoint; the core only
/// guarantees it is a non-empty lowercase token and that the index is non-negative.
/// </summary>
/// <param name="Space">Address space token, always lowercase.</param>
/// <param name="Index">Zero-based position inside the address space.</param>
public readonly record struct ChannelAddress(string Space, int Index)
{
    /// <summary>Separator between address space and index in the textual form.</summary>
    public const char Separator = ':';

    /// <summary>
    /// Parses the textual form. A bare index is rejected on purpose: the address
    /// space must be explicit so that configuration errors surface at load time
    /// instead of being guessed at run time.
    /// </summary>
    /// <exception cref="FormatException">The text is not a valid <c>space:index</c> address.</exception>
    public static ChannelAddress Parse(string text)
    {
        if (!TryParse(text, out ChannelAddress address, out string? error))
        {
            throw new FormatException(error);
        }

        return address;
    }

    /// <summary>Non-throwing counterpart of <see cref="Parse"/>, reporting why parsing failed.</summary>
    public static bool TryParse(
        [NotNullWhen(true)] string? text,
        out ChannelAddress address,
        [NotNullWhen(false)] out string? error)
    {
        address = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Channel address is empty; expected 'space:index'.";
            return false;
        }

        string trimmed = text.Trim();
        int separator = trimmed.IndexOf(Separator);
        if (separator <= 0 || separator == trimmed.Length - 1)
        {
            error = $"Channel address '{trimmed}' is not of the form 'space:index'.";
            return false;
        }

        string space = trimmed[..separator].Trim();
        string indexText = trimmed[(separator + 1)..].Trim();

        if (space.Length == 0 || !space.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
        {
            error = $"Channel address space '{space}' must be a non-empty token of letters, digits, '_' or '-'.";
            return false;
        }

        if (!int.TryParse(indexText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
        {
            error = $"Channel address index '{indexText}' is not a non-negative decimal integer.";
            return false;
        }

        address = new ChannelAddress(space.ToLowerInvariant(), index);
        error = null;
        return true;
    }

    public override string ToString() => $"{Space}{Separator}{Index.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
}
