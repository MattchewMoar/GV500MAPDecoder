using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Buffers;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Queclink.GV500MAP;

/// <summary>
/// Minimal BackgroundService that opens a TCP socket and processes GV500MAP protocol messages.
/// Uses System.IO.Pipelines for correct TCP framing (handles partial reads and
/// multi-message segments — raw NetworkStream.ReadAsync does NOT do this correctly).
///
/// This is an example listener. In production you would:
///   - Resolve the IMEI to a vehicle/user in your system
///   - Deduplicate by CountNumber (the device replays unSACKed messages on reconnect)
///   - Persist position data and OBD telemetry
///   - Feed positions into your tracking pipeline
///
/// The key protocol contract:
///   - Every message ends with '$' (the TCP frame delimiter)
///   - The device expects a SACK response for each message
///   - If you don't SACK, the message stays in the device's buffer
///   - Unprocessed message types MUST still be SACKed or they crowd out telemetry
///   - Heartbeat SACK format differs from general SACK (see BuildSack)
///
/// Register as: builder.Services.AddHostedService&lt;GV500MapTcpListenerExample&gt;();
/// </summary>
public class GV500MapTcpListenerExample : BackgroundService
{
    private readonly IGV500MapMessageParser _parser;
    private readonly ILogger<GV500MapTcpListenerExample> _logger;
    private readonly int _listenPort;

    /// <summary>
    /// Connection idle timeout. The GV500MAP default heartbeat interval is 15 minutes
    /// (AT+GTHBD). If no data arrives in 20 minutes, the connection is dead — close it
    /// so the device reconnects cleanly instead of accumulating zombie sockets.
    /// </summary>
    private static readonly TimeSpan ConnectionIdleTimeout = TimeSpan.FromMinutes(20);

    public GV500MapTcpListenerExample(
        IGV500MapMessageParser parser,
        ILogger<GV500MapTcpListenerExample> logger)
    {
        _parser = parser;
        _logger = logger;
        _listenPort = 5005; // Or pull from IConfiguration
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Any, _listenPort);
        listener.Start();
        _logger.LogInformation("GV500MAP TCP listener started on port {Port}", _listenPort);

