// =================================================================================================
// SimpleHttpServer.cs
// 内嵌迷你 HTTP/1.1 服务器（基于 TcpListener，零第三方依赖）
// -------------------------------------------------------------------------------------------------
// 为什么不用 System.Net.HttpListener：
//   Unity 2022 / Unity 6 的 .NET Standard 2.1 API 兼容级别下，HttpListener 不可用（它不在
//   netstandard2.1 表面内，仅存在于 .NET Framework / Windows 桌面运行时）。为保证
//   "兼容 Unity 2022 与 Unity 6、零依赖"，这里用 Socket 层手写一个够用的 HTTP 服务器。
//
// 支持能力：
//   - 请求：GET / POST / DELETE / OPTIONS，Content-Length 与 Transfer-Encoding: chunked 两种 body；
//   - 响应：普通响应（Content-Length + Connection: close）与 SSE 流式响应（text/event-stream）；
//   - CORS：所有响应附带 Access-Control-Allow-* 头（MCP Inspector 等浏览器客户端需要）；
//   - 生命周期：后台线程 Accept，线程池处理连接；Stop() 关闭全部连接。
//
// 线程模型说明：
//   每个连接一个线程池工作线程；SSE 长连接由处理器自身阻塞持有；
//   对同一连接的并发写入（如 SSE 会话被 /message 处理器写入）通过 HttpContext.WriteLock 串行化。
// =================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using VrchatProjectMcp.Core.Logging;

namespace VrchatProjectMcp.Core.Net
{
    /// <summary>HTTP 处理器委托。</summary>
    public delegate void HttpHandler(HttpContext context);

    /// <summary>
    /// HTTP 请求上下文：请求数据 + 响应写入能力。
    /// </summary>
    public sealed class HttpContext
    {
        /// <summary>请求方法（GET / POST / ...）。</summary>
        public string Method { get; internal set; } = "GET";

        /// <summary>原始请求目标（含查询串，如 "/message?sessionId=abc"）。</summary>
        public string RawPath { get; internal set; } = "/";

        /// <summary>路径部分（不含查询串）。</summary>
        public string Path { get; internal set; } = "/";

