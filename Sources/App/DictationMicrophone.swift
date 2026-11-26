import AVFoundation
import Foundation

/// The microphone for one recording, handed on as the sixteen-bit, 16 kHz,
/// one-channel sound the speech service takes.
///
/// A new engine for every recording: one kept from the last would still be
/// set up for a microphone that has since been unplugged.
final class DictationMicrophone {
    enum Failure: Error {
        case noMicrophone
        case wontStart
    }

    static var permission: AVAuthorizationStatus {
        AVCaptureDevice.authorizationStatus(for: .audio)
    }

    static func askPermission(_ answered: @escaping (Bool) -> Void) {
        AVCaptureDevice.requestAccess(for: .audio) { granted in
            DispatchQueue.main.async { answered(granted) }
        }
    }

    private let engine = AVAudioEngine()
    private var observer: NSObjectProtocol?

    /// `heard` is called on the audio thread. `lost` says the microphone went
    /// away or changed under the recording, which stops the engine by itself.
    func start(heard: @escaping (Data) -> Void, lost: @escaping () -> Void) throws {
        let input = engine.inputNode
        let format = input.outputFormat(forBus: 0)
        guard format.sampleRate > 0, format.channelCount > 0,
              let wanted = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: DictationSound.sampleRate,
                                         channels: 1, interleaved: true),
              let converter = AVAudioConverter(from: format, to: wanted)
        else { throw Failure.noMicrophone }

        input.installTap(onBus: 0, bufferSize: 4096, format: format) { buffer, _ in
            let room = AVAudioFrameCount(Double(buffer.frameLength) * wanted.sampleRate / format.sampleRate) + 64
            guard let converted = AVAudioPCMBuffer(pcmFormat: wanted, frameCapacity: room) else { return }
            var handedOver = false
            var problem: NSError?
            let status = converter.convert(to: converted, error: &problem) { _, state in
                if handedOver {
                    state.pointee = .noDataNow
                    return nil
                }
                handedOver = true
                state.pointee = .haveData
                return buffer
            }
            guard status != .error, converted.frameLength > 0, let samples = converted.int16ChannelData
            else { return }
            heard(Data(bytes: samples[0], count: Int(converted.frameLength) * 2))
        }
        observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange,
                                                          object: engine, queue: nil) { _ in lost() }
        engine.prepare()
        do { try engine.start() } catch {
            stop()
            throw Failure.wontStart
        }
    }

    func stop() {
        if let observer { NotificationCenter.default.removeObserver(observer) }
        observer = nil
        engine.inputNode.removeTap(onBus: 0)
        engine.stop()
    }
}
