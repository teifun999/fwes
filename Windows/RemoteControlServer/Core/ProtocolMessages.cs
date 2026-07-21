using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RemoteControlServer.Core;

/// <summary>
/// Root envelope for every message exchanged between the iOS client and the Windows server.
/// The payload itself is AES-256-GCM encrypted (see EncryptionService); this envelope is the
/// outer, unencrypted transport frame that only carries routing metadata.
///
/// Wire format (sent as a single WebSocket text frame, base64 payload):
/// {
///   "v": 1,                     // protocol version
///   "type": "handshake|frame|input|command|ack|error|...",
///   "id": "guid",                // message id, used for ack correlation
///   "ts": 1737249123000,         // unix ms timestamp
///   "iv": "base64",              // AES-GCM nonce (12 bytes)
///   "tag": "base64",             // AES-GCM auth tag (16 bytes)
///   "payload": "base64"          // AES-GCM ciphertext of the JSON body described below
/// }
/// </summary>
public class Envelope
{
    [JsonProperty("v")]
    public int Version { get; set; } = 1;

    [JsonProperty("type")]
    public string Type { get; set; } = string.Empty;

    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [JsonProperty("ts")]
    public long TimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    [JsonProperty("iv")]
    public string? Iv { get; set; }

    [JsonProperty("tag")]
    public string? Tag { get; set; }

    [JsonProperty("payload")]
    public string Payload { get; set; } = string.Empty;
}

/// <summary>Message type constants shared by both platforms.</summary>
public static class MessageType
{
    // Connection lifecycle
    public const string HelloRequest = "hello.request";     // client -> server, unencrypted, before pairing
    public const string HelloResponse = "hello.response";    // server -> client, capabilities + pairing state
    public const string PairRequest = "pair.request";        // client -> server, PIN + device info
    public const string PairResponse = "pair.response";      // server -> client, session key exchange result
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string Ack = "ack";
    public const string Error = "error";

    // Emulator management
    public const string EmulatorList = "emulator.list";
    public const string EmulatorSelect = "emulator.select";
    public const string EmulatorStart = "emulator.start";
    public const string EmulatorStop = "emulator.stop";
    public const string EmulatorRestart = "emulator.restart";
    public const string EmulatorStatus = "emulator.status";

    // Streaming
    public const string StreamStart = "stream.start";
    public const string StreamStop = "stream.stop";
    public const string StreamConfig = "stream.config";      // quality/fps changes
    public const string VideoFrame = "stream.frame";         // server -> client, encoded video chunk
    public const string StreamStats = "stream.stats";        // fps/ping/bitrate telemetry

    // Input
    public const string InputTouch = "input.touch";
    public const string InputMultiTouch = "input.multitouch";
    public const string InputKey = "input.key";
    public const string InputText = "input.text";
    public const string InputMouse = "input.mouse";
    public const string InputButton = "input.button";        // Home/Back/Menu/Recents/Volume/Power/etc.

    // Extras
    public const string ApkInstall = "apk.install";
    public const string AdbCommand = "adb.command";
    public const string ScreenshotRequest = "screenshot.request";
    public const string ScreenshotResponse = "screenshot.response";
    public const string MacroRecordStart = "macro.record.start";
    public const string MacroRecordStop = "macro.record.stop";
    public const string MacroPlay = "macro.play";
    public const string MacroList = "macro.list";
    public const string MacroSave = "macro.save";
    public const string FileTransfer = "file.transfer";
    public const string ClipboardSync = "clipboard.sync";
    public const string SystemStats = "system.stats";        // CPU/RAM of host PC
    public const string ConnectionLost = "connection.lost";  // used for push-notification trigger on client
}

// ---------- Payload DTOs (these are what's inside the decrypted "payload" JSON) ----------