        try
        {
            // Fire-and-forget accept loop. If you await HandleConnectionAsync here,
            // the server blocks on one device at a time — all others queue in the
            // TCP backlog until the current connection disconnects.
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(stoppingToken);
                    _ = Task.Run(() => HandleConnectionAsync(client, stoppingToken), stoppingToken);
                }
                catch (SocketException ex)
                {
                    // Cellular modems drop during TCP handshake constantly.
                    // Log and continue — do NOT let this break the accept loop.
                    _logger.LogWarning(ex, "TCP handshake failed, continuing accept loop");
                }
            }
        }
        catch (OperationCanceledException) { /* Clean shutdown */ }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "TCP accept loop failed — vehicle tracking is DOWN");
        }
        finally
        {
            listener.Stop();
            _logger.LogInformation("GV500MAP TCP listener stopped");
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken stoppingToken)
    {
        var remoteEp = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("Device connected from {RemoteEndpoint}", remoteEp);

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var reader = PipeReader.Create(stream);

                while (!stoppingToken.IsCancellationRequested)
                {
                    ReadResult readResult;
                    try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                        timeoutCts.CancelAfter(ConnectionIdleTimeout);
                        readResult = await reader.ReadAsync(timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        _logger.LogWarning("Connection idle timeout for {Remote} — closing", remoteEp);
                        break;
                    }
                    catch (OperationCanceledException) { break; }

                    var buffer = readResult.Buffer;
                    SequencePosition consumed = buffer.Start;
                    SequencePosition examined = buffer.End;

                    try
                    {
                        // Scan for '$' delimiters — each complete message ends with '$'
                        while (TryReadMessage(ref buffer, out var messageBytes))
                        {
                            var rawMessage = Encoding.ASCII.GetString(messageBytes);
                            consumed = buffer.Start;

                            await ProcessMessageAsync(rawMessage, stream, stoppingToken);
                        }

                        examined = buffer.End;
                    }
                    finally
                    {
                        reader.AdvanceTo(consumed, examined);
                    }

                    if (readResult.IsCompleted)
                    {
                        _logger.LogInformation("Device disconnected from {Remote}", remoteEp);
                        break;
                    }
                }

                await reader.CompleteAsync();
            }
        }
        catch (IOException ex)
        {
            _logger.LogInformation("Device disconnected (IO) from {Remote}: {Reason}",
                remoteEp, ex.InnerException?.Message ?? ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error for connection from {Remote}", remoteEp);
        }
    }

    /// <summary>
    /// Scan the buffer for a '$' delimiter. If found, slice out the message
    /// (including the '$') and advance the buffer past it.
    ///
    /// This is why System.IO.Pipelines matters: TCP is a stream protocol, not a
    /// message protocol. A single ReadAsync call may return half a message, two
    /// messages glued together, or anything in between. This method correctly
    /// handles all of those cases.
    /// </summary>
    private static bool TryReadMessage(ref ReadOnlySequence<byte> buffer, out ReadOnlySpan<byte> message)
    {
        var reader = new SequenceReader<byte>(buffer);
        if (reader.TryAdvanceTo((byte)'$', advancePastDelimiter: true))
        {
            var slice = buffer.Slice(buffer.Start, reader.Position);
            message = slice.IsSingleSegment
                ? slice.FirstSpan
                : slice.ToArray();
            buffer = buffer.Slice(reader.Position);
            return true;
        }

        message = default;
        return false;
    }

    private async Task ProcessMessageAsync(
        string rawMessage,
        NetworkStream stream,
        CancellationToken ct)
    {
        var parsed = _parser.Parse(rawMessage);
        if (parsed == null)
        {
            _logger.LogWarning("Unparseable message: {Msg}", Truncate(rawMessage));
            return; // No SACK for truly unparseable messages
        }

        // Always SACK first — the device holds unsacked messages in a finite buffer.
        // If you do expensive processing before SACKing, you risk buffer congestion
        // on the device side, especially with high-frequency GTFRI reports.
        await SendSackAsync(stream, parsed, ct);

        // ── Route by message type ──────────────────────────────────────────

        if (parsed.MessageType == "GTHBD")
        {
            _logger.LogDebug("Heartbeat from {Imei}", parsed.Imei);
            return;
        }

        if (!parsed.HasPosition)
        {
            _logger.LogDebug("Info message {Type} from {Imei}", parsed.MessageType, parsed.Imei);
            return;
        }

        // ── Position message — this is where your logic goes ───────────────

        _logger.LogInformation(
            "{Type} from {Imei}: ({Lat}, {Lon}) speed={Speed}km/h ignition={Ign}",
            parsed.MessageType,
            parsed.Imei,
            parsed.Latitude,
            parsed.Longitude,
            parsed.SpeedKmh,
            parsed.IgnitionOn);

        // OBD data (GTFRI carries RPM/fuel/fuel-level, GTOBD carries the full ECU dump)
        if (parsed.EngineRpm.HasValue)
        {
            _logger.LogDebug(
                "  OBD: RPM={Rpm} fuel={Fuel}L/100km level={Level}%",
                parsed.EngineRpm, parsed.FuelConsumptionLPer100Km, parsed.FuelLevelPercent);
        }

        if (parsed.Vin != null)
        {
            _logger.LogDebug("  VIN={Vin} ECU_Odo={Odo}km", parsed.Vin, parsed.EcuOdometerKm);
        }

        // TODO: Your processing here. Examples:
        //   - Resolve parsed.Imei to a vehicle in your system
        //   - Deduplicate by parsed.CountNumber
        //   - Persist the position to your database
        //   - Broadcast to a real-time tracking UI
        //   - Check geofences
        //   - Store OBD telemetry for fleet maintenance
    }

    private static async Task SendSackAsync(
        NetworkStream stream,
        ParsedDeviceMessage parsed,
        CancellationToken ct)
    {
        var sack = GV500MapMessageParser.BuildSack(parsed);
        var bytes = Encoding.ASCII.GetBytes(sack);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static string Truncate(string value, int maxLength = 80)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
