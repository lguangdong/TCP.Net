# C# 语言细节复习笔记

> 整理日期：2026-10-27
> 项目：`d:\VCProject\SocketTest`（.NET 8 TFM / C# 12 / .NET 9 SDK）
> 来源：逐行阅读 `src/SocketTest.Protocol/` 协议层代码时提出的问题

---

## 问题索引

| 编号 | 原问题 | 所属主题 | 一句话答案 |
|---|---|---|---|
| Q1 | `public ReadOnlyMemory<byte> Payload { get; }` 为什么用 `ReadOnlyMemory<byte>`？ | 内存视图 | 三条约束的交集：只读 + 能跨 `await` 存字段 + 能零成本转 `Span` |
| Q2 | 什么是 byref-like 类型？ | 类型系统 | 内部持有托管指针、实例只能上栈的类型，C# 里就是 `ref struct` |
| Q3 | 因为没有 `readonly array`，所以用 `readonly memory`？ | 内存视图 | 不是。`ReadOnlyMemory<T>` 是"能存进字段的 `ReadOnlySpan<T>`"，不是数组的替代品 |
| Q4 | `await stream.WriteAsync(buffer, ct).ConfigureAwait(false);` 这行什么意思？ | async | 选中 `ValueTask` 重载发出全量字节；`ConfigureAwait(false)` 表示不捕获调用方同步上下文 |
| Q5 | `record` 关键字是什么？ | 数据建模 | C# 9 的"以数据为中心的类"，自动生成构造、`init` 属性、值相等、`with`、`ToString` |
| Q6 | 什么是 DTO？ | 数据建模 | Data Transfer Object，只装数据不含行为、形状由通信契约决定的对象 |
| Q7 | 什么是 EOF？ | 连接生命周期 | End Of File；在 Socket 语境下表示"对端关闭了写方向"，不是错误 |
| Q8 | 数组 / `Memory` / `Span` 什么关系？ | 内存视图 | 数组是所有者，`Memory` 是能上堆的视图，`Span` 是只能在栈上的视图 |
| Q9 | `WriteHeader(buffer, ...)` 把 `byte[]` 传给 `Span<byte>` 参数是隐式转换吗？ | 内存视图 | 是，`Span<T>.op_Implicit(T[]?)`，零拷贝；坑是 `null` 会静默转成空 `Span` |
| Q10 | `Stream` 从哪里来？它的作用是什么？ | Stream 与 IO 抽象 | 来自 `TcpClient.GetStream()` 返回的 `NetworkStream`（对 Socket 的流式封装）；作用是让协议层只依赖 `Stream` 抽象，从而可替换为 TLS/压缩/内存流，并获得"全量写出"与"Read 返回 0 = EOF"的语义保证 |
| Q11 | 脱离项目解释一下 `Stream` | Stream 通用模型 | `System.IO.Stream` 是「有序字节序列」的抽象基类，提供增量读写；核心是四条契约（`Read` 返 0 才结束、部分写不保证落盘、`leaveOpen`、非线程安全）与两个正交维度（能否 Seek、是否装饰流） |
| Q12 | server 里为什么也有 `TcpClient`？ | 连接建立 | 那个 `TcpClient` 不是 `new` 出来的，是 `TcpListener.AcceptTcpClientAsync()` 生出来的；TCP 连接建立后两端对称，`TcpClient` 只是「一条已建立连接」的持有者，不代表"客户端角色" |
| Q13 | `await HandleAsync(frame.Value, ...)` 为什么要 `.Value`，直接 `frame.Type` 不行吗？ | 可空值类型 | 因为 `ReadFrameAsync` 返回 `ValueTask<Frame?>`，`frame` 的静态类型是 `Nullable<Frame>` 而不是 `Frame`——它是一个包装盒，必须 `.Value` 拆箱；值类型的 `T?` 和引用类型的 `T?` 在类型层面是两回事 |
| Q14 | JSON 反序列化怎么知道每个字段占多少字节？`string` 是引用类型，连续字节怎么切分？ | 数据建模 · 序列化 | 不按长度切，按**语法**切。JSON 是自描述文本格式，字符串由双引号定界、字段由 `:` 和 `,` 分隔，解析器逐字符扫描即可；固定长度/长度前缀只在二进制协议（protobuf、MessagePack）里才需要 |
| Q15 | `using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct); timeoutCts.CancelAfter(timeout);` 这两行什么意思？ | async · 取消与超时 | 造一个**新的** CTS，把"外部主动取消"和"超时自动取消"两个信号链到一起，从而用一个 token 同时响应两者；`using var` 是 C# 8 的 using 声明，块尾自动 Dispose（linked CTS 不 Dispose 会往父 token 上泄漏回调） |
| Q16 | `ReadFrameAsync` 里 stream 没数据时，是不是一直挂在 `_stream.ReadAsync`，直到数据到来？ | async · 异步读挂起 | 是的，会挂起，但**不占线程**——`await` 让出线程，IOCP/epoll 在内核侧等待，数据到达才回调恢复；也正是"不占线程"意味着**永远等不到**也只能一直挂，所以必须有应用层心跳兜底 |
| Q17 | `Channel<byte[]> _sendQueue` 和 `TaskCompletionSource _closed` 这两个字段是干什么的？ | 并发原语 | `Channel` 是异步的生产者-消费者队列，用"单写者"约束解决多线程并发写 socket 会切碎数据的问题，队列满时反压；`TaskCompletionSource` 手工完成一个 `Task`，`TrySetResult()` 的原子性正好用作**幂等关闭锁**，必须加 `RunContinuationsAsynchronously` 否则续体会在调用线程上同步执行 |
| Q18 | 什么是无锁 CAS？ | 并发原语 | Compare-And-Swap：一条 CPU 原子指令完成「比较 + 条件写入」，失败就自旋重试而不阻塞线程；`Interlocked.*` 和 `TrySetResult()` 的幂等性都建立在它之上 |
| Q19 | `new[] { _readLoop, _sendLoop }.Where(...).Select(task => task!).ToArray()` 这段在干什么？ | LINQ 与语法糖 | 把两个可能为 null 的 `Task?` 字段过滤后收集成 `Task[]`，供 `Task.WhenAll` 等待；`!` 是 null-forgiving 运算符，纯编译期、运行时零开销，因为 `Where` 不会收窄元素类型所以必须手动告诉编译器 |
| Q20 | 既然要串行化发送，为什么不用 `ConcurrentQueue`？ | 并发原语 | `ConcurrentQueue` 只解决「多线程安全存取」，不解决「**等**」——没数据时 `TryDequeue` 返回 false，消费端只能轮询；Channel 的 `WaitToReadAsync` 会挂起并在有数据时被唤醒，还内建了有界背压和 `Complete` 完成语义 |
| Q21 | .NET 各种集合形态分别什么时候用？ | 集合选型 | 先问三件事：要不要下标访问、要不要按 key 查、要不要跨线程。非并发按访问模式选 `T[]`/`List`/`Dictionary`/`HashSet`/`Queue`/`LinkedList`；并发集合再分「消费者主动拉取」用 `ConcurrentQueue`，「消费者需要挂起等」用 `Channel`；构建后只读高频查用 .NET 8 的 `FrozenDictionary` |
| Q22 | `case "--host" when i + 1 < args.Length:` 里的语法怎么理解？ | 语法糖 · 模式匹配 | `case <模式> when <布尔表达式>:` 是「常量模式 + case guard」，guard 只在模式匹配成功后才求值，为假则继续尝试后面的 case；配合 `args[++i]`（前缀自增，先加再取值）实现「偷步消费下一个参数」，`options with { ... }` 则是对 record 做浅拷贝生成新对象 |
| Q23 | `public void Start() => _runLoop ??= Task.Run(() => RunAsync(_shutdownCts.Token));` 语法 | 语法糖 | 表达式体方法 + `??=`（C# 8，右侧**惰性求值**，非 null 时不执行）+ `Task.Run(Func<Task>)` 重载（会 unwrap，等待内部异步方法真正结束）；用途是「幂等启动」，但 `??=` **不是原子操作**，并发调用会启动两个循环 |
| Q24 | 一个请求挂起期间又来新请求，会在同一处挂起吗？ | async · 并发 | 不会。每次调用有独立的状态机实例与 `seq`，各自的 `pending.Task` 互不相干；唤醒靠 `frame.Seq` 在 `ConcurrentDictionary` 里配对，所以响应可以乱序到达。挂起期间 0 线程占用，N 个请求 = N 个状态机 + N 个 TCS |
| Q25 | `CancellationToken cancellationToken = default` 里的 `default` 是什么？ | async · 取消令牌 | `default` 是 C# 7.1 的 default 字面量，等于 `default(CancellationToken)` = `CancellationToken.None`——一个**永远不会被取消**的 token。它使该参数成为可选参数，调用方可以只写 `EchoAsync("hi")` |

---

# 主题 A：内存视图与所有权

> 覆盖 Q1、Q3、Q8、Q9

## A1. 三种类型的分工（Q8）

```
T[]        → 内存的【所有者】
Memory<T>  → 能上堆的【视图】
Span<T>    → 只能在栈上的【视图】
```

| | `T[]` | `Memory<T>` | `Span<T>` |
|---|---|---|---|
| 本质 | 内存本身 | 视图 | 视图 |
| 类型类别 | 引用类型 | 普通 `struct` | `ref struct`（byref-like） |
| 实例位置 | 堆 | 堆（可做字段、可装箱） | **仅栈** |
| 能跨 `await` | ✅ | ✅ | ❌ |
| 能做类字段 | ✅ | ✅ | ❌ |
| 能指向 `stackalloc` | ❌ | ❌ | ✅ |
| 能指向原生内存 | ❌ | ❌ | ✅ |
| 能自己增长 | ✅（换新数组） | ❌ | ❌ |
| 索引开销 | 最快 | 高（每次要转 `Span`） | 最快（JIT 可消除边界检查） |
| 只读变体 | 无 | `ReadOnlyMemory<T>` | `ReadOnlySpan<T>` |

## A2. 内存布局 —— 为什么一个能上堆、一个不能

x64 上两者都是 16 字节，差别只在第一格：

```
Span<byte>                     Memory<byte>
┌──────────────────┐           ┌──────────────────┐
│ ByReference  (8B)│           │ object _object   │──→ 指向数组
│ int _length  (4B)│           │ int _index   (4B)│
└──────────────────┘           │ int _length  (4B)│
   裸地址 → data[10]            └──────────────────┘
```

- `Span` 存**裸地址**：GC 看不见它 → 数组被压缩移动后就是野指针 → 所以绝不能上堆。
- `Memory` 存**对象引用 + 索引**：GC 完全看得见数组 → 地址在 `.Span` 那一刻才现算 → 可以安全上堆。

`Memory<T>` 取 `.Span` 比数组直接索引慢的那一点点，就是这次"现算地址"的成本。

## A3. 转换只向下免费（Q3、Q8）

```
        隐式              隐式
T[] ──────────→ Memory<T> ──────────→ Span<T>
                （.Span 属性）          全是视图构造，0 拷贝

反向不允许：
Span<T>   ──✗──→  Memory<T>     语言层面不存在这条转换
Memory<T> ──✗──→  T[]          .ToArray() 是复制
```

**这条规则解释了代码里那处 `ToArray()`：**

```csharp
// src/SocketTest.Protocol/FrameCodec.cs:88
frame = new Frame(type, seq, buffer.Slice(ProtocolConstants.HeaderSize, length).ToArray(), flags);
```

`buffer` 是 `ReadOnlySpan<byte>`，而 `Frame.Payload` 需要 `ReadOnlyMemory<byte>`。向上转换不存在，只能复制。

## A4. `Frame.Payload` 为什么是 `ReadOnlyMemory<byte>`（Q1）

不是"没有只读数组才将就"，是三条硬约束的交集：

**① 必须只读** —— `Frame` 会被广播给所有订阅者（`FrameReceived?.Invoke`），`byte[]` 意味着任何订阅者都能 `Payload[0] = 0` 改写内容，影响其他人拿到的同一条消息。`readonly struct` + `{ get; }` 只保证"引用不可换"，保证不了"内容不可改"。

**② 必须能跨 `await` 存字段** —— `FrameReader.ReadFrameAsync` 是 `async`，`Span<byte>` 是 byref-like，不能做普通 struct 字段（CS8345），也进不了 async 状态机。这是硬约束，不是性能取舍。

**③ 必须能零成本取出 `ReadOnlySpan<byte>`** —— 解析器签名是

```csharp
// src/SocketTest.Protocol/FrameCodec.cs:113
public static T? FromUtf8<T>(ReadOnlySpan<byte> payload)
```

`IReadOnlyList<byte>` / `ReadOnlyCollection<byte>` 想喂进去必须 `ToArray()` 复制一遍，把第②条省下的开销又还回去了。

**候选方案对比：**

| 类型 | 只读 | 跨 `await` | 零成本转 `Span` | 结论 |
|---|---|---|---|---|
| `byte[]` | ❌ 内容可变 | ✅ | ✅ | 语义不合格 |
| `IReadOnlyList<byte>` | ✅ | ✅ | ❌ | 接口调用开销，无法喂给 STJ |
| `ReadOnlyCollection<byte>` | ✅ | ✅ | ❌ | 多个包装对象分配 |
| `ImmutableArray<byte>` | ✅ 真不可变 | ✅ | ✅ | 构建即复制，比 `ToArray` 还贵 |
| `ReadOnlySequence<byte>` | ✅ | ✅ | 需遍历分段 | 面向不连续多段缓冲，单帧连续负载用不上 |
| `ReadOnlySpan<byte>` | ✅ | ❌ byref-like | ✅ | 不能做字段 |
| **`ReadOnlyMemory<byte>`** | ✅ | ✅ | ✅ | **唯一解** |

## A5. 只读视图 ≠ 不可变数据（重要认知）

`ReadOnlyMemory<T>` 只是**挡住你自己**，不阻止别人。如果还有别的可写引用指向同一块底层内存，内容照样能被改。

真正的不可变只能靠：
- **复制**（`ImmutableArray`），或
- **约定**（没有任何人保留可写引用）

`FrameReader` 走的是第二条：那个 `byte[]` 创建后没有任何人再持有它，所以"只读视图"在实践中就等于不可变。

⚠️ 一旦改成 `ArrayPool` 复用缓冲区，这层保证就没了——缓冲区归还后可能被下一次读操作覆盖。这就是零拷贝要付的所有权代价（见文末"待优化点"）。

## A6. `byte[]` → `Span<byte>` 的隐式转换（Q9）

```csharp
// src/SocketTest.Protocol/FrameCodec.cs:21
var buffer = new byte[ProtocolConstants.HeaderSize + payload.Length];
WriteHeader(buffer, type, seq, payload.Length, flags);   // byte[] → Span<byte>
```

编译器调用 `Span<T>.op_Implicit(T[]?)`，构造一个视图，**写入的就是 `Encode` 里那个 `buffer` 本身**，零拷贝。

⚠️ **坑**：null 数组会转成**空 `Span`**，不抛 `NullReferenceException`。

```csharp
byte[]? maybeNull = null;
Span<byte> s = maybeNull;   // Length == 0，不报错
```

后果是这类 API 会**静默吞掉 null**——传 null 进去不报错，只是编码出一条空负载的消息。要在入口显式 `ArgumentNullException.ThrowIfNull`。

---

# 主题 B：byref-like 类型（Q2）

## B1. 定义

**byref-like type**：内部包含托管指针（`ref T`）的类型，实例只能存在于**栈**上。

- CLI/运行时规范里叫 *byref-like*
- C# 里的语法是 **`ref struct`**
- 元数据上标记为 `System.Runtime.CompilerServices.IsByRefLikeAttribute`

```csharp
public readonly ref struct Span<T>   // 内部只有 ref T + int
```

## B2. 为什么必须这样限制

`stackalloc` 分配的栈帧一返回就失效。如果允许 `Span` 被装箱、塞进类字段、存进 `List<Span<byte>>`，那么它可能指向已经消失的栈帧——**悬挂指针**，而 GC 完全看不见，既不会阻止回收也不会修正地址。

C# 的选择：**根本不允许它上堆**，把这类 bug 变成编译错误。

## B3. 限制清单

| 禁止操作 | 原因 |
|---|---|
| 作为 class / 普通 struct 的字段或自动属性 | 对象在堆上（**CS8345**） |
| 装箱成 `object`、转 `dynamic` | 堆分配 |
| 作为数组元素类型 | 数组在堆上 |
| 作为泛型类型实参（`List<Span<int>>`） | 泛型实例可能在堆上（**CS0306**） |
| 跨 `await` / `yield`（C# 12 及以前全面禁止） | async 状态机是堆上的类 |
| 被 lambda / 局部函数闭包捕获 | 捕获类在堆上 |

**传染性（containment）**：普通 `struct` 里只要有一个 byref-like 字段，这个 struct 自己**必须**也是 `ref struct`。

## B4. 判断方法

```csharp
typeof(Span<byte>).IsByRefLike            // true
typeof(ReadOnlySpan<byte>).IsByRefLike    // true
typeof(ReadOnlyMemory<byte>).IsByRefLike  // false ← 普通 struct
typeof(Memory<byte>).IsByRefLike          // false
```

常见 byref-like 类型：`Span<T>`、`ReadOnlySpan<T>`、`Utf8JsonReader`、`DefaultInterpolatedStringHandler`、`TypedReference`、`ArgIterator`。

## B5. 项目里踩过的真实坑

`ProtocolConstants.Magic` 原本是：

```csharp
public static ReadOnlySpan<byte> Magic => "SK"u8;   // ❌
```

在 async 方法里使用直接编译失败（**CS9202**）——静态字段在堆上，而 async 状态机的字段也在堆上。

