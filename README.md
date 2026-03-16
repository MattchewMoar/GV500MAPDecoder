# Queclink GV500MAP — C# Parser & TCP Listener

C# parser and TCP listener for the Queclink GV500MAP OBD GPS tracker. Implements the `@Track` ASCII protocol defined in **TRACGV500MAPAN001 R1.00**.

I bought one of these because Traccar listed it as compatible. It wasn't. There is basically nothing online about this device. So here's what I ended up writing after going through the protocol PDF field by field.

## What's here

`src/GV500MapMessageParser.cs` — Stateless parser. No I/O, no dependencies beyond `ILogger`. Handles GTFRI, GTSTT, GTIGN/GTIGF, GTVGN/GTVGF, GTOBD, the position report family, heartbeats, and a catchall for everything else.

`src/GV500MapTcpListenerExample.cs` — Minimal `BackgroundService` TCP listener. Uses `System.IO.Pipelines` for framing. Parses messages, sends SACKs, logs output. Add your own processing.

## Supported messages

**GTFRI** — Fixed interval report. Your main telemetry. Includes GNSS position plus OBD tail (RPM, fuel consumption, fuel level). Can contain one or two GNSS positions per the spec; the parser takes the last one.

**GTSTT** — Motion status change. Fires when the device transitions between states like ignition-off-rest, ignition-on-moving, towed, etc.

**GTIGN / GTIGF** — Ignition on/off. Includes duration since last ignition state change, hour meter, and mileage.

**GTVGN / GTVGF** — Virtual ignition on/off. Same idea but triggered via OBD voltage change instead of a direct ignition wire. Has an extra Reserved and Report Type field before Duration.

**GTOBD** — Full OBD-II dump. VIN, RPM, vehicle speed, coolant temp, throttle, fuel level, MIL status, DTCs, ECU odometer. Variable length because of the DTC field in the middle.

**Position report family** — GTGEO, GTSPD, GTRTL, GTHBM, GTIGL, GTVGL, GTTOW, GTDOG. All share the same field layout. No OBD.

**GTHBD** — Heartbeat. No position.

**GTPNA / GTPDP** — Power on, GPRS attached. No position.

Everything else gets a minimal parse (IMEI + count number) so you can still SACK it.

## Setup

Needs .NET 8+ and `Microsoft.Extensions.Logging` / `Microsoft.Extensions.Hosting`. No other packages.

```csharp
builder.Services.AddSingleton<IGV500MapMessageParser, GV500MapMessageParser>();
builder.Services.AddHostedService<GV500MapTcpListenerExample>();
```

### Sending AT commands to the device

If your SIM card has a phone number, you can send AT commands to the device via SMS. If you have a data-only SIM (no phone number), you'll need to connect the device to your computer with the included USB data cable and send commands through a serial terminal.

Fair warning: on Windows 11 the USB serial driver doesn't work out of the box. You have to go into Device Manager and roll back the driver to an older version before the COM port will show up. I've included a working driver in the `drivers/` folder so you don't have to hunt for it.

### Protocol PDF

The protocol document you need is **TRACGV500MAPAN001 — GV500MAP @Track Air Interface Protocol R1.00**. I can't include it here for copyright reasons. As of this writing it's not on Queclink's website (page not found), but I was able to find it by googling the document title. You can also try requesting it from Queclink or your distributor directly.

### Device configuration

Point the device at your server with these AT commands. The default password is `gv500`. All examples below are pulled from the protocol PDF.

**Server connection** (AT+GTSRI) — report mode 3 is TCP long-connection, heartbeat interval is in minutes, SACK enable is the second-to-last real parameter:

```
AT+GTSRI=gv500,3,,1,your.server.ip,5005,,,,,15,1,,,,,0001$
```

That sets: TCP long-connection mode, buffer mode 1, your server IP on port 5005, no backup server, no SMS gateway, 15-minute heartbeat, SACK enabled. The full parameter list from the spec:

```
AT+GTSRI=<Password>,<Report Mode>,<Reserved>,<Buffer Mode>,
  <Main Server IP>,<Main Server Port>,<Backup Server IP>,<Backup Server Port>,
  <SMS Gateway>,<Heartbeat Interval>,<SACK Enable>,<Protocol Format>,
  <Enable SMS ACK>,<High Priority Report Mask>,<Reserved>,<Serial Number>$
```

Or if you want to set APN and server in one shot and it fits in 160 bytes, use AT+GTQSS:

```
AT+GTQSS=gv500,your.apn,,,3,,1,your.server.ip,5005,,,,15,1,,,0002$
```

**Fixed report** (AT+GTFRI) — mode 1 is time-based, send interval is in seconds:

```
AT+GTFRI=gv500,1,0,,1,0000,0000,,10,1000,1000,,45,600,,,,,FFFF$
```

That's mode 1 (fixed time), 10-second send interval, 600-second interval when ignition is off. The full format:

```
AT+GTFRI=<Password>,<Mode>,<Discard No Fix>,<Reserved>,<Period Enable>,
  <Start Time>,<End Time>,<Reserved>,<Send Interval>,<Distance>,<Mileage>,
  <Reserved>,<Corner Report>,<IGF Report Interval>,<Reserved>,<Reserved>,
  <Reserved>,<Reserved>,<Serial Number>$
```

