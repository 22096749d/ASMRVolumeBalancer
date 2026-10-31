ASMR Volume Balancer 2.1 source
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

2.1 playback implementation:
PreviewPlayer.cs adds a validated RIFF/WAVE PCM reader, shared A/B timeline
controller, and Windows MCI waveform-audio device (winmm.dll Unicode APIs).
Native playback streams the existing preview; seeking does not regenerate
or copy five-minute WAV files. Backend is loaded only when playback starts.
The controller uses audio-device positions, not a stopwatch approximation.
Preview generation pads/trims both variants to equal sample counts and
uses a continuous sample-based preview clock; batch video muxing is unchanged.
Source build.cmd includes PreviewPlayer.cs. No new third-party runtime is required.
Validation-v21.json distinguishes real audio-file/UI tests from mock audio
device tests; native Windows MCI calls have not run in this Linux environment.