改成 `static readonly byte[]` 后解决。

**同一个根因导致的两个错误码：**

| 场景 | 错误码 |
|---|---|
| 作为静态字段 / 普通结构字段 | CS8345 |
| 在 async 方法里持有 | CS9202 |

## B6. C# 13 的松动（本项目用不到）

```csharp
// 1) anti-constraint：泛型参数可以接受 ref struct
public static int Count<T>(scoped ReadOnlySpan<T> span) where T : allows ref struct

// 2) async 方法里允许 ref struct 局部变量，只要不跨 await
async Task FooAsync()
{
    Span<byte> buf = stackalloc byte[16];   // C# 13 允许
    buf.Fill(1);
    await Task.Delay(1);                     // await 之后不能再用 buf
}
```

⚠️ 本项目 TFM 是 net8.0，默认 `LangVersion 12`，**这两条都用不了**。

永远放不开的：字段、装箱、数组元素、泛型实参——这是运行时级别的根本约束。

---

# 主题 C：async 与流写出（Q4）

```csharp
// src/SocketTest.Protocol/FrameCodec.cs:102
await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
```

## C1. 命中的是哪个重载

实参是两个，所以是：

```csharp
public virtual ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default);
```

- `byte[]` → `ReadOnlyMemory<byte>` 是隐式转换，只包一层视图，**不复制**。
- 返回 `ValueTask` 而非 `Task`：写操作绝大多数时候同步完成，省掉每次分配 `Task` 对象。

## C2. `await` 在这里的真实开销

`await` 先看 `IsCompleted`：

- **已完成**（内核发送缓冲区有空间）→ 直接继续，不产生状态机调度
- **未完成**（缓冲区满）→ 注册回调，线程还回线程池

所以通常只是一次系统调用的开销，不是线程切换。

## C3. 但它保证了一件关键的事：全量写出

`Stream.Write` 的契约是：**成功返回 = 全部字节已被接受；否则抛异常**。内部会循环调用 `Socket.Send` 直到写完。

对比 `Socket.Send`：**可能只发出去一部分**，必须自己比对返回值、切剩余切片、再发一次。

这是 `Stream` 比裸 `Socket` 好用、但也多一层拷贝的根本原因。

## C4. `ConfigureAwait(false)` 到底做什么

```csharp
await something;                        // 捕获 SynchronizationContext.Current / TaskScheduler.Current
await something.ConfigureAwait(false);  // 不捕获，续体直接在线程池线程上继续
```

**不加**时，`await` 会记住当前同步上下文，操作完成后把剩余代码 **Post 回那个上下文**。

**为什么库代码必须写：**

1. **死锁**。UI 线程上 `task.Wait()` 同步阻塞，而续体要 Post 回 UI 线程 → 互等。
   ```csharp
   var task = connection.SendAsync(...);  // 内部某处 await 没写 ConfigureAwait(false)
   task.Wait();                           // 💀
   ```
2. **性能**。每次 `await` 多一次上下文切换 + 排队。
3. **职责分离**。库代码不该假设调用方环境。切回主线程是业务层的事——项目里的 `MainThreadDispatcher` 就是干这个的，两者配套。

**诚实的补充**：这一行是方法体最后一行，后面没有续体代码，所以实际收益接近于零。但项目里全量一致地写，是**纪律而非优化**——将来在 `await` 后加代码时不会埋雷。

## C5. `async ValueTask` 没有 `return`

方法签名是 `async ValueTask`，但方法体末尾没有 `return`。这是合法的：正常执行到结尾即返回已完成的 `ValueTask`；异常则存在 faulted 状态里，由 `await` 方重新抛出。

⚠️ `ValueTask` **只能 `await` 一次**，可能被复用。需要多次等待必须先 `.AsTask()`。

## C6. 这行的成本账

```csharp
var buffer = Encode(type, seq, payload.Span);        // 一次堆分配 + 一次内存拷贝
await stream.WriteAsync(buffer, cancellationToken);  // 一次系统调用
```

想省掉拷贝，就得用 `Socket.SendAsync` 的 scatter-gather，或 `IBufferWriter<byte>` + `ArrayPool`。教学项目里"**一次拷贝换一次系统调用**"是最合理的第一版。

## C7. 超时与取消：`CreateLinkedTokenSource` + `CancelAfter`（Q15）

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:67
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(timeout);
```

### 前提：两个类型的分工

```
CancellationTokenSource         ← 唯一能 Cancel() / CancelAfter() 的
        │
        │ .Token
        ▼
CancellationToken               ← 只读句柄，只能"听"，不能"说"
```

- `CancellationToken` 是**只读视图**，本身没有 `Cancel()` 方法
- 要发起取消，必须有 `CancellationTokenSource`

所以想让"外部 token"带上超时能力，**造不出**只读 token，只能造一个新的 Source。

### 第一行语法：`using var`

`using var` 是 **C# 8** 引入的 using 声明，不是块语句：

```csharp
// 老写法：using 语句块
using (var cts = new CancellationTokenSource())
{
    ...
}   // ← 这里 Dispose

// 新写法：using 声明
using var cts = new CancellationTokenSource();
...
// ← 当前语句块（方法体）结束时 Dispose
```

编译器实际生成：

```csharp
var cts = new CancellationTokenSource();
try
{
    ...
}
finally
{
    cts?.Dispose();
}
```

要点：
- 作用域是**当前大括号块**，不是整个类
- 声明必须是一条独立语句，不能塞进表达式
- 多个 `using var` 时**按声明的逆序**释放

### 第一行调用：`CreateLinkedTokenSource`

```csharp
public static CancellationTokenSource CreateLinkedTokenSource(params CancellationToken[] tokens);
public static CancellationTokenSource CreateLinkedTokenSource(CancellationToken token1, CancellationToken token2);
```

行为：

1. 返回一个**全新的** CTS
2. 在传入的每个 token 上注册回调
3. 任何一个源 token 取消 → 新 CTS **也跟着取消**
4. **链接是单向的**：新 CTS 取消，不影响源 token

内部有专门的优化子类：`Linked1CancellationTokenSource`、`Linked2CancellationTokenSource`、`LinkedNCancellationTokenSource`（单参和双参走特化路径，避免数组分配）。

### 第二行：`CancelAfter`

```csharp
public void CancelAfter(TimeSpan delay);
public void CancelAfter(int millisecondsDelay);
```

- **非阻塞**，安排一个定时器后立即返回
- 到期后自动调用 `Cancel()`
- 重复调用时**最后一次生效**（覆盖前一个定时器）
- `CancelAfter(Timeout.InfiniteTimeSpan)`（-1 ms）= 取消定时器
- 传其他负数会抛 `ArgumentOutOfRangeException`
- 是线程安全的

### 合起来的效果

```csharp
using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
timeoutCts.CancelAfter(timeout);

try
{
    await tcp.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
}
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
{
    tcp.Dispose();
    throw new TimeoutException($"连接 {host}:{port} 超时");
}
```

取消来源有两个，但下游只看到一个 `timeoutCts.Token`：

```
外部 cancellationToken ──┐
                        ├──→ timeoutCts.Token ──→ ConnectAsync
超时定时器 CancelAfter ──┘
```

### 关键配套：`catch ... when` 区分"谁取消的"

`OperationCanceledException` 只有一个类型，从异常本身**无法知道**是外部取消还是超时。所以用异常筛选器（C# 6 的 exception filter）：

```csharp
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
```

逻辑表：

| 外部 token 取消？ | 结果 | 走向 |
|---|---|---|
| 否 | 只能是超时触发的 | `when` 为 true → 转成 `TimeoutException` |
| 是 | 用户主动取消 | `when` 为 false → 原样抛出 `OperationCanceledException`，不动它 |

**这是标准范式**：超时转成业务异常，主动取消必须保持原样——因为调用方通常要靠 `OperationCanceledException` 判断"是我取消的，不该当成错误"。

### 为什么 `using var` 不能省

`CreateLinkedTokenSource` 会在**父 token** 上注册一个回调：

```csharp
// 概念上的实现
var cts = new CancellationTokenSource();
var registration = parentToken.Register(() => cts.Cancel());
```

不 Dispose 的话：
- 父 token 的 `_registrations` 链表会一直增长
- 每次调用泄漏一个 `CancellationTokenRegistration`
- 父 token 若是长生命周期的（连接级 / 应用级），**这是真实的内存泄漏**

微软文档明确要求 linked CTS 必须 Dispose。

⚠️ 注意 `Dispose()` 的语义：
- **不触发取消**，只是释放定时器和注册
- Dispose 之后再访问 `.Token` 会抛 `ObjectDisposedException`
- Dispose 之后再 `Cancel()` 在 .NET Core 3.0+ 是安全的（.NET Framework 会抛）

### 两个反例

```csharp
// ❌ 编译不过：CancellationToken 没有 CancelAfter
cancellationToken.CancelAfter(timeout);

// ❌ 外部取消失效：新 CTS 只受超时控制，跟外部 token 毫无关系
var cts = new CancellationTokenSource(timeout);
```

### 项目里更微妙的一处用法

`RpcClient.RequestAsync` 里两个阶段用了**不同的 token**：

```csharp
// labs/LabC1_Client/Rpc/RpcClient.cs:96
await connection.SendAsync(requestType, seq, payload, cancellationToken).ConfigureAwait(false);
return await pending.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
```

| 阶段 | 用的 token | 理由 |
|---|---|---|
| 发送 | `cancellationToken`（原始） | 调用方主动取消要能立刻生效 |
| 等待响应 | `timeoutCts.Token`（链接后） | 响应可能永远不来，必须有超时 |

⚠️ 代价是：**发送阶段没有超时保护**。`SendAsync` 内部是往 `Channel` 写（`WriteAsync`），如果队列满了（默认 1024 容量）而写循环又卡在网络上，这里会无限期等待——除非调用方主动取消。

更稳妥的写法是发送阶段也给一个短超时（比如 `timeout` 的一半），或者发送和等待用同一份 `timeoutCts.Token`。

### `Task.WaitAsync` 的异常差异

```csharp
task.WaitAsync(ct)             // 取消 → OperationCanceledException
task.WaitAsync(TimeSpan)       // 超时 → TimeoutException
```

项目用的是 **token 版本**，所以捕获 `OperationCanceledException` 再手工转成 `TimeoutException`。如果当初用 `WaitAsync(timeout)`，就直接是 `TimeoutException`，不需要转换——但那样就无法复用链接后的 token。

## C8. 异步读的挂起机制（Q16）

> 问题：`ReadFrameAsync` 里如果 stream 没有数据，是不是就一直在 `_stream.ReadAsync` 挂起，直到有数据到来？

### 答案：是的，会挂起，但**不占线程**

```csharp
// src/SocketTest.Protocol/FrameReader.cs:85
var read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
```

没有数据时：

1. `ReadAsync` 返回一个**未完成**的 `ValueTask<int>`
2. `await` 让出当前线程（线程池线程立刻去做别的事）
3. `ReadLoopAsync` 返回的 `Task` 进入 `WaitingForActivation` 状态
4. 数据到达 → 内核通知 → 续体在线程池线程上恢复执行

**关键区别**：

| | 同步 `Read` | `ReadAsync` |
|---|---|---|
| 调用后 | 线程被阻塞，睡着等 | 线程立即归还线程池 |
| 1 万个空闲连接 | 1 万线程 × 1MB 栈 ≈ 10GB | 1 万个挂起的 Task，线程池还是那十几个线程 |
| 唤醒方式 | 操作系统调度 | IO 完成回调 |

### 底层是真的异步 I/O

`NetworkStream.ReadAsync` → `Socket.ReceiveAsync` → 操作系统异步 IO：

- **Windows**：I/O 完成端口（IOCP）
- **Linux / macOS**：epoll / kqueue

等待期间**没有任何线程在"守着"这个 socket**，内核替你记账，数据到了才回调。

⚠️ 注意这是 .NET Core 3.0+ 的路径。`.NET Framework` 上 `NetworkStream` 的异步读会**先尝试同步读，失败才转异步**，那个假异步会阻塞线程池线程。

### 四种唤醒路径

挂起之后，只有这几种情况会让它继续：

| 事件 | `ReadAsync` 结果 | 循环走向 |
|---|---|---|
| **有数据到达** | 返回 `n > 0` | 继续拼帧 |
| **对端正常关闭** | 返回 `0` | `ReadFullyAsync` 返回 false → `ReadFrameAsync` 返回 `null` → `break` |
| **`_cts.Cancel()`** | 抛 `OperationCanceledException` | 被 `catch` 捕获，`error` 保持 null → `Shutdown(null)` |
| **RST / 网络错误** | 抛 `IOException` | `error = ex` → `Shutdown(ex)` |

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:104
private async Task ReadLoopAsync()
{
    Exception? error = null;
    try
    {
        while (!_cts.IsCancellationRequested)
        {
            var frame = await _reader.ReadFrameAsync(_cts.Token).ConfigureAwait(false);
            if (frame is null)
            {
                break; // 对端正常关闭
            }
            ...
```

### ⚠️ 最大的风险：`ReadAsync` 没有超时

这是"永远挂起"的另一面：

- `NetworkStream.ReadTimeout` **只对同步 `Read` 有效**，对 `ReadAsync` **完全无效**
- `ReadAsync` 的参数里没有 `TimeSpan` 重载

所以如果对端**既不发送数据，也不关闭连接**——拔网线、NAT 表项超时、对端进程被挂起（不是 kill）——这个 `await` 会**无限期挂着**。

TCP 自带的 keepalive 默认 2 小时才探测，等于没有。

**这正是 `HeartbeatMonitor` 存在的根本原因**：读循环自己发现不了"半开连接"，必须由外部定时 Ping，连续丢失 N 次后主动 `Abort()` → `Shutdown()` → `_cts.Cancel()` → 把那个挂死的 `ReadAsync` 踢出来。

这就是 Q7（EOF）里说的"EOF ≠ 连接断开"的第三种情况。

### `_cts.Token` 在这里的作用

它是**唯一**能让挂起的读操作提前退出的机制：

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:176
_cts.Cancel();
```

`Cancel()` 之后，挂起的 `ReadAsync` 立刻抛 `OperationCanceledException`，读循环退出，`Shutdown` 的第二阶段才能执行完。

注意 `Shutdown` 里的顺序：**先 `TryComplete` + `Cancel`，再 `Shutdown(SocketShutdown.Both)`**。如果反过来，读循环可能还卡在 socket 上，`Shutdown` 之后走异常路径，`error` 被赋成 `IOException`，日志里就会出现一条假的"断开原因"。

### 一个易忽略的快速路径

`ReadAsync` **不一定真的挂起**。如果数据已经在内核接收缓冲区里，`ValueTask` 可能是同步完成的（`IsCompletedSuccessfully == true`），此时 `await` 直接取值继续，**不产生状态机调度**。

高流量场景下大部分读都走这条快速路径，所以 `ReadLoopAsync` 在忙的时候基本是同步循环在跑。

### `while (!_cts.IsCancellationRequested)` 是冗余的吗

基本是，但无害：

- 取消时 `ReadAsync` 会抛异常，循环自然退出
- 这个检查只在"刚好在两次迭代之间被取消"的窗口起作用，省掉一次必然失败的 `ReadAsync` 调用

保留它主要是**可读性**——让"主动关闭"这件事在循环条件里就可见。

### 半帧时被取消会怎样

`ReadFullyAsync` 是循环读的，可能已经读了 8 字节帧头：

```csharp
// src/SocketTest.Protocol/FrameReader.cs:80
private async ValueTask<bool> ReadFullyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
```

此时取消，抛的是 `OperationCanceledException` 而**不是** `EndOfStreamException`。

外层：

```csharp
catch (OperationCanceledException)
{
    // 主动关闭
}
```

`error` 保持 `null` → `Shutdown(null)` → `Closed?.Invoke(null)` → 上层理解为"正常关闭"。

这是对的：**主动关闭时不应该抱怨"帧读到一半"**，那个半帧是我们要它停的。

## C10. `CancellationToken cancellationToken = default` 是什么意思（Q25）

```csharp
// labs/LabC1_Client/ChatClient.cs:206
public Task<string?> EchoAsync(string text, CancellationToken cancellationToken = default)
    => _rpc.EchoAsync(text, _options.RequestTimeout, cancellationToken);
```

### `default` 是 C# 7.1 的 default 字面量

```csharp
= default                      // C# 7.1+，类型由上下文推断
= default(CancellationToken)   // C# 2.0+，显式写类型
```

两者完全等价。由于参数声明处类型已知（`CancellationToken`），所以可以省掉。

### `default(CancellationToken)` 的语义

`CancellationToken` 是个 `struct`，内部只有一个字段 `CancellationTokenSource? _source`。`default` 时它是 `null`：

| 成员 | 值 | 含义 |
|---|---|---|
| `CanBeCanceled` | `false` | 这个 token **永远不会被取消** |
| `IsCancellationRequested` | `false` | 永远 `false` |
| `Register(callback)` | 返回空的 `CancellationTokenRegistration` | 回调**永远不会被触发** |
| `ThrowIfCancellationRequested()` | 不抛异常 | — |

**一句话：`default(CancellationToken)` = "我没有取消需求"。**

传给 `Task.Delay(ms, token)`、`WaitAsync(token)`、`ReadAsync(..., token)` 时，效果就等于"不带取消功能"。

### 四种写法其实完全一样

```csharp
CancellationToken.None            // ← 推荐，语义最明确
default(CancellationToken)
default                           // C# 7.1
new CancellationToken()           // 也能编译，但最差
```

`CancellationToken.None` 的源码就是：

```csharp
public static CancellationToken None => default;
```

**为什么推荐 `CancellationToken.None`**：它把「我明确表示不需要取消」这个意图写在名字上了。
而 `= default` 在参数默认值位置也很常见，因为类型明确、更短。

### 它让参数变成「可选」

```csharp
public Task<string?> EchoAsync(string text, CancellationToken cancellationToken = default)
```

于是调用方有两种写法：

```csharp
// labs/LabC1_Client/Program.cs:119
var reply = await client.EchoAsync(argument).ConfigureAwait(false);        // ← token 用 default

