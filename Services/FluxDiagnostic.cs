namespace FluxStudio.Services;

public sealed class FluxDiagnostic
{
    public string Severity { get; set; } = "error";
    public string Code { get; set; } = "FS0000";
    public int Line { get; set; } = 1;
    public int Column { get; set; } = 1;
    public string Message { get; set; } = string.Empty;
}
