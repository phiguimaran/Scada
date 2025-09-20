using Microsoft.Data.SqlClient;
using Modbus.Device;
using Modbus.Extensions.Enron;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
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
            this.logger?.Log($"[Cfg] tipo_lectura filtrado = {this.tipoLecturaFilter}");
            this.logger?.Log($"[Cfg] pesaje: zeroTol={parametros.ZeroTol}, riseMin={parametros.RiseMin}, stableMs={parametros.StableMs}, endian={parametros.Endian}");
        }

        public async Task IniciarAsync(CancellationToken token)
        {
            var impactoCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
            var impactoTask = Task.Run(() => CicloImpactoBD(impactoCTS.Token), impactoCTS.Token);

            int intentos = 0;
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var cn = new SqlConnection(parametros.ConnString);
                    await cn.OpenAsync(token);
                    logger.Log("Conexión SQL exitosa.");
                    intentos = 0;

                    // Poblar tareasDict desde la base de datos
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

                    var dr = await cmd.ExecuteReaderAsync(token);
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
                    dr.Close();

                    // Selección del id_valor_peso por datalogger:
                    foreach (var kvp in dict)
                    {
                        var tarea = kvp.Value;
                        // Prioridad: bitsvalor==32 (float32) -> id_tipovalor==2 (decimal) -> id_tipovalor==1 (entero)
                        tarea.id_valor_peso =
                            tarea.ListaValores.FirstOrDefault(v => v.bitsvalor == 32)?.id_valor
                            ?? tarea.ListaValores.FirstOrDefault(v => v.id_tipovalor == 2)?.id_valor
                            ?? tarea.ListaValores.FirstOrDefault(v => v.id_tipovalor == 1)?.id_valor;

                        if (tarea.id_valor_peso == null)
                            logger.Log($"[WARN] DL {tarea.id_datalogger}: no se encontró valor 'peso'. Se imprimirá pero no se impactará BD.");
                    }

                    tareasDict.Clear();
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
                        tareasDict.TryAdd(kvp.Key, estadoT);
                    }

                    // Lanzar tareas de lectura
                    foreach (var kvp in tareasDict)
                    {
                        var estadoT = kvp.Value;
                        if (estadoT.TaskRef == null || estadoT.TaskRef.IsCompleted)
                        {
                            estadoT.TokenSource = CancellationTokenSource.CreateLinkedTokenSource(token);
                            var tokenTarea = estadoT.TokenSource.Token;
                            estadoT.TaskRef = Task.Run(() => BucleTarea(estadoT, tokenTarea), tokenTarea);
                            estadoT.Estado = "ejecutando";
                        }
                    }

                    while (!token.IsCancellationRequested && cn.State == System.Data.ConnectionState.Open)
                    {
                        try
                        {
                            await Task.Delay(parametros.IntervaloSupervisionTareas, token);
                        }
                        catch (TaskCanceledException)
                        {
                            logger.Log("Ciclo principal cancelado (detención de servicio o apagado manual).");
                            return;
                        }
                    }
                }
                catch (TaskCanceledException tcex)
                {
                    logger.Log("Ciclo cancelado por token de cancelación: " + tcex.Message);
                    break;
                }
                catch (SqlException sqlex)
                {
                    logger.Log($"Error de conexión SQL: {sqlex.Message} | Código: {sqlex.Number}");
                    intentos++;
                    foreach (var par in tareasDict)
                    {
                        try { par.Value.TokenSource?.Cancel(); } catch { }
                        par.Value.Estado = "detenida";
                    }
                    if (parametros.MaxReintentos > 0 && intentos >= parametros.MaxReintentos)
                    {
                        logger.Log($"No se pudo conectar tras {parametros.MaxReintentos} intentos. Deteniendo servicio.");
                        break;
                    }
                    else
                    {
                        await Task.Delay(parametros.IntervaloReintentoConexionBD, token);
                    }
                }
                catch (Exception ex)
                {
                    logger.Log("Error inesperado en el ciclo de conexión: " + ex.Message + " | Tipo: " + ex.GetType().Name);
                    intentos++;
                    foreach (var par in tareasDict)
                    {
                        try { par.Value.TokenSource?.Cancel(); } catch { }
                        par.Value.Estado = "detenida";
                    }
                    if (parametros.MaxReintentos > 0 && intentos >= parametros.MaxReintentos)
                    {
                        logger.Log($"No se pudo conectar tras {parametros.MaxReintentos} intentos. Deteniendo servicio.");
                        break;
                    }
                    else
                    {
                        await Task.Delay(parametros.IntervaloReintentoConexionBD, token);
                    }
                }
            }
        }

        private void BucleTarea(EstadoTarea estadoT, CancellationToken token)
        {
            try
            {
                estadoT.Estado = "ejecutando";
                int intervalo = estadoT.Datos.intervalo_lectura;
                if (intervalo < 100) intervalo = 100;

                while (!token.IsCancellationRequested && estadoT.Estado == "ejecutando")
                {
                    try
                    {
                        object valorCrudoLeido = LeerValorCrudo(
                            ip: estadoT.Datos.ip,
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
                                int registroIdx = 0;
                                int bitEnRegistro = 0;

                                foreach (var valor in estadoT.Datos.ListaValores)
                                {
                                    // ---- NUEVO: si es float32 por bits, parsear 2 registros como float (endianness desde config) ----
                                    if (valor.bitsvalor == 32)
                                    {
                                        if (registroIdx + 1 < reg.Length)
                                        {
                                            ushort hi = reg[registroIdx];
                                            ushort lo = reg[registroIdx + 1];
                                            float f = ToFloat(hi, lo, parametros.Endian);
                                            valor.ValorActual = (double)f; // guardamos como double
                                        }
                                        else
                                        {
                                            valor.ValorActual = null;
                                            logger.Log($"[WARN] float32 fuera de rango: idx={registroIdx}, len={reg.Length} (DL {estadoT.Datos.id_datalogger})");
                                        }
                                        registroIdx += 2;
                                        bitEnRegistro = 0;
                                        continue;
                                    }

                                    switch (valor.id_tipovalor)
                                    {
                                        case 1: // Entero (ushort)
                                            if (registroIdx < reg.Length)
                                            {
                                                valor.ValorActual = reg[registroIdx];
                                            }
                                            else
                                            {
                                                valor.ValorActual = null;
                                                logger.Log($"[WARN] Índice fuera de rango para entero: registroIdx={registroIdx}, largo={reg.Length}");
                                            }
                                            registroIdx++;
                                            bitEnRegistro = 0;
                                            break;

                                        case 2: // Decimal (short con 1 decimal)
                                            if (registroIdx < reg.Length)
                                            {
                                                short signedValue = unchecked((short)reg[registroIdx]);
                                                valor.ValorActual = Math.Round(signedValue / 10.0, 1);
                                            }
                                            else
                                            {
                                                valor.ValorActual = null;
                                                logger.Log($"[WARN] Índice fuera de rango para decimal: registroIdx={registroIdx}, largo={reg.Length}");
                                            }
                                            registroIdx++;
                                            bitEnRegistro = 0;
                                            break;

                                        case 5: // Bit
                                            if (registroIdx < reg.Length)
                                            {
                                                valor.ValorActual = ((reg[registroIdx] & (1 << bitEnRegistro)) != 0);
                                            }
                                            else
                                            {
                                                valor.ValorActual = null;
                                                logger.Log($"[WARN] Índice fuera de rango para bit: registroIdx={registroIdx}, largo={reg.Length}");
                                            }
                                            bitEnRegistro++;
                                            if (bitEnRegistro == 16)
                                            {
                                                registroIdx++;
                                                bitEnRegistro = 0;
                                            }
                                            break;

                                        default:
                                            // Otros tipos no soportados
                                            valor.ValorActual = null;
                                            logger.Log($"[WARN] Tipo de valor no soportado: id_tipovalor={valor.id_tipovalor}, posicion={valor.posicion}");
                                            break;
                                    }
                                }
                                // No marcamos "PendienteDeImpacto" global: el impacto lo gatilla la lógica de pesaje
                                estadoT.Datos.PendienteDeImpacto = true; // sirve para indicar que hay lectura nueva
                            }
                            else
                            {
                                logger.Log($"[DEBUG] ValorCrudoActual tipo={estadoT.Datos.ValorCrudoActual?.GetType().Name ?? "null"} NO es ushort[]");
                            }
                        }

                        estadoT.UltimoError = "";
                    }
                    catch (Exception exInt)
                    {
                        estadoT.UltimoError = exInt.Message;
                        estadoT.Datos.CalidadLecturaActual = LecturaCalidadEnum.ErrorComunicacion;
                        estadoT.Datos.PendienteDeImpacto = true;
                        estadoT.Estado = "error";
                        break;
                    }
                    Thread.Sleep(intervalo);
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
                if (estadoT.Estado != "error")
                    estadoT.Estado = "detenida";
            }
        }

        private object LeerValorCrudo(
            string ip,
            short modoLectura,
            short offset,
            short cantidadBits,
            string puertoSerial = null)
        {
            // cantidadBits está en "bits" a nivel datalogger (p.ej. 32 => 2 registros)
            ushort count = (ushort)Math.Max(1, cantidadBits / 16);
            ushort addr = (ushort)offset;

            using (var client = new TcpClient(ip, 502))
            {
                var master = ModbusIpMaster.CreateIp(client);

                return modoLectura switch
                {
                    1 => master.ReadHoldingRegisters(1, addr, count), // FC3
                    2 => master.ReadInputRegisters(1, addr, count),   // FC4 (inputs)
                    _ => master.ReadHoldingRegisters(1, addr, count),
                };
            }
        }

        private void CicloImpactoBD(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    foreach (var tarea in tareasDict.Values)
                    {
                        // Solo procesamos si hubo lectura nueva
                        if (!tarea.Datos.PendienteDeImpacto) continue;

                        // Identificar el valor de PESO:
                        var vPeso = tarea.Datos.id_valor_peso.HasValue
                            ? tarea.Datos.ListaValores.FirstOrDefault(v => v.id_valor == tarea.Datos.id_valor_peso.Value)
                            : null;

                        if (vPeso == null)
                        {
                            // No hay valor de peso configurado -> nada para impactar, solo limpiar flag
                            tarea.Datos.PendienteDeImpacto = false;
                            continue;
                        }

                        // Convertir a double si es posible
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

                        // ---- Máquina de estados (por datalogger) ----
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
                                        // ---- Evento: imprimir + impactar BD con calidad=100 ----
                                        string msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{tarea.Datos.id_datalogger}] PESO={tarea.TrackedRounded:0.00}";
                                        Console.WriteLine(msg);
                                        logger.Log(msg);

                                        if (tarea.Datos.id_valor_peso.HasValue)
                                        {
                                            try
                                            {
                                                using var cn = new SqlConnection(parametros.ConnString);
                                                cn.Open();
                                                using var cmd = new SqlCommand(@"
UPDATE valores 
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
                                                logger.Log($"[Impacto] DL {tarea.Datos.id_datalogger}: id_valor={tarea.Datos.id_valor_peso.Value}, valor={tarea.TrackedRounded:0.00}, filas={rows}");
                                            }
                                            catch (Exception exImp)
                                            {
                                                logger.Log($"[ERROR impacto] DL {tarea.Datos.id_datalogger}: {exImp.GetType().Name} - {exImp.Message}");
                                            }
                                        }

                                        // reiniciar ciclo
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

                        // Marcamos que ya procesamos esta tanda
                        tarea.Datos.PendienteDeImpacto = false;
                    }
                }
                catch (Exception ex)
                {
                    logger.Log("Error en CicloImpactoBD: " + ex.Message);
                }

                Thread.Sleep(parametros.IntervaloImpactoBD);
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
