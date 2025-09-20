namespace Balanza
{
    public enum LecturaCalidadEnum
    {
        OK = 0,
        Timeout = 1,
        Desconexion = 2,
        ErrorComunicacion = 3,
        ChecksumError = 4,
        DatoInvalido = 5,
        SinRespuesta = 6,
        OtroError = 99
    }
}
