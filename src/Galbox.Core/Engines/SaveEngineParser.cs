namespace Galbox.Core.Engines;

/// <summary>
/// Base class for save engine parsers.
/// </summary>
public abstract class SaveEngineParser
{
    public abstract string EngineName { get; }
    public abstract bool CanParse(string savePath);
    public abstract SaveData Parse(string savePath);
}

public class SaveData
{
    public string? SceneLabel { get; set; }
    public string? SaveName { get; set; }
    public int ChapterProgress { get; set; }
    public int CgUnlockCount { get; set; }
    public int TotalCgCount { get; set; }
    public DateTime? SaveDate { get; set; }
}