**OBD report** (AT+GTOBD) — the report mask controls which fields appear in +RESP:GTOBD. Bits 20–22 control GNSS, cell info, and mileage in the tail — you want those set if you want position data in GTOBD messages:

```
AT+GTOBD=gv500,1,30,60,0,70FFFF,7,2,0,,,7F,,,,FFFF$
```

That enables OBD, checks every 30 seconds, reports every 60 seconds when ignition is on, no reporting when ignition is off, mask `70FFFF` (everything including GNSS + GSM + mileage). The full format:

```
AT+GTOBD=<Password>,<Mode>,<OBD Check Interval>,<OBD Report Interval>,
  <OBD Report Interval IGF>,<OBD Report Mask>,<OBD Event Mask>,
  <Displacement>,<Fuel Oil Type>,<Custom Fuel Ratio>,<Custom Fuel Density>,
  <Journey Summary Mask>,<Reserved>,<IGF Debounce Time>,<Reserved>,
  <Reserved>,<Serial Number>$
```

## What I learned the hard way

### Get the right PDF

The GV500 and GV500MAP are different devices with different field layouts. The protocol document you want is **TRACGV500MAPAN001**, not the GV500 one. If your offsets are all wrong, this is probably why.

### SACK

SACK is an optional feature — `SACK Enable` defaults to 0 (off) in AT+GTSRI. When you turn it on, the server needs to reply to every message from the device. Two formats:

Heartbeat SACK: `+SACK:GTHBD,{ProtocolVersion},{CountNumber}$` — the protocol version field is optional, you can leave it empty.

General SACK: `+SACK:{CountNumber}$`

In practice, if you have SACK enabled and don't respond to a message type you don't care about, the device will keep retrying it. That's why the parser has a catchall that returns a minimal result for any structurally valid Queclink message — so you can SACK it and move on.

### GTOBD is variable length

The Diagnostic Trouble Codes field in GTOBD can be up to 4×127 bytes. This means you can't use fixed offsets from the front of the message to find the GNSS position and mileage at the end.

The parser handles this by reading OBD fields forwards from the fixed head (indices 0–23 are stable) and reading position/mileage fields backwards from the tail. The AT+GTOBD Report Mask controls which fields appear in the report — the backward-from-tail strategy only works if the mask includes GNSS, cell info, and mileage fields.

### Fuel consumption includes "Inf" and "NaN"

The spec says the fuel consumption field range is `0.0 - 999.9(L/100km)|Inf|NaN` for GTFRI, and `0.0 - 999.9(L/100km)|inf|nan` for GTOBD. The device calculates it from ECU values and when those are invalid (like speed = 0), you get those literal strings. `decimal.TryParse` returns false for them, which is the right behavior.

### GTFRI multi-position

The spec says GTFRI "may" contain one or two GNSS positions. When there are two, the second set of 7 GNSS fields (accuracy through UTC time) repeats, and everything after it — cell info, mileage, hour meter, device status, OBD fields — shifts by 7. The parser handles this by computing the tail offset from the Number field.

### Device status codes

The first two characters of the 6-char device status string in GTFRI, or the motion status field in GTSTT:

- `11` — Ignition off, motionless
- `12` — Ignition off, moving (pre-tow state)
- `16` — Ignition off, towed
- `1A` — Ignition off, possible tow ("Fake Tow")
- `21` — Ignition on, motionless
- `22` — Ignition on, moving
- `41` — No ignition signal detected, motionless
- `42` — No ignition signal detected, moving

### Count Number

4-character hex value (0000–FFFF) that increments by 1 per message and rolls over after FFFF. Useful for deduplication — the device will resend unacknowledged messages when it reconnects, same count number.

### TCP framing

Every message ends with `$`. TCP is a stream protocol so you can't just do `ReadAsync` into a buffer and assume you got one complete message. The example listener uses `System.IO.Pipelines` to scan for `$` delimiters, which handles partial reads and multiple messages in a single TCP segment correctly.

### Heartbeat interval

Configurable via AT+GTSRI, range is 5–360 minutes, default is 0 which means disabled. You'll want to set this when using TCP long-connection mode (report mode 3). The example listener has a 20-minute idle timeout that assumes you've set it to something reasonable.

## Testing the parser

No DI container needed:

```csharp
var logger = NullLoggerFactory.Instance.CreateLogger<GV500MapMessageParser>();
var parser = new GV500MapMessageParser(logger);

var result = parser.Parse(
    "+RESP:GTFRI,5E0300,861971050199116,,,,10,1,1,17.7,240,267.6," +
    "-82.999236,40.011088,20260313202448,0310,0410,5408,029D7311," +
    "00,0.0,,,,,220000,1133,13.5,38,20260313202450,025E$");

Assert.NotNull(result);
Assert.Equal("GTFRI", result.MessageType);
Assert.Equal("861971050199116", result.Imei);
Assert.Equal(40.011088, result.Latitude);
Assert.Equal(-82.999236, result.Longitude);
Assert.Equal(1133, result.EngineRpm);
Assert.True(result.IgnitionOn);
Assert.True(result.IsMoving);
```

## License

MIT.