var reply = await client.EchoAsync(argument, someCt).ConfigureAwait(false); // ← 自己传
```

也可以用命名参数跳过前面的可选参数：

```csharp
await client.EchoAsync("hi", cancellationToken: ct);
```

⚠️ 可选参数必须放在参数列表**末尾**，且默认值必须是**编译期常量**（`default` 满足）。

### 传下去之后发生什么

`ChatClient.EchoAsync` 把它连同时配置里的超时一起传给 `RpcClient.EchoAsync`：

```csharp
=> _rpc.EchoAsync(text, _options.RequestTimeout, cancellationToken);
```

最终进到 `RequestAsync`：

```csharp
// labs/LabC1_Client/Rpc/RpcClient.cs:99
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
{
    throw new TimeoutException($"请求 {requestType} 超时（{timeout.TotalMilliseconds:0} ms）");
}
```

因为 `default` 的 token `IsCancellationRequested` **永远是 false**，所以：

```
任何 OperationCanceledException 都会被判定为「不是外部取消」
→ 一定是超时触发的
→ 转成 TimeoutException
```

这正是调用方期望的行为——他根本没打算取消，所以只可能是超时。

### 默认参数的一个坑：值在**调用方**编译期固化

```csharp
public void Foo(int x = 5) { }
```

`5` 是在**编译 `Foo` 的调用方**时嵌入到调用点的。如果改了库里的默认值为 `6`，**已经编译好的调用方不会变**，还是传 5。

对 `= default` 来说无所谓（永远是 default，不会变）。但如果哪天想改成 `= CancellationToken.None` 或者别的，记得**重新编译所有调用方**。

这也是 C# 不允许默认值是 `new` 表达式的原因——必须是编译期常量。

### 为什么不写成 `CancellationToken?`

```csharp
public Task<string?> EchoAsync(string text, CancellationToken? cancellationToken = null)
```

没必要。`default(CancellationToken)` 已经完整表达了"不取消"的语义，用 `Nullable` 反而多一层拆包，调用方还得 `.Value`。

**`CancellationToken` 这个类型的设计本身就把"不取消"做成了一个合法的状态**，不需要 null 来表达"没有"。

---

# 主题 D：数据建模

## D1. `record`（Q5）

C# 9 引入的"以数据为中心的类"，编译器自动生成一整套样板。

**项目里的用法（位置记录）：**

```csharp
// src/SocketTest.Protocol/Messages.cs
public sealed record LoginRequest(string UserName, string Password);
public sealed record LoginResponse(bool Success, string? Token, string? Error);
public sealed record EchoPayload(string Text);
public sealed record TextMessage(string From, string Text, DateTimeOffset SentAt);
public sealed record ServerNotice(string Text, string Level = "info");
```

**这一行 `LoginResponse` 等价于手写：**

```csharp
public sealed class LoginResponse : IEquatable<LoginResponse>
{
    public bool Success { get; init; }
    public string? Token { get; init; }
    public string? Error { get; init; }

    public LoginResponse(bool success, string? token, string? error) { ... }

    protected LoginResponse(LoginResponse original) { ... }        // 拷贝构造，with 靠它
    public void Deconstruct(out bool s, out string? t, out string? e) { ... }

    public bool Equals(LoginResponse? other) =>                     // 值相等：逐字段
        other is not null && Success == other.Success && Token == other.Token && Error == other.Error;
    public override int GetHashCode() => HashCode.Combine(Success, Token, Error);
    public static bool operator ==(LoginResponse? l, LoginResponse? r) => ...;

    public override string ToString() => $"LoginResponse {{ Success = {Success}, Token = {Token}, Error = {Error} }}";
}
```

**三个本质区别（与普通 class）：**

| 特性 | 说明 |
|---|---|
| **值相等** | `new LoginResponse(true,"a",null) == new LoginResponse(true,"a",null)` → `true` |
| **`init`-only** | 属性只能在构造/对象初始化器里赋值，之后 `resp.Success = false` 直接编译错误（CS8852） |
| **`with` 表达式** | `notice with { Level = "warn" }` → 浅拷贝 + 改字段，原对象不动 |

**为什么这里用 `record` 而不用 `class`：**

- 纯数据 DTO，手写 class 要 20+ 行样板
- 值相等让测试断言天然可用
- `init` 编译期保证"消息发出后不会被偷偷改"（和 `Frame` 用 `ReadOnlyMemory` 是同一思路）
- `ToString()` 自带内容，调试友好

**为什么 `Frame` 不用 `record`：**

- 在热路径上（每条消息一个），`record` 是引用类型会在堆上分配
- 不需要值相等语义和 `with`
- 字段少，`readonly struct` 传值成本低

**`sealed` 的作用：**

1. 序列化安全——继承后 STJ 反序列化到基类会丢派生字段
2. `Equals` 有"运行时类型必须完全一致"的检查，继承层数多了容易意外
3. 明确表达"这是叶子 DTO，不打算扩展"

**`record` 家族：**

| 语法 | 版本 | 类型 | 注意 |
|---|---|---|---|
| `record` / `record class` | C# 9 | 引用 | 属性是 `get; init;` |
| `record struct` | C# 10 | 值 | 属性是 `get; set;`，**默认可变** |
| `readonly record struct` | C# 10 | 值 | 才有 C# 9 record 的不可变语义 |

**四个坑：**

1. **数组成员是引用比较** —— `new Bag(new byte[]{1}) == new Bag(new byte[]{1})` 结果是 `false`
2. **默认参数对 JSON 反序列化无效** —— `ServerNotice(string Text, string Level = "info")` 里的 `"info"` 只在 C# 调用时生效；对端 JSON 缺 `level` 字段时，STJ 按属性名匹配后传 `default`，得到的是 `null` 不是 `"info"`
3. **`GetHashCode` 逐字段计算** —— 每条消息都算不便宜，只在真要放进 `Dictionary`/`HashSet` 时才值得
4. **`init`-only ≠ 数据合法** —— 只保证"你不能改"，不保证内容正确。`Token` 可能是 `null`，校验要另做

## D2. DTO（Q6）

**Data Transfer Object，数据传输对象。** 出自 Martin Fowler《Patterns of Enterprise Application Architecture》。

**三条判定标准：**

1. **只装数据**，不含业务规则
2. **形状由通信契约决定**，而非内部业务模型
3. **可序列化**，能变成字节流

**项目里的对应关系：**

| 类型 | 是不是 DTO | 原因 |
|---|---|---|
| `LoginRequest` 等 5 个 record | ✅ | 纯数据，形状 = 线上 JSON 的形状 |
| `Frame` | ❌ | 协议帧的**运行时表示**，传输层载体而非业务数据 |
| `ClientOptions` | ❌ | 本地配置，不上网 |
| `ConnectionStateMachine.State` | ❌ | 领域概念 |

```
业务逻辑 → DTO（Messages.cs）→ PayloadCodec → byte[]
                  ↑
           序列化边界，跨过去就是"网线上的字节"
```

**DTO vs 领域模型：**

| | DTO | 领域模型 / Entity |
|---|---|---|
| 有行为吗 | 无 | 有 |
| 谁决定形状 | 通信契约 | 业务规则 |
| 可序列化吗 | 必须 | 一般不（会漏内部状态） |
| 改字段意味着 | **协议变更** | 业务逻辑变更 |
| 相等语义 | 值相等 | 通常按 Id |

**关键认知**：DTO 一旦发到线上就是**公开契约**。加字段安全（老客户端忽略即可，JSON 天然支持）；删字段或改类型是破坏性变更。

**为什么必须区分**：避免领域模型泄漏到线上（密码哈希、权限位被一起序列化出去）。DTO 是白名单。

## D3. JSON 反序列化怎么切分字段（Q14）

```csharp
// src/SocketTest.Protocol/FrameCodec.cs:102 附近
var request = PayloadCodec.FromUtf8<LoginRequest>(frame.Payload.Span);
```

### 前提纠正：这里没有"按字段长度切分"

`PayloadCodec.FromUtf8<T>` 内部是：

```csharp
// src/SocketTest.Protocol/FrameCodec.cs:113
public static T? FromUtf8<T>(ReadOnlySpan<byte> payload)
    => payload.IsEmpty ? default : JsonSerializer.Deserialize<T>(payload, Options);
```

**这是 JSON 解析，不是二进制拆包。** 你不需要知道每个字段占多少字节——因为 JSON 是**自描述格式**。

### `frame.Payload` 里到底躺着什么

假设发的是 `new LoginRequest("alice", "1")`，payload 是这段文本的 **UTF-8 字节**：

```json
{"userName":"alice","password":"1"}
```

对应字节（十六进制）：

```
7B               {
22               "
75 73 65 72 4E 61 6D 65    u s e r N a m e
22               "
3A               :
22               "
61 6C 69 63 65    a l i c e
22               "
2C               ,
22 70 61 73 73 77 6F 72 64 22    " p a s s w o r d "
3A 22 31 22                      : " 1 "
7D               }
```

全是可打印 ASCII。**分隔信息就在数据本身里**，不在外部长度表里。

### 切分靠语法，不靠长度

| 元素 | 定界方式 |
|---|---|
| 对象 | `{` … `}` |
| 字段 | `"名字"` + `:` + 值，字段之间用 `,` |
| **字符串** | **双引号 `"` 定界**，读到下一个「未转义的 `"`」为止 |
| 数字 | 读到第一个非数字字符为止（`0-9 . e E + -`） |
| 布尔 / null | 固定字面量 `true` / `false` / `null` |
| 嵌套 | 递归下降，靠括号配对 |

字符串之所以不需要长度，是因为：**`"` 就是终止符**。

```json
"alice"     → 读到第二个 " 结束，5 个字节
"he said \"hi\""   → 遇到 \" 是转义，跳过，继续读到真正的结尾
"中文"      → UTF-8 是 6 个字节，但解析器不关心字节数，只找结尾的 "
```

转义序列（`\"` `\\` `\n` `\uXXXX`）由解析器在引号内部处理，不影响定界。

### 核心洞察：两层用了两种完全不同的切分机制

```
┌─────────────────────┬──────────────────────────────────┐
│  16 字节帧头         │   payload (N 字节)               │
│  ... payloadLen=N   │  {"userName":"alice",...}         │
└─────────────────────┴──────────────────────────────────┘
        ↑ 长度前缀切分              ↑ 语法定界切分
        （不知道内容也能切）          （必须理解内容才能切）
```

| 层次 | 切分依据 | 为什么这样设计 |
|---|---|---|
| **帧层**（`FrameReader`） | 头里的 `payloadLen` 长度前缀 | 必须在**完全不懂内容**的前提下快速定位边界；还要做长度上限校验防内存攻击 |
| **负载层**（`PayloadCodec`） | JSON 语法（引号/冒号/逗号） | 要灵活、可读、字段能增删；人肉调试方便 |

两层职责分明：帧层保证"把完整的一块交给你"，负载层保证"把这块解释成对象"。

### 如果换成二进制协议

那就必须**长度前缀**了，因为没有引号这种定界符：

**protobuf**（length-delimited）
```
tag(1B) + length(1-5B varint) + UTF-8 bytes
```

**MessagePack**
```
0xa5          ← 字符串头：0xa5 = fixstr, 长度 5
61 6C 69 63 65   ← "alice"
```

**手写格式**
```
[4 字节大端长度][N 字节 UTF-8]
```

这些格式的解析器先读长度、再按长度切片，最后 `Encoding.UTF8.GetString(bytes)` 还原成 `string`。

**所以你的困惑其实是"二进制思维"套在文本格式上了**——JSON 里 `string` 的边界由语法定义，跟它是不是引用类型毫无关系。

### 项目的序列化配置

```csharp
private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
```

`JsonSerializerDefaults.Web` 一次性打开三项：

| 配置 | 效果 |
|---|---|
| `PropertyNamingPolicy = CamelCase` | 序列化时 `UserName` → `userName` |
| `PropertyNameCaseInsensitive = true` | 反序列化时 `userName` / `UserName` / `USERNAME` 都能匹配 |
| `NumberHandling = AllowReadingFromString` | `"123"` 也能读成 `int` |

这就是为什么 payload 里是 `userName`（小写开头）而不是 `UserName`。

### record 是怎么被填充的

`LoginRequest` 是位置记录：

```csharp
public sealed record LoginRequest(string UserName, string Password);
```

STJ 的反序列化流程：

1. 把 `ReadOnlySpan<byte>` 当 UTF-8 文本，用 `Utf8JsonReader` 流式扫描（**不经过 `string` 中间转换**）
2. 找到 `LoginRequest` 唯一的公开构造函数，识别出参数 `userName`、`password`
3. 逐 token 读取 JSON，按名字（大小写不敏感）匹配到构造参数
4. 读到 `"alice"` 时——引号内的字节直接 `Encoding.UTF8.GetString` 成 `string` 对象
5. 全部参数凑齐后调用构造函数

**`string` 是引用类型这件事在这里完全透明**：JSON 里它就是一段被引号包住的字节，STJ 负责 new 一个 `string`。

⚠️ 缺字段时传 `default`（`null`），不会用 C# 的默认参数值（见 D1 的坑 #2）。

### 代价

| 方案 | 解析方式 | 相对开销 |
|---|---|---|
| JSON | 逐字符扫描 + 字符串分配 | 1× |
| MessagePack | 长度前缀 + 直接拷贝 | ~3–5× 快 |
| protobuf | 长度前缀 + 强类型编码 | ~5–10× 快 |

JSON 慢在两个地方：**逐字符语法扫描**和**每个字符串都要 `new string`**。

这正是 `Messages.cs` 开头注释的意思：

```csharp
// 这是 Lab 阶段用的"业务协议"：负载统一用 UTF-8 JSON，方便肉眼调试。
// 真实项目里换成 MessagePack / protobuf 时，只需要替换 PayloadCodec，帧格式完全不动。
```

**帧格式（二进制长度前缀）不变，只换负载层的序列化器**——因为两层切分机制本来就是独立的。

### 与前面话题的联系

`PayloadCodec.FromUtf8` 接受 `ReadOnlySpan<byte>` 而不是 `string`，正是为了走 **UTF-8 直读**路径：

```
byte[] → ReadOnlyMemory → .Span → Utf8JsonReader → 对象
                                    ↑ 全程不产生中间 string
```

如果改成 `FromUtf8(string json)`，就得先 `Encoding.UTF8.GetString(payload)` 分配一个大字符串——白扔一次转换（呼应 Q1、Q8）。

---

# 主题 E：连接生命周期（Q7）

## E1. EOF 的定义

**End Of File。** 但在 Socket/流式语境下要立刻纠正一个直觉：这里**没有"文件"**，EOF 表示**对端关闭了写方向**。

## E2. 底层发生了什么

```
客户端                        服务端
  │  ──── FIN ──────────→  │   我没有数据要发了
  │  ←─── ACK ───────────  │
  │                         │
  │  此后本端 Read() 返回 0 = EOF
```

- **FIN 只关闭一个方向**，这叫**半关闭**（half-close）。对端说"我不发了"≠"你不能发"。
- 主动半关闭用 `socket.Shutdown(SocketShutdown.Send)`；`Close()` 会同时关两个方向。
- EOF 是**正常信号**，不是错误。

## E3. `Stream.Read` 的返回值语义

| 返回值 | 含义 |
|---|---|
| `> 0` | 读到了这么多字节（**可能少于请求量，不是 EOF**） |
| `0` | 对端已关闭，不会再有数据 |
| 抛异常 | 出错（RST、超时、取消） |

⚠️ 高频误判：把 `read < requested` 当成 EOF。TCP 是字节流，一次返回多少由内核缓冲区决定，**只有返回 0 才是 EOF**。

## E4. 项目里刻意区分的两种 EOF ★

**情况 A：帧边界上的 EOF → 正常关闭**

```csharp
// src/SocketTest.Protocol/FrameReader.cs:33
if (!await ReadFullyAsync(_header, cancellationToken).ConfigureAwait(false))
{
    return null; // 一个字节都没读到 = 对端正常关闭
}
```

