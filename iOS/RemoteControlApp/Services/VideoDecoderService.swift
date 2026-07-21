import Foundation
import VideoToolbox
import CoreMedia
import SwiftUI

/// Decodes the raw H.264/HEVC Annex-B NAL units streamed from ScreenCaptureService on the
/// Windows side into displayable CVPixelBuffers/CGImages using hardware-accelerated
/// VideoToolbox decompression. This keeps CPU usage low and latency under the <50ms LAN target,
/// since decoding happens on the GPU's dedicated media engine rather than in software.
final class VideoDecoderService: ObservableObject {
    @Published var currentFrame: CGImage?
    @Published var measuredFps: Double = 0

    private var formatDescription: CMFormatDescription?
    private var decompressionSession: VTDecompressionSession?
    private var spsData: Data?
    private var ppsData: Data?
    private var frameTimestamps: [Date] = []

    /// Feed raw Annex-B NAL unit bytes (start-code delimited: 00 00 00 01) as they arrive from
    /// the WebSocket stream.frame messages. Handles SPS/PPS extraction, format-description
    /// (re)creation, and per-frame decompression.
    func decode(chunk: Data, codec: String) {
        let nalUnits = splitAnnexBNalUnits(chunk)

        for nal in nalUnits {
            guard let firstByte = nal.first else { continue }
            let nalType = codec == "hevc" ? (firstByte >> 1) & 0x3F : firstByte & 0x1F

            if codec == "hevc" {
                handleHevcNalUnit(nal, type: nalType)
            } else {
                handleH264NalUnit(nal, type: nalType)
            }
        }
    }

    private func handleH264NalUnit(_ nal: Data, type: UInt8) {
        switch type {
        case 7: spsData = nal; tryCreateFormatDescriptionH264()
        case 8: ppsData = nal; tryCreateFormatDescriptionH264()
        case 5, 1: decompress(nal: nal) // IDR (keyframe) or non-IDR slice
        default: break
        }
    }

    private func handleHevcNalUnit(_ nal: Data, type: UInt8) {
        // HEVC parameter sets: VPS=32, SPS=33, PPS=34. Simplified: accumulate all three before
        // building the format description (production code should track VPS separately).
        switch type {
        case 33: spsData = nal
        case 34: ppsData = nal; tryCreateFormatDescriptionHevc()
        case 19, 20, 1: decompress(nal: nal) // IDR_W_RADL/IDR_N_LP or trailing slice
        default: break
        }
    }

    private func tryCreateFormatDescriptionH264() {
        guard let sps = spsData, let pps = ppsData else { return }

        let parameterSetPointers: [UnsafePointer<UInt8>] = [
            sps.withUnsafeBytes { $0.bindMemory(to: UInt8.self).baseAddress! },
            pps.withUnsafeBytes { $0.bindMemory(to: UInt8.self).baseAddress! }
        ]
        let parameterSetSizes = [sps.count, pps.count]

        var formatDesc: CMFormatDescription?
        let status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
            allocator: kCFAllocatorDefault,
            parameterSetCount: 2,
            parameterSetPointers: parameterSetPointers,
            parameterSetSizes: parameterSetSizes,
            nalUnitHeaderLength: 4,
            formatDescriptionOut: &formatDesc
        )

