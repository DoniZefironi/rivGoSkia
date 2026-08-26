if ($env:VSCMD_ARG_TGT_ARCH -ne "x64") {
    Import-Module "C:\Program Files\Microsoft Visual Studio\18\Insiders\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
    Enter-VsDevShell -InstallPath "C:\Program Files\Microsoft Visual Studio\18\Insiders" `
                     -DevCmdArguments "-arch=x64 -host_arch=x64" -SkipAutomaticLocation
}
if ($env:VSCMD_ARG_TGT_ARCH -ne "x64") { throw "нужно x64-окружение" }

$rive = "$HOME\source\repos\rive-runtime"
$out  = "$rive\tests\out\release"
$deps = "$rive\tests\dependencies"

$defines = @(
  "/D","WITH_RIVE_TOOLS","/D","WITH_RIVE_TEXT","/D","RIVE_CANVAS",
  "/D","WITH_RIVE_LAYOUT","/D","RIVE_ORE","/D","RELEASE","/D","NDEBUG",
  "/D","NOMINMAX","/D","RIVE_WINDOWS","/D","_USE_MATH_DEFINES",
  "/D","_HAS_EXCEPTIONS=0","/D","YOGA_EXPORT="
)

$includes = @(
  "/I","$rive\include", "/I","$rive\dependencies", "/I","$rive\renderer\include",
  "/I","$rive\decoders\include",
  "/I","$deps\rive-app_harfbuzz_rive_13.1.1\src",
  "/I","$deps\Tehreer_SheenBidi_v2.6\Headers",
  "/I","$deps\rive-app_miniaudio_rive_changes_5",
  "/I","$deps\rive-app_yoga_rive_changes_v2_0_1_2_grid"
)

$forced = @("/FIrive_png_renames.h","/FIrive_harfbuzz_renames.h","/FIrive_yoga_renames.h")

$libs = @(
  "$out\rive.lib","$out\rive_harfbuzz.lib","$out\rive_sheenbidi.lib",
  "$out\rive_yoga.lib","$out\rive_decoders.lib","$out\libpng.lib",
  "$out\zlib.lib","$out\libjpeg.lib","$out\libwebp.lib","$out\miniaudio.lib"
)

$clArgs = @("/LD","/O2","/std:c++17","/MT","/GR-","/EHs-c-") +
          $defines + $includes + $forced +
          @("rive_shim.cpp", "$rive\utils\no_op_factory.cpp", "/Fe:rive_shim.dll", "/link") +
          $libs

cl @clArgs