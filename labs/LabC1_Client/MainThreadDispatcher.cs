using System.Collections.Concurrent;

namespace LabC1.Client;

/// <summary>
/// 主线程调度器。
///
/// 为什么必须有它：Socket 的收包回调跑在线程池线程，而 UI / 引擎对象（Unity 的
/// Transform、GameObject）只能在主线程访问。任何"从回调里直接改界面"的写法，
/// 在控制台里看着没问题，搬到 Unity 立刻炸。
///
/// 用法：后台线程 Post(action)，主线程（Unity 的 Update）里 Pump()。
/// </summary>
public sealed class MainThreadDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public void Post(Action action) => _queue.Enqueue(action);

    /// <summary>在主线程循环里调用，把排队的回调依次执行。</summary>
    public void Pump()
    {
        while (_queue.TryDequeue(out var action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[主线程回调异常] {ex}");
            }
        }
    }

    public bool IsEmpty => _queue.IsEmpty;
}
