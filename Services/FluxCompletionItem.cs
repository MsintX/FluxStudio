namespace FluxStudio.Services;

public sealed class FluxCompletionItem
{
    public string Label { get; init; } = string.Empty;
    public string Detail { get; init; } = string.Empty;
    public string FilterText { get; init; } = string.Empty;
    public string InsertText { get; init; } = string.Empty;
    public int CaretOffset { get; init; } = -1;
    public int SelectionLength { get; init; }
}
