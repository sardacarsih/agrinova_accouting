using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static partial class Program
{
    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task RunCentralSyncMockServerAsync(
        TcpListener listener,
        CentralSyncMockState state,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                var acceptTask = listener.AcceptTcpClientAsync();
                var completed = await Task.WhenAny(acceptTask, Task.Delay(250, cancellationToken));
                if (completed != acceptTask)
                {
                    continue;
                }

                client = acceptTask.Result;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                continue;
            }

            if (client is null)
            {
                continue;
            }

            using var tcpClient = client;

            try
            {
                await using var stream = tcpClient.GetStream();
                var request = await ReadMockHttpRequestAsync(stream, cancellationToken);
                var authHeader = request.Headers.TryGetValue("X-Api-Key", out var authValue) ? authValue : string.Empty;
                if (!string.Equals(authHeader, state.ExpectedApiKey, StringComparison.Ordinal))
                {
                    await WriteMockJsonResponseAsync(stream, 401, new { IsSuccess = false, Message = "Unauthorized" }, cancellationToken);
                    continue;
                }

                var path = request.Path ?? string.Empty;
                if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/api/inventory/sync/upload", StringComparison.OrdinalIgnoreCase))
                {
                    state.UploadCallCount++;
                    state.LastUploadBody = request.Body;

                    var itemCode = ExtractFirstItemCodeFromUploadBody(state.LastUploadBody) ?? "UNKNOWN";
                    var responsePayload = new
                    {
                        IsSuccess = true,
                        Message = "Mock upload accepted.",
                        ServerWatermarkUtc = DateTime.UtcNow,
                        ItemResults = new[]
                        {
                            new { ItemCode = itemCode, Operation = "UPLOAD", Result = "SUCCESS", IsSuccess = true }
                        }
                    };
                    await WriteMockJsonResponseAsync(stream, 200, responsePayload, cancellationToken);
                    continue;
                }

                if (request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) &&
                    path.Equals("/api/inventory/sync/download", StringComparison.OrdinalIgnoreCase))
                {
                    state.DownloadCallCount++;
                    var responsePayload = new
                    {
                        IsSuccess = true,
                        Message = "Mock download accepted.",
                        ServerWatermarkUtc = DateTime.UtcNow,
                        Categories = new[]
                        {
                            new
                            {
                                CategoryCode = state.DownloadCategoryCode,
                                CategoryName = state.DownloadCategoryName,
                                AccountCode = state.DownloadAccountCode,
                                IsActive = false,
                                UpdatedAtUtc = DateTime.UtcNow
                            }
                        },
                        Items = new[]
                        {
                            new
                            {
                                ItemCode = state.DownloadItemCode,
                                ItemName = state.DownloadItemName,
                                Uom = "PCS",
                                CategoryCode = state.DownloadCategoryCode,
                                IsActive = false,
                                UpdatedAtUtc = DateTime.UtcNow
                            }
                        }
                    };
                    await WriteMockJsonResponseAsync(stream, 200, responsePayload, cancellationToken);
                    continue;
                }

                await WriteMockJsonResponseAsync(stream, 404, new { IsSuccess = false, Message = "Not Found" }, cancellationToken);
            }
            catch
            {
                // Ignore response errors in mock server.
            }
        }
    }

    private static async Task<MockHttpRequest> ReadMockHttpRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var headerBytes = new List<byte>(4096);
        var buffer = new byte[1024];
        var headerEndIndex = -1;

        while (headerEndIndex < 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            headerBytes.AddRange(buffer.AsSpan(0, read).ToArray());
            headerEndIndex = IndexOfHeaderTerminator(headerBytes);
            if (headerBytes.Count > 512 * 1024)
            {
                throw new InvalidOperationException("Mock request header too large.");
            }
        }

        if (headerEndIndex < 0)
        {
            throw new InvalidOperationException("Invalid HTTP request: header terminator not found.");
        }

        var fullBytes = headerBytes.ToArray();
        var headerText = Encoding.UTF8.GetString(fullBytes, 0, headerEndIndex);
        var lines = headerText.Split(["\r\n"], StringSplitOptions.None);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Invalid HTTP request line.");
        }

        var requestLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (requestLineParts.Length < 2)
        {
            throw new InvalidOperationException("Invalid HTTP request line.");
        }

        var method = requestLineParts[0];
        var pathAndQuery = requestLineParts[1];
        var queryIndex = pathAndQuery.IndexOf('?');
        var path = queryIndex >= 0 ? pathAndQuery[..queryIndex] : pathAndQuery;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex <= 0)
            {
                continue;
            }

            var key = line[..colonIndex].Trim();
            var value = line[(colonIndex + 1)..].Trim();
            headers[key] = value;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var contentLengthRaw))
        {
            _ = int.TryParse(contentLengthRaw, out contentLength);
        }

        var bodyStart = headerEndIndex + 4;
        var availableBodyLength = fullBytes.Length - bodyStart;
        var bodyBytes = new byte[Math.Max(0, contentLength)];
        if (contentLength > 0)
        {
            if (availableBodyLength > 0)
            {
                var copyLength = Math.Min(contentLength, availableBodyLength);
                Buffer.BlockCopy(fullBytes, bodyStart, bodyBytes, 0, copyLength);
                var offset = copyLength;
                while (offset < contentLength)
                {
                    var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }
            else
            {
                var offset = 0;
                while (offset < contentLength)
                {
                    var read = await stream.ReadAsync(bodyBytes.AsMemory(offset, contentLength - offset), cancellationToken);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }
        }

        return new MockHttpRequest
        {
            Method = method,
            Path = path,
            Headers = headers,
            Body = contentLength > 0 ? Encoding.UTF8.GetString(bodyBytes) : string.Empty
        };
    }

    private static int IndexOfHeaderTerminator(IReadOnlyList<byte> bytes)
    {
        for (var i = 3; i < bytes.Count; i++)
        {
            if (bytes[i - 3] == (byte)'\r' &&
                bytes[i - 2] == (byte)'\n' &&
                bytes[i - 1] == (byte)'\r' &&
                bytes[i] == (byte)'\n')
            {
                return i - 3;
            }
        }

        return -1;
    }

    private static async Task WriteMockJsonResponseAsync(
        NetworkStream stream,
        int statusCode,
        object payload,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var reasonPhrase = statusCode switch
        {
            200 => "OK",
            401 => "Unauthorized",
            404 => "Not Found",
            500 => "Internal Server Error",
            _ => "OK"
        };
        var header = $"HTTP/1.1 {statusCode} {reasonPhrase}\r\nContent-Type: application/json\r\nContent-Length: {bodyBytes.Length}\r\nConnection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, cancellationToken);
        await stream.WriteAsync(bodyBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static string? ExtractFirstItemCodeFromUploadBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("Items", out var itemsElement) ||
                itemsElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in itemsElement.EnumerateArray())
            {
                if (!item.TryGetProperty("ItemCode", out var itemCodeElement))
                {
                    continue;
                }

                var itemCode = itemCodeElement.GetString();
                if (!string.IsNullOrWhiteSpace(itemCode))
                {
                    return itemCode.Trim();
                }
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private sealed class MockHttpRequest
    {
        public string Method { get; init; } = string.Empty;

        public string Path { get; init; } = string.Empty;

        public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public string Body { get; init; } = string.Empty;
    }

    private sealed class CentralSyncMockState
    {
        public string ExpectedApiKey { get; set; } = string.Empty;

        public int UploadCallCount { get; set; }

        public int DownloadCallCount { get; set; }

        public string? LastUploadBody { get; set; }

        public string DownloadCategoryCode { get; set; } = string.Empty;

        public string DownloadCategoryName { get; set; } = "Mock Download Category";

        public string DownloadItemCode { get; set; } = string.Empty;

        public string DownloadItemName { get; set; } = "Mock Download Item";

        public string DownloadAccountCode { get; set; } = "HO.11000.009";
    }

}
