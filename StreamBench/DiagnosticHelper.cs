// DiagnosticHelper.cs
// Centralized exception logging with source location (file, line, member).
// Writes to both TraceLog (file) and stderr for immediate visibility.

using System.Runtime.CompilerServices;

namespace StreamBench;

public static class DiagnosticHelper
{
    /// <summary>
    /// Logs an exception with source location to TraceLog and stderr.
    /// Returns a formatted string suitable for console display.
    /// </summary>
    public static string LogException(
        Exception ex,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string memberName = "")
    {
        string fileName = Path.GetFileName(filePath);
        string message = ex.Message;

        TraceLog.DiagnosticError(message, fileName, lineNumber, memberName);

        string diagnostic = $"[{fileName}:{lineNumber}] {memberName}: {message}";
        Console.Error.WriteLine(diagnostic);

        if (ex.InnerException is not null)
        {
            string inner = $"  Inner: {ex.InnerException.Message}";
            Console.Error.WriteLine(inner);
        }

        return diagnostic;
    }

    /// <summary>
    /// Logs an error message (without exception) with source location.
    /// </summary>
    public static string LogError(
        string message,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string memberName = "")
    {
        string fileName = Path.GetFileName(filePath);
        TraceLog.DiagnosticError(message, fileName, lineNumber, memberName);

        string diagnostic = $"[{fileName}:{lineNumber}] {memberName}: {message}";
        Console.Error.WriteLine(diagnostic);
        return diagnostic;
    }

    /// <summary>
    /// Logs a warning message with source location.
    /// </summary>
    public static string LogWarning(
        string message,
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0,
        [CallerMemberName] string memberName = "")
    {
        string fileName = Path.GetFileName(filePath);
        TraceLog.DiagnosticWarning(message, fileName, lineNumber, memberName);

        string diagnostic = $"[{fileName}:{lineNumber}] {memberName}: {message}";
        Console.Error.WriteLine(diagnostic);
        return diagnostic;
    }
}
