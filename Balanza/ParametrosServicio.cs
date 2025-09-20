using Microsoft.Extensions.Configuration;

namespace Balanza
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
        public int TipoLecturaFilter { get; set; } = 1;

        // NUEVO: umbrales/endian desde raíz (para lógica de pesaje y float32)
        public double ZeroTol { get; set; } = 0.05;
        public double RiseMin { get; set; } = 0.10;
        public int StableMs { get; set; } = 2000;
        public string Endian { get; set; } = "DCBA";

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
            TipoLecturaFilter = section.GetValue<int>("TipoLecturaFilter", 1);

            // raíz
            ZeroTol = configuration.GetValue<double?>("zeroTol") ?? ZeroTol;
            RiseMin = configuration.GetValue<double?>("riseMin") ?? RiseMin;
            StableMs = configuration.GetValue<int?>("stableMs") ?? StableMs;
            Endian = configuration.GetValue<string>("endian") ?? Endian;
        }

        public bool Validar(RotatingLogger logger)
        {
            if (string.IsNullOrEmpty(ConnString))
            {
                logger.Log("Cadena de conexión no especificada.");
                return false;
            }
            if (MaxReintentos < 0 || IntervaloReintentoConexionBD <= 0 || IntervaloSupervisionTareas <= 0 || MaxLogMB <= 0)
            {
                logger.Log("Parámetros numéricos inválidos.");
                return false;
            }
            return true;
        }
    }
}
