$ErrorActionPreference = 'Stop'
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Install Visual Studio with Desktop development with C++ and CMake tools.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
$native = Join-Path $PSScriptRoot 'native'
$build = Join-Path $PSScriptRoot 'build\native'
$dist = Join-Path $PSScriptRoot 'dist'
& cmd.exe /d /c "call `"$vcvars`" && `"$cmake`" -S `"$native`" -B `"$build`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_MAKE_PROGRAM=`"$ninja`" && `"$cmake`" --build `"$build`""
if ($LASTEXITCODE) { throw 'Native YAML helper build failed.' }
& dotnet publish (Join-Path $PSScriptRoot 'AoTPresetEditor.csproj') -c Release -r win-x64 --self-contained true -o $dist
if ($LASTEXITCODE) { throw 'Desktop app publish failed.' }
Copy-Item -LiteralPath (Join-Path $build 'AoTYaml.exe') -Destination $dist
Copy-Item -LiteralPath (Join-Path $build 'licenses') -Destination $dist -Recurse -Force
Write-Host "Ready: $dist\AoTPresetEditor.exe"
