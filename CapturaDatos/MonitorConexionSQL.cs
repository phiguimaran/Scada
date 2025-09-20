using Microsoft.Data.SqlClient;
using Modbus.Device;
using Modbus.Extensions.Enron;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;


namespace CapturaDatos
{
    public class MonitorConexionSQL
    {
        private readonly ParametrosServicio parametros;
        private readonly RotatingLogger logger;
        public ConcurrentDictionary<int, EstadoTarea> tareasDict = new();
        
        public MonitorConexionSQL(ParametrosServicio parametros, RotatingLogger logger)
        {
            this.parametros = parametros;
            this.logger = logger;
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
                    string sql = @"SELECT D.id_datalogger, D.ip, D.cantvalores, D.id_modolectura, D.inicio, D.bits AS bitsdatalogger, D.intervalo_lectura,
                                V.id_valor, V.posicion, T.id_tipovalor, T.bits AS bitsvalor
                                FROM dataloggers D WITH (NOLOCK)
                                JOIN valores V WITH (NOLOCK) ON D.id_datalogger = V.id_datalogger
                                JOIN tiposvalor T WITH (NOLOCK) ON V.id_tipovalor = T.id_tipovalor
                                WHERE D.activo = 1 AND D.tipo_lectura = 1";

                    var cmd = new SqlCommand(sql, cn);
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

                    tareasDict.Clear();
                    foreach (var kvp in dict)
                    {
                        var estadoT = new EstadoTarea
                        {
                            Datos = kvp.Value,
                            Estado = "pendiente"
                        };
                        tareasDict.TryAdd(kvp.Key, estadoT);
                    }

                    // Lanzar tareas
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
                            return; // Salida limpia, no es error
                        }
                    }
                }
                catch (TaskCanceledException tcex)
                {
                    logger.Log("Ciclo cancelado por token de cancelación: " + tcex.Message);
                    // Esto ocurre si se cancela el token externo o de la app. No es un error real.
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

                        // Solo parsea si cambió el crudo o la calidad
                        if (crudoCambio || estadoT.Datos.CalidadLecturaActual != estadoT.Datos.CalidadLecturaImpactada)
                        {
                            if (estadoT.Datos.ValorCrudoActual is ushort[] reg)
                            {
                                foreach (var valor in estadoT.Datos.ListaValores)
                                {
                                    // Posición 1-based desde el "inicio" del datalogger
                                    int pos1 = Math.Max(1, (int)valor.posicion);

                                    // Caso especial: float32 indicado por bitsvalor == 32 (2 palabras)
                                    if (valor.bitsvalor == 32)
                                    {
                                        int registroIdx = (pos1 - 1); // índice 0-based de palabra inicial
                                        if (registroIdx + 1 < reg.Length)
                                        {
                                            ushort hi = reg[registroIdx];
                                            ushort lo = reg[registroIdx + 1];
                                            float f = ToFloat(hi, lo, parametros.Endian); // helper ya existente en la clase
                                            valor.ValorActual = (double)f;
                                        }
                                        else
                                        {
                                            valor.ValorActual = null;
                                            logger.Log($"[WARN] DL {estadoT.Datos.id_datalogger}: float32 fuera de rango (pos={pos1}, len={reg.Length})");
                                        }
                                        continue;
                                    }

                                    switch (valor.id_tipovalor)
                                    {
                                        case 1: // Entero 16-bit (ushort)
                                            {
                                                int registroIdx = (pos1 - 1);
                                                if (registroIdx < reg.Length)
                                                {
                                                    valor.ValorActual = reg[registroIdx];
                                                }
                                                else
                                                {
                                                    valor.ValorActual = null;
                                                    logger.Log($"[WARN] DL {estadoT.Datos.id_datalogger}: entero fuera de rango (pos={pos1}, len={reg.Length})");
                                                }
                                                break;
                                            }

                                        case 2: // Decimal en 16-bit (short con 1 decimal)
                                            {
                                                int registroIdx = (pos1 - 1);
                                                if (registroIdx < reg.Length)
                                                {
                                                    short signedValue = unchecked((short)reg[registroIdx]);
                                                    valor.ValorActual = Math.Round(signedValue / 10.0, 1);
                                                }
                                                else
                                                {
                                                    valor.ValorActual = null;
                                                    logger.Log($"[WARN] DL {estadoT.Datos.id_datalogger}: decimal16 fuera de rango (pos={pos1}, len={reg.Length})");
                                                }
                                                break;
                                            }

                                        case 5: // Bit: posicion es bit absoluto 1-based desde 'inicio'
                                            {
                                                int bitAbs0 = (pos1 - 1);      // 0-based
                                                int registroIdx = bitAbs0 / 16; // palabra
                                                int bitEnRegistro = bitAbs0 % 16;
                                                if (registroIdx < reg.Length)
                                                {
                                                    valor.ValorActual = ((reg[registroIdx] & (1 << bitEnRegistro)) != 0);
                                                }
                                                else
                                                {
                                                    valor.ValorActual = null;
                                                    logger.Log($"[WARN] DL {estadoT.Datos.id_datalogger}: bit fuera de rango (posBit={pos1}, len={reg.Length})");
                                                }
                                                break;
                                            }

                                        default:
                                            valor.ValorActual = null;
                                            logger.Log($"[WARN] Tipo no soportado: id_tipovalor={valor.id_tipovalor}, pos={pos1}");
                                            break;
                                    }
                                }

                                // hubo nueva lectura y parseo
                                estadoT.Datos.PendienteDeImpacto = true;
                            }
                            else
                            {
                                // Si no es ushort[], loguea el tipo real
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

        /// <summary>
        /// Lee el valor crudo de un datalogger utilizando el modo y la configuración indicados.
        /// El valor retornado puede ser un byte[], string, etc, según el hardware y el protocolo.
        /// </summary>
        /// <param name="ip">IP del datalogger</param>
        /// <param name="modoLectura">Tipo/protocolo de lectura (ej: ModbusTCP, Serial, etc.)</param>
        /// <param name="offset">Posición inicial/registro/base de lectura</param>
        /// <param name="cantidadBits">Cantidad de bits (o bytes) a leer</param>
        /// <param name="puertoSerial">Nombre del puerto serial (si aplica)</param>
        /// <returns>El valor crudo leído (byte[], string, etc.)</returns>

        private object LeerValorCrudo(
            string ip,
            short modoLectura,
            short offset,
            short cantidadBits,
            string puertoSerial = null)
        {
            switch (modoLectura)
            {
                case 1:
                    //logger.Log("ip: " + ip.ToString() + " offset: " + offset.ToString() + "  cantidad:" + cantidadBits.ToString());

                    using (var client = new TcpClient(ip, 502))
                    {
                        var master = ModbusIpMaster.CreateIp(client);
                        ushort[] buffer = master.ReadHoldingRegisters(1, (ushort)offset, (ushort)(cantidadBits / 16));

                        //logger.Log("valores: " + buffer[0].ToString() + " - " + buffer[1].ToString() + " - " + buffer[2].ToString() + " - " + buffer[3].ToString() + " - " + buffer[4].ToString() + " - " + buffer[5].ToString());

                        return buffer;

                    }
                    // Ejemplo: Modbus TCP
                    // Aquí va tu lógica real con librería de Modbus.
                    // Por ejemplo, supón que usás NModbus:
                    /*
                    using (var modbusClient = new ModbusTcpClient(ip, 502))
                    {
                        modbusClient.Connect();
                        byte[] buffer = modbusClient.ReadHoldingRegisters(offset, cantidadBits / 16);
                        return buffer;
                    }
                    */

                case 2: // Ejemplo: Serial
                        // Aquí podrías usar System.IO.Ports.SerialPort, etc.
                    /*
                    using (var sp = new SerialPort(puertoSerial, 9600, Parity.None, 8, StopBits.One))
                    {
                        sp.Open();
                        byte[] buffer = new byte[cantidadBits / 8];
                        sp.Read(buffer, 0, buffer.Length);
                        return buffer;
                    }
                    */
                    return ("serial");

                case 3: // Otro protocolo, por ejemplo, HTTP o UDP
                        // Implementar según tu protocolo/hardware
                    return ("otro protocolo");

                default:
                    // Por defecto, retorna nulo o lanza excepción
                    return null;
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
                        if (tarea.Datos.PendienteDeImpacto)
                        {
                            using var cn = new SqlConnection(parametros.ConnString);
                            cn.Open();

                            foreach (var valor in tarea.Datos.ListaValores)
                            {
                                string campoPrincipal = null;
                                string setOtros = "";
                                SqlParameter valorParam;

                                switch (valor.id_tipovalor)
                                {
                                    case 1: // Entero
                                        campoPrincipal = "valor_entero";
                                        setOtros = "valor_decimal = NULL, valor_bit = NULL, valor_string = NULL, valor_binario = NULL";
                                        valorParam = new SqlParameter("@valor", System.Data.SqlDbType.Int)
                                        {
                                            Value = valor.ValorActual ?? DBNull.Value
                                        };
                                        break;

                                    case 2: // Decimal
                                        campoPrincipal = "valor_decimal";
                                        setOtros = "valor_entero = NULL, valor_bit = NULL, valor_string = NULL, valor_binario = NULL";
                                        valorParam = new SqlParameter("@valor", System.Data.SqlDbType.Decimal)
                                        {
                                            Precision = 18,
                                            Scale = 4,
                                            Value = valor.ValorActual ?? DBNull.Value
                                        };
                                        break;

                                    case 5: // Bit
                                        campoPrincipal = "valor_bit";
                                        setOtros = "valor_entero = NULL, valor_decimal = NULL, valor_string = NULL, valor_binario = NULL";
                                        valorParam = new SqlParameter("@valor", System.Data.SqlDbType.Bit)
                                        {
                                            Value = valor.ValorActual ?? DBNull.Value
                                        };
                                        break;

                                    default:
                                        logger.Log($"[WARN] Tipo de valor no soportado en impacto BD: id_tipovalor={valor.id_tipovalor}, id_valor={valor.id_valor}");
                                        continue;
                                }

                                var sql = $@"
                            UPDATE valores 
                            SET {campoPrincipal} = @valor,
                                {setOtros},
                                calidad = @calidad, 
                                timestamp = @fecha 
                            WHERE id_valor = @id_valor";

                                using var cmd = new SqlCommand(sql, cn);
                                cmd.Parameters.Add(valorParam);
                                cmd.Parameters.AddWithValue("@calidad", tarea.Datos.CalidadLecturaActual);
                                cmd.Parameters.AddWithValue("@fecha", DateTime.Now);
                                cmd.Parameters.AddWithValue("@id_valor", valor.id_valor);
                                cmd.ExecuteNonQuery();
                            }

                            tarea.Datos.UltimoValorCrudoImpactado = tarea.Datos.ValorCrudoActual;
                            tarea.Datos.CalidadLecturaImpactada = tarea.Datos.CalidadLecturaActual;
                            tarea.Datos.PendienteDeImpacto = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.Log("Error en CicloImpactoBD: " + ex.Message);
                }

                Thread.Sleep(parametros.IntervaloImpactoBD);
            }
        }


    }
}
