import UIKit
import AVFoundation

/// Standard AVCaptureMetadataOutput-based QR scanner. Requires NSCameraUsageDescription in
/// Info.plist ("Wird benötigt, um den Pairing-QR-Code deines PCs zu scannen.").
///
/// NOTE on the "app crashes as soon as I tap Scan QR code" symptom: the previous version called
/// AVCaptureDeviceInput(device:) / captureSession.startRunning() immediately, without ever
/// checking or explicitly requesting camera authorization first. The very first time iOS shows
/// the permission dialog this can work by accident, but it reliably crashes/hangs on: a second
/// install where permission was previously denied, TestFlight/sideloaded builds where the
/// automatic-prompt-on-first-use behaviour is unreliable, and iPads/Simulators with no camera at
/// all (AVCaptureDevice.default(for:) returns nil there, and the old code just silently failed,
/// which looks like "nothing happens" while the surrounding sheet still tries to render an empty
/// preview layer). This version explicitly checks/requests authorization, and shows a clear
/// on-screen message instead of a black screen or crash whenever the camera can't be used.
final class QrScannerViewController: UIViewController, AVCaptureMetadataOutputObjectsDelegate {
    var onScan: ((String) -> Void)?

    private let captureSession = AVCaptureSession()
    private var hasScanned = false
    private var previewLayer: AVCaptureVideoPreviewLayer?
    private let messageLabel = UILabel()

    override func viewDidLoad() {
        super.viewDidLoad()
        view.backgroundColor = .black
        setupMessageLabel()
        requestCameraAccessAndStart()
    }

    override func viewDidLayoutSubviews() {
        super.viewDidLayoutSubviews()
        previewLayer?.frame = view.bounds
    }

    private func setupMessageLabel() {
        messageLabel.translatesAutoresizingMaskIntoConstraints = false
        messageLabel.numberOfLines = 0
        messageLabel.textAlignment = .center
        messageLabel.textColor = .white
        messageLabel.font = .systemFont(ofSize: 16)
        messageLabel.isHidden = true
        view.addSubview(messageLabel)
        NSLayoutConstraint.activate([
            messageLabel.centerYAnchor.constraint(equalTo: view.safeAreaLayoutGuide.centerYAnchor),
            messageLabel.leadingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.leadingAnchor, constant: 24),
            messageLabel.trailingAnchor.constraint(equalTo: view.safeAreaLayoutGuide.trailingAnchor, constant: -24),
        ])
    }

    private func showMessage(_ text: String) {
        DispatchQueue.main.async { [weak self] in
            self?.messageLabel.text = text
            self?.messageLabel.isHidden = false
        }
    }

    /// Explicitly checks/requests camera authorization before touching AVCaptureSession at all.
    /// This is the safe pattern Apple recommends - never assume the OS will prompt for you.
    private func requestCameraAccessAndStart() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            setupCamera()
        case .notDetermined:
            AVCaptureDevice.requestAccess(for: .video) { [weak self] granted in
                if granted {
                    self?.setupCamera()
                } else {
                    self?.showMessage("Kamera-Zugriff wurde abgelehnt.\nBitte in den iOS-Einstellungen unter\nDatenschutz > Kamera erlauben.")
                }
            }
        case .denied, .restricted:
            showMessage("Kein Kamera-Zugriff.\nBitte in den iOS-Einstellungen unter\nDatenschutz > Kamera für RemoteEmuControl erlauben.")
        @unknown default:
            showMessage("Kamera-Status unbekannt.")
        }
    }

    private func setupCamera() {
        guard let device = AVCaptureDevice.default(for: .video) else {
            showMessage("Kein Kamera-Gerät gefunden (z. B. im Simulator gibt es keine Kamera).")
            return
        }

        let input: AVCaptureDeviceInput
        do {
            input = try AVCaptureDeviceInput(device: device)
        } catch {
            showMessage("Kamera konnte nicht geöffnet werden:\n\(error.localizedDescription)")
            return
        }

        captureSession.beginConfiguration()
        defer { captureSession.commitConfiguration() }

        guard captureSession.canAddInput(input) else {
            showMessage("Kamera-Eingang konnte nicht hinzugefügt werden.")
            return
        }
        captureSession.addInput(input)

        let metadataOutput = AVCaptureMetadataOutput()
        guard captureSession.canAddOutput(metadataOutput) else {
            showMessage("QR-Erkennung konnte nicht initialisiert werden.")
            return
        }
        captureSession.addOutput(metadataOutput)
        metadataOutput.setMetadataObjectsDelegate(self, queue: .main)

        // Only request the QR type if the session actually supports it on this device/output
        // combination - assigning an unsupported type here is a documented crash in AVFoundation.
        if metadataOutput.availableMetadataObjectTypes.contains(.qr) {
            metadataOutput.metadataObjectTypes = [.qr]
        } else {
            showMessage("Dieses Gerät unterstützt keine QR-Code-Erkennung.")
            return
        }

        DispatchQueue.main.async { [weak self] in
            guard let self else { return }
            let layer = AVCaptureVideoPreviewLayer(session: self.captureSession)
            layer.frame = self.view.bounds
            layer.videoGravity = .resizeAspectFill
            self.view.layer.insertSublayer(layer, at: 0)
            self.previewLayer = layer
        }

        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            self?.captureSession.startRunning()
        }
    }

    func metadataOutput(_ output: AVCaptureMetadataOutput, didOutput metadataObjects: [AVMetadataObject], from connection: AVCaptureConnection) {
        guard !hasScanned,
              let object = metadataObjects.first as? AVMetadataMachineReadableCodeObject,
              let stringValue = object.stringValue else { return }

        hasScanned = true
        captureSession.stopRunning()
        onScan?(stringValue)
    }

    override func viewWillDisappear(_ animated: Bool) {
        super.viewWillDisappear(animated)
        if captureSession.isRunning {
            DispatchQueue.global(qos: .userInitiated).async { [weak self] in
                self?.captureSession.stopRunning()
            }
        }
    }
}