上游走优雅关闭：

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:112
var frame = await _reader.ReadFrameAsync(_cts.Token).ConfigureAwait(false);
if (frame is null)
{
    break; // 对端正常关闭
}
```

**情况 B：帧中间的 EOF → 异常**

```csharp
// src/SocketTest.Protocol/FrameReader.cs:70
if (!await ReadFullyAsync(payload, cancellationToken).ConfigureAwait(false))
{
    throw new EndOfStreamException("帧读到一半连接就断了");
}
```

**为什么必须区分：**

| | 含义 | 连接状态 | 处理方式 |
|---|---|---|---|
| 帧边界 EOF | 对端礼貌地说"说完了" | 干净 | 返回 `null`，走重连/退出 |
| 帧中间 EOF | 头部说发 1000 字节，只发了 300 就 FIN | **脏**，缓冲区剩半条消息 | 抛异常，连接不可信 |

后者意味着对端崩溃 / 被 `kill -9` / 网络断。这是**协议错误**，不是正常关闭。

## E5. EOF ≠ 连接断开：三种"断开"

| 现象 | 底层 | 正常？ | 项目里对应 |
|---|---|---|---|
| **EOF** | 收到 FIN，`Read` 返回 0 | ✅ 正常关闭 | 返回 `null` |
| **RST** | 抛 `IOException`（"远程主机强迫关闭了一个现有的连接"） | ❌ 异常 | 异常上抛 → 触发重连 |
| **静默无响应** | 什么都不来，`ReadAsync` 永远挂着 | ❌ 半开连接 | `HeartbeatMonitor` 连续丢失 → `Dead` |

第三种是 `HeartbeatMonitor` 存在的**根本原因**：拔网线、NAT 超时、对端断电时，对方根本发不出 FIN。TCP 自带 keepalive 默认要两小时，必须有应用层心跳。

## E6. 其他地方的 EOF

- `Console.ReadLine()` 返回 `null` = stdin 关闭（Ctrl+Z / Ctrl+D）
- `StreamReader.EndOfStream` 属性，内部 Peek 看是不是 -1
- `EndOfStreamException` 继承自 `IOException`
- TLS 层：EOF 由 `close_notify` alert 表达；对方直接 RST 而没有 `close_notify` 就是 truncation attack

---

# 主题 F：Stream 与 IO 抽象（Q10）

## F1. Stream 从哪来 —— 完整调用链

### 客户端

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:64
var tcp = new TcpClient();
await tcp.ConnectAsync(host, port, timeoutCts.Token);   // ← TCP 三次握手完成

var connection = new ClientConnection(tcp);              // :85
```

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:43
private ClientConnection(TcpClient tcp)
{
    _tcp = tcp;
    _tcp.NoDelay = true;        // 关掉 Nagle
    _stream = tcp.GetStream();  // ★ Stream 在这里诞生
    _reader = new FrameReader(_stream);
    ...
}
```

读用 `_reader`，写用同一份 `_stream`：

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:150
await _stream.WriteAsync(buffer, _cts.Token).ConfigureAwait(false);
```

### 服务端

```csharp
// labs/Lab0_MockServer/MockServer.cs:21
_listener = new TcpListener(IPAddress.Any, port);
var tcp = await _listener.AcceptTcpClientAsync();   // 每个新连接一个 TcpClient
var session = new ClientSession(tcp, this);
```

```csharp
// labs/Lab0_MockServer/ClientSession.cs:26
public ClientSession(TcpClient tcp, MockServer server)
{
    _tcp = tcp;
    _tcp.NoDelay = true;
    _stream = tcp.GetStream();          // ★ 同上
    _reader = new FrameReader(_stream);
```

### 一句话

**`Stream` 来自 `TcpClient.GetStream()`，返回类型是 `NetworkStream`，它是对底层 `Socket` 的流式封装。**

```
Socket                     真正的系统调用出口
   ↑ 被包装
NetworkStream : Stream     ownsSocket = true，Dispose 时连带关 socket
   ↑ 被当作
Stream                     协议层只认这个抽象类型
```

## F2. NetworkStream 的特殊性

| 能力 | 说明 |
|---|---|
| 双向 | 同时支持 Read 和 Write（TCP 全双工） |
| **不支持** `Seek`/`Position`/`Length` | 网络数据是流动的，回不去，访问抛 `NotSupportedException` |
| 不缓冲 | 自己不缓冲，每次 Write 直接进内核发送缓冲区 |
| 无默认超时 | `ReadTimeout`/`WriteTimeout` 默认 -1（无限），超时靠 `CancellationToken` |
| `DataAvailable` | 非阻塞查"有没有可读字节"，但**不能**用来判断"有没有完整帧" |

## F3. 作用一：让协议层不依赖 TCP（依赖倒置）

协议层的两个入口签名：

```csharp
// src/SocketTest.Protocol/FrameReader.cs:20
public FrameReader(Stream stream, int maxPayloadSize = ProtocolConstants.MaxPayloadSize)

// src/SocketTest.Protocol/FrameCodec.cs:94
public static async ValueTask WriteAsync(Stream stream, MessageType type, ...)
```

**参数是 `Stream`，不是 `NetworkStream`，更不是 `Socket`。**

带来的实际好处：

1. **测试可以塞 `MemoryStream`** —— 不起真服务器就能测粘包/拆包
2. **Lab-C6 上 TLS 零改动**
   ```csharp
   var ssl = new SslStream(tcp.GetStream());
   await ssl.AuthenticateAsClientAsync(...);
   var reader = new FrameReader(ssl);   // 协议层完全不知道底下加了密
   ```
3. **可插入中间层** —— 套 `GZipStream` 或自定义 `Stream` 子类即可做压缩/流量统计
4. **换传输方式时改动集中在一处** —— 将来接 WebSocket/UDP 只需换传入的 `Stream`

这是**依赖倒置**：高层模块（帧编解码）依赖抽象（`Stream`），不依赖细节（`Socket`）。

## F4. 作用二：把 Socket 的"字节原语"升级成"流语义"

| | `Socket` | `Stream`（`NetworkStream`） |
|---|---|---|
| 读写参数 | `byte[] + offset + count` | `Memory<byte>` / `ReadOnlyMemory<byte>` |
| 部分发送 | **可能发生**，必须自己循环 | 契约保证**全写**，否则抛异常 |
| EOF 判定 | `Receive` 返回 0 | `Read` 返回 0 |
| 异步模型 | `ReceiveAsync` + `SocketAsyncEventArgs` | 标准 `ValueTask` + `CancellationToken` |
| 可替换性 | 绑定 TCP | 可套 SSL / 压缩 / 内存流 |

`FrameCodec.WriteAsync` 能只写一句 `await stream.WriteAsync(...)` 就完事（见 Q4），正是因为 `Stream` 在契约层面吃掉了"可能只发一半"这个坑。

## F5. 生命周期

```
new TcpClient()
    ↓ ConnectAsync()              三次握手，socket 就绪
TcpClient.GetStream()             返回 NetworkStream（内部缓存，多次调用是同一对象）
    ↓
ReadAsync / WriteAsync            每次都是真正的系统调用
    ↓
Shutdown(SocketShutdown.Both)     主动发 FIN，走四次挥手
    ↓
Close() / Dispose()               释放句柄
```

项目里的关闭顺序（`ClientConnection.Shutdown` + `DisposeAsync`）：

```
1. _closed.TrySetResult()              幂等保护：读循环/写循环/Abort 三路都会进来
2. _sendQueue.Writer.TryComplete()     让写循环自然退出
3. _cts.Cancel()                       取消挂起中的 ReadAsync
4. _tcp.Client?.Shutdown(Both)         主动发 FIN
5. _tcp.Close()                        关句柄
6. _stream.Dispose() / _tcp.Dispose()  兜底
```

⚠️ 第 3 步必须在第 4 步之前：`ReadAsync` 正挂在 I/O 上，不先取消它，`Shutdown` 之后读循环可能走到异常路径。

## F6. 五个常见坑

**① 用 `stream.Length` 判断"还剩多少数据"**
→ `NetworkStream` 直接抛 `NotSupportedException`。网络流没有总长度概念。

**② 以为 `DataAvailable == true` 就能读到完整消息**
→ 它只表示"内核缓冲里有字节"，可能只有半个帧头。要用 `FrameReader.ReadFrameAsync`。

**③ 反复调 `TcpClient.GetStream()`**
→ .NET 内部是 `_stream ??= new NetworkStream(Client, true)`，返回同一对象，不会重复分配；但 **Dispose 之后就不能再用**。项目里只在构造函数取一次并持有，是正确写法。

**④ 忘了 `NoDelay = true`**
→ Nagle 算法会攒小包再发，16 字节帧头 + 小负载这种极小包可能被延迟到 200ms。两端构造函数都设了 `_tcp.NoDelay = true;`，这是游戏/IM 协议的标配。

**⑤ 关流不关 socket（或反过来）**
→ `TcpClient.GetStream()` 内部用 `new NetworkStream(Client, ownsSocket: true)`，`stream.Dispose()` 会连带关 socket；`tcp.Close()` 也会让流失效。项目里的做法是**先 Shutdown，再两边都 Dispose**，最稳。

---

# 主题 G：Stream 通用模型（Q11，脱离项目）

> 主题 F 讲的是"项目里的 Stream 从哪来"，本节完全不依赖项目，讲 `System.IO.Stream` 本身。

## G1. 本质

`System.IO.Stream` 是 .NET 对「**有序字节序列**」的抽象基类。

注意它抽象的不是"文件"，也不是"网络"——**只是字节的流动**。

必须实现的成员只有三个：

```csharp
public abstract int  Read(Span<byte> buffer);          // → 实际读到多少字节
public abstract void Write(ReadOnlySpan<byte> buffer);
public abstract bool CanRead { get; }
public abstract bool CanWrite { get; }
public abstract bool CanSeek { get; }
```

其余是虚成员：`Flush()`、`Seek()`、`Position`、`Length`、`SetLength()`、`CopyTo()`、`ReadAsync()`、`WriteAsync()` 等。

## G2. 为什么用 Stream 而不是 `byte[]`

三个理由，缺一不可：

1. **数据可能比内存大** —— 4GB 文件不能 `File.ReadAllBytes`
2. **数据可能还没生成完** —— 网络、管道、实时编码
3. **数据源/目的地种类爆炸** —— 文件、内存、网络、管道、压缩、加密、标准输入输出。如果每个 API 针对具体类型写，组合数会失控

Stream 提供的是**增量处理**模型：一次处理一块，处理完就扔。
内存占用是 **O(缓冲区大小)**，不是 **O(数据大小)**。

## G3. 两个正交的分类维度

### 维度 1：能不能回退（`CanSeek`）

| 可 Seek | 不可 Seek |
|---|---|
| `FileStream` | `NetworkStream` |
| `MemoryStream` | `SslStream` / `CryptoStream` |
| `UnmanagedMemoryStream` | `GZipStream` / `DeflateStream` / `BrotliStream` |
| `BufferedStream`（取决于内层） | `PipeStream`、标准输入输出 |

不可 Seek 的流上访问 `Position` / `Length` / `Seek()` 一律抛 `NotSupportedException`。数据流过就没了。

### 维度 2：是不是装饰流

| 类型 | 含义 | 例子 |
|---|---|---|
| **终端流** | 直接对接数据源/目的地 | `FileStream`、`MemoryStream`、`NetworkStream` |
| **装饰流** | 包着另一个 `Stream`，加工数据 | `BufferedStream`、`GZipStream`、`CryptoStream`、`SslStream` |

装饰流可以层层嵌套，这是 Stream 设计最漂亮的地方：

```
FileStream → BufferedStream → GZipStream → CryptoStream → 调用方
```

写入时从里往外裹，读取时从外往里剥。每一层只关心自己那件事。

## G4. 四条契约（最容易搞错的地方）

### 契约 1：`Read` 返回 0 才表示结束

```csharp
int n = stream.Read(buffer, 0, 1000);
// n == 300  ← 这很正常！只代表"这次给了 300 字节"
// n == 0    ← 这才是"没有更多数据了，且以后也不会有"
```

⚠️ **`Read` 返回值小于请求长度 ≠ 结束**。这是新手第一大坑。
TCP、文件、管道都可能"部分返回"。

所以永远要循环读。.NET 6+ 提供了：

```csharp
stream.ReadExactly(buffer);        // 读满，否则抛 EndOfStreamException
stream.ReadAtLeast(buffer, minimumBytes);   // 至少读 N
stream.CopyTo(dest);                       // 内部就是循环
```

### 契约 2：`Write` 成功 ≠ 数据落盘

`Write` 成功只表示数据被"接受"了（进了 Stream 自己的缓冲或 OS 缓冲）。

- 要保证进入 OS → `Flush()`
- 要保证落到物理磁盘 → `FileStream.Flush(flushToDisk: true)`

不 Flush 就在 `using` 里抛异常，数据可能丢。

### 契约 3：不要 Dispose 别人的流

包装流的构造函数几乎都有 `leaveOpen` 参数：

```csharp
using var gzip = new GZipStream(fileStream, CompressionMode.Decompress, leaveOpen: true);
```

不传 `leaveOpen: true` 的话，`gzip.Dispose()` 会**连带关掉** `fileStream`。
嵌套使用时要么显式 `leaveOpen`，要么只让最外层负责关闭。

### 契约 4：Stream 不是线程安全的

多线程同时读写同一个 Stream 会导致数据交错。
必须自己串行化——加锁、单写者队列、或 SemaphoreSlim。

## G5. 家族谱

```
Stream (abstract)
├── 存储类
│   ├── FileStream
│   ├── MemoryStream
│   ├── UnmanagedMemoryStream
│   └── IsolatedStorageFileStream
├── 网络 / 管道类（都不可 Seek）
│   ├── NetworkStream
│   └── PipeStream → AnonymousPipeStream / NamedPipeServerStream / NamedPipeClientStream
├── 装饰类
│   ├── BufferedStream
│   ├── GZipStream / DeflateStream / BrotliStream / ZLibStream
│   ├── CryptoStream
│   └── SslStream : AuthenticatedStream
├── 控制台
│   └── Console.OpenStandardInput() / OpenStandardOutput() / OpenStandardError()
└── Stream.Null        黑洞流：写进去丢弃，读永远返回 0
```

`Stream.Null` 很实用：需要传一个 Stream 但不想真处理数据时（比如测性能、丢日志）。

## G6. 四种典型用法模式

**① CopyTo —— 最常用的搬运**

```csharp
await input.CopyToAsync(output);
```

内部帮你处理了"部分读取"和循环。默认缓冲区 81920 字节，高频调用可以用重载传 `ArrayPool` 的缓冲区避免分配。

**② 装饰叠加 —— 读一个加密压缩过的文件**

```csharp
using var file   = File.OpenRead("data.gz.enc");
using var crypto = new CryptoStream(file, decryptor, CryptoStreamMode.Read, leaveOpen: true);
using var gzip   = new GZipStream(crypto, CompressionMode.Decompress, leaveOpen: true);
using var reader = new StreamReader(gzip);

var text = reader.ReadToEnd();
```

**③ MemoryStream 当临时缓冲**

```csharp
using var ms = new MemoryStream();
serializer.Serialize(ms, obj);
return ms.ToArray();   // 注意：这里又复制了一次
```

⚠️ `MemoryStream` 内部就是 `byte[]`，扩容时翻倍复制。大对象别拿它当临时缓冲，用 `ArrayPool` + 自定义 `IBufferWriter<byte>` 更合适。

**④ 直接消费 `Span`**

```csharp
Span<byte> head = stackalloc byte[16];
stream.ReadExactly(head);   // 零堆分配读帧头
```

## G7. 与其他抽象的关系

| 抽象 | 单位 | 定位 |
|---|---|---|
| `Stream` | 字节 | 有序字节流，增量、可组合 |
| `TextReader` / `TextWriter` | 字符 | 在 Stream 之上加编码 |
| `BinaryReader` / `BinaryWriter` | 原始类型 | 在 Stream 之上做类型转换（注意字节序，默认小端） |
| `PipeReader` / `PipeWriter` | 字节块（多段） | 为高吞吐服务器设计，支持多段缓冲 + 背压 |
| `Span<T>` / `Memory<T>` | 内存视图 | **不是 IO 抽象**，是内存抽象；Stream 的读写参数用它 |

`System.IO.Pipelines`（`PipeReader`）是现代替代品，专为网络服务器优化——它解决了 Stream 的两个限制：

1. Stream 每次 `Read` 都要把数据拷进你的缓冲区；Pipe 可以给你**直接指向内部缓冲的只读视图**
2. Stream 一次只能有一个待处理的 `ReadAsync`；Pipe 支持更灵活的消费模型

代价是 API 复杂，且不适合"一次读完一个文件"这种场景。

## G8. 一句话总结

**Stream 是"字节的传送带"：你只能一次放一块、拿一块，不知道它从哪来、到哪去，也不承诺下一块什么时候到。**

---

# 主题 H：连接建立 —— TcpListener / TcpClient / Socket（Q12）

## H1. 三个类型的分工

| 类型 | 职责 | 一句话 |
|---|---|---|
| `Socket` | 一切底层能力的真正实现 | 系统调用的出口 |
| `TcpListener` | **等新连接进来** | 只有 `Start` / `Accept*` / `Stop`，**不能读写** |
| `TcpClient` | **持有并操作一条已建立的连接** | 有 `GetStream` / `NoDelay` / `Close`，**不能 Accept** |

职责是严格分开的：会 accept 的不会读写，会读写的不会 accept。

## H2. 服务端那个 TcpClient 从哪来

```csharp
// labs/Lab0_MockServer/MockServer.cs:51
tcp = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
```

```csharp
// labs/Lab0_MockServer/MockServer.cs:63
var session = new ClientSession(tcp, this);
```

它**不是 `new TcpClient()`**，而是 `AcceptTcpClientAsync()` 的返回值。

对比客户端：

```csharp
var tcp = new TcpClient();                 // 主动方：先造对象
await tcp.ConnectAsync(host, port);        // 再连出去
```

两种来源，同一个类型：

```
客户端：new TcpClient() → ConnectAsync()         主动发起
服务端：listener.AcceptTcpClientAsync()          被动接收（内部 new TcpClient(acceptedSocket)）
```

## H3. 为什么类型能一样 —— TCP 连接的对称性

三次握手完成后，两端各有一个 socket 句柄，**地位完全对等**：

- 双方都能主动 `Send`
- 双方都能主动 `Shutdown` / `Close`
- 双方都能收到 FIN 或 RST

所谓"客户端/服务端"只是**谁先调用了 `connect()`** 这个行为差异，不是对象类型上的差异。

所以 .NET 没有必要造两个类型。`TcpClient` 的 `Client` 指的是「**连接端点**」（connection endpoint），不是「客户端角色」。

## H4. TcpClient 只是 Socket 的门面

