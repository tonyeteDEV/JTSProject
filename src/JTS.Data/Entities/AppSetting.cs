namespace JTS.Data.Entities;

public class AppSetting
{
    public string Key { get; set; } = string.Empty;
    public string? Value { get; set; }
    public bool IsEncrypted { get; set; }
}
