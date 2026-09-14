namespace EntreePrintPlugin.Services;

public sealed class CommandException(string code, string message, Exception? innerException = null) : Exception(message, innerException)
{
    public string Code { get; } = code;
}