```csharp
// 概念上的实现
public class TcpClient : IDisposable
{
    public Socket Client { get; set; }                       // 底层 socket
    public bool NoDelay { get; set; }                        // 转发到 Socket.NoDelay
    public NetworkStream GetStream() => _stream ??= new NetworkStream(Client, true);
}
```

`AcceptTcpClientAsync` 的内部大致是：

```csharp
Socket socket = await _serverSocket.AcceptAsync(cancellationToken);
return new TcpClient(socket);    // 直接接管，不新建连接
```

**所以"服务端也用 TcpClient"= "把 accept 出来的 socket 包进 TcpClient"**，仅此而已。

## H5. 命名确实糟糕

官方文档说 `TcpClient` "Provides client connections for TCP network services"，这个描述容易让人以为它是"客户端专用"。

实际上叫它 `TcpConnection` 或 `TcpSocket` 会贴切得多。.NET 里同类命名混乱还有不少（`HttpClient` 才是真客户端，`TcpClient` 不是）。

## H6. 项目里两端的对称性

| 环节 | 客户端 `ClientConnection` | 服务端 `ClientSession` |
|---|---|---|
| 拿到 TcpClient | `new TcpClient()` + `ConnectAsync()` | `listener.AcceptTcpClientAsync()` |
| 关 Nagle | `_tcp.NoDelay = true` | `_tcp.NoDelay = true` |
| 取流 | `tcp.GetStream()` | `tcp.GetStream()` |
| 建读器 | `new FrameReader(_stream)` | `new FrameReader(_stream)` |
| 关闭 | `Client.Shutdown(Both)` + `Close()` | 只有 `Close()` |

除了最后一行，几乎逐字相同——因为连接建立后两端就是同一件事。

## H7. 一个值得注意的不对称

服务端的 `Close()` 只有一句 `_tcp.Close()`，没有先 `Shutdown(SocketShutdown.Both)`：

```csharp
// labs/Lab0_MockServer/ClientSession.cs:185
public void Close()
{
    try { _tcp.Close(); }
    catch { }
}
```

而客户端 `Shutdown` 是两段式的（先 `Shutdown(Both)` 发 FIN，再 `Close()` 释放句柄）。

服务端这样写在 `KickAll()` 的语义下是**合理的**：

```csharp
// labs/Lab0_MockServer/MockServer.cs:121
/// <summary>模拟服务端重启 / 强制踢人。客户端的重连逻辑就靠这个验证。</summary>
public int KickAll()
```

服务端直关连接、不跟对端客气，客户端更可能拿到异常而不是优雅 EOF——正好用来验证客户端在**异常断开**路径上的重连逻辑，比优雅关闭更接近真实事故。

如果要优雅关闭，服务端也应该：

```csharp
_tcp.Client?.Shutdown(SocketShutdown.Both);
_tcp.Close();
```

## H8. 不想用 TcpClient 的替代写法

`TcpListener` 还有另一个方法，直接给裸 `Socket`：

```csharp
Socket socket = await _listener.AcceptSocketAsync(cancellationToken);
var stream = new NetworkStream(socket, ownsSocket: true);
var reader = new FrameReader(stream);
```

三者能力完全等价，`TcpClient` 只是省掉了 `new NetworkStream(...)` 这行和几个便利属性。

什么时候值得跳过 `TcpClient`：

- 需要 `Socket` 的精细控制（`ReceiveBufferSize`、`LingerState`、`SetSocketOption`、`SocketAsyncEventArgs`）
- 需要 `AcceptAsync` 返回的 `Socket` 直接配置（比如 `TcpListener.Server` 上设 `NoDelay` 也会继承）
- 做高性能服务器，直接用 `Socket` + `System.IO.Pipelines` 避免 `NetworkStream` 的中间层

---

# 主题 I：可空值类型 Nullable&lt;T&gt;（Q13）

## I1. 现象

```csharp
// labs/Lab0_MockServer/ClientSession.cs:42
var frame = await _reader.ReadFrameAsync(cancellationToken).ConfigureAwait(false);
if (frame is null)
{
    return; // 客户端正常关闭
}

// :51
_server.Log($"[丢包] {Describe()} 的 {frame.Value.Type} 被丢弃");

// :56
await HandleAsync(frame.Value, cancellationToken).ConfigureAwait(false);
```

直觉上 `frame` 已经是一个 Frame 对象了，`frame.Type` 应该能直接访问。但写 `frame.Type` 会报错：

```
CS1061: 'Frame?' does not contain a definition for 'Type'
```

## I2. 根因：`frame` 的类型不是 `Frame`

`ReadFrameAsync` 的签名是：

```csharp
// src/SocketTest.Protocol/FrameReader.cs:30
public async ValueTask<Frame?> ReadFrameAsync(CancellationToken cancellationToken)
```

`Frame` 是 `readonly struct`（**值类型**），所以 `Frame?` **不是**「可能为 null 的 Frame」，而是另一个完整的类型：

```csharp
Frame?  ≡  System.Nullable<Frame>
```

`Nullable<T>` 的定义（简化）：

```csharp
public struct Nullable<T> where T : struct
{
    private readonly bool _hasValue;
    private readonly T _value;

    public bool HasValue => _hasValue;
    public T Value => _hasValue ? _value : throw new InvalidOperationException("Nullable object must have a value.");
}
```

所以 `frame` 是一个**包装盒**：`{ HasValue, 内部的 Frame }`。
盒子上没有 `Type`、没有 `Seq`、没有 `Payload`——**要先用 `.Value` 把里面的 `Frame` 取出来**。

## I3. 核心洞察：值类型的 `T?` ≠ 引用类型的 `T?`

这是最容易混淆的地方。同样写 `Frame?`，行为完全不同：

| | `Frame?`（**值**类型） | `Frame?`（**引用**类型，假设改成 record class） |
|---|---|---|
| 真实类型 | `Nullable<Frame>` | `Frame`（只是可空标注） |
| `frame.Type` 能编译吗 | ❌ CS1061 | ✅ 能 |
| `if (frame is null) return;` 之后 | 仍然要 `.Value` | 可以直接 `frame.Type` |
| 编译器做了什么 | 无类型收窄 | 做 null-state 流分析，标注为 not-null |
| 内存 | `Frame` + 1 字节标志（对齐后可能多 8 字节） | 一个引用（8 字节） |

**原因**：值类型的 `Nullable<T>` 是**另一个 struct**，编译器不会因为你检查过 `HasValue` 就改变变量的静态类型。
引用类型的 `T?` 只是元数据上的可空标注，**类型还是 T**，所以 null 检查之后成员访问直接放行。

一句话：

> **值类型的 `?` 改变的是类型；引用类型的 `?` 改变的是标注。**

## I4. 那为什么 `if (frame is null) return;` 不生效

因为它只是**运行时保证**，不是**编译期类型转换**。

那段代码在语义上是：

```csharp
if (!frame.HasValue) return;   // 编译器知道这里 HasValue == true
// 但 frame 的类型仍然是 Nullable<Frame>，不会自动变身成 Frame
```

C# 的流分析（nullable flow analysis）只跟踪**引用类型的 null 状态**，不改变值类型变量的类型。

## I5. `.Value` 安全吗

安全，因为前面已经挡过了：

```csharp
if (frame is null) return;              // ← 保证后续 HasValue == true
...
await HandleAsync(frame.Value, ...)     // ← 不会抛
```

如果**没有**那个检查，`frame.Value` 在 `HasValue == false` 时抛 `InvalidOperationException`，消息是 "Nullable object must have a value."。

`.Value` 和 `is null` 是配套的：**先判断，再取值**。

## I6. 三种等价写法，推荐哪种

**写法 A：`if (frame is null) return;` + 反复 `.Value`**（项目当前写法）

优点：直白。
缺点：每处都得写 `.Value`，出现次数多了啰嗦。

**写法 B：引入局部变量**

```csharp
var frame = await _reader.ReadFrameAsync(ct);
if (frame is null)
{
    return;
}

var f = frame.Value;          // 只写一次
await HandleAsync(f, ct);
```

**写法 C：模式匹配一次搞定（最优雅）**

```csharp
if (frame is not { } f)       // 等价于 "没有值就 early return"，同时把值解到 f
{
    return;
}

await HandleAsync(f, ct);     // 直接用 f，不需要 .Value
```

`is not { }` 是属性模式对 `Nullable<T>` 的特殊处理：它同时完成「检查 HasValue」和「解包」两件事。

也可以用显式类型模式：

```csharp
if (frame is not Frame f) return;
```

三种写法完全等价，选择看团队风格。项目当前用 A，因为它最贴近"读一条帧 → 判断是不是 null → 处理"的思维流。

## I7. 为什么不用 `GetValueOrDefault()`

```csharp
await HandleAsync(frame.GetValueOrDefault(), ct);   // ❌ 危险
```

没有值时它返回 `default(Frame)`，也就是：

| 属性 | 值 |
|---|---|
| `Type` | `(MessageType)0` ← 一个未定义的枚举值 |
| `Seq` | `0` |
| `Flags` | `0` |
| `Payload` | `default(ReadOnlyMemory<byte>)`，`Length == 0` |

**它不抛异常，而是静默地产生一条"空消息"**，然后进入 `HandleAsync` 的 `default` 分支，给对端回一条"未知消息类型: 0"。

`.Value` 的 fail-fast 在这里是**正确选择**：错误要立刻暴露，不要变成下游的诡异行为。

## I8. 如果 `Frame` 改成 `record class` 会怎样

```csharp
public sealed record Frame(...);   // 引用类型
```

那么：

```csharp
var frame = await _reader.ReadFrameAsync(ct);   // frame 类型是 Frame?（只是标注）
if (frame is null) return;
frame.Type          // ✅ 直接可用，不需要 .Value
```

`.Value` 的问题**自动消失**。

但这不是免费的：
- 每条消息都要在堆上分配一个对象 → GC 压力
- `Frame` 会被频繁传递（`FrameReceived?.Invoke(frame.Value)`），值类型传的是副本，引用类型传的是指针
- **`Frame` 在热路径上（每条消息一个），所以选了 `readonly struct`**

这就是取舍：**为了省堆分配，接受了 `Nullable<Frame>` 和 `.Value` 的写法成本**。

（回到 Q1：`Payload` 用 `ReadOnlyMemory<byte>` 而不用 `Span<byte>`，也是同一个"struct 不能带 byref-like 字段"约束链上的事。）

## I9. 一句话记忆

> **`struct` 的 `?` 是给值套了个盒子，取东西必须开盒（`.Value`）；`class` 的 `?` 只是在盒子上贴了张"可能空"的标签，标签撕掉（判 null）就直接能用。**

---

# 主题 J：并发原语与单写者模型（Q17）

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:22
private readonly Channel<byte[]> _sendQueue;

// :24
private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
```

## J1. `Channel<byte[]>`：异步的生产者-消费者队列

### 它解决什么问题

类注释已经写明了根本原因：

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:12
/// 为什么发送一定要走队列：UI 线程发聊天、心跳定时器发 Ping、后台回调发 ACK，
/// 三个线程同时写同一个 Socket 会把数据交叉切碎。单写者是硬约束。
```

**Socket 的写入不是原子的**（尤其大块数据会被拆成多次 `send`）。三个线程同时 `WriteAsync` 同一个 `NetworkStream`，字节会交错：

```
线程A: [帧头A][负载A前半..................]
线程B:                [帧头B][负载B][负载A后半.......]
                       ↑ 对端收到的是一堆乱码
```

**单写者（single-writer）** 是唯一可靠的解法：所有发送请求先排队，由一个专门的循环按顺序写出。

### Channel 的两个半部分

```csharp
Channel<byte[]>            // 队列本体
├── .Writer : ChannelWriter<byte[]>   // 生产端：WriteAsync / TryWrite / TryComplete
└── .Reader : ChannelReader<byte[]>   // 消费端：ReadAsync / TryRead / WaitToReadAsync / ReadAllAsync
```

`Channel<T>` 本身是抽象类，通过静态工厂创建：

```csharp
Channel.CreateUnbounded<T>()               // 无界：内存可能爆炸
Channel.CreateBounded<T>(capacity)         // 有界：满了怎么办由 FullMode 决定
Channel.CreateBounded<T>(options)
```

### 项目里的配置逐项解读

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:51
_sendQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(SendQueueCapacity)
{
    FullMode = BoundedChannelFullMode.Wait, // 队列满了就反压发送方，而不是丢数据
    SingleReader = true,
});
```

| 配置 | 作用 | 为什么这样选 |
|---|---|---|
| `CreateBounded(1024)` | 队列最多 1024 帧 | 无界队列在网络慢于生产时会把内存吃光 |
| `FullMode = Wait` | 满了让 `WriteAsync` 异步等待 | **反压**：宁可让发送方慢下来，也不丢消息。丢一条消息可能破坏协议状态 |
| `SingleReader = true` | 承诺只有一个消费者 | 内部可以用更轻量的同步原语，省掉锁开销 |

其他三个 `FullMode`：

| 模式 | 行为 | 适用场景 |
|---|---|---|
| `Wait`（默认） | `WriteAsync` 挂起等待 | 不能丢数据（项目用的） |
| `DropOldest` | 挤掉最老的 | 实时视频帧、遥测 |
| `DropNewest` | 挤掉最新的 | 同上 |
| `DropWrite` | 丢弃本次写入且**不报错** | 尽力而为的通知 |

⚠️ `SingleReader` / `SingleWriter` 是**承诺不是检查**。违反了不会有异常，而是数据丢失或状态损坏这类未定义行为。项目里 `SendLoopAsync` 是唯一消费者，所以正确。

### 为什么元素是 `byte[]` 而不是 `Frame`

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:97
public ValueTask SendAsync(MessageType type, uint seq, ReadOnlyMemory<byte> payload, CancellationToken ct)
    => _sendQueue.Writer.WriteAsync(FrameCodec.Encode(type, seq, payload.Span), ct);
```

注意：**编码在入队那一刻就完成了**，队列里存的是"已经拼好的完整帧"。

如果队列元素改成 `Frame`，会有**所有权问题**：

- `Frame.Payload` 是 `ReadOnlyMemory<byte>`，可能指向**调用方**的缓冲区
- 入队后调用方立即返回，可能复用或修改那块内存
- 真正的写出去发生在几十毫秒后 → 读到的是被改过的数据

用 `byte[]` 相当于在边界处**复制一份快照**（move semantics），所有权清晰：谁持有谁负责。

这也是"一次拷贝换一次跨线程安全"的取舍（呼应 C6）。

