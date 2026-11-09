# 项目信息

- **名称**：Smart HDR Manager
- **版本**：2.6.2
- **作者**：VantaCode420
- **类型**：Windows 托盘应用（无控制台窗口）

---

# 编译指导

## 环境要求

- **.NET SDK 10.0** 或更高版本
- **Visual Studio**（可选，用于 IDE 开发）

## SDK 安装位置

```
C:\Users\admin\.dotnet\sdk\10.0.403
```

## 编译命令

```powershell
& "C:\Users\admin\.dotnet\dotnet.exe" build SmartHdrManager.csproj -c Release
```

## 编译异常处理

如果编译时提示 `SmartHdrManager.exe` 文件被占用（进程正在运行），需要先结束进程再重新编译：

```powershell
Stop-Process -Name SmartHdrManager -Force
& "C:\Users\admin\.dotnet\dotnet.exe" build SmartHdrManager.csproj -c Release
```

## 编译配置

- **仅允许 Release 配置**，禁止 Debug 配置
- 提交前清理 `obj/` 和 `bin/` 中的 Debug 产物

```powershell
dotnet clean SmartHdrManager.csproj
```

## 输出位置

- **EXE**：`bin\Release\net10.0-windows\win-x64\publish\SmartHdrManager.exe`
- **发布包**：`bin\Release\net10.0-windows\win-x64\publish\`（自包含部署，含完整 .NET 运行时）

## 目标框架

- `net10.0-windows`，平台 `x64`
- 自包含部署（`SelfContained=true`），无需目标机器安装 .NET
- 支持滚动到主版本（`RollForward=Major`）

## 项目属性

| 属性 | 值 |
|------|------|
| OutputType | WinExe |
| Nullable | enable |
| ImplicitUsings | disable |
| LangVersion | latest |
| InvariantGlobalization | true |
| ConcurrentGarbageCollection | false |
| SatelliteResourceLanguages | en |

## 警告处理

| 警告码 | 说明 | 处理方式 |
|--------|------|----------|
| CS0472 | `bool` 与 `bool?` 比较 | 移除 `!= null` 判断 |
| CS0414 | 字段已赋值但从未使用 | 删除未使用字段 |
| CS0649 | 字段从未赋值 | 补充赋值或移除字段 |

---

# 代码规范

## 编译规范

- **仅允许 Release 配置**，禁止 Debug 配置
- **每次代码修改后必须重新编译**，确保代码能正确构建

## 代码规范

1. **禁止魔法数字和硬编码**——提取为命名常量
2. **函数职责单一**——超过 30 行应拆分，命名准确描述行为
3. **文件职责单一**——大文件按逻辑拆分，避免多类混杂
4. **不使用注释**——通过清晰命名和结构自解释
5. **清理死代码**——删除未使用字段/方法/导入和冗余逻辑
6. **保持解耦**——接口注入依赖，避免循环依赖和上帝类
