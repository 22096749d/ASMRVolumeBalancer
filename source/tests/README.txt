Playback regression checks (no external packages needed)
From a Windows Developer Command Prompt, in this directory:
  csc /nologo /out:WaveOutTests.exe ../PreviewPlayer.cs WaveOutTests.cs
  WaveOutTests.exe test-output optional-300-second-pcm.wav
Without the optional WAV, 23 checks run. With a valid 300-second WAV, 24 run.
The test injects a WinMM contract simulator and reads actual unmanaged PCM memory.
It does not test real Windows speakers, driver P/Invoke, or audible output.
Synthetic WAV fixtures are generated locally; test-output can be removed afterwards.
