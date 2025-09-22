using Microsoft.Data.SqlClient;
using Modbus.Device;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Balanza
{
    public class MonitorConexionSQL
    {
        private readonly ParametrosServicio parametros;
        private readonly RotatingLogger logger;
        public ConcurrentDictionary<int, EstadoTarea> tareasDict = new();

        private readonly int tipoLecturaFilter;

        public MonitorConexionSQL(ParametrosServicio parametros, RotatingLogger logger)
        {
            this.parametros = parametros;
            this.logger = logger;

            this.tipoLecturaFilter = (parametros?.TipoLecturaFilter > 0) ? parametros.TipoLecturaFilter : 1;
            this.logger?.LogInfo($"[Cfg] tipo_lectura filtrado = {this.tipoLecturaFilter}");
            this.logger?.LogInfo($"[Cfg] pesaje: zeroTol={parametros.ZeroTol}, riseMin={parametros.RiseMin}, stableMs={parametros.StableMs}, endian={parametros.Endian}");
        }

        public async Task IniciarAsync(CancellationToken token)
        {
            using var impactoCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
            var impactoTask = CicloImpactoBDAsync(impactoCTS.Token);

            try
            {
                int intentos = 0;
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        using var cn = new SqlConnection(parametros.ConnString);
                        await cn.OpenAsync(token);
                        logger.LogInfo("Conexión SQL exitosa.");
                        intentos = 0;

                        string sql = @"
SELECT D.id_datalogger, D.ip, D.cantvalores, D.id_modolectura, D.inicio, D.bits AS bitsdatalogger, D.intervalo_lectura,
       V.id_valor, V.posicion, T.id_tipovalor, T.bits AS bitsvalor
FROM dataloggers D WITH (NOLOCK)
JOIN valores V WITH (NOLOCK) ON D.id_datalogger = V.id_datalogger
JOIN tiposvalor T WITH (NOLOCK) ON V.id_tipovalor = T.id_tipovalor
WHERE D.activo = 1
  AND D.tipo_lectura = @TipoLecturaFilter";

                        var cmd = new SqlCommand(sql, cn);
                        cmd.Parameters.AddWithValue("@TipoLecturaFilter", this.tipoLecturaFilter);

                        await using var dr = await cmd.ExecuteReaderAsync(token);
                        var dict = new Dictionary<int, TareaDatalogger>();
                        while (await dr.ReadAsync(token))
                        {
                            int idDL = Convert.ToInt32(dr["id_datalogger"]);
                            if (!dict.ContainsKey(idDL))
                            {
                                var tarea = new TareaDatalogger
                                {
                                    id_datalogger = idDL,
                                    ip = dr["ip"].ToString(),
                                    cantvalores = Convert.ToInt16(dr["cantvalores"]),
                                    id_modolectura = Convert.ToInt16(dr["id_modolectura"]),
                                    inicio = Convert.ToInt16(dr["inicio"]),
                                    bitsdatalogger = Convert.ToInt16(dr["bitsdatalogger"]),
                                    intervalo_lectura = Convert.ToInt32(dr["intervalo_lectura"]),
                                    ListaValores = new List<ValorDatalogger>()
                                };
                                dict.Add(idDL, tarea);
                            }
                            dict[idDL].ListaValores.Add(new ValorDatalogger
                            {
                                id_valor = Convert.ToInt16(dr["id_valor"]),
                                posicion = Convert.ToInt16(dr["posicion"]),
                                id_tipovalor = Convert.ToInt16(dr["id_tipovalor"]),
                                bitsvalor = Convert.ToInt16(dr["bitsvalor"])
                            });
                        }

                        foreach (var kvp in dict)
                        {
                            var tarea = kvp.Value;
                            tarea.ListaValores.Sort((a, b) => a.posicion.CompareTo(b.posicion));
                            tarea.id_valor_peso =
                                tarea.ListaValores.FirstOrDefault(v => v.id_tipovalor == 7)?.id_valor
                                ?? tarea.ListaValores.FirstOrDefault(v => v.bitsvalor == 32)?.id_valor
                                ?? tarea.ListaValores.FirstOrDefault(v => v.id_tipovalor == 2)?.id_valor
                                ?? tarea.ListaValores.FirstOrDefault(v => v.id_tipovalor == 1)?.id_valor;

                            if (tarea.id_valor_peso == null)
                                logger.LogWarn($"DL {tarea.id_datalogger}: no se encontró valor 'peso'. Se imprimirá pero no se impactará BD.");
                        }

                        await CancelarYEsperarTareasAsync(clearDict: true);

                        foreach (var kvp in dict)
                        {
                            var estadoT = new EstadoTarea
                            {
                                Datos = kvp.Value,
                                Estado = "pendiente",
                                EstadoPesaje = "WaitingZero",
                                Armed = false,
                                TrackedRounded = null,
                                EstableDesdeUtc = null
                            };

                            tareasDict[kvp.Key] = estadoT;

                            estadoT.TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                            var tokenTarea = estadoT.TokenSource.Token;
                            estadoT.TaskRef = Task.Run(() => BucleTareaAsync(estadoT, tokenTarea), tokenTarea);
                            estadoT.Estado = "ejecutando";
                        }

                        while (!token.IsCancellationRequested && cn.State == System.Data.ConnectionState.Open)
                        {
                            try
                            {
                                await Task.Delay(parametros.IntervaloSupervisionTareas, token);
                            }
                            catch (TaskCanceledException)
                            {
                                logger.LogInfo("Ciclo principal cancelado (detención de servicio o apagado manual).");
                                return;
                            }
                        }
                    }
                    catch (TaskCanceledException tcex)
                    {
                        logger.LogInfo("Ciclo cancelado por token de cancelación: " + tcex.Message);
                        break;
                    }
                    catch (SqlException sqlex)
                    {
                        logger.LogError($"Error de conexión SQL: {sqlex.Message} | Código: {sqlex.Number}");
                        intentos++;
                        await CancelarYEsperarTareasAsync(clearDict: true);
                        if (parametros.MaxReintentos > 0 && intentos >= parametros.MaxReintentos)
                        {
                            logger.LogError($"No se pudo conectar tras {parametros.MaxReintentos} intentos. Deteniendo servicio.");
                            break;
                        }
                        else
                        {
                            try
                            {
                                await Task.Delay(parametros.IntervaloReintentoConexionBD, token);
                            }
                            catch (TaskCanceledException)
                            {
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError("Error inesperado en el ciclo de conexión: " + ex.Message + " | Tipo: " + ex.GetType().Name);
                        intentos++;
                        await CancelarYEsperarTareasAsync(clearDict: true);
                        if (parametros.MaxReintentos > 0 && intentos >= parametros.MaxReintentos)
                        {
                            logger.LogError($"No se pudo conectar tras {parametros.MaxReintentos} intentos. Deteniendo servicio.");
                            break;
                        }
                        else
                        {
                            try
                            {
                                await Task.Delay(parametros.IntervaloReintentoConexionBD, token);
                            }
                            catch (TaskCanceledException)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            finally
            {
                impactoCTS.Cancel();
                try
                {
                    await impactoTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    logger.LogWarn("CicloImpactoBD finalizó con error: " + ex.Message);
                }

                await CancelarYEsperarTareasAsync(clearDict: true);
            }
        }

        private async Task BucleTareaAsync(EstadoTarea estadoT, CancellationToken token)
        {
            ModbusIpMaster master = null;
            TcpClient tcpClient = null;

            try
            {
                estadoT.Estado = "ejecutando";
                int intervalo = estadoT.Datos.intervalo_lectura;
                if (intervalo < 100) intervalo = 100;

                while (!token.IsCancellationRequested && estadoT.Estado == "ejecutando")
                {
                    try
                    {
                        if (tcpClient == null || !tcpClient.Connected)
                        {
                            master?.Dispose();
                            tcpClient?.Close();
                            tcpClient?.Dispose();

                            token.ThrowIfCancellationRequested();
                            tcpClient = new TcpClient();
                            var ip = estadoT.Datos.ip;
                            const int puertoModbus = 502;
                            logger?.LogDebug($"Abriendo TCP a {ip}:{puertoModbus} con TimeoutMs={parametros.TimeoutMs}");
                            await tcpClient.ConnectAsync(ip, puertoModbus).ConfigureAwait(false);
                            tcpClient.ReceiveTimeout = parametros.TimeoutMs;
                            tcpClient.SendTimeout = parametros.TimeoutMs;
                            token.ThrowIfCancellationRequested();
                            master = ModbusIpMaster.CreateIp(tcpClient);
                        }

                        object valorCrudoLeido = LeerValorCrudo(
                            master,
                            modoLectura: estadoT.Datos.id_modolectura,
                            offset: estadoT.Datos.inicio,
                            cantidadBits: estadoT.Datos.bitsdatalogger
                        );

                        LecturaCalidadEnum calidadLeida = LecturaCalidadEnum.OK;

                        estadoT.Datos.ValorCrudoActual = valorCrudoLeido;
                        estadoT.Datos.CalidadLecturaActual = calidadLeida;

                        bool crudoCambio = false;

                        if (estadoT.Datos.ValorCrudoActual is ushort[] actual &&
                            estadoT.Datos.UltimoValorCrudoImpactado is ushort[] anterior)
                        {
                            crudoCambio = !actual.SequenceEqual(anterior);
                        }
                        else
                        {
                            crudoCambio = !Equals(estadoT.Datos.ValorCrudoActual, estadoT.Datos.UltimoValorCrudoImpactado);
                        }

                        if (crudoCambio || estadoT.Datos.CalidadLecturaActual != estadoT.Datos.CalidadLecturaImpactada)
                        {
                            if (estadoT.Datos.ValorCrudoActual is ushort[] reg)
                            {
                                foreach (var valor in estadoT.Datos.ListaValores)
                                {
                                    int baseIndex = valor.posicion - 1;
                                    if (baseIndex < 0)
                                    {
                                        valor.ValorActual = null;
                                        logger.LogWarn($"Posición inválida (id_valor={valor.id_valor}, posicion={valor.posicion}) para DL {estadoT.Datos.id_datalogger}");
                                        continue;
                                    }

                                    if (valor.id_tipovalor == 5)
                                    {
                                        int registroIdx = baseIndex;
                                        if (registroIdx < 0 || registroIdx >= reg.Length)
                                        {
                                            valor.ValorActual = null;
                                            logger.LogWarn($"Bit fuera de rango: registro={registroIdx}, len={reg.Length} (id_valor={valor.id_valor}, DL {estadoT.Datos.id_datalogger})");
                                            continue;
                                        }

                                        int bitOffset = valor.bitsvalor;
                                        if (bitOffset < 0 || bitOffset > 15)
                                        {
                                            valor.ValorActual = null;
                                            logger.LogWarn($"Offset de bit inválido (bitsvalor={valor.bitsvalor}) para id_valor={valor.id_valor} en DL {estadoT.Datos.id_datalogger}");
                                            continue;
                                        }

                                        valor.ValorActual = (reg[registroIdx] & (1 << bitOffset)) != 0;
                                        continue;
                                    }

                                    bool esFloat32 = valor.id_tipovalor == 7 || valor.bitsvalor == 32;
                                    int bitsDeclarados = valor.bitsvalor > 0 ? valor.bitsvalor : (short)(esFloat32 ? 32 : 16);
                                    int palabrasNecesarias = Math.Max(1, (bitsDeclarados + 15) / 16);
                                    if (esFloat32)
                                    {
                                        palabrasNecesarias = Math.Max(palabrasNecesarias, 2);
                                    }

                                    if (baseIndex < 0 || baseIndex + palabrasNecesarias > reg.Length)
                                    {
                                        valor.ValorActual = null;
                                        logger.LogWarn($"Posición fuera de rango: posicion={valor.posicion}, palabras={palabrasNecesarias}, len={reg.Length} (id_valor={valor.id_valor}, DL {estadoT.Datos.id_datalogger})");
                                        continue;
                                    }

                                    if (esFloat32)
                                    {
                                        ushort hi = reg[baseIndex];
                                        ushort lo = reg[baseIndex + 1];
                                        float f = ToFloat(hi, lo, parametros.Endian);
                                        valor.ValorActual = (double)f;
                                        continue;
                                    }

                                    switch (valor.id_tipovalor)
                                    {
                                        case 1:
                                            valor.ValorActual = reg[baseIndex];
                                            break;

                                        case 2:
                                            short signedValue = unchecked((short)reg[baseIndex]);
                                            valor.ValorActual = Math.Round(signedValue / 10.0, 1);
                                            break;

                                        default:
                                            valor.ValorActual = null;
                                            logger.LogWarn($"Tipo de valor no soportado: id_tipovalor={valor.id_tipovalor}, posicion={valor.posicion} (DL {estadoT.Datos.id_datalogger})");
                                            break;
                                    }
                                }
                                estadoT.Datos.PendienteDeImpacto = true;
                            }
                            else
                            {
                                logger.LogDebug($"ValorCrudoActual tipo={estadoT.Datos.ValorCrudoActual?.GetType().Name ?? "null"} NO es ushort[]");
                            }
                        }

                        estadoT.UltimoError = "";
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception exInt)
                    {
                        estadoT.UltimoError = exInt.Message;
                        estadoT.Datos.CalidadLecturaActual = LecturaCalidadEnum.ErrorComunicacion;
                        estadoT.Datos.PendienteDeImpacto = true;
                        estadoT.Estado = "error";
                        break;
                    }

                    try
                    {
                        await Task.Delay(intervalo, token).ConfigureAwait(false);
                    }
                    catch (TaskCanceledException)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                estadoT.Estado = "error";
                estadoT.UltimoError = ex.Message;
                estadoT.Datos.CalidadLecturaActual = LecturaCalidadEnum.OtroError;
                estadoT.Datos.PendienteDeImpacto = true;
            }
            finally
            {
                master?.Dispose();
                if (tcpClient != null)
                {
                    tcpClient.Close();
                    tcpClient.Dispose();
                }

                if (estadoT.Estado != "error")
                    estadoT.Estado = "detenida";
            }
        }
        private static object LeerValorCrudo(
            ModbusIpMaster master,
            short modoLectura,
            short offset,
            short cantidadBits)
        {
            ushort count = (ushort)Math.Max(1, cantidadBits / 16);
            ushort addr = (ushort)offset;

            return modoLectura switch
            {
                1 => master.ReadHoldingRegisters(1, addr, count),
                2 => master.ReadInputRegisters(1, addr, count),
                _ => master.ReadHoldingRegisters(1, addr, count),
            };
        }

        private async Task CicloImpactoBDAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var tarea in tareasDict.Values)
                    {
                        if (!tarea.Datos.PendienteDeImpacto) continue;

                        var vPeso = tarea.Datos.id_valor_peso.HasValue
                            ? tarea.Datos.ListaValores.FirstOrDefault(v => v.id_valor == tarea.Datos.id_valor_peso.Value)
                            : null;

                        if (vPeso == null)
                        {
                            tarea.Datos.PendienteDeImpacto = false;
                            continue;
                        }

                        double? peso = null;
                        if (vPeso.ValorActual is double d) peso = d;
                        else if (vPeso.ValorActual is float f) peso = f;
                        else if (vPeso.ValorActual is int i) peso = i;
                        else if (vPeso.ValorActual is ushort us) peso = us;
                        else if (vPeso.ValorActual is short s) peso = s;

                        if (peso == null)
                        {
                            tarea.Datos.PendienteDeImpacto = false;
                            continue;
                        }

                        double abs = Math.Abs(peso.Value);
                        double round2 = Math.Round(peso.Value, 2, MidpointRounding.AwayFromZero);

                        switch (tarea.EstadoPesaje)
                        {
                            case "WaitingZero":
                                tarea.TrackedRounded = null;
                                tarea.EstableDesdeUtc = null;
                                if (abs < parametros.ZeroTol) tarea.Armed = true;
                                if (tarea.Armed && peso.Value > parametros.RiseMin) tarea.EstadoPesaje = "WaitingRise";
                                break;

                            case "WaitingRise":
                                if (abs < parametros.ZeroTol) { tarea.EstadoPesaje = "WaitingZero"; tarea.Armed = true; break; }
                                if (peso.Value > parametros.RiseMin)
                                {
                                    tarea.EstadoPesaje = "Stabilizing";
                                    tarea.TrackedRounded = round2;
                                    tarea.EstableDesdeUtc = DateTime.UtcNow;
                                }
                                break;

                            case "Stabilizing":
                                if (abs < parametros.ZeroTol)
                                {
                                    tarea.EstadoPesaje = "WaitingZero"; tarea.Armed = true;
                                    tarea.TrackedRounded = null; tarea.EstableDesdeUtc = null;
                                    break;
                                }

                                if (round2 == tarea.TrackedRounded)
                                {
                                    if (tarea.EstableDesdeUtc.HasValue &&
                                        (DateTime.UtcNow - tarea.EstableDesdeUtc.Value).TotalMilliseconds >= parametros.StableMs)
                                    {
                                        string msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{tarea.Datos.id_datalogger}] PESO={tarea.TrackedRounded:0.00}";
                                        Console.WriteLine(msg);
                                        logger.LogInfo(msg);

                                        if (tarea.Datos.id_valor_peso.HasValue)
                                        {
                                            try
                                            {
                                                using var cn = new SqlConnection(parametros.ConnString);
                                                cn.Open();
                                                using var cmd = new SqlCommand(@"UPDATE valores
   SET valor_decimal = @valor,
       calidad       = 100,
       [timestamp]   = @ts
 WHERE id_valor      = @id_valor", cn);

                                                cmd.Parameters.Add(new SqlParameter("@valor", System.Data.SqlDbType.Decimal)
                                                {
                                                    Precision = 18,
                                                    Scale = 6,
                                                    Value = (object)tarea.TrackedRounded ?? DBNull.Value
                                                });
                                                cmd.Parameters.AddWithValue("@ts", DateTime.Now);
                                                cmd.Parameters.AddWithValue("@id_valor", tarea.Datos.id_valor_peso.Value);

                                                int rows = cmd.ExecuteNonQuery();
                                                logger.LogInfo($"[Impacto] DL {tarea.Datos.id_datalogger}: id_valor={tarea.Datos.id_valor_peso.Value}, valor={tarea.TrackedRounded:0.00}, filas={rows}");
                                            }
                                            catch (Exception exImp)
                                            {
                                                logger.LogError($"Impacto BD fallido para DL {tarea.Datos.id_datalogger}: {exImp.GetType().Name} - {exImp.Message}");
                                            }
                                        }

                                        tarea.EstadoPesaje = "WaitingZero";
                                        tarea.Armed = false;
                                        tarea.TrackedRounded = null;
                                        tarea.EstableDesdeUtc = null;
                                    }
                                }
                                else
                                {
                                    tarea.TrackedRounded = round2;
                                    tarea.EstableDesdeUtc = DateTime.UtcNow;
                                }
                                break;
                        }

                        tarea.Datos.PendienteDeImpacto = false;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError("Error en CicloImpactoBD: " + ex.Message);
                }

                try
                {
                    await Task.Delay(parametros.IntervaloImpactoBD, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task CancelarYEsperarTareasAsync(bool clearDict)
        {
            var snapshot = tareasDict.ToArray();
            var tareas = new List<Task>();

            foreach (var par in snapshot)
            {
                try
                {
                    par.Value.TokenSource?.Cancel();
                }
                catch
                {
                }

                if (par.Value.TaskRef != null)
                {
                    tareas.Add(par.Value.TaskRef);
                }
            }

            if (tareas.Count > 0)
            {
                var completion = Task.WhenAll(tareas);
                var finished = await Task.WhenAny(completion, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
                if (finished == completion)
                {
                    try
                    {
                        await completion.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (AggregateException agg)
                    {
                        foreach (var ex in agg.InnerExceptions)
                        {
                            if (ex is OperationCanceledException)
                            {
                                continue;
                            }

                            logger.LogWarn($"Error esperando tareas: {ex.GetType().Name} - {ex.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarn($"Error esperando tareas: {ex.GetType().Name} - {ex.Message}");
                    }
                }
                else
                {
                    _ = completion.ContinueWith(t =>
                    {
                        if (t.IsFaulted && t.Exception != null)
                        {
                            foreach (var ex in t.Exception.InnerExceptions)
                            {
                                if (ex is OperationCanceledException)
                                {
                                    continue;
                                }

                                logger.LogWarn($"Error esperando tareas tras timeout: {ex.GetType().Name} - {ex.Message}");
                            }
                        }
                    }, TaskScheduler.Default);

                    logger.LogWarn("Tiempo agotado esperando que las tareas de lectura se detengan tras la cancelación.");
                }
            }

            foreach (var par in snapshot)
            {
                par.Value.TokenSource?.Dispose();
                par.Value.TokenSource = null;
            }

            if (clearDict)
            {
                tareasDict.Clear();
            }
        }

        private static float ToFloat(ushort hi, ushort lo, string endian)
        {
            byte[] bytes = endian.ToUpper() switch
            {
                "ABCD" => new byte[] { (byte)(hi >> 8), (byte)(hi & 0xFF), (byte)(lo >> 8), (byte)(lo & 0xFF) },
                "BADC" => new byte[] { (byte)(hi & 0xFF), (byte)(hi >> 8), (byte)(lo >> 8), (byte)(lo & 0xFF) },
                "CDAB" => new byte[] { (byte)(lo >> 8), (byte)(lo & 0xFF), (byte)(hi >> 8), (byte)(hi & 0xFF) },
                _ => new byte[] { (byte)(lo & 0xFF), (byte)(lo >> 8), (byte)(hi & 0xFF), (byte)(hi >> 8) }, // DCBA
            };
            return BitConverter.ToSingle(bytes, 0);
        }
    }
}
