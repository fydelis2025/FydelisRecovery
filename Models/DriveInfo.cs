namespace FydelisRecovery.Models;

public class DiskDriveInfo
{
    public int Index { get; set; }
    public string Model { get; set; } = string.Empty;
    public long Size { get; set; }
    public string SizeFormatted => Size switch
    {
        < 1024L * 1024 * 1024 => $"{Size / (1024 * 1024):N0} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):N2} GB"
    };
}