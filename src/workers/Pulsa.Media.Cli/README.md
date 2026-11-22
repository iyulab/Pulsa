# PulsaMedia.Cli

Command line for Pulsa's ffmpeg module ([`PulsaMedia`](https://www.nuget.org/packages/PulsaMedia)) and caption
drafting: run the capabilities without a consuming application.

```
PulsaMedia.Cli compose --images <path...> --captions <text...> --scene-duration <seconds> --output <path> --ffmpeg-dir <dir>
      [--aspect-ratio 16:9|9:16] [--title-image <path> --title-text <text>] [--outro-image <path> --outro-text <text>]
      [--font <font name>] [--fonts-dir <dir>]
PulsaMedia.Cli draft-captions --images <path...> --intro <text> --openai-key <key> [--openai-model <model>]
PulsaMedia.Cli redact --input <path> --output <path> --region <x>,<y>,<width>,<height>
      [--start <seconds>] [--end <seconds>] [--ffmpeg-dir <dir>]
```

Contracts and the changelog: [github.com/iyulab/Pulsa](https://github.com/iyulab/Pulsa).
