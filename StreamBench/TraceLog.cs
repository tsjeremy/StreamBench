// TraceLog.cs
// Simple file-based diagnostic logger for StreamBench.
// Writes structured log lines to StreamBench_trace_<timestamp>.log
// in the same directory as the running executable.
//
// Log format (one line per event, easy for humans and AI tools to parse):
//   [2026-03-03T15:08:56.1234567Z] [INFO ] App started. Args: --cpu
//   [2026-03-03T15:08:57.2345678Z] [WARN ] Backend not found. Tried: stream_cpu_win_x64.exe
//   [2026-03-03T15:08:58.3456789Z] [ERROR] Benchmark error. Type: CPU, Error: ...

using System.Text;

namespace StreamBench;

/// <summary>
/// Static file-based trace logger. All events are appended to a single
/// timestamped log file with level, category, and structured message.
/// </summary>
public static class TraceLog
{
    private static readonly StreamWriter? Writer;
    public static readonly string LogPath;

    static TraceLog()
    {
        try
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppContext.BaseDirectory
                ?? ".";

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            LogPath = Path.Combine(dir, $"StreamBench_trace_{timestamp}.log");

            Writer = new StreamWriter(LogPath, append: false, Encoding.UTF8) { AutoFlush = true };
            Writer.WriteLine($"# StreamBench Trace — {DateTime.Now:O}");
            Writer.WriteLine($"# Process: {Environment.ProcessPath}");
            Writer.WriteLine($"# Machine: {Environment.MachineName}");
            Writer.WriteLine($"# OS: {Environment.OSVersion}");
            Writer.WriteLine($"# CLR: {Environment.Version}");
            Writer.WriteLine();
        }
        catch
        {
            Writer = null;
            LogPath = "(log unavailable)";
        }
    }

    // ── Core write ────────────────────────────────────────────────────────

    private static void Write(string level, string message)
    {
        try { Writer?.WriteLine($"[{DateTime.UtcNow:O}] [{level,-5}] {message}"); }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    public static void Flush()
    {
        try { Writer?.Flush(); } catch { }
    }

    // ── Application lifecycle ─────────────────────────────────────────────

    public static void AppStarted(string args)
        => Info($"App started. Args: {args}");

    public static void AppExiting(int exitCode)
        => Info($"App exiting. ExitCode: {exitCode}");

    public static void UnhandledException(string message, string filePath, int lineNumber, string memberName)
        => Error($"Unhandled exception at {filePath}:{lineNumber} in {memberName}: {message}");

    // ── Benchmark operations ──────────────────────────────────────────────

    public static void BenchmarkStarted(string type, string exePath, long arraySize)
        => Info($"Benchmark started. Type: {type}, Exe: {exePath}, ArraySize: {arraySize}");

    public static void BenchmarkCompleted(string type, long durationMs)
        => Info($"Benchmark completed. Type: {type}, Duration: {durationMs}ms");

    public static void BenchmarkError(string type, string error, string filePath, int lineNumber)
        => Error($"Benchmark error. Type: {type}, Error: {error} at {filePath}:{lineNumber}");

    public static void BackendProcessStarted(string exePath, int pid)
        => Info($"Backend process started. Exe: {exePath}, PID: {pid}");

    public static void BackendProcessExitedWithError(int exitCode, string exePath)
        => Warn($"Backend process exited with error. ExitCode: {exitCode}, Exe: {exePath}");

    public static void JsonParseFailed(string error, string jsonPreview)
        => Error($"JSON parse failed. Error: {error}, Preview: {jsonPreview}");

    // ── Embedded backend extraction ───────────────────────────────────────

    public static void BackendExtracting(string fileName)
        => Info($"Extracting embedded backend: {fileName}");

    public static void BackendExtracted(string targetPath)
        => Info($"Backend extracted to: {targetPath}");

    public static void BackendNotFound(string triedNames)
        => Warn($"Embedded backend not found. Tried: {triedNames}");

    public static void BackendCacheHit(string targetPath)
        => Info($"Backend already cached: {targetPath}");

    // ── AI benchmark operations ───────────────────────────────────────────

    public static void AiServiceStarting()
        => Info("AI service starting");

    public static void AiServiceStarted()
        => Info("AI service started");

    public static void AiServiceStartFailed(string error, string filePath, int lineNumber)
        => Error($"AI service start failed. Error: {error} at {filePath}:{lineNumber}");

    public static void AiServiceStopping()
        => Info("AI service stopping");

    public static void AiServiceStopped()
        => Info("AI service stopped");

    public static void AiCliFound(string name)
        => Info($"AI CLI found: {name}");

    public static void AiCliNotFound(string triedNames)
        => Warn($"AI CLI not found. Tried: {triedNames}");

    public static void AiModelLoading(string modelId, string device)
        => Info($"AI model loading. Model: {modelId}, Device: {device}");

    public static void AiModelLoaded(string modelId, long durationMs)
        => Info($"AI model loaded. Model: {modelId}, Duration: {durationMs}ms");

    public static void AiModelLoadFailed(string modelId, string error, string filePath, int lineNumber)
        => Error($"AI model load failed. Model: {modelId}, Error: {error} at {filePath}:{lineNumber}");

    public static void AiModelSelected(string modelId, string alias, string device, string reason)
        => Info($"AI model selected. Model: {modelId}, Alias: {alias}, Device: {device}, Reason: {reason}");

    public static void AiModelDownloadStarted(string modelId, double sizeMb)
        => Info($"AI model download started. Model: {modelId}, Size: {sizeMb:F0} MB");

    public static void AiModelDownloadCompleted(string modelId, long durationMs)
        => Info($"AI model download completed. Model: {modelId}, Duration: {durationMs}ms");

    public static void AiModelDownloadFailed(string modelId, string error)
        => Error($"AI model download failed. Model: {modelId}, Error: {error}");

    public static void AiModelDownloadSkipped(string modelId, string device)
        => Info($"AI model download skipped (no-download mode). Model: {modelId}, Device: {device}");

    public static void AiInferenceStarted(string question, string device)
        => Info($"AI inference started. Question: {question}, Device: {device}");

    public static void AiInferenceCompleted(long durationMs, int tokens)
        => Info($"AI inference completed. Duration: {durationMs}ms, Tokens: {tokens}");

    public static void AiInferenceFailed(string error, string filePath, int lineNumber)
        => Error($"AI inference failed. Error: {error} at {filePath}:{lineNumber}");

    public static void AiCatalogUnavailable(string error)
        => Warn($"AI catalog unavailable. Error: {error}");

    public static void AiCatalogLoaded(int modelCount, long durationMs)
        => Info($"AI catalog loaded. Models: {modelCount}, Duration: {durationMs}ms");

    public static void AiModelUnloaded(string modelId)
        => Info($"AI model unloaded. Model: {modelId}");

    public static void AiProcessTimeout(string command, int timeoutMs)
        => Warn($"AI process timeout. Command: {command}, Timeout: {timeoutMs}ms");

    public static void AiSharedModelAttempt(string alias, int devicesCovered, int devicesTotal)
        => Info($"AI shared model attempt. Alias: {alias}, Coverage: {devicesCovered}/{devicesTotal}");

    public static void AiPassStarted(string passName, int deviceCount)
        => Info($"AI pass started. Pass: {passName}, Devices: {deviceCount}");

    public static void AiPassCompleted(string passName, int successCount, int deviceCount)
        => Info($"AI pass completed. Pass: {passName}, Success: {successCount}/{deviceCount}");

    public static void AiBenchmarkDeviceStarted(string device, string modelId)
        => Info($"AI benchmark device started. Device: {device}, Model: {modelId}");

    public static void AiBenchmarkDeviceCompleted(string device, string modelId, long durationMs)
        => Info($"AI benchmark device completed. Device: {device}, Model: {modelId}, Duration: {durationMs}ms");

    public static void AiRelationDatasetLoaded(int memFiles, int aiFiles, int memSamples, int aiSamples)
        => Info($"AI relation dataset loaded. MemFiles: {memFiles}, AiFiles: {aiFiles}, MemSamples: {memSamples}, AiSamples: {aiSamples}");

    public static void AiRelationAutoEnabled(string reason)
        => Info($"AI relation summary auto-enabled. Reason: {reason}");

    public static void AiRelationSkipped(string reason)
        => Info($"AI relation summary skipped. Reason: {reason}");

    // ── System info detection ─────────────────────────────────────────────

    public static void SystemInfoDetectionStarted()
        => Info("System info detection started");

    public static void SystemInfoDetectionCompleted()
        => Info("System info detection completed");

    public static void SystemInfoDetectionWarning(string component, string error)
        => Warn($"System info detection warning. Component: {component}, Error: {error}");

    // ── File operations ───────────────────────────────────────────────────

    public static void FileSaved(string filePath)
        => Info($"File saved: {filePath}");

    public static void FileSaveFailed(string filePath, string error)
        => Warn($"File save failed. Path: {filePath}, Error: {error}");

    // ── General diagnostic ────────────────────────────────────────────────

    public static void DiagnosticError(string message, string filePath, int lineNumber, string memberName)
        => Error($"{message} at {filePath}:{lineNumber} in {memberName}");

    public static void DiagnosticWarning(string message, string filePath, int lineNumber, string memberName)
        => Warn($"{message} at {filePath}:{lineNumber} in {memberName}");

    public static void DiagnosticInfo(string message)
        => Info(message);
}
