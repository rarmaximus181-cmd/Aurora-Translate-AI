# More voices in Aurora Translate

Open **Settings → Find your voice**. Download a built-in Piper voice, then choose it in the speaking-voice selector. The AI conversation and translated text use the same selector. **Automatic** chooses an installed voice for the current language. Alba and Ryan are already installed on this PC.

There are 12 extra voices: Amy, Lessac, Ryan, Alba, Alan, Davefx, Siwis, Thorsten, Faber, Paola, Irina, and Huayan. They cover US/UK English, Spanish, French, German, Brazilian Portuguese, Italian, Russian, and Chinese. Each download is about 61 MB. Internet is needed for the download; after that, speech is generated on your PC with no key or speech-service quota.

## Import a downloaded voice

1. Download a **Piper ONNX voice model** and its matching JSON configuration. For example, `en_US-amy-medium.onnx` and `en_US-amy-medium.onnx.json`.
2. Click **Import voice model** and choose the `.onnx` model. Aurora finds its matching configuration in the same folder; if needed, it prompts you to choose the JSON file.
3. The imported voice is selected automatically. You can change the speaking voice using the selector above.

Aurora copies both files into its own voice library, so moving the original downloads later will not break the imported voice. The language is read from the model's configuration. Choose a voice that matches the text's language; an English voice cannot read translations correctly in every other language. Multispeaker models use their default speaker (speaker 0).

Only Piper `.onnx` models up to 500 MB and matching JSON files up to 2 MB are supported. MP3/WAV recordings, ZIP packages, RVC weights, XTTS models, and `.pth` files cannot be imported. The voice model adds text-to-speech; microphone recognition still uses your Windows speech language.

The official voice repository and individual model cards are available at [rhasspy/piper-voices](https://huggingface.co/rhasspy/piper-voices). Each downloaded voice keeps its `MODEL_CARD` and source information in the app's voice folder. Licenses vary by voice, so review the model card before redistributing a model or using it commercially. For example, Ryan's card lists a noncommercial license; some cards link to separate terms or report an unknown license. This app does not bundle those voice weights.

The bundled speech engine is the archived [Piper Windows release 2023.11.14-2](https://github.com/rhasspy/piper/releases/tag/2023.11.14-2). Newer Piper releases use a different license and are not substituted automatically.

## Where voices are stored

Voice files are in `%LOCALAPPDATA%\AuroraTranslate\Voices`. Removing a voice in Settings deletes that imported copy. Your original downloaded files remain available. The app runs its own bundled engine; importing a voice never runs an executable from the downloaded model folder.
