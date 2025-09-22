using System.Collections.Generic;

namespace Balanza
{
    public class TareaDatalogger
    {
        public int id_datalogger { get; set; }
        public string ip { get; set; }
        public short cantvalores { get; set; }
        public short id_modolectura { get; set; }
        public short inicio { get; set; }
        public short bitsdatalogger { get; set; }
        public int intervalo_lectura { get; set; }
        public byte Unit { get; set; } = 1;

        // NUEVAS PROPIEDADES para la lógica centralizada de cambios
        public object ValorCrudoActual { get; set; }
        public object UltimoValorCrudoImpactado { get; set; }
        public LecturaCalidadEnum CalidadLecturaActual { get; set; }
        public LecturaCalidadEnum CalidadLecturaImpactada { get; set; }
        public bool PendienteDeImpacto { get; set; }

        // NUEVO: id_valor que consideramos "peso" (para imprimir/impactar)
        public int? id_valor_peso { get; set; }

        public List<ValorDatalogger> ListaValores { get; set; } = new List<ValorDatalogger>();
    }

    public class ValorDatalogger
    {
        public short id_valor { get; set; }
        public short posicion { get; set; }
        public short id_tipovalor { get; set; }
        public short bitsvalor { get; set; }

        public object ValorActual { get; set; } // Último valor parseado (actual en memoria)
    }
}
