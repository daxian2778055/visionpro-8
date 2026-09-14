# CI 说明（VP八相机海康）

本仓库的 CI 分两层，**风险从低到高**。

## 第 1 层：单元测试 CI（已启用，公共 runner 可跑）

- 文件：`.github/workflows/ci.yml`
- 内容：push / PR 到 main 时，自动 `dotnet test Tests/UnitTests.csproj`
- 只测**纯逻辑**，不碰 Cognex/Hsl 的 DLL，所以在 GitHub 公共 runner 直接绿：
  - `FaultFrameTests` —— 999 故障判定（runError / inputAssignFailed → 各通道发 999、IO 放 NG）
  - `ModbusCrcTests` —— Modbus RTU CRC-16 回归（生产实现 vs 教科书参考实现交叉验证）
  - `IoPulseOrderTests` —— IO 脉冲 on/off 单调性 + Flush `outstanding` 屏障（覆盖"出队未执行完"窗口）
- 本地复现：`cd Tests && dotnet test`

> 目前 19 项全通过。

## 第 2 层：完整主工程编译 CI（需 self-hosted runner）

主工程依赖 **Cognex VisionPro 59.2** 商业 DLL（`E:\vp\vp\VisionPro\ReferencedAssemblies\*`）与海康 `MvCamCtrl`，
这些在 GitHub 公共 runner 上不可得，所以**完整编译必须在装了 VisionPro 的内网机器上跑**。

落地步骤：
1. 在一台能编译本 `.sln` 的 Windows 机器上安装 Git、注册为 **self-hosted runner**（`runs-on: [self-hosted, windows]`）。
2. 复制一份 `ci.yml` 的 job，runner 标签指向该机器，命令改为：
   ```
   "C:\Program Files\Microsoft Visual Studio\...\MSBuild.exe" WindowsFormsApplication1.sln /p:Configuration=Release /p:Platform="Any CPU|x64"
   ```
   或直接 `dotnet build WindowsFormsApplication1.sln -c Release`（若已挂 GAC 引用）。
3. 商业 DLL 建议**不入库**，放在 runner 机器的 `E:\vp\vp\VisionPro\ReferencedAssemblies\`，
   或用 NuGet 私库（Artifactory/Verdaccio）镜像后由 csproj 引用。
4. 通过后可再加：`MSBuild /t:Rebuild` 作为 PR 门禁（红了不可合并）。

## 建议门禁强度

- **必须**：第 1 层（测试全绿）作为 main 的合并条件。
- **建议**：第 2 层（完整编译）作为"发布前"门禁，而非每次 PR——因为商业库环境重、慢。

## 下一步（继续提分）

- 把 `999 判定 / CRC / 脉冲时序` 抽到**独立类**（现在测试里是"复刻"，理想是直接引用生产代码），
  即对 `Modbus.GetCRC16`、`getrecord` 的故障判定做少量重构，让测试直接覆盖真实实现而非拷贝。
  这样 CI 才是真·回归，而非"测试自己会写错"的镜像验证。