public class HelloRequestPayload
{
    [JsonProperty("deviceName")] public string DeviceName { get; set; } = string.Empty;
    [JsonProperty("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonProperty("appVersion")] public string AppVersion { get; set; } = string.Empty;
}

public class HelloResponsePayload
{
    [JsonProperty("serverName")] public string ServerName { get; set; } = Environment.MachineName;
    [JsonProperty("requiresPairing")] public bool RequiresPairing { get; set; }
    [JsonProperty("protocolVersion")] public int ProtocolVersion { get; set; } = 1;
    [JsonProperty("supportsHevc")] public bool SupportsHevc { get; set; } = true;
}

public class PairRequestPayload
{
    [JsonProperty("pin")] public string Pin { get; set; } = string.Empty;
    [JsonProperty("clientPublicKey")] public string ClientPublicKeyBase64 { get; set; } = string.Empty; // ECDH
    [JsonProperty("deviceId")] public string DeviceId { get; set; } = string.Empty;
}

public class PairResponsePayload
{
    [JsonProperty("success")] public bool Success { get; set; }
    [JsonProperty("serverPublicKey")] public string ServerPublicKeyBase64 { get; set; } = string.Empty; // ECDH
    [JsonProperty("sessionToken")] public string SessionToken { get; set; } = string.Empty;
    [JsonProperty("reason")] public string? Reason { get; set; }
}

public class EmulatorInfo
{
    [JsonProperty("id")] public string Id { get; set; } = string.Empty;
    [JsonProperty("name")] public string Name { get; set; } = string.Empty;
    [JsonProperty("type")] public string Type { get; set; } = string.Empty; // "LDPlayer" | "BlueStacks" | "MuMu" | ...
    [JsonProperty("status")] public string Status { get; set; } = "stopped"; // stopped|starting|running|stopping
    [JsonProperty("adbSerial")] public string? AdbSerial { get; set; }
    [JsonProperty("resolutionW")] public int ResolutionW { get; set; }
    [JsonProperty("resolutionH")] public int ResolutionH { get; set; }
    [JsonProperty("isActive")] public bool IsActive { get; set; }
}

public class TouchEventPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("x")] public double X { get; set; }        // normalized 0..1
    [JsonProperty("y")] public double Y { get; set; }        // normalized 0..1
    [JsonProperty("action")] public string Action { get; set; } = "down"; // down|move|up|tap|doubletap|longpress
    [JsonProperty("pointerId")] public int PointerId { get; set; }
}

public class MultiTouchPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("points")] public List<TouchPoint> Points { get; set; } = new();
}

public class TouchPoint
{
    [JsonProperty("id")] public int Id { get; set; }
    [JsonProperty("x")] public double X { get; set; }
    [JsonProperty("y")] public double Y { get; set; }
    [JsonProperty("phase")] public string Phase { get; set; } = "moved"; // began|moved|ended|cancelled
}

public class KeyEventPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("keyCode")] public int KeyCode { get; set; } // Android keycode
    [JsonProperty("action")] public string Action { get; set; } = "down"; // down|up
}

public class TextInputPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("text")] public string Text { get; set; } = string.Empty;
}

public class ButtonPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("button")] public string Button { get; set; } = string.Empty; // home|back|menu|recents|volumeup|volumedown|power|screenshot|rotate
}

public class StreamConfigPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("codec")] public string Codec { get; set; } = "h264"; // h264|hevc
    [JsonProperty("fps")] public int Fps { get; set; } = 30;
    [JsonProperty("quality")] public int Quality { get; set; } = 80; // 0-100
    [JsonProperty("maxWidth")] public int MaxWidth { get; set; } = 1280;
}

public class StreamStatsPayload
{
    [JsonProperty("pingMs")] public double PingMs { get; set; }
    [JsonProperty("fps")] public double Fps { get; set; }
    [JsonProperty("bitrateKbps")] public double BitrateKbps { get; set; }
    [JsonProperty("cpuPercent")] public double CpuPercent { get; set; }
    [JsonProperty("ramPercent")] public double RamPercent { get; set; }
    [JsonProperty("activeEmulatorId")] public string? ActiveEmulatorId { get; set; }
}

public class AdbCommandPayload
{
    [JsonProperty("emulatorId")] public string EmulatorId { get; set; } = string.Empty;
    [JsonProperty("command")] public string Command { get; set; } = string.Empty; // raw args after "adb -s <serial>"
}

public class ErrorPayload
{
    [JsonProperty("code")] public string Code { get; set; } = string.Empty;
    [JsonProperty("message")] public string Message { get; set; } = string.Empty;
}
