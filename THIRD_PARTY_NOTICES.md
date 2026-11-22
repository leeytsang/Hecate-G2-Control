# Third-party notices

This project uses the following third-party packages. The package versions are
declared in the project files and are restored from NuGet.

## Runtime

| Package | License | Project |
|---|---|---|
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Desktop | MIT | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Themes.Fluent | MIT | https://github.com/AvaloniaUI/Avalonia |
| CommunityToolkit.Mvvm | MIT | https://github.com/CommunityToolkit/dotnet |
| SkiaSharp (transitive) | MIT | https://github.com/mono/SkiaSharp |
| HarfBuzzSharp (transitive) | MIT | https://github.com/mono/SkiaSharp |
| MicroCom.Runtime (transitive) | MIT | https://github.com/AvaloniaUI/Avalonia |
| Tmds.DBus.Protocol (transitive) | MIT | https://github.com/tmds/Tmds.DBus |
| Avalonia.Angle.Windows.Natives (transitive) | BSD-style (ANGLE) | https://chromium.googlesource.com/angle/angle/ |

Avalonia's Windows renderer may include the ANGLE native runtime. Its BSD-style license and copyright notices are included in [THIRD_PARTY_NOTICES/ANGLE.txt](THIRD_PARTY_NOTICES/ANGLE.txt); keep this notice with binary redistributions.

The application also uses Windows Core Audio, Windows Bluetooth/WinRT APIs,
Windows Registry APIs, and the Microsoft .NET runtime. These are platform
components and are not redistributed as source code by this project.

## Tests only

| Package | License |
|---|---|
| xUnit | Apache-2.0 |
| xUnit runner for Visual Studio | Apache-2.0 |
| Microsoft.NET.Test.Sdk | MIT |
| coverlet.collector | MIT |

Test-only packages are not part of the published application runtime.

The authoritative license text for each NuGet package is included in its
package metadata and/or the package's repository. This notice is informational
and does not replace those licenses.