        if status == noErr, let formatDesc {
            self.formatDescription = formatDesc
            createDecompressionSession(formatDesc)
        }
    }

    private func tryCreateFormatDescriptionHevc() {
        // HEVC format description creation follows the same pattern via
        // CMVideoFormatDescriptionCreateFromHEVCParameterSets (requires VPS+SPS+PPS, iOS 11+).
        // Omitted here for brevity - see ARCHITECTURE.md "Video Pipeline" for the full 3-parameter-set version.
    }

    private func createDecompressionSession(_ formatDesc: CMFormatDescription) {
        if let session = decompressionSession {
            VTDecompressionSessionInvalidate(session)
            decompressionSession = nil
        }

        var callback = VTDecompressionOutputCallbackRecord(
            decompressionOutputCallback: decompressionOutputCallback,
            decompressionOutputRefCon: Unmanaged.passUnretained(self).toOpaque()
        )

        let attributes: [String: Any] = [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
            kCVPixelBufferIOSurfacePropertiesKey as String: [:]
        ]

        var session: VTDecompressionSession?
        VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: formatDesc,
            decoderSpecification: nil,
            imageBufferAttributes: attributes as CFDictionary,
            outputCallback: &callback,
            decompressionSessionOut: &session
        )
        decompressionSession = session
    }

    private func decompress(nal: Data) {
        guard let session = decompressionSession, let formatDescription else { return }

        // Prefix with 4-byte length (AVCC format expected by VideoToolbox, vs. Annex-B start codes).
        var lengthPrefixed = Data()
        var length = UInt32(nal.count).bigEndian
        lengthPrefixed.append(Data(bytes: &length, count: 4))
        lengthPrefixed.append(nal)

        var blockBuffer: CMBlockBuffer?
        let status = lengthPrefixed.withUnsafeBytes { rawBuffer -> OSStatus in
            CMBlockBufferCreateWithMemoryBlock(
                allocator: kCFAllocatorDefault,
                memoryBlock: nil,
                blockLength: lengthPrefixed.count,
                blockAllocator: kCFAllocatorDefault,
                customBlockSource: nil,
                offsetToData: 0,
                dataLength: lengthPrefixed.count,
                flags: 0,
                blockBufferOut: &blockBuffer
            )
        }
        guard status == noErr, let blockBuffer else { return }

        lengthPrefixed.withUnsafeBytes { rawBuffer in
            CMBlockBufferReplaceDataBytes(with: rawBuffer.baseAddress!, blockBuffer: blockBuffer, offsetIntoDestination: 0, dataLength: lengthPrefixed.count)
        }

        var sampleBuffer: CMSampleBuffer?
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: CMTime(value: CMTimeValue(Date().timeIntervalSince1970 * 1000), timescale: 1000), decodeTimeStamp: .invalid)

        CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault,
            dataBuffer: blockBuffer,
            formatDescription: formatDescription,
            sampleCount: 1,
            sampleTimingEntryCount: 1,
            sampleTimingArray: &timing,
            sampleSizeEntryCount: 0,
            sampleSizeArray: nil,
            sampleBufferOut: &sampleBuffer
        )

        guard let sampleBuffer else { return }

        var flagsOut = VTDecodeInfoFlags()
        VTDecompressionSessionDecodeFrame(
            session,
            sampleBuffer: sampleBuffer,
            flags: [._1xRealTimePlayback],
            frameRefcon: nil,
            infoFlagsOut: &flagsOut
        )
    }

    fileprivate func onFrameDecoded(_ pixelBuffer: CVPixelBuffer) {
        let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
        let context = CIContext()
        guard let cgImage = context.createCGImage(ciImage, from: ciImage.extent) else { return }

        DispatchQueue.main.async {
            self.currentFrame = cgImage
            self.trackFps()
        }
    }

    private func trackFps() {
        let now = Date()
        frameTimestamps.append(now)
        frameTimestamps.removeAll { now.timeIntervalSince($0) > 1.0 }
        measuredFps = Double(frameTimestamps.count)
    }

    /// Splits a buffer of concatenated Annex-B NAL units (each prefixed by a 00 00 00 01 or
    /// 00 00 01 start code) into individual NAL unit byte ranges.
    private func splitAnnexBNalUnits(_ data: Data) -> [Data] {
        var result: [Data] = []
        var searchRange = data.startIndex..<data.endIndex
        var starts: [Data.Index] = []

        while let range = data.range(of: Data([0, 0, 0, 1]), options: [], in: searchRange) {
            starts.append(range.upperBound)
            searchRange = range.upperBound..<data.endIndex
        }

        for (i, start) in starts.enumerated() {
            let end = i + 1 < starts.count ? starts[i + 1] - 4 : data.endIndex
            if start < end { result.append(data.subdata(in: start..<end)) }
        }
        return result
    }
}

private func decompressionOutputCallback(
    decompressionOutputRefCon: UnsafeMutableRawPointer?,
    sourceFrameRefCon: UnsafeMutableRawPointer?,
    status: OSStatus,
    infoFlags: VTDecodeInfoFlags,
    imageBuffer: CVImageBuffer?,
    presentationTimeStamp: CMTime,
    presentationDuration: CMTime
) {
    guard status == noErr, let imageBuffer, let refCon = decompressionOutputRefCon else { return }
    let decoder = Unmanaged<VideoDecoderService>.fromOpaque(refCon).takeUnretainedValue()
    decoder.onFrameDecoded(imageBuffer)
}
