# engines/

Runtime tools live here. Nothing in this folder except this README is committed.
The app looks here by default (next to the executable in published builds; this
folder during development via the `EnginesDirectory` setting or the
`W2XSG_ENGINES_DIR` environment variable).

Expected layout (Windows shown; on Linux/macOS drop `.exe`):

```
engines/
  waifu2x-ncnn-vulkan/
    waifu2x-ncnn-vulkan.exe
    models-cunet/ models-upconv_7_anime_style_art_rgb/ models-upconv_7_photo/
  realesrgan-ncnn-vulkan/
    realesrgan-ncnn-vulkan.exe
    models/            (realesr-animevideov3-x2/x3/x4, realesrgan-x4plus, realesrgan-x4plus-anime …)
  realcugan-ncnn-vulkan/
    realcugan-ncnn-vulkan.exe
    models-se/ models-pro/ models-nose/
  rife-ncnn-vulkan/
    rife-ncnn-vulkan.exe
    rife-v4.6/ …
  ffmpeg/              (optional; falls back to ffmpeg/ffprobe on PATH)
    ffmpeg.exe
    ffprobe.exe
```

Sources (release zips):
- https://github.com/nihui/waifu2x-ncnn-vulkan/releases
- https://github.com/xinntao/Real-ESRGAN-ncnn-vulkan/releases (or the realesrgan-ncnn-vulkan zip from https://github.com/xinntao/Real-ESRGAN/releases)
- https://github.com/nihui/realcugan-ncnn-vulkan/releases
- https://github.com/nihui/rife-ncnn-vulkan/releases
- https://ffmpeg.org/download.html
