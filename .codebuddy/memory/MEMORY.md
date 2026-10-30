# MEMORY

## 项目

`d:\VCProject\SocketTest` —— .NET Socket 学习沙盒，用户以 C# 客户端开发视角系统学习 Socket 编程。

- `src/SocketTest.Protocol/`：客户端与服务端共用的协议层（`ProtocolConstants` 16 字节帧头、`Frame`、`FrameCodec`、`FrameReader`、`PayloadCodec`、`Messages`）
- `labs/Lab0_MockServer/`：可注入故障（丢包/延迟/抖动/kill）的假服务端
- `labs/LabC1_Client/`：`ClientConnection` + `RpcClient` + `ConnectionStateMachine` + `HeartbeatMonitor` + `ChatClient`
- 环境：仅 .NET 9 SDK，项目统一 TFM=net8.0，默认 LangVersion 12 → **C# 13 的特性（`allows ref struct`、async 里用 ref struct 局部变量）在此项目不可用**
- 学习路线：Lab-C2 增量解析 → Lab-C3 请求响应 → Lab-C4 拔网线重连 → Lab-C5 弱网注入 → Lab-C6 TLS → 最终 IM + 文件传输

## 协作约定（核心，必须遵守）

**用户会在本会话中逐个提问 C# 语言细节，我的职责是把每一轮问答自动总结进 `docs/CSharp-语言细节复习.md`，不需要用户每次提醒。**

具体执行方式：

- 每回答完一个问题，就把该问题的完整答案整理进文档（同一文件持续追加，**不为单个问题新建文件**）
- 问题编号连续递增（Q1、Q2……），先在开头的**问题索引表**加一行（编号 / 原问题 / 主题 / 一句话答案）
- 正文按**知识主题**分组而非按提问顺序：能并入已有主题章节就并入，跨主题则新开一章
- 每个条目必须包含「**项目内对应的代码位置 + 常见坑**」，不能只写通用知识
- 文档结构保持为：索引表 → 主题章节 → 附录速查表（决策树 / 错误码对照 / 踩坑）→ 自测题（`<details>` 折叠答案）
- 只有单个问题大到需要完整改造练习（如整个 Lab 的实现）时，才拆独立文件，并在索引表里做链接

## 用户偏好

- 关注「**为什么这样设计**」而非「怎么用」，喜欢先被纠正因果倒置（例："因为没有 readonly array 才用 readonly memory" → 实际是三条约束的交集）
- 已具备 C# 基础，需要的是底层机制层面解释：内存布局、GC 可见性、编译期约束、错误码
- 回答用中文，类型名/关键字/代码保留英文
- 讲解应带具体行号锚点，方便回代码里对照

## 已产出

- `docs/CSharp-语言细节复习.md`（2026-10-27 创建，当前 Q1–Q25，含 12 个主题 + 速查表 + 10 道自测题）
  - 主题 A 内存视图与所有权 / B byref-like / C async 与流写出 / D 数据建模 / E 连接生命周期 / F Stream 与 IO 抽象（项目内追踪）/ G Stream 通用模型（脱离项目）/ H 连接建立 TcpListener·TcpClient·Socket / I 可空值类型 Nullable&lt;T&gt; / J 并发原语与单写者模型 / K LINQ 与 C# 语法糖 / L 集合类型选型
