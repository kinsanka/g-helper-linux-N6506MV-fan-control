# G-Helper Linux — ASUS Vivobook Pro 15 N6506MV 风扇控制

基于 [utajum/g-helper-linux](https://github.com/utajum/g-helper-linux) 的机型专用 fork，保留上游功能与提交历史，增加 N6506MV 风扇控制和完整校准。

- 版本：`1.0.97-n6506.5`；开发分支：`main`。
- 已实测：ASUS Vivobook Pro 15 N6506MV、BIOS `N6506MV.313`、Core Ultra 9 185H、RTX 4060 Laptop、Bazzite Linux。
- 专用后端严格检查机型、BIOS、控制表版本18和双风扇，其他机型不启用该后端。

## 功能

- CPU/GPU风扇曲线以目标RPM编辑，读取实际转速并反馈调节。
- 上方选择静音、平衡或增强模式，下方只显示该模式的曲线；曲线和启用状态分别保存。
- 所有模式共用硬件校准表，各模式的温度—转速曲线独立。
- 完整校准：双风扇先设为0%，先CPU再GPU，各自从0%递增到100%，另一只保持0%；每档写入后等待1秒再读取，共202个实测点。
- 每只到100%后，额外等待至少8秒，取四次稳定读数的中位数作为最高转速。
- 校准期间临时关闭CPU睿频，完成、取消或中止后恢复原状态，不改动保存的模式设置。
- 校准表保存到`~/.config/ghelper/n6506-fan-calibration.json`，重启继续使用，无过期时间；只有下次完整成功校准才替换，上一份另存`.bak`。

一次实机校准测得CPU约7927RPM、GPU约6196RPM，并验证重启读取。这些数字仅代表该台机器的本次测量，每台机器应单独校准。每档一秒响应可能包含转速惯性，原始读数如实保存；日常控制使用校准映射和转速反馈修正。

## 使用与边界

打开“风扇 + 功率”，选择模式、编辑曲线、勾选该模式的启用开关。点击“校准”执行完整测量，约4–6分钟，可随时取消。

校准会暂时停扇，仅在空闲时执行。开始要求CPU/GPU低于65°C；达到75°C、控制量不符、切换模式或通信异常会中止并恢复高风量。普通曲线保留非零最低控制量；辅助进程还检查超时与心跳。

**关闭曲线或退出程序，目前保持已验证的高风量，尚未完成可靠的固件自动温控交接。** 专用后端不写风扇模式寄存器，不使用此前在该机器上导致停扇的模式0。真正休眠/唤醒尚未完成实机验证。

更新检测指向本fork的GitHub Releases，按当前运行格式下载对应文件，校验SHA256后替换，并保留上一版`.bak`；不会用上游原版覆盖本机型功能。CPU降压选项出现不代表固件实际支持；185H的负电压偏移尚未验证。功耗滑块显示也不应替代硬件读回。

## 下载与构建

推荐下载[最新版本](https://github.com/kinsanka/g-helper-linux-N6506MV-fan-control/releases/latest)中的`GHelper-N6506MV-x86_64.AppImage`。AppImage是Linux应用格式，无需解压或安装.NET运行时。

下载后在文件属性中允许执行，双击启动；也可运行：

```bash
chmod +x GHelper-N6506MV-x86_64.AppImage
./GHelper-N6506MV-x86_64.AppImage
```

首次启动会将程序安装到`~/Applications/`并添加应用菜单入口，原有配置与校准表继续使用。使用普通用户运行，按应用提示安装硬件辅助程序；启动前退出其他G-Helper或风扇控制程序。发布页另提供原生单文件`ghelper`供手动安装，`SHA256SUMS`用于校验下载文件。应用内更新检查只使用本仓库的发布包，更新完成后点击重启。

AppImage需要系统支持FUSE；此版本已在Bazzite实机验证。

源码构建需要.NET10 SDK及上游原生构建依赖：

```bash
./build.sh
./dist/ghelper
```

依赖和上游通用功能见[原项目说明](docs/README.upstream.md)，其中原项目的下载、安装和更新链接仍指向上游。

验证：

```bash
dotnet run -c Debug --project tests/GHelper.Linux.Tests
cc -O2 -Wall -Wextra -Werror -DN6506_TEST vendor/gpu-helper/n6506_fan.c -o /tmp/n6506-helper-tests
/tmp/n6506-helper-tests
python3 tests/n6506-helper-protocol.py /tmp/n6506-helper-tests
```

169项C#测试通过（包括安装位置、更新版本、校验和、文件替换与配置保留）；原生模拟测试覆盖普通零控制量拒绝、顺序归零、其他通道保持0、退出恢复高风量、独立档位截止时间和失联保护。模拟测试不会访问真实风扇。

## 代码与许可

- 上游基础提交：`cf0633476ec4d74ecdcc0e984df7143ff8db9abd`。
- [技术说明与配置格式](docs/n6506-fans.md)。
- 原项目及修改沿用[GPL-3.0](LICENSE)，保留上游作者归属。
- 仓库包含源码，不包含个人配置、密码、登录凭据、本机校准快照或编译中间文件。
