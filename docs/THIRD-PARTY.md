# Third-party licenses (engines, models, tools)

Checked 2026-11-03 against upstream repos. Full texts: [`licenses/`](licenses/) (in releases: `licenses/` next to this file).
**Result: every engine and model we use can be redistributed** — permissive licenses, ship the texts.

| Component | Used as | License | Text |
|---|---|---|---|
| waifu2x-ncnn-vulkan (nihui) | engine exe | MIT | `waifu2x-ncnn-vulkan.MIT.txt` |
| waifu2x models (cunet, upconv_7) — nagadomi/waifu2x | models | MIT | `waifu2x-models.MIT.txt` |
| Real-ESRGAN-ncnn-vulkan (xinntao; incl. realsr-ncnn-vulkan) | engine exe | MIT | `realesrgan-ncnn-vulkan.MIT.txt` |
| Real-ESRGAN models (x4plus, x4plus-anime, animevideov3) | models | BSD-3-Clause | `realesrgan-models.BSD-3-Clause.txt` |
| realcugan-ncnn-vulkan (nihui) | engine exe | MIT | `realcugan-ncnn-vulkan.MIT.txt` |
| Real-CUGAN models (se/pro/nose) — bilibili/ailab | models | MIT | `realcugan-models.MIT.txt` |
| rife-ncnn-vulkan (nihui) | engine exe | MIT | `rife-ncnn-vulkan.MIT.txt` |
| RIFE models v1.5–v4.6 — hzwer ECCV2022-RIFE / Practical-RIFE | models | MIT | `rife-models-*.MIT.txt` |
| ncnn (Tencent), linked into every engine | library | BSD-3 (+zlib/BSD-2 parts) | `ncnn.BSD-3-Clause.txt` |
| libwebp, linked into every engine | library | BSD-3 + patent grant | `libwebp.*.txt` |
| glslang (via ncnn) | library | BSD-3/MIT/Apache-2, Bison exception | `glslang.txt` |
| `vcomp140.dll` (MSVC OpenMP runtime) | DLL next to engines | Microsoft VC++ redistributable | — (Visual Studio "Distributable Code") |

Notes when bundling:
- The Real-ESRGAN Windows zip ships **no license file** → we must add both its texts ourselves.
- Do **not** ship from that zip: `vcomp140d.dll` (debug runtime, not redistributable) or the sample
  `input*.jpg` / `onepiece_demo.mp4` (third-party copyrighted content).
- **ffmpeg** is the only non-permissive part. Releases ship BtbN's shared ffmpeg 8.1 **GPL** build (keeps
  x264/x265) as separate executables we only call: license text in `licenses/ffmpeg.GPL.txt`, exact source
  links + SHA-256 in `SOURCES.txt` (`build/package.cs --ffmpeg lgpl` builds an LGPL package instead).

App (in the executable / next to it):

| Component | License | Text |
|---|---|---|
| Avalonia | MIT | `avalonia.MIT.txt` |
| SkiaSharp / Skia (`libSkiaSharp`) | MIT / BSD-3 | `skiasharp.MIT.txt`, `skia.BSD-3-Clause.txt` |
| HarfBuzzSharp / HarfBuzz (`libHarfBuzzSharp`) | MIT / Old MIT | `harfbuzz.txt` |
| ANGLE (`av_libglesv2.dll`, Windows) | BSD-3 | `angle.BSD-3-Clause.txt` |
| CommunityToolkit.Mvvm | MIT | `communitytoolkit.MIT.txt` |
| .NET runtime (AOT-compiled in) | MIT + notices | `dotnet-runtime.*.txt` |
| Inter font | OFL-1.1 | `inter-font.OFL-1.1.txt` |
