# STS2 Telemetry Mod

这是 STS2 Telemetry 的公开 mod 源码导出，用来审阅客户端采集逻辑和隐私边界。

此公开仓库只包含游戏 mod、本地 inspector、updater、测试和 mod 发布脚本。生产服务端、
奖励发放、管理后台、部署和反滥用规则不在公开仓库中。

## 快速验证

```bash
dotnet build src/Sts2Telemetry/Sts2Telemetry.csproj
dotnet run --project tests/Sts2Telemetry.Tests/Sts2Telemetry.Tests.csproj
dotnet run --project tests/Sts2Telemetry.Inspector.Tests/Sts2Telemetry.Inspector.Tests.csproj
```

## 隐私边界

不收集 Steam ID、OS 用户名、本地文件路径、原始存档本地路径、本地身份字段、硬件指纹、
基于 IP 的地理位置。