### 消费者端：两级循环

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:146
while (await _sendQueue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
{
    while (_sendQueue.Reader.TryRead(out var buffer))
    {
        await _stream.WriteAsync(buffer, _cts.Token).ConfigureAwait(false);
    }
}
```

- **外层** `WaitToReadAsync`：队列空时**异步挂起，不占线程**，有数据了才恢复
- **内层** `TryRead`：批量取，一次唤醒尽量多处理，减少挂起/恢复次数

这个"外层等 + 内层批量取"是 Channel 消费的标准写法。

### 优雅停止：`TryComplete`

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:175
_sendQueue.Writer.TryComplete();
_cts.Cancel();
```

`TryComplete()` 的含义：**不会再有新数据进来了**。

- 消费者把队列里剩余数据**读完**之后，`WaitToReadAsync` 返回 `false`，循环自然退出
- 之后再 `WriteAsync` 会抛 `ChannelClosedException`

**顺序很讲究**：先 `TryComplete`（尽力发完剩余的），再 `Cancel`（强制中断挂起的操作）。反过来就变成"直接丢弃队列里的积压"。

### 为什么不用 `BlockingCollection<T>`

| | `BlockingCollection<T>` | `Channel<T>` |
|---|---|---|
| 消费 API | `Take()` 同步阻塞 | `ReadAsync()` 异步 |
| 线程占用 | 每个等待者占一个线程 | 不占线程 |
| 批量取 | 无 | `TryRead` 循环 |
| 背压 | 有（有界时） | 有（`FullMode.Wait`） |
| 年代 | .NET 4.0 | .NET Core 3.0+ |

`Channel` 就是 `BlockingCollection` 的异步现代化版本。

### 服务端为什么用 `SemaphoreSlim` 而不是 Channel

对比 `ClientSession.SendAsync`：

```csharp
// labs/Lab0_MockServer/ClientSession.cs:163
await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
try
{
    await FrameCodec.WriteAsync(_stream, type, seq, payload, cancellationToken).ConfigureAwait(false);
}
finally
{
    _sendLock.Release();
}
```

| | 客户端 Channel | 服务端 SemaphoreSlim |
|---|---|---|
| 调用方语义 | 入队即返回，真正发送在后台 | await 到**真正写完**才返回 |
| 能否背压 | 队列满时才反压 | 立刻串行化 |
| 内存占用 | 最多 1024 帧积压 | 无积压 |
| 适用 | UI 线程不能等 | 服务端本来就是异步的，等一下无妨 |

两者都实现了"单写者"，但客户端的目的是**解耦"提交"和"完成"**，服务端没有这个需求。

## J2. `TaskCompletionSource`：手工完成一个 Task

### 它是什么

`Task` 是"将来会有结果"的承诺。通常由 `async` 方法、`Task.Run`、`Task.FromResult` 产生——**你只能等，不能决定它什么时候完成**。

`TaskCompletionSource` 把"完成权"交给你：

```csharp
var tcs = new TaskCompletionSource();
Task task = tcs.Task;          // 给别人等
...
tcs.SetResult();               // 由你来宣布"完成了"
```

它有三个完成方法：

| 方法 | 效果 |
|---|---|
| `SetResult()` | 成功完成 |
| `SetException(ex)` | 失败完成，`await` 时抛 `ex` |
| `SetCanceled()` | 取消完成，`await` 时抛 `TaskCanceledException` |

**每个都有 `Try` 前缀的版本，返回 `bool` 表示"我是不是第一个完成它的"。**

> `TaskCompletionSource`（非泛型，无 `Result`）是 .NET 5 新增的。之前只能写 `TaskCompletionSource<bool>` 这种丑陋形式。

### 项目里的用途：连接死亡信号

```csharp
public Task Completion => _closed.Task;
public bool IsClosed => _closed.Task.IsCompleted;
```

外部就能这样等：

```csharp
await connection.Completion;        // 一直等到连接结束
```

比轮询 `IsClosed` 干净得多。

### 核心技巧：`TrySetResult()` 当幂等锁

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:170
if (!_closed.TrySetResult())
{
    return;
}
// 以下关闭逻辑只会执行一次
```

`Shutdown` 有**四条调用路径**：

1. 读循环异常退出
2. 写循环异常退出
3. 外部 `Abort()`（心跳超时）
4. `DisposeAsync()`

它们可能**同时**发生。`TrySetResult()` 内部用 `Interlocked` 做原子 CAS，**只有第一个调用者拿到 `true`**，其余全部返回 `false` 并立刻 return。

这比 `lock + bool _disposed` 更好：

| | `lock + bool` | `TrySetResult` |
|---|---|---|
| 开销 | 可能进入内核态 | 无锁 CAS |
| 死锁风险 | 持锁时回调别的代码很危险 | 无锁 |
| 额外能力 | 无 | 顺便提供了 `Completion` 这个可 await 的信号 |

**一个字段两个用途**：既是幂等锁，又是可等待的完成信号。

### 最关键的部分：`RunContinuationsAsynchronously`

```csharp
private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
```

**不加这个选项会怎样？**

默认情况下，`TrySetResult()` 会**同步执行**所有 `await _closed.Task` 的续体，**在调用 `TrySetResult` 的那个线程上，而且是在 `TrySetResult()` 返回之前**。

```csharp
// 假设 Shutdown 在读循环线程上执行
if (!_closed.TrySetResult()) return;

// 此刻，所有 await Completion 的代码已经开始跑了！
// 而且是在这个线程上、嵌套在 Shutdown 的调用栈里
Console.WriteLine("关闭完成");   // ← 要等所有续体跑完才轮到这行
```

会带来四类问题：

| 问题 | 说明 |
|---|---|
| **死锁** | 续体里如果去拿 `Shutdown` 已经持有的锁，两边互等 |
| **栈溢出** | 完成链嵌套太长（A 完成触发 B，B 完成触发 C……） |
| **线程饥饿** | 一个线程被长时间占用，其余工作排队 |
| **时序混乱** | `Shutdown` 后半段代码要等所有订阅者跑完 |

加了 `RunContinuationsAsynchronously` 后，续体被**排到线程池**，`TrySetResult()` 立即返回，`Shutdown` 继续往下执行。

> 这是 .NET 里**最容易漏写、后果最隐蔽**的选项之一。微软自己的框架代码里几乎处处都加。

### 完整时序

```
读循环/写循环/Abort/Dispose 任一路径
        │
        ▼
   Shutdown(error)
        │
        ├─ TrySetResult() ── 只有第一个进来的人成功
        │       │
        │       └─→ 完成 Completion ─→ 线程池上唤醒所有等待者
        │
        ├─ TryComplete()  ── 写循环读完积压后退出
        ├─ Cancel()       ── 踢出挂起的 ReadAsync
        ├─ Shutdown(Both) ── 发 FIN
        └─ Close()        ── 释放句柄
```

## J3. 语法补充：`new(...)` 是什么

```csharp
private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
```

`new(...)` 是 **C# 9 的 target-typed new 表达式**（目标类型推断）。

编译器从字段声明的类型 `TaskCompletionSource` 推断出构造函数，等价于：

```csharp
private readonly TaskCompletionSource _closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
```

省掉了重复的类型名。常用场景：

```csharp
List<int> a = new();                      // List<int>
Dictionary<string, int> b = new();        // Dictionary<string, int>
private readonly Tcs _x = new();          // 自定义类型
```

⚠️ 不能用于 `var` 声明（`var x = new();` 无法推断），也不能用于没有无参构造函数或需要明确类型的场景。

## J4. 两个字段的配合

```csharp
private readonly Channel<byte[]> _sendQueue;      // 发送路径的"管道"
private readonly TaskCompletionSource _closed;    // 关闭路径的"信号灯"
```

| | `_sendQueue` | `_closed` |
|---|---|---|
| 职责 | 把多线程的发送请求**串行化** | 把多路径的关闭请求**归一化** |
| 解决问题的关键词 | 单写者 | 幂等 |
| 生命周期 | 构造时创建，`TryComplete` 结束 | 字段初始化，`TrySetResult` 完成 |
| 是否 async 友好 | `WriteAsync` / `WaitToReadAsync` 都不占线程 | `Completion` 可 `await` |
| 共同点 | **都是"用类型承载协调逻辑"，而不是靠 lock 硬扛** | 同左 |

这两个字段合起来，就是 `ClientConnection` 里"读循环 + 写循环 + 外部操作"三方协作的全部机制。

## J5. 什么是无锁 CAS（Q18）

### 一句话

**CAS = Compare-And-Swap（比较并交换）**：一条 **CPU 原子指令**，完成「如果我看到的值还是 A，就把它改成 B，并告诉我成功了」。

### 语义

```
bool CAS(ref T location, T expected, T newValue)
{
    // ↓ 这一整段是不可分割的，由 CPU 硬件保证
    if (location == expected)
    {
        location = newValue;
        return true;
    }
    return false;
}
```

关键点：**"比较"和"写入"之间没有任何缝隙**。别的线程不可能在这个瞬间插进来改掉 `location`。

### 底层是硬件指令

| 平台 | 指令 |
|---|---|
| x86 / x64 | `LOCK CMPXCHG` |
| ARM | `LDREX` / `STREX`（LL/SC 对） |

普通的情况下，`if (x == 0) x = 1;` 编译成三条指令，中间可能被抢占。CAS 是一步到位，**不存在中间态**。

### 失败怎么办：自旋重试

CAS 返回 `false` 表示"我读到的值已经变了"，于是重新来一遍：

```csharp
// 用 CAS 实现原子递增
static void Increment(ref int counter)
{
    while (true)
    {
        int old = counter;              // 读
        int next = old + 1;             // 算
        if (CAS(ref counter, old, next)) // 比较并写
        {
            return;                     // 成功
        }
        // 失败 → 说明别人抢先改了 → 重来
    }
}
```

**这就是"无锁"（lock-free）的核心**：线程**永远不阻塞**，只是"重试到成功为止"。

### C# 里的 API

```csharp
Interlocked.Increment(ref int)         // 原子 ++
Interlocked.Decrement(ref int)         // 原子 --
Interlocked.Add(ref int, int)          // 原子 +=
Interlocked.Exchange(ref T, T)         // 无条件替换，返回旧值
Interlocked.CompareExchange(ref T, T value, T comparand)   // ← CAS 本体
```

`CompareExchange` 的参数顺序容易记混，记住**返回值是原始值**：

```csharp
int original = Interlocked.CompareExchange(ref _state, value: 1, comparand: 0);
// original == 0  → 成功了（原来确实是 0，已改成 1）
// original != 0  → 失败了（原来是别的，什么都没改）
```

所以判断成功的惯用写法是：

```csharp
if (Interlocked.CompareExchange(ref _state, 1, 0) == 0) { /* 我是第一个 */ }
```

### 项目里的三个实例

**① `Interlocked.Increment` 生成请求序号**

```csharp
// labs/LabC1_Client/Rpc/RpcClient.cs:87
var seq = (uint)Interlocked.Increment(ref _seq);
```

多线程并发发请求，必须保证每个 seq 唯一且不被重复分配。用 `lock` 也行，但这是一条指令的事。

**② `TrySetResult()` 的幂等性就是 CAS**

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:170
if (!_closed.TrySetResult())
{
    return;
}
```

`TaskCompletionSource` 内部维护一个状态字段，`TrySetResult` 概念上是：

```csharp
// 概念实现
if (Interlocked.CompareExchange(ref _state, Completed, NotCompleted) == NotCompleted)
{
    /* 我是第一个完成它的，执行真正的工作 */
}
return false;
```

**状态只能从「未完成」跃迁到「已完成」一次**——这正是实现幂等锁的方式（呼应 Q17）。

**③ 自己用 CAS 写一个幂等标志**

```csharp
private int _closedFlag;

public bool TryClose()
{
    return Interlocked.CompareExchange(ref _closedFlag, 1, 0) == 0;
}
```

和 `TrySetResult()` 是同一个模式。比 `lock + bool` 更快，也不会死锁。

### 和 `lock` 的对比

| | `lock`（Monitor） | `Interlocked` / CAS |
|---|---|---|
| 底层 | 用户态锁 + 可能进内核态 | **单条 CPU 指令** |
| 其他线程 | **阻塞等待** | 不阻塞，失败就重试 |
| 无竞争开销 | ~20–50 ns | ~1–5 ns |
| 高竞争开销 | 内核态排队（慢但公平） | 自旋空转，可能更慢 |
| 能保护代码块 | ✅ 能 | ❌ 只能保护**单个变量** |
| 死锁风险 | ✅ 有 | ❌ 无 |

### 三个致命限制

**① 只能保护一个变量**

```csharp
// ❌ 这样不是原子的：两条指令之间可能被插入
Interlocked.Increment(ref _count);
Interlocked.Add(ref _total, amount);
```

两个变量要保持一致，CAS 做不到，必须 `lock` 或设计成单个结构（比如把两个值打包进一个 `long` 或用 `ValueTuple` 的引用来 CAS）。

**② ABA 问题**

```
线程1: 读到 A，准备把 A 改成 C
线程2: 把 A 改成 B，又改回 A
线程1: CAS(A → C) 成功 —— 但中间已经被人改过两轮了
```

对"只是一次性跃迁"的场景（如 `TrySetResult`）无害，但对"值本身有语义"的场景可能出错（比如无锁栈的 pop）。

解法：**带版本号一起 CAS**（`(value, version)` 打包成一个整体）。

**③ 高竞争时自旋浪费 CPU**

100 个线程抢同一个变量，99 个在空转。这种情况用 `lock` 反而更好——它会让等待的线程真正睡下去。

> CAS 的黄金场景是**低到中等竞争**。

### 严格术语

| 术语 | 定义 | CAS 自旋 | `lock` |
|---|---|---|---|
| **Wait-free** | 每个线程都能在有限步内完成 | ❌ | ❌ |
| **Lock-free** | **至少有一个**线程能推进 | ✅ | ❌ |
| **Blocking** | 可能所有线程一起停住 | ❌ | ✅ |

`lock` 不是 lock-free：持锁线程被操作系统挂起，其余所有线程全部停摆。
CAS 自旋是 lock-free：抢输的线程不断重试，总有人能成功。

### 内存序：`Interlocked` 自带完整栅栏

`Interlocked.*` 的操作有 **full fence** 语义（acquire + release），所以：

```csharp
_flag = 1;
Interlocked.CompareExchange(ref _ready, 1, 0);   // ← 前面的写入一定对别的线程可见
```

不需要额外加 `Thread.MemoryBarrier()`。

对比 `Volatile.Read/Write` 只有半栅栏（acquire 或 release 之一），适合"单标志位"这类简单场景。

### 一句话记忆

> **`lock` 是"排队进厕所，一个人用完下一个"；CAS 是"大家一起挤，谁手快谁先上，挤不上的再来一次"。**
>
> 前者简单但慢，后者快但只适用于"一件事"（单个变量），而且人太多时会互相踩踏。

## J6. 为什么不用 `ConcurrentQueue` 做发送队列（Q20）

### 一句话

> **`ConcurrentQueue` 解决的是「多线程安全地存取」；`Channel` 解决的是「在安全存取的基础上，还能等」。**

没有"等"，消费端只能**轮询**。

### 换成 ConcurrentQueue 会变成什么样

```csharp
private readonly ConcurrentQueue<byte[]> _queue = new();

// 生产端：没问题
_queue.Enqueue(FrameCodec.Encode(type, seq, payload.Span));

// 消费端：问题全在这里
private async Task SendLoopAsync()
{
    while (!_cts.IsCancellationRequested)
    {
        if (_queue.TryDequeue(out var buffer))
        {
            await _stream.WriteAsync(buffer, _cts.Token);
        }
        else
        {
            await Task.Delay(1, _cts.Token);   // ← 必须睡，否则空转烧满一个核
        }
    }
}
```

问题清单：

| 问题 | 后果 |
|---|---|
| `TryDequeue` 是非阻塞的 | 队列空时只能"睡一会再看" |
| `Task.Delay(1)` 的实际精度 | Windows 上约 15ms（时钟中断周期） |
| 每条消息平均延迟 | ~7.5ms（等一半） |
| 睡不着的时间 | 空转，CPU 100% |
| 延迟 vs CPU | **无法两全** |

对一个要测 RTT 的心跳机制来说，这是灾难——RTT 统计会被 15ms 的量化误差完全污染。

### 三个维度的本质差距

| 能力 | `ConcurrentQueue<T>` | `Channel<T>` |
|---|---|---|
| **线程安全存取** | ✅ 无锁，很快 | ✅ 有锁 / 信号 |
| **空队列等待** | ❌ 只能轮询 | ✅ `WaitToReadAsync` 挂起 + 唤醒 |
| **有界 + 背压** | ❌ 永远无界 | ✅ `CreateBounded` + `FullMode.Wait` |
| **完成语义** | ❌ 要自己加标志位 | ✅ `Complete()` 内建 |
| **单读者优化** | — | ✅ `SingleReader = true` |

### 差距一：没有"等"，只能轮询

Channel 的消费循环：

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:146
while (await _sendQueue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
{
    while (_sendQueue.Reader.TryRead(out var buffer))
    {
        await _stream.WriteAsync(buffer, _cts.Token).ConfigureAwait(false);
    }
}
```

`WaitToReadAsync` 在队列空时**挂起当前线程并且不占线程**（呼应 Q16），队列由空变非空时由内部信号**立即唤醒**——延迟是微秒级。

`ConcurrentQueue` 根本没有这个能力。它的 API 只有 `TryDequeue`（立刻返回 bool），没得选。

**"能不能等"是这两个类型的分水岭**，其余差距都是由它派生出来的。

### 差距二：没有背压

`ConcurrentQueue` **永远无界**。生产快于消费时，内存无限增长，直到 OOM。

如果想加容量限制，只能自己写：

```csharp
// ❌ 而且这段有 race
while (_queue.Count >= 1024)
{
    await Task.Delay(1);       // 又是轮询
}
_queue.Enqueue(buffer);
```

两个线程可能同时看到 `Count == 1023`，然后都入队 → 变成 1025。

真正要正确实现，得配 `SemaphoreSlim` 控制槽位——**而这已经是手工重造 Channel 了**。

### 差距三：没有完成语义

Channel：

```csharp
_sendQueue.Writer.TryComplete();    // 拒绝后续写入
// 消费者把剩余数据读完后，WaitToReadAsync 返回 false → 循环自然退出
```

这是**一等公民**的状态机：既不丢积压数据，又能优雅终止。

`ConcurrentQueue` 需要自己造标志位：

```csharp
// ❌ 又是轮询 + 竞态
while (!_closed || _queue.Count > 0)
{
    if (_queue.TryDequeue(out var b)) { ... }
    else await Task.Delay(1);
}
```

### 如果非要"用 ConcurrentQueue 实现"

正确做法是配一个 `SemaphoreSlim` 当"有多少条消息"的信号：

```csharp
private readonly ConcurrentQueue<byte[]> _queue = new();
private readonly SemaphoreSlim _signal = new(0);

// 生产
_queue.Enqueue(buffer);
_signal.Release();

// 消费
await _signal.WaitAsync(ct);        // 等"至少有一条"
if (_queue.TryDequeue(out var buffer))
{
    await _stream.WriteAsync(buffer, ct);
}
```

这样能工作。但注意代价：

- 要再加**第二个** `SemaphoreSlim` 控制容量，才有背压
- 退出时还要处理"信号已 Release 但队列已空"的竞态
- 异常路径下容易忘记 `Release`，导致永久卡死
- **两个信号量 + 一个队列 = 手工重造 Channel 的劣质翻版**

### 性能的真相

必须承认：**单次入队/出队，`ConcurrentQueue` 比 `Channel` 快**。

| 操作 | `ConcurrentQueue` | `Channel`（Bounded + Wait） |
|---|---|---|
| 入队 | ~20 ns（无锁） | ~50–100 ns（锁 + 信号） |
| 出队（有数据） | ~20 ns | ~40 ns |
| 出队（**空队列**） | 立即返回 false | **挂起，0 CPU** |

但网络发送不是"消息永不停歇"的场景——**大部分时间队列是空的**。

空队列时：
- `ConcurrentQueue` 方案：要么 CPU 空转，要么加 15ms 延迟
- `Channel` 方案：挂起，0 CPU，来消息立刻唤醒

**每条约 50ns 的额外开销，换来空闲时零 CPU 和微秒级响应，这笔交易在任何网络应用里都值。**

### 所有候选方案的完整对比

| 方案 | 能等 | 异步友好 | 有界/背压 | 完成语义 | 结论 |
|---|---|---|---|---|---|
| `ConcurrentQueue<T>` | ❌ | — | ❌ | ❌ | 不适合做队列消费者 |
| `ConcurrentQueue` + `SemaphoreSlim` | ✅ | ✅ | 需自己加 | 需自己加 | 手工劣化版 Channel |
| `BlockingCollection<T>` | ✅ 同步阻塞 | ❌ 占线程 | ✅ | ✅ | 异步场景不合适 |
| `BufferBlock<T>`（Dataflow） | ✅ | ✅ | ✅ | ✅ | 要额外 NuGet 包，更重 |
| **`Channel<T>`** | ✅ | ✅ | ✅ | ✅ | **BCL 内置，现代首选** |

`BlockingCollection<T>` 是 .NET 4.0 的经典方案，`Take()` 有等待机制，但那是**同步阻塞**——每个等待的消费者占死一个线程，跟 Q16 讲的异步模型冲突。

`Channel<T>` 就是它的异步现代化版本，.NET Core 3.0 起进入 BCL。

### 一句话记忆

> **`ConcurrentQueue` 是"快递柜"——你自己去看有没有件；`Channel` 是"快递柜 + 到件短信"——有件了它会叫你。**

---

# 主题 K：LINQ 与 C# 语法糖（Q19）

## K1. 这段代码在干什么

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:209
var loops = new[] { _readLoop, _sendLoop }
    .Where(task => task is not null)
    .Select(task => task!)
    .ToArray();

try
{
    await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
}
catch
{
    // 关闭阶段的异常没有价值
}
```

目标：**把两个可能为 null 的循环 `Task` 过滤后收集成数组，等它们都结束**。

`Task.WhenAll` 不接受 null 元素（会抛 `ArgumentNullException`），所以必须先过滤。

## K2. 逐段语法

### ① `new[] { _readLoop, _sendLoop }` —— 隐式类型数组

```csharp
new[] { a, b }        // 编译器从元素推断类型：Task?[] 
new Task?[] { a, b }  // 显式写法
```

两个字段都是 `Task?`，所以推断结果是 `Task?[]`。

⚠️ 如果元素里有 `null` 字面量（不是 `Task?` 变量），推断可能失败或退化：

```csharp
new[] { task, null }        // ❌ CS0826：找不到最佳类型
new[] { task, (Task?)null } // ✅ 显式标注
```

### ② `.Where(task => task is not null)` —— 过滤

`task` 的静态类型是 `Task?`，`is not null` 为真的才留下。

**但关键问题**：`Where` 的签名是

```csharp
IEnumerable<T> Where<T>(this IEnumerable<T> source, Func<T, bool> predicate)
```

`T` 是 `Task?`，**`Where` 不会改变 `T`**。所以结果仍然是 `IEnumerable<Task?>`——编译器不知道这个序列里已经没有 null 了。

### ③ `.Select(task => task!)` —— null-forgiving 运算符

`!` 是 **null-forgiving operator**（C# 8）：

- **纯编译期行为，运行时不生成任何代码**
- 作用：告诉编译器的可空流分析「这里我保证不是 null，别再警告了」
- 如果实际是 null，**照样会 NRE**

所以这一步的唯一目的：把 `IEnumerable<Task?>` 变成 `IEnumerable<Task>`，让 `ToArray()` 得到 `Task[]`，`Task.WhenAll` 才接受。

### ④ `.ToArray()` —— 物化

把惰性序列变成数组。**这是唯一真正遍历的一步**，前面 `Where`/`Select` 都只是搭管道。

### ⑤ `var loops` 的类型

`Task[]`（因为 `Select` 把它变成了 `IEnumerable<Task>`）。

## K3. 为什么 `!` 在这里是安全的

因为**紧挨着的 `Where` 已经保证了非 null**。

```
Where 的语义保证：这条链上每个元素都过了 task is not null 检查
Select 的 ! ：把这层保证告诉编译器
```

`safe` 的 `!` 判断标准：

| 情况 | 该用 `!` 吗 |
|---|---|
| 前一行刚判过 null | ✅ 应该 |
| 构造函数刚赋过非 null 值 | ✅ 应该 |
| 「应该不会为 null 吧」 | ❌ 不该，去查为什么 |
| 为了让警告消失 | ❌ 绝对不该 |

> `!` 的正确含义是「我比编译器知道得多」，不是「闭嘴」。

## K4. 三种等价写法对比

### 写法 A：项目当前（Where + Select + ToArray）

```csharp
var loops = new[] { _readLoop, _sendLoop }
    .Where(task => task is not null)
    .Select(task => task!)
    .ToArray();
```

分配：数组 + 2 个迭代器 + 结果数组 = **4 次分配**。

### 写法 B：`OfType<Task>()`（一次搞定过滤 + 收窄）

```csharp
var loops = new[] { _readLoop, _sendLoop }
    .OfType<Task>()
    .ToArray();
```

`OfType<T>` 的语义是「只保留能转成 T 的元素」——`null` 转不成 `Task`，自动被过滤。**同时**把类型收窄成 `IEnumerable<Task>`，不需要 `!`。

分配：数组 + 1 个迭代器 + 结果数组 = 3 次。**更简洁**。

### 写法 C：完全不用 LINQ（最直白、最快）

```csharp
var loops = new List<Task>(2);
if (_readLoop is not null) loops.Add(_readLoop);
if (_sendLoop is not null) loops.Add(_sendLoop);

await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2));
```

分配：1 个 List（内部 1 个数组）= **1 次**。

### 该选哪个

| | 可读性 | 分配 | 适用 |
|---|---|---|---|
| A | 中 | 4 | 字段多、逻辑简单时还行 |
| B | **高** | 3 | **推荐**，语义最准确 |
| C | 高 | **1** | 元素少且追求零浪费 |

这段在 `DisposeAsync` 里只执行一次，性能完全无所谓，所以 A 的写法不是错误——但 **B 更好**，因为它把「过滤 + 类型收窄」表达成了单一操作，`!` 这个「向编译器撒谎」的动作也消失了。

## K5. 为什么 `_readLoop` / `_sendLoop` 是 `Task?`

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:26
private Task? _readLoop;
private Task? _sendLoop;
```

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:90
private void Start()
{
    _readLoop = Task.Run(ReadLoopAsync);
    _sendLoop = Task.Run(SendLoopAsync);
}
```

它们为 null 的窗口：

```csharp
var connection = new ClientConnection(tcp);   // 构造函数里 _readLoop 还是 null
connection.Start();                           // 这里才赋值
```

如果 `new` 之后、`Start()` 之前抛出什么（虽然当前代码没有），或者未来某处直接调 `DisposeAsync`，就会遇到 null。

所以过滤是**必要的防御**，不是多余的。

## K6. `Task.WhenAll(...).WaitAsync(TimeSpan)` 的语义

```csharp
await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2));
```

两层含义：

| API | 作用 |
|---|---|
| `Task.WhenAll(loops)` | 等**所有**任务完成（任一失败则整体失败） |
| `.WaitAsync(2s)` | 最多等 2 秒，超时抛 `TimeoutException` |

⚠️ **`WaitAsync` 超时并不会取消那两个循环**——它只是「我不等了」。

所以超时后代码继续往下走，直接 `_stream.Dispose()` / `_tcp.Dispose()`。那两个还活着的循环会因为 IO 对象被销毁而抛异常，从而自然退出。

这是一个**有意的设计**：先 `Shutdown(null)`（`Cancel` + `Close`），理论上循环很快就能退出；2 秒只是兜底，防止某个循环因为某个 bug 卡死，导致 `DisposeAsync` 永远不返回。

`catch { }` 吞掉所有异常（`TimeoutException`、循环里的 `IOException` 等），注释给了理由：「关闭阶段的异常没有价值」——此时已经没有任何补救动作可做。

## K7. 一个隐患：`_disposed` 不是线程安全的

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:199
public async ValueTask DisposeAsync()
{
    if (_disposed)
    {
        return;
    }

    _disposed = true;
    Shutdown(null);
```

`_disposed` 是普通 `bool`，**两个线程同时进来可能都通过检查**。

不过这里影响有限，因为紧接着的 `Shutdown(null)` 内部用 `TrySetResult()` 做了幂等（见 Q17）——重复调用会直接 return。

如果要真正做到「只有一次进入」，用 CAS 更干净（见 Q18）：

```csharp
private int _disposedFlag;

public async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _disposedFlag, 1) != 0)
    {
        return;
    }
    ...
}
```

`Interlocked.Exchange` 返回旧值，旧值非 0 说明已经被别人抢了。

> 按 `IAsyncDisposable` 的约定，Dispose **不要求线程安全**，所以现在的写法可以接受。但知道「怎么改成真正安全」是有价值的。

## K8. 两个语法细节

### `is not null` 和 `!= null` 对引用类型等价

但对**重载了 `==` 运算符**的类型不同：

```csharp
x is null       // 永远编译成引用比较（ceq），不调用 operator ==
x == null       // 如果类型重载了 ==，可能调用它
```

所以现代 C# 推荐 `is null` / `is not null`——**保证是纯粹的「存在性检查」，不受运算符重载干扰**。

### `Task.WhenAll` 空数组是安全的

```csharp
await Task.WhenAll(Array.Empty<Task>());   // 立即完成，不抛异常
```

所以如果两个字段都是 null，`WhenAll` 得到空数组也完全没问题——这也是过滤逻辑可以放心写的原因。

## K9. 一句话总结

> `Where(...)` 只过滤**值**，不改变**类型**；`!` 是给编译器看的一张「保证书」，运行时什么都不做。
>
> 想让编译器和运行时都省事，用 `OfType<Task>()`——过滤和类型收窄一次完成。

## K10. `case ... when ...` 语法拆解（Q22）

```csharp
// labs/LabC1_Client/Program.cs:197
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--host" when i + 1 < args.Length:
            options = options with { Host = args[++i] };
            break;
