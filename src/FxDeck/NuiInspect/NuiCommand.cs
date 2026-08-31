namespace FxDeck.NuiInspect;

/// <summary>
/// One command extracted from the chat NUI (design memo §3.10). <see cref="Name"/> is the console
/// name (leading <c>/</c> stripped); empty help/params are normalised to <c>null</c> so the cache stays lean.
/// </summary>
public sealed class NuiCommand
{
    public string Name { get; set; } = string.Empty;

    public string? Help { get; set; }

    public List<NuiCommandParam>? Params { get; set; }
}

public sealed class NuiCommandParam
{
    public string Name { get; set; } = string.Empty;

    public string? Help { get; set; }

    public string? Type { get; set; }

    public bool? Optional { get; set; }
}
