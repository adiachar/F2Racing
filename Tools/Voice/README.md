# Team radio voices

`make_radio.py` builds `Assets/Resources/Radio/radio.bytes`, the voice pack that `Assets/Scripts/Game/TeamRadio.cs`
plays during races (race engineer + the player's driver). The lines are in `radio_lines.py`; their ids must match
the `C` enum in `TeamRadio.cs`. `demo.ogg` is a few transmissions mixed by the game's own code (listen to check).

## Rebuild

```sh
pip install kokoro-onnx soundfile scipy --break-system-packages
mkdir -p ~/kokoro && cd ~/kokoro
curl -LO https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/kokoro-v1.0.onnx
curl -LO https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.0/voices-v1.0.bin
cd -; python3 make_radio.py --models ~/kokoro --preview /tmp/radio   # previews optional
```

Raw TTS takes are cached in `~/.cache/f2racing-radio`, so changing only the radio processing is fast.

## What it does

1. Speech: [Kokoro-82M](https://github.com/hexgrad/kokoro) neural TTS via kokoro-onnx. Engineer: `bm_george`
   (British male); driver: `am_michael`.
2. Radio sound, pre-processed here (no filters on the phone): trim, band-pass 300-3400 Hz with a presence lift,
   fast compressor, soft-clip distortion. The driver also gets in-car engine rumble and more drive. Hiss, the key-on
   chirp and the key-off squelch are separate clips that the game mixes round each transmission.
3. Storage: 11025 Hz mono IMA ADPCM (4 bits per sample), one `.bytes` file (~1.05 MB for 126 clips, 190 s).
   Unity cannot decode Ogg from bytes at runtime (and Safari has no Ogg for WebGL), so `TeamRadio.cs` decodes the
   ADPCM itself and upsamples to 22050 Hz. Same code path on Android and WebGL.

## Licences (all allow commercial use)

| Part | Licence | Used as |
| --- | --- | --- |
| Kokoro-82M model weights and voices (hexgrad) | Apache-2.0 | generates the audio offline |
| kokoro-onnx (thewh1teagle), ONNX export of the model | MIT | runs the model offline |
| phonemizer, espeak-ng (via espeakng-loader) | GPL-3.0 | offline tools only (text to phonemes); not shipped. GPL covers the programs, not the audio they output |
| The generated clips in `radio.bytes` | ours | shipped in the game |

Apache-2.0 asks for the notice to be kept when the model is redistributed; the game ships only generated audio, not
the model, but crediting "Voices generated with Kokoro-82M (Apache-2.0)" is good practice.

All lines are made up: no real teams, drivers or sponsors.

## Not used

- Piper TTS: its voice models are only on huggingface.co, which this build environment's network blocks.
- espeak-ng alone: robotic; only kept as Kokoro's phonemizer.
