namespace LabC1.Client.Rpc;

/// <summary>连接已断。业务层应该据此判断"这个请求根本没发出去 / 不会再有回包"。</summary>
public sealed class ConnectionClosedException : Exception
{
    public ConnectionClosedException(string message) : base(message)
    {
    }
}
