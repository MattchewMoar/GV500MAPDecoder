using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Queclink.GV500MAP;

// ═══════════════════════════════════════════════════════════════════
// Parsed output — one per successfully parsed device message.
// ═══════════════════════════════════════════════════════════════════

public class ParsedDeviceMessage
{
    // Common to all messages
    public string MessageType { get; set; } = string.Empty;    // "GTFRI", "GTIGF", "GTHBD", etc.
    public string ProtocolVersion { get; set; } = string.Empty; // "5E0300"
    public string Imei { get; set; } = string.Empty;
    public string CountNumber { get; set; } = string.Empty;     // hex rolling counter
    public bool HasPosition { get; set; }

    // Position (null if HasPosition = false, e.g. GTHBD)
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public decimal? SpeedKmh { get; set; }
    public int? Azimuth { get; set; }
    public decimal? AltitudeMeters { get; set; }
    public int? GnssAccuracy { get; set; }                     // HDOP
    public DateTime? GnssUtcTime { get; set; }

    // OBD (populated from GTFRI and GTOBD)
    public int? EngineRpm { get; set; }
    public decimal? FuelConsumptionLPer100Km { get; set; }
    public int? FuelLevelPercent { get; set; }
    public int? ExternalPowerMillivolts { get; set; }

    // OBD-only fields (GTOBD — ECU-sourced data)
    public string? Vin { get; set; }
    public bool? ObdConnected { get; set; }
    public int? ObdVehicleSpeedKmh { get; set; }
    public int? CoolantTempCelsius { get; set; }
    public int? ThrottlePercent { get; set; }
    public bool? MilActive { get; set; }
    public decimal? EcuOdometerKm { get; set; }               // from GTOBD Mileage (ECU, not GPS)

    // Device state
    public string? DeviceStatus { get; set; }                  // "220000"
    public bool? IgnitionOn { get; set; }
    public bool? IsMoving { get; set; }
    public bool? IsTowed { get; set; }

    // Odometer
    public decimal? MileageKm { get; set; }
    public decimal? EngineHours { get; set; }                  // parsed from HHHHH:MM:SS

    // Protocol
    public int? ReportId { get; set; }
    public int? ReportType { get; set; }

    // Cell tower
    public string? CellMcc { get; set; }
    public string? CellMnc { get; set; }
    public string? CellLac { get; set; }
    public string? CellId { get; set; }

    // Raw for diagnostics
    public DateTime? DeviceSendTime { get; set; }
}

// ═══════════════════════════════════════════════════════════════════
// Parser interface + implementation
// ═══════════════════════════════════════════════════════════════════

public interface IGV500MapMessageParser
{
    /// <summary>
    /// Parse a raw comma-delimited GV500MAP protocol message.
    /// Returns null for unparseable or unrecognized messages.
    /// Pure logic — no I/O. Fully unit-testable.
    /// </summary>
    ParsedDeviceMessage? Parse(string rawMessage);
}

/// <summary>
/// Stateless parser for the Queclink GV500MAP @Track ASCII protocol
/// (TRACGV500MAPAN001 R1.00).
///
/// IMPORTANT: The GV500MAP protocol doc is TRACGV500MAPAN001, NOT the GV500 doc.
/// The GV500 and GV500MAP are different devices with different field layouts.
/// If you're referencing the wrong PDF, every offset will be wrong.
///
/// Four distinct field layouts per the protocol specification:
///
///   GTFRI (§3.3.1 Fixed Report):
///     31 fields. OBD-II tail. GNSS block starts at p[8].
///     Supports Number > 1 (multi-position — takes last fix).
///
///   Position Report family (§3.3.1):
///     GTGEO, GTSPD, GTRTL, GTHBM, GTIGL, GTVGL, GTTOW, GTDOG
///     23 fields. Same GNSS positions as GTFRI (p[8]–p[14]).
///     No OBD. Has Report ID/Type at p[6], Number at p[7].
///
///   GTSTT (§3.3.4 Event Report — Motion Status):
///     20 fields. Motion Status at p[5]. GNSS starts at p[6].
///     No Report ID/Type, no Number field.
///
///   GTIGN/GTIGF (§3.3.4 Event Report — Ignition):
///     22 fields. Duration at p[5]. GNSS starts at p[6].
///     Hour Meter at p[18], Mileage at p[19].
///
///   GTVGN/GTVGF (§3.3.4 Event Report — Virtual Ignition):
///     23 fields. Same as GTIGN/GTIGF but with extra Reserved + Report Type
///     fields before Duration. For OBD-based ignition detection.
///
///   GTOBD (§3.3.7 OBD-II Information Report):
///     Variable-length (DTCs in middle). Parse backwards from tail for
///     position data, forwards from head for OBD fields.
///     Requires AT+GTOBD mask with bits 20+21+22 set (e.g. 70FFFF).
///
///   Heartbeat (§3.4 GTHBD):
///     6 fields, no position.
///
/// DI registration: Singleton (stateless).
/// </summary>
public class GV500MapMessageParser : IGV500MapMessageParser
{
    private readonly ILogger<GV500MapMessageParser> _logger;

