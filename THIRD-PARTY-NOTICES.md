# Third-party components

Hzip's own UI, orchestration and archive services are independent application code. Bandizip binaries, skin assets, activation data and SDK identifiers are not bundled or required by Hzip. Development interoperability tests may invoke an independently installed Bandizip; those tests are not part of the runtime.

## 7-Zip 26.04

Copyright (c) Igor Pavlov and the respective source contributors. The unmodified engine is distributed as a separate, replaceable component in engine/. Applicable terms include GNU LGPL 2.1 or later, BSD licenses and the unRAR restriction; see engine/License.txt and licenses/7zip-license.txt. Exact corresponding upstream source is included as licenses/7z2604-src.tar.xz. Upstream: https://github.com/ip7z/7zip/releases/tag/26.04 . Provenance and hashes accompany the distribution. The RAR decoder is not used to implement a RAR encoder.

## SharpZipLib 1.4.2 with Hzip compatibility extension

Copyright (c) SharpZipLib contributors; licensed under MIT. Hzip compiles this component from source into Hzip.ZipCodec.dll. A small, clearly marked change in Encryption/ZipAESTransform.cs allows task-scoped password encodings, implemented by the Hzip-owned ZipPasswordEncodingScope.cs. It preserves the upstream AES algorithm while permitting UTF-8 and legacy Windows code pages. Encoding choices are isolated by AsyncLocal; there is no global process-wide encoding switch.

The original upstream source archive and MIT license are in licenses/. The source distribution contains the exact modified sources and build project; the portable distribution additionally includes a matching Hzip.ZipCodec-source.zip. Original upstream: https://github.com/icsharpcode/SharpZipLib/tree/v1.4.2 . Hzip modifications are identified in source and have not been published under the unmodified upstream assembly name.

Hzip 0.2 additionally marks a small extension in Zip/ZipFile.cs exposing an authenticated, decrypted ZIP-LZMA stream. It keeps upstream encryption/HMAC handling and delegates method 14 decompression to the separately distributed 7-Zip engine, followed by size and applicable CRC checks. ZIP rewrites may recompress compatible entries with Deflate while preserving their verified plaintext content.

## Microsoft .NET 8 / WPF

The self-contained Windows distribution includes Microsoft .NET and Windows Desktop runtime components. Their licenses and third-party notices are preserved with the distribution. Upstream: https://github.com/dotnet/runtime and https://github.com/dotnet/wpf . The framework-dependent build uses the machine's installed runtime.

## Native context menu runtime

Hzip.Shell.dll is Hzip's own native COM implementation, built with LLVM/MinGW-w64. It statically links the compiler's C++ runtime components; the LLVM Apache 2.0 license with LLVM exceptions and applicable MinGW-w64 runtime notices are preserved in licenses/LLVM-LICENSE.txt and licenses/COPYING.MinGW-w64*.txt (plus the threading notice). The compiler and Windows SDK packaging tools are build dependencies and are not bundled as application runtime tools. The local-development context menu package is signed with a development certificate; its private key is not distributed.

## Test fixture sources

The source-only RAR fixtures come from the libarchive test corpus. Their source URLs, hashes and copying notice are included in tests/fixtures/. They are development data and are not part of the portable application.
