# Third-party prerequisites and attribution

No vendored third-party implementation or external NuGet PackageReference was
detected in the selected first-party projects. Internal ProjectReferences are
included. This source review does not independently prove authorship of every
snippet. The rights holder authorized licensing the Soligent code Apache-2.0.

The .NET SDK/runtime and framework reference packs are external prerequisites,
not bundled or relicensed. Their upstream source uses MIT and accompanying
third-party notices:
https://github.com/dotnet/runtime/blob/main/LICENSE.TXT
https://github.com/dotnet/sdk/blob/main/LICENSE.TXT

The native bootstrap host uses Windows APIs (including bcrypt) and links the
Microsoft C++ runtime through a separately licensed installed MSVC toolchain.
Visual Studio/Windows SDK tools, CMake and Ninja retain their licenses. They
are not included. Do not infer redistribution rights for those prerequisites
from this repository's license.
https://visualstudio.microsoft.com/license-terms/vs2022-ga-proenterprise/
https://gitlab.kitware.com/cmake/cmake/-/blob/master/Copyright.txt
https://github.com/ninja-build/ninja/blob/master/COPYING

Studio/Avalonia, VSIX dependencies, website fonts/images and token-platform
work are outside this snapshot. They receive no Apache relicensing here.
