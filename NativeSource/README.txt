NATIVE SPEECH ENGINE SOURCE AND LICENSES

Aurora 1.1 embeds the Windows amd64 CLI package from:
https://github.com/rhasspy/piper/releases/tag/2023.11.14-2

Source archives, build scripts and license text for the native speech engine
and phonemizer are provided alongside this executable. The source archives
retain their original directory structures and upstream build instructions.

piper-2023.11.14-2-source.zip: Piper CLI v1.2.0 release source, MIT-licensed.
piper-phonemize-source.zip: Piper phonemizer source at the pre-release commit
listed in phonemize-commit.txt, with its CMake build configuration.
espeak-ng-source.zip: eSpeak NG source at the pre-release commit listed in
espeak-commit.txt. eSpeak NG is licensed under GPLv3 or later; its full license
is in ESPEAK-GPL-LICENSE.txt and inside the source archive.

To rebuild on Windows, follow the upstream CMake and Windows build instructions
inside each archive with a C++17-capable MSVC compiler and CMake. Build/install
the supplied eSpeak NG source, then the supplied phonemizer source, then Piper.
Use CMake ESPEAK_NG_DIR and PIPER_PHONEMIZE_DIR to select those local builds
instead of downloading the current master branches. The phonemizer's CMake
file identifies ONNX Runtime 1.14.1 as its dependency. Piper's CMake file
identifies fmt 10.0.0 and spdlog 1.12.0 as its additional dependencies.
Original third-party code is unmodified. ONNX Runtime license and third-party
notices are included here. All source archives can also be obtained from the
upstream repository links retained in their build configurations.

Voice model weights are downloaded separately at the user's request, except
for voices installed locally during this user's requested setup. Their model
cards are stored beside the voice files in the user's Aurora voice library.
