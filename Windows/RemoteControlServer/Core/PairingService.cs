using System.Collections.Generic;
using System.Linq;
using System;
using System.IO;
using System.Security.Cryptography;
using QRCoder;

namespace RemoteControlServer.Core;

/// <summary>
/// Manages pairing state: the current 6-digit PIN (rotated periodically), the QR-code payload
/// that encodes host/port/PIN/serverId for one-tap pairing, and the set of already-trusted
/// device IDs (persisted so returning devices don't need to re-enter the PIN every time).
/// </summary>
public class PairingService
{
    private readonly string _configPath;
    private string _currentPin;
    private readonly HashSet<string> _trustedDeviceIds = new();
    private readonly object _lock = new();

    public string ServerId { get; }

    public PairingService(string configDirectory)
    {
        _configPath = Path.Combine(configDirectory, "trusted_devices.json");
        ServerId = LoadOrCreateServerId(configDirectory);
        _currentPin = GeneratePin();
        LoadTrustedDevices();
    }

    /// <summary>Generates a fresh 6-digit PIN. Call this on server start and optionally on a timer.</summary>
    public string GeneratePin()
    {
        lock (_lock)
        {
            int value = RandomNumberGenerator.GetInt32(0, 1_000_000);
            _currentPin = value.ToString("D6");
            return _currentPin;
        }
    }

    public string CurrentPin
    {
        get { lock (_lock) return _currentPin; }
    }

    public bool ValidatePin(string pin)
    {
        lock (_lock) return string.Equals(pin, _currentPin, StringComparison.Ordinal);
    }

    public bool IsTrusted(string deviceId)
    {
        lock (_lock) return _trustedDeviceIds.Contains(deviceId);
    }

    public void TrustDevice(string deviceId)
    {
        lock (_lock)
        {
            _trustedDeviceIds.Add(deviceId);
            SaveTrustedDevices();
        }
    }

    public void RevokeDevice(string deviceId)
    {
        lock (_lock)
        {
            _trustedDeviceIds.Remove(deviceId);
            SaveTrustedDevices();
        }
    }

    /// <summary>
    /// Builds the QR code payload as a compact JSON string: host, port, serverId and current PIN.
    /// The iOS app scans this and connects directly without the user typing anything.
    /// </summary>
    public byte[] GenerateQrCodePng(string host, int port)
    {
        string payload = $"remoteemu://pair?host={host}&port={port}&sid={ServerId}&pin={CurrentPin}";

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var pngQr = new PngByteQRCode(data);
        return pngQr.GetGraphic(10);
    }

    private string LoadOrCreateServerId(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        string idPath = Path.Combine(configDirectory, "server_id.txt");
        if (File.Exists(idPath))
            return File.ReadAllText(idPath).Trim();

        string id = Guid.NewGuid().ToString("N");
        File.WriteAllText(idPath, id);
        return id;
    }

    private void LoadTrustedDevices()
    {
        try
        {
            if (!File.Exists(_configPath)) return;
            string json = File.ReadAllText(_configPath);
            var ids = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(json) ?? new();
            foreach (var id in ids) _trustedDeviceIds.Add(id);
        }
        catch
        {
            // Corrupt file - start fresh rather than crashing the server.
        }
    }

    private void SaveTrustedDevices()
    {
        try
        {
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(_trustedDeviceIds.ToList());
            File.WriteAllText(_configPath, json);
        }
        catch
        {
            // Best-effort persistence; pairing still works for the current process lifetime.
        }
    }
}
