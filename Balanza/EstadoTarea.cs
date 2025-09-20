using System.Threading;
using System.Threading.Tasks;

namespace Balanza
{
    public class EstadoTarea
    {
        public TareaDatalogger Datos { get; set; }
        public string Estado { get; set; } // "ejecutando", "detenida", "error"
        public Task TaskRef { get; set; }
        public CancellationTokenSource TokenSource { get; set; }
        public string UltimoError { get; set; }
        public string UltimoErrorAnterior { get; set; }

        // ---- NUEVO: estado de pesaje (máquina de estados por datalogger) ----
        // "WaitingZero" | "WaitingRise" | "Stabilizing"
        public string EstadoPesaje { get; set; } = "WaitingZero";
        public bool Armed { get; set; } = false;
        public double? TrackedRounded { get; set; } = null;
        public System.DateTime? EstableDesdeUtc { get; set; } = null;
    }
}
