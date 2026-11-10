namespace PhotoShelver.App.ViewModels;

/// <param name="Pattern">Template text, e.g. "{YYYY}/{MM}-{DD}".</param>
/// <param name="Example">Rendered folder path for today, e.g. "2026\11-08".</param>
public sealed record TemplatePreset(string Pattern, string Example)
{
    // Editable ComboBox puts ToString() into the text box when an item is picked.
    public override string ToString() => Pattern;
}