    // Position Report family — same GNSS layout as GTFRI, no OBD tail (§3.3.1)
    private static readonly HashSet<string> PositionReportTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTGEO", "GTSPD", "GTRTL", "GTHBM", "GTIGL", "GTVGL", "GTTOW", "GTDOG"
    };

    // Info-only messages — genuinely no position data (§3.3.4)
    // NOTE: GTMPN/GTMPF are NOT info-only — they carry a full GNSS block (§3.3.4).
    // They hit the catchall and get SACKed. If you later need their position data,
    // add a dedicated parser (GNSS starts at p[5], no Report ID, no Number field).
    private static readonly HashSet<string> InfoOnlyTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "GTPNA", "GTPDP"
    };

    public GV500MapMessageParser(ILogger<GV500MapMessageParser> logger)
    {
        _logger = logger;
    }

    public ParsedDeviceMessage? Parse(string rawMessage)
    {
        if (string.IsNullOrWhiteSpace(rawMessage))
            return null;

        // Strip trailing '$' and any whitespace
        var cleaned = rawMessage.TrimEnd('$', ' ', '\r', '\n');
        var parts = cleaned.Split(',');

        if (parts.Length < 4)
        {
            _logger.LogWarning("Message too short ({Length} fields): {Msg}",
                parts.Length, Truncate(rawMessage));
            return null;
        }

        // Field 0 determines message type. Strip "+RESP:" or "+ACK:" prefix.
        var msgType = ExtractMessageType(parts[0]);
        if (string.IsNullOrEmpty(msgType))
        {
            _logger.LogWarning("Could not extract message type from: {Field0}", parts[0]);
            return null;
        }

        // Route to the correct parser
        if (msgType == "GTHBD")
            return ParseHeartbeat(parts, msgType);

        if (msgType == "GTFRI" && parts.Length >= 31)
            return ParseGtfri(parts, msgType);

        if (msgType == "GTSTT" && parts.Length >= 20)
            return ParseGtstt(parts, msgType);

        if ((msgType == "GTIGN" || msgType == "GTIGF") && parts.Length >= 22)
            return ParseIgnitionEvent(parts, msgType);

        if ((msgType == "GTVGN" || msgType == "GTVGF") && parts.Length >= 23)
            return ParseVirtualIgnitionEvent(parts, msgType);

        if (msgType == "GTOBD" && parts.Length >= 10)
            return ParseGtobd(parts, msgType);

        if (PositionReportTypes.Contains(msgType) && parts.Length >= 23)
            return ParsePositionReport(parts, msgType);

        if (InfoOnlyTypes.Contains(msgType))
        {
            _logger.LogDebug("Info-only message: {Type}", msgType);
            return ParseInfoOnly(parts, msgType);
        }

        _logger.LogDebug(
            "Unknown or unsupported message type {Type} ({Length} fields)",
            msgType, parts.Length);

        // Catchall: any structurally valid Queclink message we don't specifically parse.
        // Returns a minimal result so the listener SACKs it — prevents device buffer
        // congestion. Without this, unprocessed message types (GTCRA, GTBPL, GTSTC,
        // GTMPN, GTMPF, etc.) permanently occupy buffer slots and
        // eventually crowd out GTFRI telemetry.
        if (parts.Length >= 6 && !string.IsNullOrEmpty(SafeField(parts, 2)))
        {
            return new ParsedDeviceMessage
            {
                MessageType = msgType,
                ProtocolVersion = parts[1],
                Imei = parts[2],
                CountNumber = parts[parts.Length - 1],
                HasPosition = false
            };
        }

        return null;
    }

    // ═══════════════════════════════════════════════════════════════
    // GTFRI — Fixed Report (§3.3.1), 31+ fields, OBD-II tail
    //
    // Protocol field order (Number=1):
    //   0  +RESP:GTFRI
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  VIN
    //   4  Device Name
    //   5  External Power Voltage (mV)
    //   6  Report ID / Report Type
    //   7  Number (of GNSS positions, usually 1)
    //  ── GNSS block (repeats Number times) ──
    //   8  GNSS Accuracy (HDOP, 0=no fix)
    //   9  Speed (km/h)
    //  10  Azimuth (0–359)
    //  11  Altitude (m)
    //  12  Longitude
    //  13  Latitude
    //  14  GNSS UTC Time
    //  ── Tail (shifts by 7 for each extra position) ──
    //  15  MCC
    //  16  MNC
    //  17  LAC
    //  18  Cell ID
    //  19  Reserved (00)
    //  20  Mileage (km)
    //  21  Hour Meter Count (HHHHH:MM:SS)
    //  22  Reserved
    //  23  Reserved
    //  24  Reserved
    //  25  Device Status (6-char hex)
    //  26  Engine RPM
    //  27  Fuel Consumption (L/100km)
    //  28  Fuel Level (%)
    //  29  Send Time
    //  30  Count Number
    //
    // Example:
    // +RESP:GTFRI,5E0300,861971050199116,,,,10,1,1,17.7,240,267.6,
    //   -82.999236,40.011088,20260313202448,0310,0410,5408,029D7311,
    //   00,0.0,,,,,220000,1133,13.5,38,20260313202450,025E$
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage ParseGtfri(string[] p, string msgType)
    {
        // Handle multi-position messages: Number at p[7].
        // Each extra position adds 7 fields (accuracy through GNSS UTC time).
        // We take the LAST position (most recent) and shift the tail accordingly.
        var number = IntParse(p[7]) ?? 1;
        var gnssOffset = 8 + 7 * (number - 1);   // start of last GNSS block
        var tailOffset = 8 + 7 * number;          // start of MCC after all GNSS blocks

        if (number > 1)
        {
            // Verify we have enough fields for multi-position
            var expectedLength = 31 + 7 * (number - 1);
            if (p.Length < expectedLength)
            {
                _logger.LogWarning(
                    "GTFRI Number={Number} but only {Length} fields (need {Expected})",
                    number, p.Length, expectedLength);
                // Fall back to first position
                gnssOffset = 8;
                tailOffset = 15;
            }
            else
            {
                _logger.LogDebug("GTFRI multi-position: Number={Number}, using last fix", number);
            }
        }

        var msg = new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            HasPosition = true,

            GnssAccuracy = IntParse(p[gnssOffset]),
            SpeedKmh = DecimalParse(p[gnssOffset + 1]),
            Azimuth = IntParse(p[gnssOffset + 2]),
            AltitudeMeters = DecimalParse(p[gnssOffset + 3]),
            Longitude = DoubleParse(p[gnssOffset + 4]),
            Latitude = DoubleParse(p[gnssOffset + 5]),
            GnssUtcTime = ParseTime(p[gnssOffset + 6]),

            CellMcc = SafeField(p, tailOffset),
            CellMnc = SafeField(p, tailOffset + 1),
            CellLac = SafeField(p, tailOffset + 2),
            CellId = SafeField(p, tailOffset + 3),

            MileageKm = DecimalParse(SafeField(p, tailOffset + 5)),
            EngineHours = ParseHourMeter(SafeField(p, tailOffset + 6)),

            // OBD-II fields
            EngineRpm = IntParse(SafeField(p, tailOffset + 11)),
            FuelConsumptionLPer100Km = ParseFuelConsumption(SafeField(p, tailOffset + 12)),
            FuelLevelPercent = IntParse(SafeField(p, tailOffset + 13)),

            DeviceSendTime = ParseTime(SafeField(p, tailOffset + 14)),
            CountNumber = SafeField(p, tailOffset + 15) ?? string.Empty
        };

        // Device status — 6-char hex at tail+10
        var status = SafeField(p, tailOffset + 10);
        if (!string.IsNullOrEmpty(status))
        {
            msg.DeviceStatus = status;
            var (ign, mov, tow) = ParseDeviceStatus(status);
            msg.IgnitionOn = ign;
            msg.IsMoving = mov;
            msg.IsTowed = tow;
        }

        // Report ID/Type from field 6 (hex nibbles)
        ParseReportField(SafeField(p, 6), msg);

        // External power mV from field 5 (often empty, requires AT+GTEPS)
        msg.ExternalPowerMillivolts = IntParse(p[5]);

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    // Position Report family (§3.3.1) — 23 fields
    //
    // Used by: GTGEO, GTSPD, GTRTL, GTHBM, GTIGL, GTVGL, GTTOW, GTDOG
    //
    // Same GNSS positions as GTFRI. No OBD tail.
    //
    // Protocol field order:
    //   0  +RESP:GTxxx
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  VIN
    //   4  Device Name
    //   5  Reserved
    //   6  Report ID / Report Type
    //   7  Number (0|1)
    //   8  GNSS Accuracy
    //   9  Speed (km/h)
    //  10  Azimuth
    //  11  Altitude (m)
    //  12  Longitude
    //  13  Latitude
    //  14  GNSS UTC Time
    //  15  MCC
    //  16  MNC
    //  17  LAC
    //  18  Cell ID
    //  19  Reserved (00)
    //  20  Mileage (km)
    //  21  Send Time
    //  22  Count Number
    //
    // Example:
    // +RESP:GTGEO,5E0100,135790246811220,,,,00,1,1,4.3,92,70.0,
    //   121.354335,31.222073,20090214013254,0460,0000,18D8,6141,
    //   00,2000.0,20090214093254,11F0$
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage ParsePositionReport(string[] p, string msgType)
    {
        // Number (p[7]) is 0|1 per spec. 0 = event fired but no GNSS fix.
        var hasPosition = (IntParse(p[7]) ?? 0) > 0;

        var msg = new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            CountNumber = p[22],
            HasPosition = hasPosition,

            GnssAccuracy = IntParse(p[8]),
            SpeedKmh = DecimalParse(p[9]),
            Azimuth = IntParse(p[10]),
            AltitudeMeters = DecimalParse(p[11]),
            Longitude = DoubleParse(p[12]),
            Latitude = DoubleParse(p[13]),
            GnssUtcTime = ParseTime(p[14]),

            CellMcc = SafeField(p, 15),
            CellMnc = SafeField(p, 16),
            CellLac = SafeField(p, 17),
            CellId = SafeField(p, 18),

            MileageKm = DecimalParse(SafeField(p, 20)),
            DeviceSendTime = ParseTime(SafeField(p, 21))
        };

        // Report ID/Type from field 6
        ParseReportField(SafeField(p, 6), msg);

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    // GTSTT — Motion Status Event (§3.3.4), 20 fields
    //
    // Protocol field order:
    //   0  +RESP:GTSTT
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  VIN
    //   4  Device Name
    //   5  Motion Status (11|12|16|21|22|41|42)
    //   6  GNSS Accuracy
    //   7  Speed (km/h)
    //   8  Azimuth
    //   9  Altitude (m)
    //  10  Longitude
    //  11  Latitude
    //  12  GNSS UTC Time
    //  13  MCC
    //  14  MNC
    //  15  LAC
    //  16  Cell ID
    //  17  Reserved (00)
    //  18  Send Time
    //  19  Count Number
    //
    // Example:
    // +RESP:GTSTT,5E0300,861971050199116,,,22,0,0.0,85,247.0,
    //   -82.999037,40.012832,20260313193154,0310,0410,5408,029A9B10,
    //   00,20260313193321,022D$
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage ParseGtstt(string[] p, string msgType)
    {
        var msg = new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            CountNumber = p[19],
            HasPosition = true,

            GnssAccuracy = IntParse(p[6]),
            SpeedKmh = DecimalParse(p[7]),
            Azimuth = IntParse(p[8]),
            AltitudeMeters = DecimalParse(p[9]),
            Longitude = DoubleParse(p[10]),
            Latitude = DoubleParse(p[11]),
            GnssUtcTime = ParseTime(p[12]),

            CellMcc = SafeField(p, 13),
            CellMnc = SafeField(p, 14),
            CellLac = SafeField(p, 15),
            CellId = SafeField(p, 16),

            DeviceSendTime = ParseTime(SafeField(p, 18))
        };

        // Motion status from field 5 — decode to ignition/moving/towed
        var motionStatus = SafeField(p, 5);
        if (!string.IsNullOrEmpty(motionStatus))
        {
            msg.DeviceStatus = motionStatus;
            var (ign, mov, tow) = ParseDeviceStatus(motionStatus);
            msg.IgnitionOn = ign;
            msg.IsMoving = mov;
            msg.IsTowed = tow;
        }

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    // GTIGN / GTIGF — Ignition On/Off Event (§3.3.4), 22 fields
    //
    // Protocol field order:
    //   0  +RESP:GTIGN or +RESP:GTIGF
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  VIN
    //   4  Device Name
    //   5  Duration of Ignition On/Off (sec)
    //   6  GNSS Accuracy
    //   7  Speed (km/h)
    //   8  Azimuth
    //   9  Altitude (m)
    //  10  Longitude
    //  11  Latitude
    //  12  GNSS UTC Time
    //  13  MCC
    //  14  MNC
    //  15  LAC
    //  16  Cell ID
    //  17  Reserved (00)
    //  18  Hour Meter Count (HHHHH:MM:SS)
    //  19  Mileage (km)
    //  20  Send Time
    //  21  Count Number
    //
    // Example:
    // +RESP:GTIGN,5E0300,861971050199116,,,0,1,0.0,85,247.0,
    //   -82.999037,40.012832,20260313193154,0310,0410,5408,029A9B10,
    //   00,00123:45:00,1234.5,20260313193321,022E$
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage ParseIgnitionEvent(string[] p, string msgType)
    {
        var msg = new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            CountNumber = p[21],
            HasPosition = true,

            GnssAccuracy = IntParse(p[6]),
            SpeedKmh = DecimalParse(p[7]),
            Azimuth = IntParse(p[8]),
            AltitudeMeters = DecimalParse(p[9]),
            Longitude = DoubleParse(p[10]),
            Latitude = DoubleParse(p[11]),
            GnssUtcTime = ParseTime(p[12]),

            CellMcc = SafeField(p, 13),
            CellMnc = SafeField(p, 14),
            CellLac = SafeField(p, 15),
            CellId = SafeField(p, 16),

            EngineHours = ParseHourMeter(SafeField(p, 18)),
            MileageKm = DecimalParse(SafeField(p, 19)),

            DeviceSendTime = ParseTime(SafeField(p, 20)),

            // Ignition state is implicit in the message type
            IgnitionOn = msgType == "GTIGN"
        };

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    // GTVGN / GTVGF — Virtual Ignition On/Off Event (§3.3.4), 23 fields
    //
    // Same as GTIGN/GTIGF but for vehicles where ignition is detected
    // via OBD voltage change rather than a direct ignition wire.
    // Has two extra fields (Reserved + Report Type) before Duration.
    //
    // Protocol field order:
    //   0  +RESP:GTVGN or +RESP:GTVGF
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  VIN
    //   4  Device Name
    //   5  Reserved ("00")
    //   6  Report Type (2|4|7)
    //   7  Duration of Ignition Off (sec)
    //   8  GNSS Accuracy
    //   9  Speed (km/h)
    //  10  Azimuth
    //  11  Altitude (m)
    //  12  Longitude
    //  13  Latitude
    //  14  GNSS UTC Time
    //  15  MCC
    //  16  MNC
    //  17  LAC
    //  18  Cell ID
    //  19  Reserved (00)
    //  20  Hour Meter Count (HHHHH:MM:SS)
    //  21  Mileage (km)
    //  22  Send Time
    //  23  Count Number
    //
    // Example:
    // +RESP:GTVGN,5E0100,135790246811220,,,00,2,382,0,0.0,0,1.0,
    //   117.201933,31.833132,20171207092206,0460,0000,5678,2D7E,00,
    //   00001:27:00,0.0,20171207092209,0117$
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage ParseVirtualIgnitionEvent(string[] p, string msgType)
    {
        var msg = new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            CountNumber = p[p.Length - 1],
            HasPosition = true,

            GnssAccuracy = IntParse(p[8]),
            SpeedKmh = DecimalParse(p[9]),
            Azimuth = IntParse(p[10]),
            AltitudeMeters = DecimalParse(p[11]),
            Longitude = DoubleParse(p[12]),
            Latitude = DoubleParse(p[13]),
            GnssUtcTime = ParseTime(p[14]),

            CellMcc = SafeField(p, 15),
            CellMnc = SafeField(p, 16),
            CellLac = SafeField(p, 17),
            CellId = SafeField(p, 18),

            EngineHours = ParseHourMeter(SafeField(p, 20)),
            MileageKm = DecimalParse(SafeField(p, 21)),

            DeviceSendTime = ParseTime(SafeField(p, 22)),

            // VGN = ignition on, VGF = ignition off
            IgnitionOn = msgType == "GTVGN"
        };

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    // GTOBD — OBDII Information Report (§3.3.7)
    //
    // Variable-length message due to DTC codes in the middle.
    // The OBD fields (RPM, speed, DTCs, etc.) sit in a variable-width
    // section, but the tail is always fixed when the Report Mask
    // includes GNSS (bit 20), GSM (bit 21), and Mileage (bit 22).
    //
    // Parse strategy: backwards from tail for position + ECU odometer,
    // forwards from the front for the fixed OBD header fields.
    //
    // REQUIRES AT+GTOBD mask with bits 20+21+22 set (e.g. 70FFFF).
    // Without those bits the tail fields are absent and offsets break.
    //
    // Fixed head (always present):
    //   0  +RESP:GTOBD
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  VIN (from header — may be empty)
    //   4  Device Name
    //   5  Report Type (0=periodic, 1=OBD event)
    //   6  Report Mask (hex)
    //   7  VIN (from ECU — the authoritative one)
    //   8  OBD Connection (0|1)
    //   9  OBD Power Voltage (mV)
    //  10  Supported PIDs (hex)
    //  11  Engine RPM
    //  12  Vehicle Speed (km/h)
    //  13  Engine Coolant Temp (°C, offset -40 applied by device)
    //  14  Fuel Consumption (L/100km, can be "Inf"/"NaN")
    //  15  DTCs Cleared Distance (km)
    //  16  MIL Activated Distance (km)
    //  17  MIL Status (0|1)
    //  18  Number of DTCs
    //  19  DTCs (variable-length hex, may contain multiple codes)
    //  20  Throttle Position (0-100%)
    //  21  Engine Load (0-100%)
    //  22  Fuel Level Input (0-100%)
    //  23  OBD Protocol (2 chars)
    //
    // Fixed tail (with mask 70FFFF, counted backwards from end):
    //  tail    Count Number
    //  tail-1  Send Time
    //  tail-2  Mileage / ECU Odometer (km) ← the prize
    //  tail-3  Reserved (00)
    //  tail-4  Cell ID
    //  tail-5  LAC
    //  tail-6  MNC
    //  tail-7  MCC
    //  tail-8  GNSS UTC Time
    //  tail-9  Latitude
    //  tail-10 Longitude
    //  tail-11 Altitude
    //  tail-12 Azimuth
    //  tail-13 Speed
    //  tail-14 GNSS Accuracy
    //
    // Example (mask 1FFF — no GNSS/GSM/Mileage, minimal tail):
    // +RESP:GTOBD,5E0100,135790246854321,1G1JC5444R7252367,,0,1FFF,
    //   1G1JC5444R7252367,1,0,FFFFDFFF,8045,181,140,30,0,20,1,2,
    //   29008200,10,20,30,20130628044803,010F$
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage? ParseGtobd(string[] p, string msgType)
    {
        // Verify the mask includes GNSS + GSM + Mileage (bits 20-22).
        // Without those, the tail offsets are wrong and we'd misparse.
        var maskStr = SafeField(p, 6);
        if (!string.IsNullOrEmpty(maskStr) &&
            long.TryParse(maskStr, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var mask))
        {
            const long GnssBit    = 1L << 20;  // 0x100000
            const long GsmBit     = 1L << 21;  // 0x200000
            const long MileageBit = 1L << 22;  // 0x400000

            if ((mask & (GnssBit | GsmBit | MileageBit)) != (GnssBit | GsmBit | MileageBit))
            {
                _logger.LogDebug(
                    "GTOBD mask {Mask} missing GNSS/GSM/Mileage bits — skipping position parse",
                    maskStr);

                // Still return a minimal message so it gets SACKed
                return new ParsedDeviceMessage
                {
                    MessageType = msgType,
                    ProtocolVersion = p[1],
                    Imei = p[2],
                    Vin = SafeField(p, 7) ?? SafeField(p, 3),
                    CountNumber = p[p.Length - 1],
                    HasPosition = false,

                    // Fixed-position OBD fields we can still grab
                    ObdConnected = SafeField(p, 8) == "1",
                    EngineRpm = IntParse(SafeField(p, 11)),
                    ObdVehicleSpeedKmh = IntParse(SafeField(p, 12)),
                    CoolantTempCelsius = IntParse(SafeField(p, 13)),
                    FuelConsumptionLPer100Km = ParseFuelConsumption(SafeField(p, 14)),
                    MilActive = SafeField(p, 17) == "1"
                };
            }
        }

        // With mask 70FFFF the tail is 15 fixed fields from the end.
        // Expect ~39 fields (24 head + 15 tail). Require at least 35 to
        // ensure tail-14 indexing doesn't land in the OBD header fields.
        if (p.Length < 35)
        {
            _logger.LogWarning("GTOBD with GNSS mask too short ({Length} fields, need >=35)", p.Length);
            return null;
        }

        int tail = p.Length - 1; // Count Number

        var msg = new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            CountNumber = p[tail],
            HasPosition = true,

            // VIN: prefer ECU-read (p[7]) over header (p[3])
            Vin = SafeField(p, 7) ?? SafeField(p, 3),

            // Fixed-position OBD fields from the head
            ObdConnected = SafeField(p, 8) == "1",
            EngineRpm = IntParse(SafeField(p, 11)),
            ObdVehicleSpeedKmh = IntParse(SafeField(p, 12)),
            CoolantTempCelsius = IntParse(SafeField(p, 13)),
            FuelConsumptionLPer100Km = ParseFuelConsumption(SafeField(p, 14)),
            MilActive = SafeField(p, 17) == "1",
            ThrottlePercent = IntParse(SafeField(p, 20)),
            FuelLevelPercent = IntParse(SafeField(p, 22)),

            // Tail: position + ECU odometer, parsed backwards
            GnssAccuracy = IntParse(SafeField(p, tail - 14)),
            SpeedKmh = DecimalParse(SafeField(p, tail - 13)),
            Azimuth = IntParse(SafeField(p, tail - 12)),
            AltitudeMeters = DecimalParse(SafeField(p, tail - 11)),
            Longitude = DoubleParse(SafeField(p, tail - 10)),
            Latitude = DoubleParse(SafeField(p, tail - 9)),
            GnssUtcTime = ParseTime(SafeField(p, tail - 8)),

            CellMcc = SafeField(p, tail - 7),
            CellMnc = SafeField(p, tail - 6),
            CellLac = SafeField(p, tail - 5),
            CellId = SafeField(p, tail - 4),

            // ECU odometer — the reason we're here
            EcuOdometerKm = DecimalParse(SafeField(p, tail - 2)),

            DeviceSendTime = ParseTime(SafeField(p, tail - 1))
        };

        return msg;
    }

    // ═══════════════════════════════════════════════════════════════
    // Heartbeat — minimal, no position (§3.4)
    //
    // Example:
    // +ACK:GTHBD,5E0300,861971050199116,,20260313215506,0297$
    //
    //   0  +ACK:GTHBD
    //   1  Protocol Version
    //   2  Unique ID (IMEI)
    //   3  Device Name
    //   4  Send Time
    //   5  Count Number
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage? ParseHeartbeat(string[] p, string msgType)
    {
        if (p.Length < 5)
        {
            _logger.LogWarning("Heartbeat too short ({Length} fields)", p.Length);
            return null;
        }

        return new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p[1],
            Imei = p[2],
            CountNumber = p.Length >= 6 ? p[5] : p[p.Length - 1],
            HasPosition = false,
            DeviceSendTime = ParseTime(SafeField(p, 4))
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Info-only messages (GTPNA, GTPDP) — no position
    // ═══════════════════════════════════════════════════════════════

    private ParsedDeviceMessage ParseInfoOnly(string[] p, string msgType)
    {
        return new ParsedDeviceMessage
        {
            MessageType = msgType,
            ProtocolVersion = p.Length > 1 ? p[1] : string.Empty,
            Imei = p.Length > 2 ? p[2] : string.Empty,
            CountNumber = p[p.Length - 1],  // always the last field before '$'
            HasPosition = false
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // Device Status Decoder
    //
    // First two chars of the 6-char status string (GTFRI field 25)
    // or the 2-char motion code (GTSTT field 5).
    // ═══════════════════════════════════════════════════════════════

    public static (bool ignitionOn, bool isMoving, bool isTowed) ParseDeviceStatus(string status)
    {
        // Take first 2 chars — works for both 6-char full status and 2-char motion code
        var motionCode = status.Length >= 2 ? status[..2] : status;
        return motionCode switch
        {
            "11" => (false, false, false),  // IGN off, stationary
            "12" => (false, true, false),   // IGN off, moving (pre-tow)
            "16" => (false, false, true),   // IGN off, towed
            "1A" => (false, false, true),   // IGN off, possible tow
            "21" => (true, false, false),   // IGN on, stationary
            "22" => (true, true, false),    // IGN on, moving
            "41" => (false, false, false),  // No ignition signal, stationary
            "42" => (false, true, false),   // No ignition signal, moving
            _ => (false, false, false)
        };
    }

    // ═══════════════════════════════════════════════════════════════
    // SACK Response Builders (§3.4, §3.5)
    //
    // Heartbeat SACK (§3.4):
    //   +SACK:GTHBD,{ProtocolVersion},{CountNumber}$
    //   Protocol version is optional per the spec.
    //
    // General SACK (§3.5):
    //   +SACK:{CountNumber}$
    // ═══════════════════════════════════════════════════════════════

    public static string BuildSack(ParsedDeviceMessage msg)
    {
        if (msg.MessageType == "GTHBD")
            return $"+SACK:GTHBD,{msg.ProtocolVersion},{msg.CountNumber}$";

        return $"+SACK:{msg.CountNumber}$";
    }

    // ═══════════════════════════════════════════════════════════════
    // Parsing Helpers — all use TryParse, never throw on bad data
    // ═══════════════════════════════════════════════════════════════

    private static string ExtractMessageType(string field0)
    {
        // "+RESP:GTFRI" → "GTFRI", "+ACK:GTHBD" → "GTHBD"
        var colonIdx = field0.LastIndexOf(':');
        return colonIdx >= 0 && colonIdx < field0.Length - 1
            ? field0[(colonIdx + 1)..]
            : string.Empty;
    }

    private static DateTime? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        return DateTime.TryParseExact(value, "yyyyMMddHHmmss",
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var dt)
            ? dt
            : null;
    }

    /// <summary>
    /// Parse HHHHH:MM:SS hour meter string to total engine hours as a decimal.
    /// e.g. "01234:30:00" → 1234.5
    /// </summary>
    private static decimal? ParseHourMeter(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        var segments = value.Split(':');
        if (segments.Length != 3)
            return null;

        if (decimal.TryParse(segments[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var hours) &&
            decimal.TryParse(segments[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var minutes) &&
            decimal.TryParse(segments[2], NumberStyles.Any, CultureInfo.InvariantCulture, out var seconds))
        {
            return hours + (minutes / 60.0m) + (seconds / 3600.0m);
        }

        return null;
    }

    /// <summary>
    /// Parse fuel consumption, handling "inf" and "NaN" (device divides by zero at speed=0).
    /// </summary>
    private static decimal? ParseFuelConsumption(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;

        // decimal.TryParse returns false for "inf" and "NaN" — exactly what we want
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }

    /// <summary>
    /// Parse report ID/type from the hex nibble field (GTFRI field 6).
    /// e.g. "10" → ReportId=1, ReportType=0
    /// </summary>
    private static void ParseReportField(string? value, ParsedDeviceMessage msg)
    {
        if (string.IsNullOrEmpty(value) || value.Length < 2)
            return;

        if (int.TryParse(value[0].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hi))
            msg.ReportId = hi;
        if (int.TryParse(value[1].ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var lo))
            msg.ReportType = lo;
    }

    private static int? IntParse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static decimal? DecimalParse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? DoubleParse(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        return double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? SafeField(string[] parts, int index)
    {
        if (index >= parts.Length) return null;
        var val = parts[index];
        return string.IsNullOrEmpty(val) ? null : val;
    }

    private static string Truncate(string value, int maxLength = 80)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }
}
