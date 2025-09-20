Scada - CapturaDatos - .NET 8 - Documentación rápida

- Abrir la solución/proyecto en Visual Studio 2022/2024.
- Configurar appsettings.json según tu entorno y cadena de conexión.
- Verifica manualmente que los valores configurados sean correctos; el servicio asume que la información es válida.
- Instala los paquetes NuGet: Microsoft.Extensions.Hosting.WindowsServices y Microsoft.Extensions.Logging.
- El ciclo principal y la lógica son 100% C#, usando GenericHost.
- El código implementa tareas independientes para cada datalogger y un ciclo de impacto periódico a la base de datos.
- El archivo MonitorConexionSQL.cs incluye comentarios en los lugares donde debes poner tu lógica de lectura real de dispositivos.
