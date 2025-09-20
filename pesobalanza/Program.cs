using System;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using Modbus.Device;

class Program
{
    static readonly CancellationTokenSource _cts = new();

    static async Task<int> Main(string[] args)
    {
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; _cts.Cancel(); };

        // Parámetros
        string ip = Get("--ip", "169.254.111.25");
        byte unit = byte.Parse(Get("--unit", "1"));
        string mode = Get("--mode", "holding"); // holding|input
        ushort addr = ushort.Parse(Get("--addr", "0"));
        ushort count = ushort.Parse(Get("--count", "2"));
        string fmt = Get("--format", "float"); // float|u16|s16|u32|s32
        string endian = Get("--endian", "DCBA");  // ABCD|BADC|CDAB|DCBA
        double scale = double.Parse(Get("--scale", "1"));
        double offs = double.Parse(Get("--offset", "0"));

        string stableMode = Get("--stableMode", "bit");     // bit|none|window
        string stableFn = Get("--stableFn", "inputs");       // inputs|coils
        ushort stableAddr = ushort.Parse(Get("--stableAddr", "0"));
        double winTol = double.Parse(Get("--winTol", "0.02"));
        int winMs = int.Parse(Get("--winMs", "500"));
        int periodMs = int.Parse(Get("--periodMs", "200"));
        int timeoutMs = int.Parse(Get("--timeoutMs", "1000"));

        Console.WriteLine("Leyendo peso por Modbus TCP. Ctrl+C para salir.");

        try
        {
            using var tcp = new TcpClient();
            tcp.ReceiveTimeout = timeoutMs;
            tcp.SendTimeout = timeoutMs;
            await tcp.ConnectAsync(ip, 502);

            using var master = ModbusIpMaster.CreateIp(tcp);
            master.Transport.ReadTimeout = timeoutMs;
            master.Transport.WriteTimeout = timeoutMs;

            var window = new Queue<(DateTime t, double v)>();

            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    double peso = mode.Equals("holding", StringComparison.OrdinalIgnoreCase)
                        ? ParseValue(master.ReadHoldingRegisters(unit, addr, count), fmt, endian, scale, offs)
                        : ParseValue(master.ReadInputRegisters(unit, addr, count), fmt, endian, scale, offs);

                    bool? estable = null;
                    if (stableMode.Equals("bit", StringComparison.OrdinalIgnoreCase))
                    {
                        bool[] bits = stableFn.Equals("coils", StringComparison.OrdinalIgnoreCase)
                            ? master.ReadCoils(unit, stableAddr, 1)
                            : master.ReadInputs(unit, stableAddr, 1);
                        estable = bits.Length > 0 ? bits[0] : null;
                    }
                    else if (stableMode.Equals("window", StringComparison.OrdinalIgnoreCase))
                    {
                        var now = DateTime.UtcNow;
                        window.Enqueue((now, peso));
                        while (window.Count > 0 && (now - window.Peek().t).TotalMilliseconds > winMs)
                            window.Dequeue();
                        if (window.Count > 1)
                        {
                            double min = double.MaxValue, max = double.MinValue;
                            foreach (var x in window) { if (x.v < min) min = x.v; if (x.v > max) max = x.v; }
                            estable = (max - min) <= winTol;
                        }
                    }

                    // Línea original (se mantiene)
                    Console.Write(estable.HasValue
                        ? $"{DateTime.Now:HH:mm:ss.fff}  Peso={peso:F3}  Estable={(estable.Value ? "SI" : "NO")}"
                        : $"{DateTime.Now:HH:mm:ss.fff}  Peso={peso:F3}   ");

                    // ---------- Solo HR[addr] para diagnosticar ----------
                    try
                    {
                        var hr = master.ReadHoldingRegisters(unit, addr, 1);
                        Console.WriteLine($"  HR[{addr}]=0x{hr[0]:X4} ({ToBits(hr[0])})");
                    }
                    catch
                    {
                        // Si no existe, no rompe nada
                    }
                    // -----------------------------------------------------
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ERR] {ex.GetType().Name}: {ex.Message}");
                }

                await Task.Delay(periodMs, _cts.Token);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL] {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        return 0;
    }

    // --- Métodos estáticos de apoyo (no locales) ---

    static float ToFloat(ushort hi, ushort lo, string endian)
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

    static double ParseValue(ushort[] regs, string format, string endian, double scale, double offset)
    {
        switch (format.ToLower())
        {
            case "u16": return regs[0] * scale + offset;
            case "s16": return unchecked((short)regs[0]) * scale + offset;
            case "u32": return (((uint)regs[0] << 16) | regs[1]) * scale + offset;
            case "s32": return ((unchecked((short)regs[0]) << 16) | regs[1]) * scale + offset;
            case "float":
            default: return ToFloat(regs[0], regs[1], endian) * scale + offset;
        }
    }

    static string Get(string key, string def)
    {
        var argv = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(argv, key);
        return (idx >= 0 && idx + 1 < argv.Length) ? argv[idx + 1] : def;
    }

    // --- Helper de diagnóstico ---
    static string ToBits(ushort v)
    {
        var s = Convert.ToString(v, 2).PadLeft(16, '0');
        return $"{s.Substring(0, 8)} {s.Substring(8, 8)}";
    }
}
