using System.IO;
using System.Reflection;

namespace CapturaDatos
{
    public class RotatingLogger
    {
        private readonly string logPath;
        private readonly long maxSize;

        public RotatingLogger(ParametrosServicio parametros)
        {
            string exePath = Assembly.GetExecutingAssembly().Location;
            string exeDir = Path.GetDirectoryName(exePath)!;
            string exeName = Path.GetFileNameWithoutExtension(exePath)!;
            logPath = Path.Combine(exeDir, exeName + ".log");
            maxSize = parametros.MaxLogMB * 1024 * 1024;
        }

        public void Log(string msg)
        {
            lock (this)
            {
                var logFile = new FileInfo(logPath);
                if (logFile.Exists && logFile.Length > maxSize)
                {
                    var backupName = logPath + "." + DateTime.Now.ToString("yyyyMMddHHmmss");
                    File.Move(logPath, backupName);
                }
                File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {msg}{Environment.NewLine}");
                //File.AppendAllText(logPath, $"{DateTime.Now:s} | {msg}{Environment.NewLine}");
            }
        }
    }
}
