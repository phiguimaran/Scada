using System.Collections.Generic;

namespace CapturaDatos
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

        // NUEVAS PROPIEDADES para la lógica centralizada de cambios
        public object ValorCrudoActual { get; set; }
        public object UltimoValorCrudoImpactado { get; set; }
        public LecturaCalidadEnum CalidadLecturaActual { get; set; }
        public LecturaCalidadEnum CalidadLecturaImpactada { get; set; }
        public bool PendienteDeImpacto { get; set; }

        public List<ValorDatalogger> ListaValores { get; set; } = new List<ValorDatalogger>();
    }

    public class ValorDatalogger
    {
        public short id_valor { get; set; }
        public short posicion { get; set; }
        public short id_tipovalor { get; set; }
        public short bitsvalor { get; set; }

        public object ValorActual { get; set; } // Último valor parseado (actual en memoria)
        // Se eliminaron las propiedades de “valor anterior” y “pendiente de impacto”
    }
}
