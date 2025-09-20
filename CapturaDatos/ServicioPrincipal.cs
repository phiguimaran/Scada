using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace CapturaDatos
{
    public class ServicioPrincipal : BackgroundService
    {
        private readonly MonitorConexionSQL monitorConexion;
        private readonly RotatingLogger logger;
        private readonly ParametrosServicio parametros;

        public ServicioPrincipal(MonitorConexionSQL monitorConexion, RotatingLogger logger, ParametrosServicio parametros)
        {
            this.monitorConexion = monitorConexion;
            this.logger = logger;
            this.parametros = parametros;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.Log("ServicioPrincipal iniciado.");
            if (!parametros.Validar(logger))
            {
                logger.Log("Configuración inválida. Deteniendo servicio.");
                return;
            }

            await monitorConexion.IniciarAsync(stoppingToken);
            logger.Log("ServicioPrincipal detenido.");
        }
    }
}
