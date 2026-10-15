# 当前版本存档

存档时间：2026-10-15 13:15:47（北京时间）。源码版本 v1.15.9，工作检查点 v9。

本次为开发存档；不创建 GitHub Release，也不执行发布或 Gitee 同步脚本。

## 保存内容

当前 src、tests、resources、docs、构建与安装脚本，以及 artifacts/current 下的最新三个平台编译产物。原作者下载副本、旧编译目录、编译中间产物、诊断日志和用户配置不纳入本次快照。保留原 MIT 许可。

## 最新修改

- 将非模态面板的取点、选对象、目录量取和预览统一转交 CAD 文档命令，修复捕捉漂移和交互卡死。
- 单张打印的正值留白使用扩大的实际纸张；保留负值缩比例处理。
- 设置页快捷键后增加日志页，可查看和导出操作日志；字段窗口恢复原布局。
- 图框库重新读取后刷新列表并闪烁一次，显示数量及时间，同步图框库更新。
- 通用批打底部排序设置替代比例设置入口，关联现有排序参数和重排。
- DWG 输出保存前按图框边界设置默认居中视图。

## 加载编译产物

- AutoCAD 2015–2024：artifacts/current/AutoCAD2015-2024/AcadBatchPlot.dll
- AutoCAD 2025–2027：artifacts/current/AutoCAD2025-2027/AcadBatchPlot.Core.dll
- ZWCAD 2025：artifacts/current/ZWCAD2025/BatchPlotter.dll

重启 CAD 后 NETLOAD 对应 DLL，保留同目录依赖。

## 构建与测试

使用 .NET SDK 8（global.json）。在项目根目录运行：

```powershell
dotnet build src/AcadBatchPlot/AcadBatchPlot.csproj -c Release
dotnet build src/AcadBatchPlot.Core/AcadBatchPlot.Core.csproj -c Release
dotnet build src/BatchPlotter/BatchPlotter.csproj -c Release
dotnet run --project tests/LibraryInputRouting/LibraryInputRouting.csproj -c Release
```

三个平台最近一次 Release 编译均为 0 警告、0 错误，已有回归测试通过。宿主替身及数值测试不等同 CAD 实机验证；最新 DWG 居中及列表闪烁仍需实机确认。

## 校验

SNAPSHOT_MANIFEST.json 记录文件相对路径、大小和 SHA-256。外层 ZIP 另附 SHA-256 校验文件。
