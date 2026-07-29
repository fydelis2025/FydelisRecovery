// Models/DiskInfo.cs
using FydelisRecovery.Models;

namespace FydelisRecovery.Models;

public class DiskInfo
{
    public int Index { get; set; }
    public string DevicePath => $@"\\.\PhysicalDrive{Index}";
    public string Model { get; set; } = "";
    public string InterfaceType { get; set; } = "";
    public long SizeBytes { get; set; }
    public string SizeDisplay => FormatSize(SizeBytes);
    public string SerialNumber { get; set; } = "";
    public List<PartitionInfo> Partitions { get; set; } = new();

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.##} {units[u]}";
    }

    public override string ToString() =>
        $"Disk {Index}: {Model} ({SizeDisplay}) [{InterfaceType}]";
}