using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using Fleck;
using Newtonsoft.Json;
using RemoteControlServer.Adb;
using RemoteControlServer.Capture;
using RemoteControlServer.Emulators;
using RemoteControlServer.Input;
// Fleck's server class is literally named "WebSocketServer", which collides with this file's
// own class name - alias it so the rest of the file stays readable.
using WebSocketServerFleck = Fleck.WebSocketServer;

namespace RemoteControlServer.Core;

/// <summary>
/// Per-connection session state: encryption context, which emulator this client currently
/// controls, active stream settings, and the connection itself.
/// </summary>
public class ClientSession
{
    public required IWebSocketConnection Socket { get; init; }
    public EncryptionService Crypto { get; } = new();
    public string DeviceId { get; set; } = string.Empty;
    public bool IsPaired { get; set; }
    public string? ActiveEmulatorId { get; set; }
    public StreamConfigPayload StreamConfig { get; set; } = new();
    public CancellationTokenSource? StreamCts { get; set; }
    public DateTime LastPongUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The core WebSocket server. Listens on a fixed port, performs the handshake/pairing dance,
/// then routes every subsequent (encrypted) message to the right subsystem:
/// EmulatorManager for lifecycle, AdbClient/InputSimulator for control, ScreenCaptureService
/// for the video stream.
///
/// One process serves any number of iOS clients concurrently (e.g. a second phone acting as
/// a spectator, or a tablet + phone controlling different emulators at once).
/// </summary>
public class RemoteControlWebSocketServer
{
    private const int Port = 7887;

    private readonly Dictionary<Guid, ClientSession> _sessions = new();
    private readonly PairingService _pairing;
    private readonly EmulatorManager _emulatorManager;
    private readonly AdbClient _adb;
    private readonly InputSimulator _input;
    private readonly ScreenCaptureService _capture;
    private WebSocketServerFleck? _server;

    public RemoteControlWebSocketServer(
        PairingService pairing,
        EmulatorManager emulatorManager,
        AdbClient adb,
        InputSimulator input,
        ScreenCaptureService capture)
    {
        _pairing = pairing;
        _emulatorManager = emulatorManager;
        _adb = adb;
        _input = input;
        _capture = capture;
    }

    public void Start()
    {
        // Fleck's WebSocketServer type name collides with our own class name; alias below.
        _server = new WebSocketServerFleck($"ws://0.0.0.0:{Port}");
        _server.Start(socket =>
        {
            Guid connectionId = Guid.NewGuid();

            socket.OnOpen = () =>
            {
                _sessions[connectionId] = new ClientSession { Socket = socket };
                Console.WriteLine($"[+] Client connected: {connectionId} from {socket.ConnectionInfo.ClientIpAddress}");
            };

            socket.OnClose = () =>
            {
                if (_sessions.TryGetValue(connectionId, out var session))
                {
                    session.StreamCts?.Cancel();
                    session.Crypto.Dispose();
                    _sessions.Remove(connectionId);
                }
                Console.WriteLine($"[-] Client disconnected: {connectionId}");
            };

            socket.OnMessage = message => HandleMessage(connectionId, message);
            socket.OnError = ex => Console.WriteLine($"[!] Socket error ({connectionId}): {ex.Message}");
        });

        Console.WriteLine($"RemoteControlServer listening on port {Port}");
    }

    public void Stop() => _server?.Dispose();

    private void HandleMessage(Guid connectionId, string rawMessage)
    {
        if (!_sessions.TryGetValue(connectionId, out var session)) return;

        try
        {
            var envelope = JsonConvert.DeserializeObject<Envelope>(rawMessage);
            if (envelope == null) return;

            switch (envelope.Type)
            {
                case MessageType.HelloRequest:
                    HandleHello(session, envelope);
                    return;

                case MessageType.PairRequest:
                    HandlePairing(session, envelope);
                    return;
            }

            // Every message beyond this point must belong to an already-paired, encrypted session.
            if (!session.IsPaired || !session.Crypto.IsSessionEstablished)
            {
                SendError(session, "NOT_PAIRED", "Session is not paired yet.");
                return;
            }

            RouteAuthenticatedMessage(session, envelope);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[!] Failed to handle message: {ex.Message}");
            SendError(session, "BAD_MESSAGE", ex.Message);
        }
    }