        /// <summary>查询参数（已 URL 解码）。</summary>
        public Dictionary<string, string> Query { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>请求头（名称大小写不敏感）。</summary>
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>请求体字节。</summary>
        public byte[] Body { get; internal set; } = new byte[0];

        /// <summary>连接流。</summary>
        public NetworkStream Stream { get; internal set; }

        /// <summary>底层 Socket（供服务器关闭连接时使用）。</summary>
        internal Socket Socket;

        /// <summary>连接级写锁：同一连接上的所有写入（含跨线程 SSE 写入）都经此串行化。</summary>
        public object WriteLock { get; } = new object();

        /// <summary>是否已进入 SSE 模式（SSE 模式下禁止再写普通响应）。</summary>
        public bool SseStarted { get; internal set; }

        /// <summary>是否已写出过响应（用于防止错误路径双写响应头）。</summary>
        public bool ResponseWritten { get; internal set; }

        /// <summary>请求体文本（UTF-8 解码，惰性）。</summary>
        public string BodyText
        {
            get { return Body != null && Body.Length > 0 ? Encoding.UTF8.GetString(Body) : string.Empty; }
        }

        /// <summary>读取查询参数。</summary>
        public string GetQuery(string key, string defaultValue = null)
        {
            if (Query.TryGetValue(key, out string value)) return value;
            return defaultValue;
        }

        /// <summary>读取请求头。</summary>
        public string Header(string name, string defaultValue = null)
        {
            if (Headers.TryGetValue(name, out string value)) return value;
            return defaultValue;
        }

        /// <summary>客户端是否接受 text/event-stream（决定 POST /mcp 走 SSE 还是 JSON 响应）。</summary>
        public bool AcceptsEventStream
        {
            get
            {
                string accept = Header("Accept");
                return accept != null && accept.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }
    }

    /// <summary>
    /// 简易 HTTP 服务器。
    /// </summary>
    public sealed class SimpleHttpServer
    {
        private static readonly byte[] HeaderTerminator = Encoding.ASCII.GetBytes("\r\n\r\n");

        private readonly Dictionary<string, HttpHandler> _routes = new Dictionary<string, HttpHandler>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<TcpClient> _clients = new HashSet<TcpClient>();
        private readonly object _clientsLock = new object();

        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private HttpHandler _fallback;

        /// <summary>日志器（可为 null）。</summary>
        public IMcpLogger Logger { get; set; }

        /// <summary>服务器是否正在运行。</summary>
        public bool IsRunning { get { return _running; } }

        /// <summary>实际绑定的端口（端口 0 自动分配时取实际值）。</summary>
        public int BoundPort { get; private set; } = -1;

        /// <summary>绑定地址。</summary>
        public string BoundHost { get; private set; }

        /// <summary>启动时间。</summary>
        public DateTime StartedAt { get; private set; }

        /// <summary>注册精确路由处理器（method 大小写不敏感，path 需完全匹配）。</summary>
        public void AddHandler(string method, string path, HttpHandler handler)
        {
            _routes[method.ToUpperInvariant() + " " + path] = handler;
        }

        /// <summary>设置未匹配路由时的兜底处理器。</summary>
        public void SetFallback(HttpHandler handler)
        {
            _fallback = handler;
        }

        /// <summary>
        /// 启动服务器。端口占用 / 无权限时抛出带中文说明的异常。
        /// </summary>
        public void Start(string host, int port)
        {
            if (_running) return;
            IPAddress address;
            try
            {
                address = IPAddress.Parse(host);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("监听地址无效: " + host + "（" + ex.Message + "）", ex);
            }

            _listener = new TcpListener(address, port);
            try
            {
                _listener.Start();
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException("端口绑定失败（" + host + ":" + port + "），端口可能已被占用或没有权限: " + ex.Message, ex);
            }

            BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BoundHost = host;
            StartedAt = DateTime.Now;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "VrcProjectMcp.Http.Accept" };
            _acceptThread.Start();
        }

        /// <summary>停止服务器并关闭全部连接（会解除 SSE 处理器的阻塞）。</summary>
        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* 已停止则忽略 */ }
            lock (_clientsLock)
            {
                foreach (TcpClient client in _clients)
                {
                    try { client.Close(); } catch { /* 忽略 */ }
                }
                _clients.Clear();
            }
        }

