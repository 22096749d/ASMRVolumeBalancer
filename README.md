# 轻声 · ASMR Volume Balancer

Windows 本地 ASMR 音量均衡工具，当前版本 **2.1**。支持批量处理 MP4，并保留左右耳方向感。

## 功能

- 三种模式：以轻语为参考压低口腔音、以触发音为参考压低轻语、自动削强补弱。
- 每种模式均可选择温和、标准、较强力度；分别保存参考设置。
- 最长 300 秒（5 分钟）A/B 试听，可拖动进度条，暂停/继续，并在相同位置切换原音与处理后音频。
- 整体增益支持 -12 至 +12 dB。视频直接复制，处理后的音轨编码为 AAC。
- 原文件保留；同名输出自动加序号；支持停止处理和输出校验。

## 构建与运行

需要 Windows 10/11 64 位及 .NET Framework 4.8，无须 Python、CUDA 或模型。

1. 将 Windows 版 `ffmpeg.exe` 和 `ffprobe.exe` 放入项目根目录的 `bin` 文件夹。可使用 FFmpeg 官网列出的 [Gyan Windows 构建](https://www.gyan.dev/ffmpeg/builds/)。
2. 双击 `source/build.cmd` 构建程序。
3. 在项目根目录运行 `ASMRVolumeBalancer.exe`。

源码仓库不包含 FFmpeg 二进制文件或个人音视频。FFmpeg 构建版本及许可证详见 `licenses/`。

## 使用

添加 MP4 → 选择模式与力度 → 生成 A/B 试听 → 满意后批量处理。

模式 1 建议测量正常轻语参考；模式 2 建议测量较小触发音参考。试听长度填写 `300` 即为五分钟。进度条表示试听片段内的位置，切换 A/B 保留当前位置。

详细说明见 [使用说明](使用说明.html)。

## 实现与验证边界

本工具按音量统计、参考片段和时间响应工作，**不进行语义识别或声音分离**；同时重叠的声音会一起调整。自动模式设有补偿上限及峰值保护，真实录播仍需试听调节。

应用严格针对微软 .NET Framework 4.8 接口编译。音频及界面验证在 Linux/Mono 中执行；播放控制逻辑使用测试音频设备验证，Windows 原生播放设备仍需实机试听。验证摘要位于 `source/validation-v2.json` 与 `source/validation-v21.json`。

## 许可证

应用源码采用 MIT 许可证，见 [LICENSE](LICENSE)。独立运行的 FFmpeg 属于第三方软件，适用其自身许可证；原始许可及构建信息保留在 `licenses/`。

## 下载程序

[下载 Windows v2.1 程序包](../../raw/refs/heads/main/dist/ASMRVolumeBalancer_v2.1_App.zip)

程序包包含可双击运行的 EXE，**不含 FFmpeg**。已有用户解压覆盖原目录并保留 `bin` 文件夹；全新安装请将 Windows x64 的 `ffmpeg.exe` 和 `ffprobe.exe` 放入程序旁的 `bin` 文件夹。下载入口见下方构建说明。