    private void HandleHello(ClientSession session, Envelope envelope)
    {
        var request = JsonConvert.DeserializeObject<HelloRequestPayload>(envelope.Payload)
                      ?? new HelloRequestPayload();
        session.DeviceId = request.DeviceId;

        bool alreadyTrusted = _pairing.IsTrusted(request.DeviceId);

        var response = new HelloResponsePayload
        {
            RequiresPairing = !alreadyTrusted,
        };

        // Hello/response is intentionally sent in the clear (no session key yet) - it carries
        // no sensitive data, only capability negotiation.
        Send(session, MessageType.HelloResponse, response, encrypt: false);

        if (alreadyTrusted)
        {
            session.IsPaired = true; // still requires the ECDH exchange below before real messages flow encrypted
        }
    }

    private void HandlePairing(ClientSession session, Envelope envelope)
    {
        var request = JsonConvert.DeserializeObject<PairRequestPayload>(envelope.Payload)
                      ?? new PairRequestPayload();

        bool alreadyTrusted = _pairing.IsTrusted(request.DeviceId);
        bool pinOk = alreadyTrusted || _pairing.ValidatePin(request.Pin);

        if (!pinOk)
        {
            Send(session, MessageType.PairResponse, new PairResponsePayload
            {
                Success = false,
                Reason = "Invalid PIN"
            }, encrypt: false);
            return;
        }

        byte[] serverPublicKey = session.Crypto.GenerateServerKeyPair();
        byte[] clientPublicKey = Convert.FromBase64String(request.ClientPublicKeyBase64);
        session.Crypto.DeriveSessionKey(clientPublicKey, salt: request.DeviceId);

        _pairing.TrustDevice(request.DeviceId);
        session.DeviceId = request.DeviceId;
        session.IsPaired = true;

        Send(session, MessageType.PairResponse, new PairResponsePayload
        {
            Success = true,
            ServerPublicKeyBase64 = Convert.ToBase64String(serverPublicKey),
            SessionToken = Guid.NewGuid().ToString("N")
        }, encrypt: false); // last unencrypted message; everything after uses the new session key
    }

    private void RouteAuthenticatedMessage(ClientSession session, Envelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageType.Ping:
                Send(session, MessageType.Pong, new { }, encrypt: true);
                break;

            case MessageType.EmulatorList:
                Send(session, MessageType.EmulatorList, _emulatorManager.GetAll(), encrypt: true);
                break;

            case MessageType.EmulatorSelect:
                var selectPayload = session.Crypto.Decrypt<EmulatorInfo>(envelope);
                session.ActiveEmulatorId = selectPayload.Id;
                _emulatorManager.SetActive(selectPayload.Id);
                break;

            case MessageType.EmulatorStart:
                var startInfo = session.Crypto.Decrypt<EmulatorInfo>(envelope);
                _ = _emulatorManager.StartAsync(startInfo.Id);
                break;

            case MessageType.EmulatorStop:
                var stopInfo = session.Crypto.Decrypt<EmulatorInfo>(envelope);
                _ = _emulatorManager.StopAsync(stopInfo.Id);
                break;

            case MessageType.EmulatorRestart:
                var restartInfo = session.Crypto.Decrypt<EmulatorInfo>(envelope);
                _ = _emulatorManager.RestartAsync(restartInfo.Id);
                break;

            case MessageType.StreamStart:
                var streamCfg = session.Crypto.Decrypt<StreamConfigPayload>(envelope);
                session.StreamConfig = streamCfg;
                StartStreaming(session);
                break;

            case MessageType.StreamStop:
                session.StreamCts?.Cancel();
                break;

            case MessageType.StreamConfig:
                session.StreamConfig = session.Crypto.Decrypt<StreamConfigPayload>(envelope);
                _capture.UpdateConfig(session.ActiveEmulatorId, session.StreamConfig);
                break;

            case MessageType.InputTouch:
                var touch = session.Crypto.Decrypt<TouchEventPayload>(envelope);
                DispatchTouch(touch);
                break;

            case MessageType.InputMultiTouch:
                var multiTouch = session.Crypto.Decrypt<MultiTouchPayload>(envelope);
                DispatchMultiTouch(multiTouch);
                break;

            case MessageType.InputKey:
                var key = session.Crypto.Decrypt<KeyEventPayload>(envelope);
                DispatchKey(key);
                break;

            case MessageType.InputText:
                var text = session.Crypto.Decrypt<TextInputPayload>(envelope);
                DispatchText(text);
                break;

            case MessageType.InputButton:
                var button = session.Crypto.Decrypt<ButtonPayload>(envelope);
                DispatchButton(button);
                break;

            case MessageType.AdbCommand:
                var adbCmd = session.Crypto.Decrypt<AdbCommandPayload>(envelope);
                _ = DispatchAdbCommandAsync(session, adbCmd);
                break;

            case MessageType.ScreenshotRequest:
                var emuInfo = session.Crypto.Decrypt<EmulatorInfo>(envelope);
                _ = SendScreenshotAsync(session, emuInfo.Id);
                break;

            default:
                Console.WriteLine($"[?] Unhandled message type: {envelope.Type}");
                break;
        }
    }

