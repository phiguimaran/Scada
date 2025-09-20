using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Balanza
{
    public class ServicioPrincipal : BackgroundService
    {
        private readonly MonitorConexionSQL monitor;
        private readonly RotatingLogger logger;
        private readonly ParametrosServicio parametros;

        public ServicioPrincipal(MonitorConexionSQL monitor, RotatingLogger logger, ParametrosServicio parametros)
        {
            this.monitor = monitor;
            this.logger = logger;
            this.parametros = parametros;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInfo("ServicioPrincipal iniciado.");
            if (!parametros.Validar(logger))
            {
                logger.LogError("Configuración inválida. Deteniendo servicio.");
                return;
            }

            await monitor.IniciarAsync(stoppingToken);
            logger.LogInfo("ServicioPrincipal detenido.");
        }
    }
}
