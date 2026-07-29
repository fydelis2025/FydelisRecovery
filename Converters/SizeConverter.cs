using System.Globalization;
using System.Windows.Data;
using FydelisRecovery.Models;

namespace FydelisRecovery.Converters;

/// <summary>
/// Converte long (bytes) → string legível (KB/MB/GB/TB).
/// Uso: {Binding SizeBytes, Converter={StaticResource SizeConverter}}
/// ConverterParameter opcional: "0" = só número + unidade; "hex" não se aplica.
/// </summary>
[ValueConversion(typeof(long), typeof(string))]
[ValueConversion(typeof(ulong), typeof(string))]
[ValueConversion(typeof(int), typeof(string))]
public class SizeConverter : IValueConverter
{
    public static readonly SizeConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        long bytes = value switch
        {
            long l => l,
            ulong ul => ul > long.MaxValue ? long.MaxValue : (long)ul,
            int i => i,
            uint ui => ui,
            short s => s,
            double d => (long)d,
            float f => (long)f,
            string str when long.TryParse(str, NumberStyles.Integer, culture, out var p) => p,
            _ => -1
        };

        if (bytes < 0)
            return "—";

        // parameter: "precise" | null
        bool precise = parameter is string sparam &&
                       sparam.Equals("precise", StringComparison.OrdinalIgnoreCase);

        return Format(bytes, precise);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
            return Binding.DoNothing;

        text = text.Trim();

        // aceita "1.5 GB", "1024MB", "2048"
        var parts = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            // pode ser "1.5GB" colado
            if (TryParseJoined(parts[0], culture, out long b1))
                return b1;
            if (long.TryParse(parts[0], NumberStyles.Integer, culture, out long raw))
                return raw;
            return Binding.DoNothing;
        }

        if (!double.TryParse(parts[0], NumberStyles.Float, culture, out double amount))
            return Binding.DoNothing;

        string unit = parts[1].Trim().ToUpperInvariant();
        return unit switch
        {
            "B" => (long)amount,
            "KB" or "KIB" => (long)(amount * 1024),
            "MB" or "MIB" => (long)(amount * 1024 * 1024),
            "GB" or "GIB" => (long)(amount * 1024 * 1024 * 1024),
            "TB" or "TIB" => (long)(amount * 1024L * 1024 * 1024 * 1024),
            _ => Binding.DoNothing
        };
    }

    public static string Format(long bytes, bool precise = false)
    {
        if (bytes < 0) return "—";

        // usa o mesmo helper do model, se existir
        try
        {
            if (!precise)
                return  DiskInfo.FormatSize(bytes);
        }
        catch
        {
            // fallback abaixo
        }

        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1)
        {
            size /= 1024;
            u++;
        }

        string fmt = precise ? (u == 0 ? "0" : "0.###") : (u == 0 ? "0" : "0.##");
        return string.Format(CultureInfo.CurrentCulture, "{0:" + fmt + "} {1}", size, units[u]);
    }

    private static bool TryParseJoined(string text, CultureInfo culture, out long bytes)
    {
        bytes = 0;
        text = text.Trim();
        // separa número e sufixo: 1.5GB / 512MiB / 1024
        int i = 0;
        while (i < text.Length && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == ',' || text[i] == '-'))
            i++;

        if (i == 0) return false;
        if (!double.TryParse(text[..i], NumberStyles.Float, culture, out double amount))
            return false;

        string unit = text[i..].Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(unit))
        {
            bytes = (long)amount;
            return true;
        }

        bytes = unit switch
        {
            "B" => (long)amount,
            "K" or "KB" or "KIB" => (long)(amount * 1024),
            "M" or "MB" or "MIB" => (long)(amount * 1024 * 1024),
            "G" or "GB" or "GIB" => (long)(amount * 1024 * 1024 * 1024),
            "T" or "TB" or "TIB" => (long)(amount * 1024L * 1024 * 1024 * 1024),
            _ => -1
        };
        return bytes >= 0;
    }
}