    private void DispatchTouch(TouchEventPayload touch)
    {
        var emulator = _emulatorManager.Get(touch.EmulatorId);
        if (emulator == null) return;

        if (_adb.IsAvailable && emulator.AdbSerial != null)
            _adb.SendTap(emulator.AdbSerial, touch);
        else
            _input.SendTouch(emulator, touch);
    }

    private void DispatchMultiTouch(MultiTouchPayload payload)
    {
        var emulator = _emulatorManager.Get(payload.EmulatorId);
        if (emulator == null) return;

        // ADB has no native multi-touch gesture command beyond `input swipe`, so multi-touch
        // (pinch/zoom, two-finger scroll) is best simulated via Windows pointer injection,
        // which BlueStacks/LDPlayer translate into Android multi-touch internally.
        _input.SendMultiTouch(emulator, payload);
    }

    private void DispatchKey(KeyEventPayload key)
    {
        var emulator = _emulatorManager.Get(key.EmulatorId);
        if (emulator == null) return;

        if (_adb.IsAvailable && emulator.AdbSerial != null)
            _adb.SendKeyEvent(emulator.AdbSerial, key.KeyCode);
        else
            _input.SendKey(emulator, key);
    }

    private void DispatchText(TextInputPayload text)
    {
        var emulator = _emulatorManager.Get(text.EmulatorId);
        if (emulator == null) return;

        if (_adb.IsAvailable && emulator.AdbSerial != null)
            _adb.SendText(emulator.AdbSerial, text.Text);
        else
            _input.SendText(emulator, text.Text);
    }

    private void DispatchButton(ButtonPayload button)
    {
        var emulator = _emulatorManager.Get(button.EmulatorId);
        if (emulator == null) return;

        if (_adb.IsAvailable && emulator.AdbSerial != null)
            _adb.SendHardwareButton(emulator.AdbSerial, button.Button);
        else
            _input.SendHardwareButton(emulator, button.Button);
    }

    private async Task DispatchAdbCommandAsync(ClientSession session, AdbCommandPayload cmd)
    {
        var emulator = _emulatorManager.Get(cmd.EmulatorId);
        if (emulator?.AdbSerial == null)
        {
            SendError(session, "ADB_UNAVAILABLE", "No ADB serial for this emulator.");
            return;
        }

        string output = await _adb.RunRawCommandAsync(emulator.AdbSerial, cmd.Command);
        Send(session, MessageType.Ack, new { command = cmd.Command, output }, encrypt: true);
    }

    private async Task SendScreenshotAsync(ClientSession session, string emulatorId)
    {
        var emulator = _emulatorManager.Get(emulatorId);
        if (emulator == null) return;

        byte[] png = await _capture.CaptureScreenshotAsync(emulator);
        Send(session, MessageType.ScreenshotResponse, new
        {
            emulatorId,
            imageBase64 = Convert.ToBase64String(png)
        }, encrypt: true);
    }

    private void StartStreaming(ClientSession session)
    {
        session.StreamCts?.Cancel();
        var cts = new CancellationTokenSource();
        session.StreamCts = cts;

        var emulator = session.ActiveEmulatorId != null ? _emulatorManager.Get(session.ActiveEmulatorId) : null;
        if (emulator == null)
        {
            SendError(session, "NO_EMULATOR", "No active emulator selected for streaming.");
            return;
        }

        _ = _capture.StreamAsync(emulator, session.StreamConfig, cts.Token, chunk =>
        {
            Send(session, MessageType.VideoFrame, new
            {
                emulatorId = emulator.Id,
                codec = session.StreamConfig.Codec,
                data = Convert.ToBase64String(chunk),
                keyFrame = false
            }, encrypt: true);
        });
    }

    private void Send<T>(ClientSession session, string type, T payload, bool encrypt)
    {
        var envelope = new Envelope { Type = type };

        if (encrypt && session.Crypto.IsSessionEstablished)
        {
            var (iv, tag, cipherPayload) = session.Crypto.Encrypt(payload);
            envelope.Iv = iv;
            envelope.Tag = tag;
            envelope.Payload = cipherPayload;
        }
        else
        {
            envelope.Payload = JsonConvert.SerializeObject(payload);
        }

        session.Socket.Send(JsonConvert.SerializeObject(envelope));
    }

    private void SendError(ClientSession session, string code, string message)
    {
        Send(session, MessageType.Error, new ErrorPayload { Code = code, Message = message }, encrypt: session.Crypto.IsSessionEstablished);
    }
}
