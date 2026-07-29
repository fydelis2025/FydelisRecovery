using FydelisRecovery.Models;

namespace FydelisRecovery.Models;

public class PartitionInfo
{
    public int DiskIndex { get; set; }
    public int PartitionIndex { get; set; }
    public string DeviceId { get; set; } = "";
    public string DriveLetter { get; set; } = "";
    public string Label { get; set; } = "";
    public string FileSystem { get; set; } = "";
    public string Type { get; set; } = "";
    public long SizeBytes { get; set; }
    public long OffsetBytes { get; set; }
    public long FreeBytes { get; set; }
    public bool Bootable { get; set; }
    public string SizeDisplay => DiskInfo.FormatSize(SizeBytes);
    public string FreeDisplay => DiskInfo.FormatSize(FreeBytes);

    public string AccessPath =>
        !string.IsNullOrEmpty(DriveLetter)
            ? $@"\\.\{DriveLetter.TrimEnd(':')}:"
            : DeviceId;

    public override string ToString()
    {
        var letter = string.IsNullOrEmpty(DriveLetter) ? "sem letra" : DriveLetter;
        return $"#{PartitionIndex} {letter} {Label} {FileSystem} {SizeDisplay}";
    }
}