import SwiftUI
import UIKit

/// Transparent gesture-capture surface placed on top of the video image in StreamView.
/// Translates raw touch events into normalized (0...1) coordinates and forwards them through
/// InputSenderService. Built on a UIKit UIView subclass (via UIViewRepresentable) rather than
/// pure SwiftUI gestures because SwiftUI's gesture system does not expose simultaneous
/// multi-touch contacts with per-finger IDs, which real pinch-to-zoom and multi-finger swipes
/// require (SwiftUI's MagnificationGesture only reports a scale factor, not raw contact points).
struct ControlOverlayView: UIViewRepresentable {
    @EnvironmentObject var connection: ConnectionViewModel
    let emulatorId: String

    func makeUIView(context: Context) -> TouchCaptureView {
        let view = TouchCaptureView()
        view.onTouchEvent = { normalizedPoint, action, pointerId in
            switch action {
            case .tap:
                connection.inputSender.sendTap(emulatorId: emulatorId, normalizedPoint: normalizedPoint)
            case .doubleTap:
                connection.inputSender.sendDoubleTap(emulatorId: emulatorId, normalizedPoint: normalizedPoint)
            case .longPress:
                connection.inputSender.sendLongPress(emulatorId: emulatorId, normalizedPoint: normalizedPoint)
            case .dragBegan:
                connection.inputSender.sendDrag(emulatorId: emulatorId, normalizedPoint: normalizedPoint, phase: .began)
            case .dragChanged:
                connection.inputSender.sendDrag(emulatorId: emulatorId, normalizedPoint: normalizedPoint, phase: .changed)
            case .dragEnded:
                connection.inputSender.sendDrag(emulatorId: emulatorId, normalizedPoint: normalizedPoint, phase: .ended)
            }
        }
        view.onMultiTouchEvent = { points in
            connection.inputSender.sendMultiTouch(emulatorId: emulatorId, points: points)
        }
        return view
    }

    func updateUIView(_ uiView: TouchCaptureView, context: Context) {}
}

enum TouchAction {
    case tap, doubleTap, longPress, dragBegan, dragChanged, dragEnded
}

/// UIKit view that owns raw UITouch tracking, since it needs stable per-finger identity across
/// the lifetime of a multi-touch gesture (pinch-zoom, two-finger scroll) which SwiftUI gestures
/// don't expose directly.
final class TouchCaptureView: UIView {
    var onTouchEvent: ((CGPoint, TouchAction, Int) -> Void)?
    var onMultiTouchEvent: (([(id: Int, point: CGPoint, phase: TouchPhaseWire)]) -> Void)?

    private var touchIdentifiers: [UITouch: Int] = [:]
    private var nextTouchId = 0
    private var longPressTimers: [UITouch: Timer] = [:]
    private var lastTapTime: Date?
    private var lastTapLocation: CGPoint?

    override init(frame: CGRect) {
        super.init(frame: frame)
        isMultipleTouchEnabled = true
        backgroundColor = .clear
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            let id = nextTouchId
            nextTouchId += 1
            touchIdentifiers[touch] = id

            let normalized = normalizedPoint(for: touch)

            // Schedule a long-press timer; cancelled if the touch moves/ends before it fires.
            let timer = Timer.scheduledTimer(withTimeInterval: 0.5, repeats: false) { [weak self] _ in
                self?.onTouchEvent?(normalized, .longPress, id)
            }
            longPressTimers[touch] = timer
        }
        emitMultiTouchIfNeeded(touches: event?.allTouches ?? touches, phase: .began)
    }

    override func touchesMoved(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            longPressTimers[touch]?.invalidate()
        }
        emitMultiTouchIfNeeded(touches: event?.allTouches ?? touches, phase: .moved)

        // Single-finger drag also goes through the simpler touch API for responsiveness.
        if touches.count == 1, let touch = touches.first, (event?.allTouches?.count ?? 1) == 1 {
            let normalized = normalizedPoint(for: touch)
            onTouchEvent?(normalized, .dragChanged, touchIdentifiers[touch] ?? 0)
        }
    }

    override func touchesEnded(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            longPressTimers[touch]?.invalidate()
            longPressTimers[touch] = nil

            let normalized = normalizedPoint(for: touch)
            let id = touchIdentifiers[touch] ?? 0

            if (event?.allTouches?.count ?? 1) == 1 {
                if let lastTime = lastTapTime, let lastLoc = lastTapLocation,
                   Date().timeIntervalSince(lastTime) < 0.3,
                   distance(lastLoc, normalized) < 0.02 {
                    onTouchEvent?(normalized, .doubleTap, id)
                    lastTapTime = nil
                } else {
                    onTouchEvent?(normalized, .tap, id)
                    lastTapTime = Date()
                    lastTapLocation = normalized
                }
            }

            touchIdentifiers[touch] = nil
        }
        emitMultiTouchIfNeeded(touches: event?.allTouches ?? touches, phase: .ended)
    }

    override func touchesCancelled(_ touches: Set<UITouch>, with event: UIEvent?) {
        for touch in touches {
            longPressTimers[touch]?.invalidate()
            longPressTimers[touch] = nil
            touchIdentifiers[touch] = nil
        }
        emitMultiTouchIfNeeded(touches: event?.allTouches ?? touches, phase: .cancelled)
    }

    /// For 2+ simultaneous contacts (pinch/zoom, multi-finger swipe), emit the full multi-touch
    /// message instead of the simplified single-touch callbacks above.
    private func emitMultiTouchIfNeeded(touches: Set<UITouch>, phase: TouchPhaseWire) {
        guard touches.count >= 2 else { return }

        let points = touches.map { touch -> (id: Int, point: CGPoint, phase: TouchPhaseWire) in
            let id = touchIdentifiers[touch] ?? 0
            return (id: id, point: normalizedPoint(for: touch), phase: phase)
        }
        onMultiTouchEvent?(points)
    }

    private func normalizedPoint(for touch: UITouch) -> CGPoint {
        let location = touch.location(in: self)
        return CGPoint(x: location.x / max(bounds.width, 1), y: location.y / max(bounds.height, 1))
    }

    private func distance(_ a: CGPoint, _ b: CGPoint) -> CGFloat {
        sqrt(pow(a.x - b.x, 2) + pow(a.y - b.y, 2))
    }
}
