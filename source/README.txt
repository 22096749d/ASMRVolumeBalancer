ASMR Volume Balancer 2.1.1 source
================================

Program.cs: Windows Forms UI, mode-specific references, queue and A/B playback.
AudioEngine.cs: Native-rate RMS analysis, filters, stream copy, validation and cancellation.
app.manifest / build.cmd: Windows .NET Framework build entry points.

Target: Microsoft .NET Framework 4.8. Build on Windows with build.cmd.
When building on Linux, explicitly reference Microsoft's net48 reference assemblies
(Microsoft.NETFramework.ReferenceAssemblies.net48 1.0.3), with -nostdlib -noconfig.
Do not use Mono runtime assemblies as the compilation target API surface.

Mode 1: Downward linked compression, fast response; user references normal whisper.
Mode 2: Downward linked compression, slower sustained envelope and higher transient
threshold; user references quiet trigger sounds. These are different reference and
response policies, not semantic classification or source separation.
Mode 3: Bidirectional linked dynaudnorm, estimated middle RMS target; maximum local
upward compensation 3 / 7 / 12 dB, with a near-silence threshold and smoothed frames.
All modes: separate -12..+12 dB overall gain, final limiter with auto-level disabled
and latency compensation enabled. No stereo-to-mono or independent-ear normalization.

Automatic reference samples up to six 15-second segments. Measurements use 100 ms RMS
windows at the original sample rate and the louder ear. Down modes choose a quiet-active
quantile; auto chooses the midpoint in dB of active 25th and 80th percentiles.
Reference measurement uses the active median of up to 20 user-selected seconds.
Processed previews include up to two seconds of pre-roll and trim back to the requested
start, so filter adaptation does not unnecessarily start at the audible boundary.

Video is copied, first audio track encoded to AAC 256 kbps, original channel count and
sample rate retained, and output validated before publication. All subprocess arguments
are quoted directly without a shell. FFmpeg is an unchanged independent executable.

Validation summary: validation-v2.json. Actual engine and UI checks ran under Linux/Mono
with native FFmpeg. Windows target API compatibility is verified by compilation against
Microsoft net48 references; no claim of testing real ASMR or a physical Windows machine.

Automatic mode also ties the lookahead peak ceiling to target RMS plus 16/14/12 dB
crest allowance. Very low ceilings use reversible scaling around the limiter so
its native minimum limit does not silently disable reference-linked peak control.

2.1.1 playback implementation (fixes the 2.1 MCI filename error):
PreviewPlayer.cs validates RIFF/WAVE PCM and reads it using managed Unicode/long-path
file I/O. WinMM waveOut receives only PCM buffers, never filenames or MCI commands.
WAVEFORMATEX, WAVEHDR and MMTIME have explicit native-compatible layouts.
One selected A/B WAV is buffered at a time (about 55 MiB for 300 sec/48k/stereo).
Seeking resets and unprepares the old header before submitting a block-aligned view
of the same PCM allocation. No re-rendering is required. Buffer memory remains valid
until the audio device releases it. Pause, resume and driver error paths are covered.
The shared timeline still uses the hardware-reported clock; TIME_BYTES, TIME_SAMPLES
and TIME_MS are supported. A/B variants preserve the shared millisecond position.
Temporary preview filenames are ASCII; parent directories can contain Unicode.
No new third-party runtime or player is required. Existing FFmpeg binaries are reused.

source/tests/WaveOutTests.cs: 24 focused regression checks with an injected WinMM
contract simulator that reads the real unmanaged PCM buffer. Includes Chinese/long
paths, 44.1k mono, 48k stereo, 300-second real WAV, repeated A/B/seek and cleanup errors.
The end-to-end UI suite passes under Mono/Xvfb; audio generation/muxing is unchanged.
Validation-v211.json records the verification boundary: no physical Windows sound
hardware was available. A mock driver is not a substitute for a Windows listening test.
