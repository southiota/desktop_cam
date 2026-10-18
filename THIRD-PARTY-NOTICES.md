# Third-party notices

Desktop CAM 은 아래 라이브러리 · 도구 · 데이터를 함께 쓰거나 배포합니다. 각 항목의 저작권은 해당 저작자에게 있습니다.

| 이름 | 쓰는 곳 | 라이선스 | 출처 |
| --- | --- | --- | --- |
| .NET 8 런타임 (배포 zip 에 포함) | 프로그램 실행 | MIT | https://github.com/dotnet/runtime |
| ScreenRecorderLib | 화면 녹화 (H.264 · MP4) | MIT | https://github.com/sskodje/ScreenRecorderLib |
| NAudio | 소리 장치 · 음량 · 모니터 | MIT | https://github.com/naudio/NAudio |
| Vosk (libvosk, Vosk.dll) | 자동스크롤 [AI] 음성인식 | Apache-2.0 | https://github.com/alphacep/vosk-api |
| vosk-model-small-ko-0.22 (처음 쓸 때 받음) | 한국어 음성 모델 | Apache-2.0 | https://alphacephei.com/vosk/models |
| FFmpeg, LGPL 빌드 (`tools/ffmpeg.exe`) | 마이크 다듬기 · 합치기 | LGPL (`tools/LICENSE.txt`) | https://ffmpeg.org · 빌드: https://github.com/BtbN/FFmpeg-Builds |
| RNNoise 모델 cb.rnnn (`tools/cb.rnnn`) | AI 잡음 제거 | 저작권 대상 아님 (출처 README) | https://github.com/GregorR/rnnoise-models |
| Press Start 2P | 픽셀 서체 | SIL OFL 1.1 (`licenses/OFL-PressStart2P.txt`) | https://fonts.google.com/specimen/Press+Start+2P |
| MinGW 런타임 (libgcc_s_seh-1, libstdc++-6, libwinpthread-1) | Vosk 가 씀 | GPL v3 + GCC 런타임 예외 · MIT/BSD | https://www.mingw-w64.org |

FFmpeg 은 따로 실행하는 프로그램(`tools/ffmpeg.exe`)으로만 부르며 Desktop CAM 에 링크하지 않습니다. 다른 FFmpeg 빌드로 바꿔 넣어도 됩니다.
