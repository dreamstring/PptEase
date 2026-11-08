namespace PptEase.Models;

public sealed class ShapeItem
{
    public int Id { get; set; }
    public int SlideId { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public bool Locked { get; set; }
    public bool Visible { get; set; }
    public bool Selected { get; set; }
    public string Key { get; set; } = "";
}
public sealed class HostSnapshot
{
    public bool Connected { get; set; }
    public string HostVersion { get; set; } = "";
    public string PresentationName { get; set; } = "";
    public int SlideIndex { get; set; }
    public bool IntegrationLoaded { get; set; }
    public List<ShapeItem> Shapes { get; set; } = [];
    public string Status { get; set; } = "";
    public object? LastRightClick { get; set; }
    public string HostAssemblyVersion { get; set; } = "";
    public string HostBuild { get; set; } = "";
    public string ColorMode { get; set; } = "";
    public object? CurrentColor { get; set; }
    public object? LastColorOperation { get; set; }
}
