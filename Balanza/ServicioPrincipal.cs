using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;

namespace Balanza
{
    public class ServicioPrincipal : BackgroundService
    {
        private readonly MonitorConexionSQL _monitor;

        public ServicioPrincipal(MonitorConexionSQL monitor)
        {
            _monitor = monitor;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await _monitor.IniciarAsync(stoppingToken);
        }
    }
}
