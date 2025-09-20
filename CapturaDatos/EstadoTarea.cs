using System.Threading;
using System.Threading.Tasks;

namespace CapturaDatos
{
    public class EstadoTarea
    {
        public TareaDatalogger Datos { get; set; }
        public string Estado { get; set; } // "ejecutando", "detenida", "error"
        public Task TaskRef { get; set; }
        public CancellationTokenSource TokenSource { get; set; }
        public string UltimoError { get; set; }
        public string UltimoErrorAnterior { get; set; }
    }
}
