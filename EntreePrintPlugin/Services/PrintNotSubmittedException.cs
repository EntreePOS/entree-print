namespace EntreePrintPlugin.Services;

// Use only when the backend can prove no document or command bytes were handed off.
public sealed class PrintNotSubmittedException(string message) : Exception(message);
