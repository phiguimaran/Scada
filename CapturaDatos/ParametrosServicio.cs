using Microsoft.Extensions.Configuration;

namespace CapturaDatos
{
    public class ParametrosServicio
    {
        public string ConnString { get; set; }
        public int MaxReintentos { get; set; }
        public int IntervaloReintentoConexionBD { get; set; }
        public int IntervaloSupervisionTareas { get; set; }
        public int IntervaloImpactoBD { get; set; }
        public int MaxLogMB { get; set; }
        public string NivelLog { get; set; }

        public ParametrosServicio(IConfiguration configuration)
        {
            var section = configuration.GetSection("ParametrosServicio");
            ConnString = section.GetValue<string>("ConexionSQL");
            MaxReintentos = section.GetValue<int>("MaxReintentosConexion");
            IntervaloReintentoConexionBD = section.GetValue<int>("IntervaloReintentoConexionBD");
            IntervaloSupervisionTareas = section.GetValue<int>("IntervaloSupervisionTareas");
            IntervaloImpactoBD = section.GetValue<int>("IntervaloImpactoBD");
            MaxLogMB = section.GetValue<int>("MaxLogSizeMB");
            NivelLog = section.GetValue<string>("NivelLog");
        }

        public bool Validar(RotatingLogger logger)
        {
            if (string.IsNullOrEmpty(ConnString))
            {
                logger.LogError("Cadena de conexión no especificada.");
                return false;
            }
            if (MaxReintentos < 0 || IntervaloReintentoConexionBD <= 0 || IntervaloSupervisionTareas <= 0 || MaxLogMB <= 0)
            {
                logger.LogError("Parámetros numéricos inválidos.");
                return false;
            }
            if (!RotatingLogger.TryParseVerbosity(NivelLog, out _))
            {
                logger.LogError($"NivelLog '{NivelLog}' no es válido. Valores permitidos: '{RotatingLogger.VerbosityDebugName}' o '{RotatingLogger.VerbosityRuntimeName}'.");
                return false;
            }
            return true;
        }
    }
}
