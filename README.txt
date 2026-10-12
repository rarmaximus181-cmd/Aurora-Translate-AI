AURORA TRANSLATE
===============

Version 2: redesigned AI workspace, stronger model, startup animation and updates.
Open the Aurora Translate Desktop shortcut, or AuroraTranslate.exe.
Close the previous app window before reopening the updated version.
No translation API key is needed.
The executable includes its Windows .NET runtime.

TRANSLATE
Choose the language you are writing in and the language you want, type your
text, and click Translate. Ctrl+Enter also translates. Swap switches languages.
Copy copies the result. Mic records one spoken phrase. Speaker buttons read
text aloud when Windows has a matching voice installed.

FREE ONLINE AND LOCAL TRANSLATION
Settings > Translation engine switches between MyMemory online and local AI.
Online sends your text to MyMemory over HTTPS and requires internet. Anonymous
MyMemory usage is limited to approximately 5,000 characters per day, shared by
the service's usage rules. Online translation quality/availability varies by
language pair. There is no paid key hidden in this app.
https://mymemory.translated.net/doc/usagelimits.php

Local AI translation uses Ollama running on this PC and has no service quota
or API key. Ollama and an installed model are needed. Settings can refresh the
model list and download a model after you click Download model. Models can be
large. qwen2.5:1.5b is a compact starter model; quality varies across languages.
An Ollama installation and this model were already present on the build PC.
On another computer, install Ollama from https://ollama.com/download/windows
and download a model through Settings. Internet is needed to download models;
local translation/chat work offline after download while Ollama is running.

AI CONVERSATION AND VOICE
Open Conversation to chat with the local AI. Choose a conversation language.
Type and Send, or press the mic to speak one phrase and send it. AI replies
stream into the chat and can be spoken automatically. You can stop speech or
cancel a request. The microphone is used only when you press its button.
Use headphones if the microphone picks up speaker playback.

The model selector and response selector are now on the conversation toolbar.
Qwen3.5:4b is the larger model installed on this PC; qwen2.5:1.5b remains a
lighter choice. Advanced conversation controls offer model reasoning (slower,
more thorough answers) and Troll display. The AI also has a bounded local
calculator for arithmetic; it cannot execute programs or system commands.
Troll display visibly changes the literal phrase "this is not allowed" to
"this is allowed" as a joke. Show original reveals the real answer. Conversation
memory, Copy original, and spoken replies always use the model's original text.

STARTUP AND GITHUB UPDATES
Aurora 2 shows an animated startup before opening your workspace. Turning off
theme animation also uses a static startup. The app checks the official GitHub
release. A newer required release opens an install screen; the app verifies its
download before replacing the EXE and restarting. See UPDATES.md. Builds1.0/1.1
need a manual upgrade once because they did not contain this updater.

Speaking uses Windows voices or downloaded/imported Piper voice models.
Settings > Voice offers 12 additional voice downloads and Preview voice.
Alba (UK English) and Ryan (US English) are already installed on this PC.
Import a Piper .onnx voice with its matching .onnx.json configuration; Aurora
copies both files into your user profile and tests the model before saving it.
Choose that voice in the speaking voice list. It speaks AI replies and
translations when their language matches. See CUSTOM-VOICES.md for details.
Downloads need internet; installed voice models speak offline without a key.
Each model has its own usage terms linked from Voice info and saved model cards.

Microphone dictation still uses Windows System.Speech recognition languages.
Use Settings > Windows speech settings to add microphone language support.
Restart Aurora after installing it. English recognition is present on this PC.
Microphone capture and speaker playback still need a user hardware check.

THEMES
Settings offers Aurora, Nebula, Ocean, and Sunset themes with drifting lights.
Import a PNG or GIF to use your own background. Animated GIF playback is
supported. Imported images are copied into your user profile so the background
works even if you move the original. Adjust background visibility and turn
animation on/off in Settings. Very large images are rejected to keep the app
responsive (20 MB maximum, 4096 pixels per dimension, bounded GIF frame count).

DISCORD SIGN-IN AND PROFILE PICTURE
Every translation and chat feature works as a guest. Click your account in the
sidebar to jump to Discord setup. Create an application in the Discord Developer
Portal, enable Public Client under OAuth2, and register this exact redirect:
http://127.0.0.1:43821/callback
Paste its Application ID into Settings and save it, then Sign in with Discord.
The app uses your browser and reads only your Discord name and profile avatar.
Your image appears in the sidebar and Discord settings card. Default Discord
avatars are supported. Refresh Discord profile updates your photo and name.
No Discord password, user token, bot token, or client secret is imported.
Tokens are encrypted for your Windows account using DPAPI. Sign-out clears the
local session and cached profile image. Discord does not sync translation/chat
history. See DISCORD-SIGN-IN.md for the complete one-time setup.

SAVED DATA
Settings, copied themes, and up to 50 translations are stored in
%LOCALAPPDATA%\AuroraTranslate. Translation history is a local JSON file; turn
history saving off or clear it from History if you do not want it retained.
Voice model files and the bundled speech engine are stored in that same profile.
Discord profile images are cached locally. Chat messages are held only in
memory for the current session. Local chat text
is sent only to the Ollama service on 127.0.0.1:11434. No analytics are included.

SOURCE AND REBUILD
Source is in the AuroraTranslate project folder. With a Windows .NET 10 SDK:
dotnet publish AuroraTranslate.csproj -c Release -r win-x64 --self-contained true
The self-contained executable is in bin\Release\net10.0-windows\win-x64\publish.
Service tests: AuroraTranslate.exe --self-test --report C:\path\self-test.json
This checks both a live online translation and the installed local AI, so it
needs internet plus an active Ollama model for all integration checks to pass.