```

原文里三个知识点叠在一起，逐层剥开。

### 第一层：`case <模式> when <布尔表达式>:`

```csharp
case "--host" when i + 1 < args.Length:
//   └─模式─┘ └────── case guard ──────┘
```

| 部分 | 名称 | 作用 |
|---|---|---|
| `"--host"` | **常量模式**（constant pattern） | 值相等才匹配 |
| `when ...` | **case guard**（case 守卫） | 模式匹配成功**之后**才求值 |

**执行规则**：

```
模式匹配失败 → 直接尝试下一个 case，guard 根本不会求值
模式匹配成功 + guard 为真 → 执行 body，结束 switch
模式匹配成功 + guard 为假 → 继续尝试后面的 case（不是跳出 switch）
```

**为什么这里必须有 guard**：

```csharp
// 如果没有 guard：
case "--host":
    options = options with { Host = args[++i] };
```

用户只传了 `--host` 一个参数时（`args = ["--host"]`），`++i` 让 `i` 变成 1，`args[1]` → **`IndexOutOfRangeException`**。

加了 `when i + 1 < args.Length` 就在越界前挡住了。

### 第二层：`args[++i]` —— 前缀自增作为表达式

| 写法 | 顺序 | 等价于 |
|---|---|---|
| `args[++i]` | **先加，再取** | `i = i + 1; 然后 args[i]` |
| `args[i++]` | **先取，再加** | `var v = args[i]; i = i + 1; 然后 v` |

这里要的是「跳到下一个参数并取出来」，所以用前缀。

**完整走查** `args = ["--host", "1.2.3.4", "--port", "8080"]`：

| 步骤 | `i` | 发生了什么 |
|---|---|---|
| 初始 | 0 | — |
| 进入 switch | 0 | `args[0] == "--host"` ✅ |
| guard | 0 | `0 + 1 = 1 < 4` ✅ |
| `args[++i]` | **1** | 先 `i = 1`，再取 `args[1] = "1.2.3.4"` |
| `break` | 1 | 跳出 switch |
| for 的 `i++` | **2** | 恰好跳过已消费的 `"1.2.3.4"` |
| 下一步 | 2 | `args[2] == "--port"` ... |

**`++i` 和 for 的 `i++` 配合，天然实现了「跳过已消费的值参数」**——这就是命令行解析里 `++i` 的经典用途。

### 第三层：`options = options with { Host = args[++i] }`

`ClientOptions` 是 `sealed record`：

```csharp
// labs/LabC1_Client/ClientOptions.cs:6
public string Host { get; init; } = "127.0.0.1";
```

| 部分 | 含义 |
|---|---|
| `options with { ... }` | **浅拷贝**一份，再改指定属性（因为 `init` 属性只能在构造/初始化器/`with` 里赋值） |
| `options = ...` | 把新对象赋回变量 |

⚠️ 注意：**这不是"修改"，是"重建并替换"**。原对象在自己的生命周期里始终不变（呼应 Q5 的 `init`-only）。

为什么不能直接写 `options.Host = args[++i]`？

```csharp
options.Host = "x";   // ❌ CS8852：init-only 属性只能在对象初始化器或 with 里赋值
```

### 第四层：`break` 跳出的是 switch

```csharp
for (var i = 0; i < args.Length; i++)   // ← 这个循环
{
    switch (args[i])
    {
        case "--host" ...:
            ...
            break;    // ← 只跳出 switch，循环继续
```

所以 `break` 之后回到 `for` 的 `i++`。

### 可以用的其他模式

既然 `switch` 用的是模式匹配，`case` 后面能写的远不止字符串常量：

```csharp
case "--host":                  // 常量模式
case "-h": case "--help":       // 多个空标签共享一个 body
case string s:                  // 类型模式（同时声明变量 s）
case null:                      // null 模式
case not null:                  // 否定模式
case > 0:                       // 关系模式
case int n and > 0:             // 组合模式（and / or / not）
case var x:                     // var 模式（永远匹配，等于兜底）
case _:                         // 丢弃模式（default 的现代替代）
```

项目里就有多标签共享的写法：

```csharp
case "-h":
case "--help":
    throw new ArgumentException(Usage);
```

### `when` 和 catch 里的 `when` 是同一个语法

```csharp
catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)   // C# 6，异常筛选器
case "--host" when i + 1 < args.Length:                                               // C# 7，case guard
```

语义相同：**在主条件满足后，再用一个额外条件做二次筛选**。

### 两个隐患

**① `i` 在 case 内部被修改，是隐式契约**

```csharp
for (var i = 0; i < args.Length; i++)   // ← i 必须能被外部修改
```

如果哪天把 `for` 改成 `foreach (var arg in args)`，`++i` 直接编译不过——这是好事，但说明这个写法绑死了"索引可写"。

**② guard 为假时会静默落到下一个 case**

```csharp
// 用户输入：--host   （没有值）
case "--host" when i + 1 < args.Length:   // guard 为假，跳过
...
default:
    throw new ArgumentException($"未知参数: {args[i]}");   // 报错信息会说"未知参数 --host"，误导
```

更好的做法是把"缺值"和"未知参数"分开报：

```csharp
case "--host":
    if (i + 1 >= args.Length) throw new ArgumentException("--host 缺少值");
    options = options with { Host = args[++i] };
    break;
```

代价是每个参数都要重复三行。项目选了 guard 版，用简洁换掉了一点点错误信息的准确性——**这是有意的取舍**。

---

# 主题 L：集合类型选型（Q21）

## L1. 先回答三个问题

选任何集合之前，先确定这三件事：

```
① 需要按下标随机访问吗？        → 数组 / List
② 需要按 key 快速查找吗？        → Dictionary 系
③ 会跨线程同时访问吗？           → Concurrent 系 / Channel
```

三个都"是"的情况很少，通常有一个主导。

## L2. 非并发集合：按访问模式选

| 需求 | 选 | 别选 | 理由 |
|---|---|---|---|
| 数量固定、下标访问、追求性能 | `T[]` | `List<T>` | 数组最少一层间接 |
| 动态数量、尾部增删、下标访问 | `List<T>` | `LinkedList<T>` | 连续内存，缓存友好 |
| FIFO（先进先出） | `Queue<T>` | `List<T>` + `RemoveAt(0)` | 后者 O(n) |
| LIFO（后进先出） | `Stack<T>` | `List<T>` + `RemoveAt(Count-1)` | 语义不清 + 无专门优化 |
| 按 key 查找（无序） | `Dictionary<K,V>` | `List<KeyValuePair>` | O(1) vs O(n) |
| 去重 / 快速成员判断 | `HashSet<T>` | `List<T>.Contains` | O(1) vs O(n) |
| 按 key 查找 + 有序遍历 | `SortedDictionary<K,V>` | `Dictionary` + 每次排序 | 内建红黑树 |
| 有序集合（无 value） | `SortedSet<T>` | `SortedList` | — |
| 多读少写 + 内存敏感 + 有序 | `SortedList<K,V>` | `SortedDictionary` | 底层是数组，内存更小 |
| 频繁在中间插入/删除 | `LinkedList<T>` | `List<T>` | List 中间插入是 O(n) |
| 构建后只读、高频查询（.NET 8+） | `FrozenDictionary` / `FrozenSet` | `Dictionary` | 专门优化的只读结构 |

## L3. 复杂度对照表

| 类型 | 下标访问 | 按 key/值查找 | 插入 | 删除 | 内存 |
|---|---|---|---|---|---|
| `T[]` | **O(1)** | O(n) | ❌ 固定 | ❌ | 最小 |
| `List<T>` | **O(1)** | O(n) | 尾部 O(1)* / 中间 O(n) | O(n) | 小 |
| `LinkedList<T>` | O(n) | O(n) | **O(1)**（已知节点） | **O(1)**（已知节点） | **大** |
| `Queue<T>` | ❌ | O(n) | O(1) | O(1)（头部） | 小 |
| `Stack<T>` | ❌ | O(n) | O(1) | O(1)（顶部） | 小 |
| `Dictionary<K,V>` | ❌ | **O(1)** | O(1) | O(1) | 中 |
| `HashSet<T>` | ❌ | **O(1)** | O(1) | O(1) | 中 |
| `SortedDictionary<K,V>` | ❌ | O(log n) | O(log n) | O(log n) | 大（红黑树节点） |
| `SortedList<K,V>` | O(1)（按索引） | O(log n)（按 key） | **O(n)** | **O(n)** | 小（数组） |
| `FrozenDictionary` | ❌ | **O(1)**（更快常数） | ❌ 不可变 | ❌ 不可变 | 中 |

\* `List<T>` 尾部插入摊还 O(1)：容量不足时翻倍扩容并复制。

**`LinkedList<T>` 的关键认知**：它的优势只在「**已经持有节点引用**」的前提下成立。按索引找节点是 O(n)，所以 `list.Find(x)` 之后再删，跟 `List` 差不多，还多了内存开销和缓存不友好。**90% 的场景 `List<T>` 是更好的选择。**

**`SortedList` vs `SortedDictionary`**：
- `SortedList` 底层是**排序数组** + 二分查找 → 查找 O(log n)、按索引 O(1)、但**插入删除 O(n)**（要挪元素）
- `SortedDictionary` 底层是**红黑树** → 增删查都 O(log n)，但每个节点是独立对象，内存大
- 选择：**多读少写用 SortedList，频繁增删用 SortedDictionary**

## L4. 并发集合：先问「消费者怎么取」

这是最容易选错的地方。关键不是「谁更快」，而是**消费模型**。

### 两种消费模型

```
模型 A：消费者周期性主动拉取（有天然节拍）
    while (gameLoop.Running) { while (q.TryDequeue(out var x)) Handle(x); DoFrame(); }
    ↑ 每帧一次，CPU 成本可忽略

模型 B：消费者需要挂起等待（无节拍，要求低延迟响应）
    while (await channel.Reader.WaitToReadAsync()) { ... }
    ↑ 有数据立刻醒，空闲零 CPU
```

| | 模型 A | 模型 B |
|---|---|---|
| 消费者 | **主动 pull**，自带节拍（每帧、每次循环） | **被动等**，没有节拍 |
| 选 | **`ConcurrentQueue`** | **`Channel<T>`** |
| 用 Channel 会怎样 | 主线程被迫 `await`，UI/引擎卡住 | — |
| 用 ConcurrentQueue 会怎样 | — | 只能轮询：要么烧 CPU，要么 +15ms 延迟（见 J7） |

### 项目里两个正例的对照（重要）

**例 1：`MainThreadDispatcher` 用 `ConcurrentQueue<Action>`（模型 A，正确）**

```csharp
// labs/LabC1_Client/MainThreadDispatcher.cs:16
private readonly ConcurrentQueue<Action> _queue = new();

public void Post(Action action) => _queue.Enqueue(action);

/// <summary>在主线程循环里调用，把排队的回调依次执行。</summary>
public void Pump()
{
    while (_queue.TryDequeue(out var action))
    {
        try { action(); }
        catch (Exception ex) { Console.Error.WriteLine($"[主线程回调异常] {ex}"); }
    }
}
```

为什么这里 `ConcurrentQueue` 是**对的**：

- 消费者是主线程（Unity 的 `Update`），它**每帧本来就在跑**，调一次 `Pump()` 成本可忽略
- 主线程**绝对不能阻塞或 await**，用 Channel 反而会卡住渲染
- 空队列时 `TryDequeue` 立即返回 false，一次循环就结束，**不是紧循环**

**例 2：`ClientConnection._sendQueue` 用 `Channel<byte[]>`（模型 B，正确）**

```csharp
// labs/LabC1_Client/Connection/ClientConnection.cs:146
while (await _sendQueue.Reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
```

- 消费者是后台 `SendLoopAsync`，**没有天然节拍**
- 要求消息来了尽快发出去（微秒级）
- 空闲时不能烧 CPU

同一个项目里，两处都用了"多线程队列"，但选了不同实现——**差别就在消费模型**。

### 并发集合清单

| 类型 | 并发模型 | 典型场景 |
|---|---|---|
| `ConcurrentDictionary<K,V>` | 分桶锁 + CAS | 多线程共享的映射表（会话表、pending 请求表） |
| `ConcurrentQueue<T>` | 无锁链表 | 多线程生产、消费者主动拉取 |
| `ConcurrentStack<T>` | 无锁链表 | 对象池、后进先出 |
| `ConcurrentBag<T>` | 线程本地 + 无锁 | 不关心顺序，追求吞吐 |
| `BlockingCollection<T>` | 包装上述 + 阻塞等待 | **同步**生产者-消费者（会占线程） |
| `Channel<T>` | 专用实现 | **异步**生产者-消费者（不占线程） |

⚠️ `ConcurrentDictionary` 的**复合操作**必须用内建方法：

```csharp
// ❌ 有 race：两步之间可能被别人插入
if (!_dict.ContainsKey(k)) _dict[k] = v;

// ✅ 原子
_dict.GetOrAdd(k, v);
_dict.AddOrUpdate(k, v, (key, old) => newValue);
_dict.TryRemove(k, out var removed);
```

## L5. 不可变 / 只读集合

| 类型 | 含义 | 开销 | 适用 |
|---|---|---|---|
| `IReadOnlyList<T>` / `IReadOnlyCollection<T>` | **接口**，只读视图 | 每次访问都走接口调用 | 方法签名，表达"我不改" |
| `ReadOnlyCollection<T>` | **包装器**，包住一个 `IList<T>` | 多一层间接 | 对外暴露内部 List |
| `ImmutableArray<T>` | 真的不可变数组 | 构建时复制 | 需要真不可变且高频读 |
| `ImmutableList<T>` | 真不可变链表树 | 构建慢 | 需要频繁派生新版本 |
| `FrozenDictionary<K,V>`（.NET 8+） | 构建后冻结，查找最快 | 构建成本高 | 静态查找表、配置表 |

⚠️ 两个容易踩的点：

```csharp
// ① ReadOnlyCollection 不复制，只包一层
var list = new List<int> { 1, 2, 3 };
var ro = new ReadOnlyCollection<int>(list);
list.Add(4);
ro.Count;    // 4！你改的是同一份数据

// ② ImmutableArray 才是真复制
var im = list.ToImmutableArray();   // 这里复制了一份
list.Add(5);
im.Length;   // 3，不受影响
```

## L6. 项目里 5 处选择的回看

| 位置 | 类型 | 为什么 |
|---|---|---|
| `MockServer._sessions` | `ConcurrentDictionary<Guid, ClientSession>` | 多个客户端并发连入/断开，按 Id 增删查 |
| `RpcClient._pending` | `ConcurrentDictionary<uint, PendingRequest>` | 收包线程写、发送线程读，按 seq 配对 |
| `ClientConnection._sendQueue` | `Channel<byte[]>` | 需要异步等待 + 有界背压（模型 B） |
| `MainThreadDispatcher._queue` | `ConcurrentQueue<Action>` | 主线程每帧主动拉取，不能阻塞（模型 A） |
| `ConnectionStateMachine.AllowedTransitions` | `static readonly Dictionary<...>` | 启动时建好的静态查找表，只读 |
| `MockServer.Sessions` | `ICollection<ClientSession>` | `_sessions.Values` 的实际类型，对外只读遍历 |

每一处都是「需求 → 类型」的直接映射，没有一处是随手写的。

## L7. 八个高频误区

| # | 误区 | 正确做法 |
|---|---|---|
| 1 | 用 `List<T>` 当队列，`RemoveAt(0)` | 用 `Queue<T>`，O(1) |
| 2 | 循环里用 `List<T>.Contains` 做去重 | 用 `HashSet<T>`，O(n²) → O(n) |
| 3 | 用 `LinkedList<T>` 做随机访问 | 用 `List<T>`，LinkedList 索引是 O(n) |
| 4 | `ConcurrentDictionary` 用 `ContainsKey` + 赋值 | 用 `GetOrAdd` / `AddOrUpdate` |
| 5 | 遍历 `Dictionary` 时增删元素 | 先 `ToList()` 快照，或遍历 `Keys` 副本 |
| 6 | `SortedList` 频繁插入 | 用 `SortedDictionary`，O(n) → O(log n) |
| 7 | 以为 `ReadOnlyCollection` 是复制 | 它只是包装，底层改动会透出来 |
| 8 | 高频调用返回 `IReadOnlyList<T>` 并循环索引 | 接口调用无法内联，热路径用具体类型 |

## L8. 决策树

```
会跨线程访问吗？
│
├─ 会 ─→ 是"生产者-消费者"模型吗？
│         │
│         ├─ 是 ─→ 消费者有天然节拍（每帧/每次循环）？
│         │         ├─ 有 → ConcurrentQueue<T>          （模型 A）
│         │         └─ 无 → Channel<T>（异步）/ BlockingCollection<T>（同步）
│         │
│         └─ 否 ─→ 需要 key-value？
│                   ├─ 是 → ConcurrentDictionary<K,V>
│                   └─ 否 → ConcurrentQueue / ConcurrentStack / ConcurrentBag
│                           （按 FIFO / LIFO / 无所谓 选）
│
└─ 不会 ─→ 需要按 key 查找吗？
            │
            ├─ 是 ─→ 需要按 key 排序遍历？
            │         ├─ 会，且多读少写 → SortedList<K,V>
            │         ├─ 会，且频繁增删 → SortedDictionary<K,V>
            │         └─ 否，且构建后不变 → FrozenDictionary<K,V>
            │               否则 → Dictionary<K,V>
            │
            └─ 否 ─→ 访问模式？
                      ├─ 下标随机访问 → T[]（固定）/ List<T>（动态）
                      ├─ 只在两端 → Queue<T>（FIFO）/ Stack<T>（LIFO）
                      ├─ 已知位置的增删 → LinkedList<T>
                      └─ 去重/成员判断 → HashSet<T>
```

## L9. 一句话总结

> **集合选型不是比谁快，而是先定义「我怎么访问它」**：
> 按下标 → 数组/List；按 key → Dictionary；按顺序进出 → Queue/Stack；跨线程 → 先问消费者是主动拉还是被动等。

---

# 附录一：速查表

## 类型选择决策树

```
需要自己创建、自己管理生命周期？
├─ 是 → T[]  或  ArrayPool<T>.Shared.Rent()
└─ 否（只是看别人给的一段）↓

需要跨 await / 存进字段 / 作为 async 方法参数？
├─ 是 → Memory<T> / ReadOnlyMemory<T>
└─ 否（只在同步方法内部）↓
        Span<T> / ReadOnlySpan<T>
```

再加两条：
- 要传给下游、不希望对方改 → 加 `ReadOnly` 前缀
- 字段里长期持有且不希望被改 → `ReadOnlyMemory<T>`（但底层仍可能被写，见 A5）

## 项目内类型分工一览

| 位置 | 类型 | 理由 |
|---|---|---|
| `Frame.Payload` | `ReadOnlyMemory<byte>` | 跨 `await` + 分发给订阅者 |
| `FrameReader.ReadFullyAsync(Memory<byte>)` | `Memory<byte>` | async 方法参数 |
| `PayloadCodec.FromUtf8(ReadOnlySpan<byte>)` | `ReadOnlySpan<byte>` | 同步解析，喂给 STJ |
| `FrameCodec.Encode(..., ReadOnlySpan<byte>)` | `ReadOnlySpan<byte>` | 同步编码 |
| `FrameCodec.WriteHeader(Span<byte>)` | `Span<byte>` | 同步写头 |
| `ProtocolConstants.Magic` | `byte[]` | 原为 `ReadOnlySpan`，被 CS9202 逼退 |

**规律：签名里有 `async` 或要存字段 → Memory；纯同步内部 → Span。**

## 错误码对照

| 错误码 | 场景 |
|---|---|
| CS8345 | byref-like 类型作为普通 struct / class 的字段 |
| CS9202 | 在 async 方法或静态字段里持有 `Span` 类 |
| CS0306 | byref-like 类型作为泛型实参 |
| CS8852 | 给 `init`-only 属性赋值（`record` 不可变） |

## 三个实际会踩的坑（Q8 补充）

**① 循环里直接用 `Memory<T>` 索引**

```csharp
// ❌ 每次索引都要经过 .Span 转换，JIT 难以消除边界检查
for (int i = 0; i < mem.Length; i++) Process(mem.Span[i]);

// ✅ 取一次
var span = mem.Span;
for (int i = 0; i < span.Length; i++) Process(span[i]);
```

`Memory<T>` 适合**搬运和存储**，不适合逐字节处理。

**② `Span.Slice` 不复制**

```csharp
var head    = buffer[..16];   // 视图，指向原数组
var payload = buffer[16..];   // 视图，指向原数组同一片内存
```

改 `payload[0]` 就是改 `buffer[16]`。零拷贝的优点，也是数据串味的来源。

**③ `byte[]` 隐式转 `Span` 时 null 静默变空**

```csharp
byte[]? maybeNull = null;
Span<byte> s = maybeNull;   // 空 Span，Length == 0，不抛异常
```

要在入口显式 `ArgumentNullException.ThrowIfNull`。

---

# 附录二：自测题

**1. `Frame` 的 `Payload` 为什么不能声明成 `Span<byte>`？**

<details><summary>答案</summary>

两个原因，缺一不可：① `Span<byte>` 是 byref-like（`ref struct`），不能作为普通 struct 的字段（CS8345）；② `FrameReader.ReadFrameAsync` 是 async 方法，`Frame` 必须能跨 `await` 存活，而 `Span` 进不了 async 状态机。

</details>

**2. `ReadOnlyMemory<byte>` 和 `byte[]` 的核心区别是什么？**

<details><summary>答案</summary>

`byte[]` 是内存**所有者**且内容可变；`ReadOnlyMemory<byte>` 是**只读视图**，内部包着数组（或 `MemoryManager`）+ 索引 + 长度。它不拥有内存，只是从类型层面禁止你写。注意它只是"视图只读"，不保证底层数据不可变。

</details>

**3. 为什么 `Span<T>` 不能转回 `Memory<T>`？**

<details><summary>答案</summary>

`Span` 内部存的是裸地址（`ByReference`），GC 看不见它指向什么。要造一个 `Memory`，必须知道底层的 `object` 引用，而这个信息在 `Span` 里已经丢失了。信息不可逆，所以只能复制（`.ToArray()`）。

</details>

**4. 项目里 `TryParse` 那一行 `ToArray()` 可以怎么消除？**

<details><summary>答案</summary>

把 `TryParse` 的入参从 `ReadOnlySpan<byte>` 改成 `byte[] buffer, int offset, int count` 或 `ReadOnlyMemory<byte>`，然后直接 `.Slice()` 得到视图。代价是必须约定：`Frame` 的生命周期不能超过接收缓冲区的复用周期——即缓冲区不能还给 `ArrayPool` 之前还有人持有 `Frame`。

</details>

**5. `ConfigureAwait(false)` 不写会导致什么问题？举一个具体场景。**

<details><summary>答案</summary>

续体会被 Post 回原来的同步上下文。如果那个上下文所在线程正在同步阻塞等待这个任务（如 UI 线程上 `task.Wait()` / `task.Result`），就会出现"续体等线程、线程等续体"的死锁。此外即使不死锁，每次 `await` 也多一次上下文切换。

</details>

**6. `Stream.Write` 和 `Socket.Send` 在"是否全量写出"上有什么区别？**

<details><summary>答案</summary>

`Stream.Write` 的契约是成功即全部字节已被接受，内部自己循环；`Socket.Send` 可能只发出一部分，必须比对返回值、切剩余切片、再发一次。前者省心但多一层拷贝，后者省拷贝但要自己处理部分发送。

</details>

**7. `record` 生成的 `==` 和普通 class 的 `==` 有什么不同？**

<details><summary>答案</summary>

`record` 的 `==` 是**值相等**（逐字段比较，且要求运行时类型完全一致）；普通 class 默认是**引用相等**。但要小心：如果字段是数组，比较的仍是数组引用，值相等会静默失效。

</details>

**8. `ServerNotice(string Text, string Level = "info")` 从 JSON 反序列化时，缺 `level` 字段会得到什么？**

<details><summary>答案</summary>

得到 `null`，不是 `"info"`。C# 的默认参数只对代码里 `new ServerNotice("x")` 这种调用生效；`System.Text.Json` 走带参构造时是按属性名逐个匹配，缺失的传 `default`。防御写法是在使用处判空，或改用可空标注 + 显式兜底。

</details>

**9. 服务端进程被 `kill -9` 时，客户端会收到 EOF 还是异常？**

<details><summary>答案</summary>

取决于位置。如果恰好断在帧边界上，客户端看到 FIN，`Read` 返回 0 → 正常的 EOF，走"对端正常关闭"分支。如果断在帧中间（头部已收、负载没收满），就是异常（`EndOfStreamException`）。

另外 `kill -9` 时如果内核发送缓冲区还有未确认数据，对端可能收到 **RST** 而不是 FIN——那种情况直接抛 `IOException`。所以三种结局都可能，取决于时序。

</details>

**10. 为什么拔网线时 `ReadAsync` 不会返回 0？**

<details><summary>答案</summary>

因为对方**发不出 FIN**——网线断了，任何包都到不了你这里。`ReadAsync` 会一直等下去（半开连接）。TCP 自带 keepalive 默认 2 小时才探测，所以必须有应用层心跳（`HeartbeatMonitor`）来发现这种情况。

</details>

---

# 附：代码中已识别但未动手的优化点

| 位置 | 问题 | 改法 |
|---|---|---|
| `FrameCodec.TryParse` 第 88 行 | `buffer.Slice(...).ToArray()` 是冗余拷贝 | 改收 `byte[] + offset + count` 或 `ReadOnlyMemory<byte>`，直接 `.Slice()` |
| `FrameReader.ReadFrameAsync` 第 69 行 | 每条消息 `new byte[length]` | 改 `ArrayPool<byte>.Shared` + `IMemoryOwner<byte>`，由 `Frame` 负责归还 |

两条都需要先解决"所有权生命周期"的约定问题，不是纯性能改动。
