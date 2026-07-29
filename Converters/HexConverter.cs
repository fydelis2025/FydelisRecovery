using System.Globalization;
using System.Windows.Data;

namespace FydelisRecovery.Converters;

/// <summary>
/// long/int → hex (0x...). Parameter = quantidade mínima de dígitos (ex: "8").
/// </summary>
[ValueConversion(typeof(long), typeof(string))]
public class HexConverter : IValueConverter
{
    public static readonly HexConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "—";

        long n = value switch
        {
            long l => l,
            int i => i,
            uint ui => ui,
            ulong ul => unchecked((long)ul),
            _ => 0
        };

        int digits = 0;
        if (parameter is string ps && int.TryParse(ps, out int d))
            digits = d;

        return digits > 0
            ? $"0x{n.ToString("X" + digits)}"
            : $"0x{n:X}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string s) return Binding.DoNothing;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];

        if (long.TryParse(s, NumberStyles.HexNumber, culture, out long result))
            return result;

        return Binding.DoNothing;
    }
}