using System;
using System.IO;
using System.Reflection;

namespace CapturaDatos
{
    public class RotatingLogger
    {
        private readonly string logPath;
        private readonly long maxSize;
        private readonly object syncRoot = new();
        private readonly LogVerbosity verbosity;

        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warn = 2,
            Error = 3
        }

        public enum LogVerbosity
        {
            Debug,
            Runtime
        }

        public const string VerbosityDebugName = "debug";
        public const string VerbosityRuntimeName = "runtime";

        public RotatingLogger(ParametrosServicio parametros)
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string exeDir = Path.GetDirectoryName(exePath)!;
            string exeName = Path.GetFileNameWithoutExtension(exePath)!;
            logPath = Path.Combine(exeDir, exeName + ".log");
            maxSize = parametros.MaxLogMB * 1024 * 1024;
            verbosity = TryParseVerbosity(parametros.NivelLog, out var parsed)
                ? parsed
                : LogVerbosity.Runtime;
        }

        public void Log(LogLevel level, string msg)
        {
            if (!ShouldLog(level))
            {
                return;
            }

            lock (syncRoot)
            {
                var logFile = new FileInfo(logPath);
                if (logFile.Exists && logFile.Length > maxSize)
                {
                    var backupName = logPath + "." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(logPath, backupName);
                }
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {msg}{Environment.NewLine}");
            }
        }

        public void LogDebug(string msg) => Log(LogLevel.Debug, msg);

        public void LogInfo(string msg) => Log(LogLevel.Info, msg);

        public void LogWarn(string msg) => Log(LogLevel.Warn, msg);

        public void LogError(string msg) => Log(LogLevel.Error, msg);

        public static bool TryParseVerbosity(string? value, out LogVerbosity verbosity)
        {
            if (string.Equals(value, VerbosityDebugName, StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LogVerbosity.Debug;
                return true;
            }

            if (string.Equals(value, VerbosityRuntimeName, StringComparison.OrdinalIgnoreCase))
            {
                verbosity = LogVerbosity.Runtime;
                return true;
            }

            verbosity = LogVerbosity.Runtime;
            return false;
        }

        private bool ShouldLog(LogLevel level)
        {
            return verbosity switch
            {
                LogVerbosity.Debug => true,
                LogVerbosity.Runtime => level >= LogLevel.Warn,
                _ => true
            };
        }
    }
}
