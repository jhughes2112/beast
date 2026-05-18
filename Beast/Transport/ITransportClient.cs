using System.Threading;
using System.Threading.Tasks;

// Abstraction over a bidirectional text message channel.
public interface ITransportClient : System.IDisposable
{
    Task SendAsync(string text, CancellationToken cancellationToken = default);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}
