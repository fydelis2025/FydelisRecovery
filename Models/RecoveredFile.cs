using FydelisRecovery.Models;

namespace FydelisRecovery.Models;

public class RecoveredFile
{
    public string Name { get; set; } = "";
    public string Extension { get; set; } = "";
    public long SizeBytes { get; set; }
    public long OffsetBytes { get; set; }
    public string Signature { get; set; } = "";
    public bool IsDeleted { get; set; }
    public string Status { get; set; } = "Encontrado";
    public string SizeDisplay => DiskInfo.FormatSize(SizeBytes);
    public byte[]? Preview { get; set; }

    public override string ToString() =>
        $"{Name} ({SizeDisplay}) @ 0x{OffsetBytes:X} [{Status}]";
}