        /// <summary>后台 Accept 循环。</summary>
        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (_running) Thread.Sleep(50); // 瞬时错误则稍后重试
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break; // 监听器已关闭
                }
                catch (Exception ex)
                {
                    Logger?.Warn("[HTTP] 接受连接异常: " + ex.Message);
                    continue;
                }

                lock (_clientsLock) { _clients.Add(client); }
                TcpClient captured = client;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try { HandleClient(captured); }
                    catch (Exception ex) { Logger?.Warn("[HTTP] 连接处理异常: " + ex.Message); }
                    finally
                    {
                        lock (_clientsLock) { _clients.Remove(captured); }
                        try { captured.Close(); } catch { /* 忽略 */ }
                    }
                });
            }
        }

        /// <summary>处理单个连接：读请求 → 路由 → 处理器负责写出响应。</summary>
        private void HandleClient(TcpClient client)
        {
            NetworkStream stream = client.GetStream();
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            var context = new HttpContext { Stream = stream, Socket = client.Client };

            if (!ReadRequest(stream, context)) return; // 连接提前关闭或无数据

            string remote = RemoteDescription(client);
            Logger?.Info("[连接] " + remote + " " + context.Method + " " + context.RawPath);

            HttpHandler handler;
            if (!_routes.TryGetValue(context.Method.ToUpperInvariant() + " " + context.Path, out handler))
            {
                handler = _fallback ?? DefaultNotFound;
            }

            try
            {
                handler(context);
            }
            catch (Exception ex)
            {
                Logger?.Warn("[HTTP] 处理器异常(" + context.Method + " " + context.Path + "): " + ex.Message);
                TryWriteError(context, 500, "内部错误: " + ex.Message);
            }
        }

        /// <summary>描述远程端点（用于日志）。</summary>
        private static string RemoteDescription(TcpClient client)
        {
            try
            {
                if (client.Client != null && client.Client.RemoteEndPoint is IPEndPoint ep) return ep.Address + ":" + ep.Port;
            }
            catch { /* 忽略 */ }
            return "未知客户端";
        }

        /// <summary>读取 HTTP 请求（请求行 + 头 + body）。返回 false 表示连接无有效数据。</summary>
        private bool ReadRequest(NetworkStream stream, HttpContext context)
        {
            // ---- 1. 读取请求行与头部块（最多 64KB） ----
            var pending = new ByteReader(stream);
            byte[] block;
            bool closed;
            try
            {
                block = ReadHeaderBlock(pending, 64 * 1024, out closed);
            }
            catch (Exception ex)
            {
                Logger?.Warn("[HTTP] 读取请求头失败: " + ex.Message);
                return false;
            }
            if (closed && block.Length == 0) return false;

            // ---- 2. 解析请求行与头部 ----
            string headerText = Encoding.ASCII.GetString(block);
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return false;

            context.Method = requestLine[0].ToUpperInvariant();
            context.RawPath = requestLine[1];
            ParseTarget(context);

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                context.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            // ---- 3. 读取 body ----
            string transferEncoding = context.Header("Transfer-Encoding");
            if (transferEncoding != null && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                context.Body = ReadChunkedBody(pending);
            }
            else
            {
                long contentLength = 0;
                long.TryParse(context.Header("Content-Length"), out contentLength);
                if (contentLength > 0)
                {
                    if (contentLength > 64 * 1024 * 1024) throw new InvalidOperationException("请求体过大（>64MB）");
                    context.Body = ReadExact(pending, (int)contentLength);
                }
            }
            return true;
        }

        /// <summary>解析请求目标为 Path 与 Query。</summary>
        private static void ParseTarget(HttpContext context)
        {
            string target = context.RawPath ?? "/";
            int question = target.IndexOf('?');
            if (question >= 0)
            {
                context.Path = target.Substring(0, question);
                string query = target.Substring(question + 1);
                foreach (string pair in query.Split('&'))
                {
                    if (string.IsNullOrEmpty(pair)) continue;
                    int eq = pair.IndexOf('=');
                    if (eq < 0) context.Query[Uri.UnescapeDataString(pair)] = string.Empty;
                    else context.Query[Uri.UnescapeDataString(pair.Substring(0, eq))] = Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            else
            {
                context.Path = target;
            }
            if (string.IsNullOrEmpty(context.Path)) context.Path = "/";
        }

        /// <summary>读取请求头块（直到 \r\n\r\n）。返回的字节可能包含 body 的开头部分。</summary>
        private static byte[] ReadHeaderBlock(ByteReader reader, int maxBytes, out bool closed)
        {
            var buffer = new MemoryStream();
            var chunk = new byte[1024];
            while (buffer.Length < maxBytes)
            {
                int available = reader.ReadAvailable(chunk, 0, chunk.Length);
                if (available <= 0)
                {
                    closed = true;
                    return buffer.ToArray();
                }
                buffer.Write(chunk, 0, available);
                byte[] data = buffer.GetBuffer();
                int length = (int)buffer.Length;
                int terminatorIndex = IndexOfBytes(data, 0, length, HeaderTerminator);
                if (terminatorIndex >= 0)
                {
                    // 头部块之后的剩余字节放回 reader，作为 body 开头
                    int headerEnd = terminatorIndex + HeaderTerminator.Length;
                    reader.PushBack(data, headerEnd, length - headerEnd);
                    closed = false;
                    byte[] header = new byte[terminatorIndex];
                    Array.Copy(data, 0, header, 0, terminatorIndex);
                    return header;
                }
            }
            closed = true;
            throw new InvalidOperationException("请求头超过大小限制（64KB）");
        }

        /// <summary>在字节数组中查找子序列。</summary>
        private static int IndexOfBytes(byte[] data, int start, int length, byte[] pattern)
        {
            int max = length - pattern.Length;
            for (int i = start; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>读取 chunked 编码的 body。</summary>
        private static byte[] ReadChunkedBody(ByteReader reader)
        {
            var body = new MemoryStream();
            while (true)
            {
                string sizeLine = reader.ReadLine();
                if (sizeLine == null) break;
                int semicolon = sizeLine.IndexOf(';');
                string hex = semicolon >= 0 ? sizeLine.Substring(0, semicolon).Trim() : sizeLine.Trim();
                int size;
                if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out size))
                    throw new InvalidOperationException("chunked 编码块大小无效: " + hex);
                if (size == 0)
                {
                    reader.SkipTrailers();
                    break;
                }
                byte[] chunk = ReadExact(reader, size);
                body.Write(chunk, 0, chunk.Length);
                reader.SkipCrlf();
            }
            return body.ToArray();
        }

        /// <summary>精确读取指定字节数。</summary>
        private static byte[] ReadExact(ByteReader reader, int count)
        {
            var buffer = new byte[count];
            int offset = 0;
            while (offset < count)
            {
                int read = reader.ReadAvailable(buffer, offset, count - offset);
                if (read <= 0) throw new EndOfStreamException("连接在请求体读取完成前关闭");
                offset += read;
            }
            return buffer;
        }

        // ------------------------------------------------------------------
        // 响应写入
        // ------------------------------------------------------------------

        /// <summary>写普通 HTTP 响应（JSON 等）。</summary>
        public void WriteResponse(HttpContext context, int status, string contentType, byte[] body, Dictionary<string, string> extraHeaders = null)
        {
            if (context.SseStarted) return; // SSE 模式下不能再写普通响应
            if (context.ResponseWritten) return; // 已写过响应，防止双写

            lock (context.WriteLock)
            {
                context.ResponseWritten = true;
                var sb = new StringBuilder(256);
                sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(StatusText(status)).Append("\r\n");
                sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
                sb.Append("Content-Length: ").Append(body != null ? body.Length : 0).Append("\r\n");
                sb.Append("Connection: close\r\n");
                sb.Append("Cache-Control: no-store\r\n");
                AppendCorsHeaders(sb);
                if (extraHeaders != null)
                {
                    foreach (KeyValuePair<string, string> kv in extraHeaders) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
                }
                sb.Append("\r\n");

                byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                context.Stream.Write(headerBytes, 0, headerBytes.Length);
                if (body != null && body.Length > 0) context.Stream.Write(body, 0, body.Length);
                context.Stream.Flush();
            }
        }

        /// <summary>进入 SSE 模式：写出事件流响应头。</summary>
        public void BeginSse(HttpContext context)
        {
            lock (context.WriteLock)
            {
                if (context.ResponseWritten) return; // 已写过普通响应则不能再转 SSE
                context.SseStarted = true;
                context.ResponseWritten = true;
                var sb = new StringBuilder(192);
                sb.Append("HTTP/1.1 200 OK\r\n");
                sb.Append("Content-Type: text/event-stream; charset=utf-8\r\n");
                sb.Append("Cache-Control: no-cache\r\n");
                sb.Append("Connection: keep-alive\r\n");
                AppendCorsHeaders(sb);
                sb.Append("\r\n");
                byte[] headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
                context.Stream.Write(headerBytes, 0, headerBytes.Length);
                context.Stream.Flush();
            }
        }

        /// <summary>向 SSE 流写入一个事件（data 多行时自动补齐 "data: " 前缀）。</summary>
        public void WriteSse(HttpContext context, string eventName, string data)
        {
            lock (context.WriteLock)
            {
                var sb = new StringBuilder(128);
                if (!string.IsNullOrEmpty(eventName)) sb.Append("event: ").Append(eventName.Replace("\r", "").Replace("\n", "")).Append('\n');
                string payload = data ?? string.Empty;
                string[] lines = payload.Replace("\r\n", "\n").Split('\n');
                foreach (string line in lines) sb.Append("data: ").Append(line).Append('\n');
                sb.Append('\n');
                byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
                context.Stream.Write(bytes, 0, bytes.Length);
                context.Stream.Flush();
            }
        }

        /// <summary>
        /// 向 SSE 流写入注释行（SSE 协议的 ": xxx" 行）。
        /// 用于长连接心跳：既能探测客户端是否已断开（写入失败抛 IOException），又不会被客户端当作事件处理。
        /// </summary>
        public void WriteSseComment(HttpContext context, string comment)
        {
            lock (context.WriteLock)
            {
                string line = ":" + (comment ?? "keep-alive").Replace("\r", "").Replace("\n", "") + "\n\n";
                byte[] bytes = Encoding.UTF8.GetBytes(line);
                context.Stream.Write(bytes, 0, bytes.Length);
                context.Stream.Flush();
            }
        }

        /// <summary>输出 CORS 头（MCP Inspector 等浏览器客户端需要）。</summary>
        private static void AppendCorsHeaders(StringBuilder sb)
        {
            sb.Append("Access-Control-Allow-Origin: *\r\n");
            sb.Append("Access-Control-Allow-Headers: Content-Type, Accept, Mcp-Session-Id, Authorization\r\n");
            sb.Append("Access-Control-Expose-Headers: Mcp-Session-Id\r\n");
        }

        /// <summary>兜底 404 处理器。</summary>
        private void DefaultNotFound(HttpContext context)
        {
            var body = new Json.JsonObject()
                .Set("error", "未找到路径")
                .Set("method", context.Method)
                .Set("path", context.Path);
            WriteResponse(context, 404, "application/json; charset=utf-8",
                Encoding.UTF8.GetBytes(Json.MiniJson.Serialize(body)));
        }

        /// <summary>尽力写出错误响应（已写过响应或连接损坏时静默放弃，防止双写）。</summary>
        private void TryWriteError(HttpContext context, int status, string message)
        {
            if (context.ResponseWritten || context.SseStarted) return;
            try
            {
                var body = new Json.JsonObject().Set("error", message);
                WriteResponse(context, status, "application/json; charset=utf-8",
                    Encoding.UTF8.GetBytes(Json.MiniJson.Serialize(body)));
            }
            catch { /* 忽略 */ }
        }

        /// <summary>HTTP 状态码 → 状态文本。</summary>
        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                case 501: return "Not Implemented";
                default: return "OK";
            }
        }
    }

    /// <summary>
    /// 字节读取器：支持 push back（请求头块多读的 body 字节回退），供后续精确读取。
    /// </summary>
    internal sealed class ByteReader
    {
        private readonly NetworkStream _stream;
        private byte[] _pending = new byte[0];
        private int _pendingLength;

        /// <summary>构造读取器。</summary>
        public ByteReader(NetworkStream stream)
        {
            _stream = stream;
        }

        /// <summary>把多余字节放回待读区（追加到待读数据尾部，保持原有顺序）。</summary>
        public void PushBack(byte[] data, int offset, int count)
        {
            if (count <= 0) return;
            var merged = new byte[_pendingLength + count];
            if (_pendingLength > 0) Array.Copy(_pending, 0, merged, 0, _pendingLength);
            Array.Copy(data, offset, merged, _pendingLength, count);
            _pending = merged;
            _pendingLength += count;
        }

        /// <summary>读取可用数据：先消费待读区，再读网络流。返回 0 表示连接关闭。</summary>
        public int ReadAvailable(byte[] buffer, int offset, int count)
        {
            int total = 0;
            if (_pendingLength > 0)
            {
                int take = Math.Min(count, _pendingLength);
                Array.Copy(_pending, 0, buffer, offset, take);
                // 前移待读区剩余数据
                if (take < _pendingLength)
                {
                    Array.Copy(_pending, take, _pending, 0, _pendingLength - take);
                }
                _pendingLength -= take;
                total += take;
                offset += take;
                count -= take;
            }
            if (count > 0)
            {
                int read = _stream.Read(buffer, offset, count);
                if (read > 0) total += read;
            }
            return total;
        }

        /// <summary>读取一行（以 \r\n 结尾，返回不含换行的内容；连接关闭返回 null）。</summary>
        public string ReadLine()
        {
            var sb = new StringBuilder(128);
            int prev = -1;
            var one = new byte[1];
            while (true)
            {
                int read = ReadAvailable(one, 0, 1);
                if (read <= 0) return sb.Length > 0 ? sb.ToString() : null;
                char c = (char)one[0];
                if (prev == '\r' && c == '\n') return sb.ToString(0, sb.Length - 1);
                sb.Append(c);
                prev = c;
            }
        }

        /// <summary>跳过 chunked 结束后的 trailer 区（直到空行）。</summary>
        public void SkipTrailers()
        {
            string line;
            do { line = ReadLine(); } while (line != null && line.Length > 0);
        }

        /// <summary>跳过紧随的 \r\n（chunk 数据后的换行）。</summary>
        public void SkipCrlf()
        {
            var two = new byte[2];
            ReadAvailable(two, 0, 2);
        }
    }
}
