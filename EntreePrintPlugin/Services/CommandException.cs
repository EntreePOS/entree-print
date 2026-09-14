namespace EntreePrintPlugin.Services;

public sealed class CommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
