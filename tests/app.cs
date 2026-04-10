using System.Net;
using System.Net.Sockets;
using System.Text;

var configuredPort = Environment.GetEnvironmentVariable("PORT");
var port = int.TryParse(configuredPort, out var parsedPort) && parsedPort is > 0 and <= 65535
    ? parsedPort
    : 5000;
using var shutdownCts = new CancellationTokenSource();
using var listener = new TcpListener(IPAddress.Loopback, port);

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownCts.Cancel();
};

listener.Start();
Console.WriteLine($"C# single-file app listening on http://127.0.0.1:{port}");

var heartbeatTask = Task.Run(async () =>
{
    var tick = 0;
    while (!shutdownCts.IsCancellationRequested)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), shutdownCts.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }

        tick++;
        Console.WriteLine($"csharp-heartbeat-{tick}");

        if (tick % 4 == 0)
        {
            Console.Error.WriteLine($"csharp-stderr-heartbeat-{tick}");
        }
    }
});

try
{
    while (!shutdownCts.IsCancellationRequested)
    {
        var client = await listener.AcceptTcpClientAsync(shutdownCts.Token);
        _ = Task.Run(() => HandleClientAsync(client, shutdownCts.Token, port), shutdownCts.Token);
    }
}
catch (OperationCanceledException)
{
    // Normal shutdown path.
}
finally
{
    shutdownCts.Cancel();
    listener.Stop();

    try
    {
        await heartbeatTask;
    }
    catch (OperationCanceledException)
    {
        // Ignore cancellation during shutdown.
    }

    Console.WriteLine("C# single-file app stopped");
}

static async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken, int port)
{
    using (client)
    {
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true)
        {
            NewLine = "\r\n",
            AutoFlush = true
        };

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
        {
            return;
        }

        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancellationToken)))
        {
            // Read and discard headers.
        }

        var responseBody = $"{{\"service\":\"csharp-single-file-test-app\",\"status\":\"ok\",\"port\":{port},\"time\":\"{DateTimeOffset.UtcNow:o}\"}}";
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);

        await writer.WriteLineAsync("HTTP/1.1 200 OK");
        await writer.WriteLineAsync("Content-Type: application/json; charset=utf-8");
        await writer.WriteLineAsync($"Content-Length: {responseBytes.Length}");
        await writer.WriteLineAsync("Connection: close");
        await writer.WriteLineAsync(string.Empty);
        await stream.WriteAsync(responseBytes, cancellationToken);
    }
}
