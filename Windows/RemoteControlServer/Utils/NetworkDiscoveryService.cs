using System;
using System.Threading.Tasks;
using System.Net;
using System.Net.Sockets;
using System.Text;
using RemoteControlServer.Core;

namespace RemoteControlServer.Utils;

/// <summary>
/// Lightweight UDP broadcast responder that lets the iOS app auto-discover this PC on the local
/// network without any manual IP entry. The app broadcasts a small "who's out there" datagram to
/// port 47887; every RemoteControlServer instance on the LAN answers with its hostname, the
/// WebSocket port, its serverId (from PairingService) and whether pairing is required.
///
/// This intentionally runs over plain UDP (not mDNS/Bonjour) so it works identically on networks
/// where multicast DNS is blocked by router/AP client isolation settings, which auto-discovery
/// features on consumer routers often break.
/// </summary>
public class NetworkDiscoveryService
{
    private const int DiscoveryPort = 47887;
    private const string DiscoveryRequestMagic = "REMOTEEMU_DISCOVER_V1";

    private readonly PairingService _pairing;
    private readonly int _wsPort;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;

    public NetworkDiscoveryService(PairingService pairing, int wsPort)
    {
        _pairing = pairing;
        _wsPort = wsPort;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _udpClient = new UdpClient(DiscoveryPort) { EnableBroadcast = true };
        _ = ListenLoopAsync(_cts.Token);
        Console.WriteLine($"[i] Network discovery listening on UDP {DiscoveryPort}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udpClient?.Close();
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient!.ReceiveAsync(token);
                string message = Encoding.UTF8.GetString(result.Buffer);
                if (message != DiscoveryRequestMagic) continue;

                string responseJson = Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    hostname = Environment.MachineName,
                    port = _wsPort,
                    serverId = _pairing.ServerId,
                    requiresPairing = true, // client always checks per-device trust during hello anyway
                });

                byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
                await _udpClient.SendAsync(responseBytes, responseBytes.Length, result.RemoteEndPoint);
            }
            catch (ObjectDisposedException) { break; } // socket closed on Stop()
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Discovery listener error: {ex.Message}");
            }
        }
    }
}
