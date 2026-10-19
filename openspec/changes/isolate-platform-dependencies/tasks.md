# Tasks

## 1. 平台隔离与兼容

- [x] 1.1 记录原测试基线，提取 Windows/Serial 项目并注入 IAtChannelProvider，更新所有宿主及 solution；通过 solution 构建、原测试和迁移代码 diff 确认协议与探测策略不变。
- [x] 1.2 增加模拟通道的连接/回退/释放/事件和大小写回归，以及 Core 平台依赖检查；运行全部离线测试并记录结果。
- [x] 1.3 增加三平台 Core 测试和 Serial 构建 CI，编写平台边界/迁移/人工冒烟文档；核对非 Windows 测试入口不引用 WPF 或 Windows 项目。

## 2. 集成与后续依赖

- [x] 2.1 更新音频 change 的前置依赖和重叠任务，保留 .NET 10 与音频实现任务；两个 change 均通过严格 OpenSpec 校验。
- [x] 2.2 验证 Windows framework-dependent/self-contained 发布包含新程序集，记录完整构建测试结果与未执行的真机/其他 OS 验证范围。

## Workflow follow-up

- 发布前在 Windows 执行文档中的 UI/真机冒烟；Linux/macOS CI 结果通过后才能声称对应系统离线测试已验证。
- 审阅后另行归档，不在本次自动归档或发布。
