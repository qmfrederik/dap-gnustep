using System.Buffers;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

namespace ObjcDap
{
    /// <summary>
    /// Launches an external debug adapter (by default lldb-dap) and transparently relays
    /// DAP messages (Content-Length framed JSON) between the client and that adapter.
    /// Selected messages can be intercepted by overriding the protected virtual hooks (e.g. <see cref="OnVariablesAsync"/>).
    /// </summary>
    internal class DebugAdapterProxy
    {
        public const string DefaultAdapterPath = @"c:\Program Files\LLVM\bin\lldb-dap.exe";

        // Sequence numbers used for requests we originate; far above anything a client will use.
        private const int FirstProxySeq = 1_000_000_000;

        private static ReadOnlySpan<byte> ContentLengthHeader => "Content-Length:"u8;
        private static ReadOnlySpan<byte> HeaderTerminator => "\r\n"u8;

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

        private readonly string adapterPath;
        private readonly Process process;
        private readonly Stream clientInput;
        private readonly Stream clientOutput;

        private readonly SemaphoreSlim clientWriteLock = new(1, 1);
        private readonly SemaphoreSlim upstreamWriteLock = new(1, 1);
        private readonly CancellationTokenSource shutdown = new();

        private PipeWriter? clientWriter;
        private PipeWriter? upstreamWriter;
        private int nextProxySeq = FirstProxySeq;

        // Requests we sent to the adapter ourselves, completed when the matching response arrives.
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> pendingProxyRequests = new();

        // In-flight client requests, keyed by seq.
        private readonly ConcurrentDictionary<int, VariablesArguments> pendingVariables = new();
        private readonly ConcurrentDictionary<int, int> pendingScopes = new();

        // variablesReference -> frameId, so evaluations can run in the right stack frame.
        private readonly ConcurrentDictionary<int, int> referenceFrames = new();

        public DebugAdapterProxy(Stream input, Stream output, string? adapterPath = null)
        {
            this.clientInput = input;
            this.clientOutput = output;
            this.adapterPath = adapterPath ?? DefaultAdapterPath;

            var startInfo = new ProcessStartInfo(this.adapterPath)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // lldb-dap needs the Python runtime DLLs on PATH.
            var pythonDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Python",
                "pythoncore-3.14-64");
            var currentPath = startInfo.Environment.TryGetValue("PATH", out var p) ? p : null;
            startInfo.Environment["PATH"] = string.IsNullOrEmpty(currentPath)
                ? pythonDir
                : pythonDir + Path.PathSeparator + currentPath;

            this.process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start {this.adapterPath}.");
        }

        /// <summary>
        /// Called with the response to a "variables" request before it is returned to the client.
        /// Modify <paramref name="response"/> in place (or return a replacement) to change what the client sees.
        /// This runs off the relay threads, so it may call <see cref="EvaluateAsync"/>.
        /// The default implementation returns the response unchanged.
        /// </summary>
        /// <param name="arguments">The arguments of the original request.</param>
        /// <param name="frameId">The stack frame the variables belong to, if known.</param>
        /// <param name="response">The response body produced by the debug adapter.</param>
        /// <param name="cancellationToken">Cancelled when the proxy shuts down.</param>
        protected virtual ValueTask<VariablesResponse> OnVariablesAsync(
            VariablesArguments arguments,
            int? frameId,
            VariablesResponse response,
            CancellationToken cancellationToken) => ValueTask.FromResult(response);

        /// <summary>
        /// Evaluates an expression in the debuggee through the upstream adapter. Returns the result text, or null on failure.
        /// </summary>
        /// <param name="language">
        /// Forces the expression language (for example "c"). The DAP <c>evaluate</c> request has no language field, so this
        /// runs lldb's <c>expression --language</c> command via the REPL context (backtick prefix). Without it, lldb picks the
        /// language of the current frame, which for Objective-C frames rejects expressions it cannot handle.
        /// </param>
        protected async Task<string?> EvaluateAsync(string expression, int? frameId, CancellationToken cancellationToken, string? language = null)
        {
            var arguments = new JsonObject();
            if (language is null)
            {
                arguments["expression"] = expression;
                arguments["context"] = "watch";
            }
            else
            {
                arguments["expression"] = $"`expression --language {language} -- {expression}";
                arguments["context"] = "repl";
            }

            if (frameId is not null)
            {
                arguments["frameId"] = frameId.Value;
            }

            var response = await this.SendProxyRequestAsync("evaluate", arguments, cancellationToken).ConfigureAwait(false);
            if (response?["success"]?.GetValue<bool>() != true)
            {
                Debug.WriteLine($"evaluate failed: {response?["message"]}");
                return null;
            }

            return response["body"]?["result"]?.GetValue<string>();
        }

        /// <summary>
        /// Runs a raw lldb command (as typed in the debug console) and returns its output, or null on failure.
        /// </summary>
        protected async Task<string?> RunCommandAsync(string command, CancellationToken cancellationToken)
        {
            var arguments = new JsonObject
            {
                ["expression"] = "`" + command,
                ["context"] = "repl",
            };

            var response = await this.SendProxyRequestAsync("evaluate", arguments, cancellationToken).ConfigureAwait(false);
            return response?["success"]?.GetValue<bool>() == true
                ? response["body"]?["result"]?.GetValue<string>()
                : response?["message"]?.GetValue<string>();
        }

        private async Task<JsonNode?> SendProxyRequestAsync(string command, JsonObject arguments, CancellationToken cancellationToken)
        {
            var seq = Interlocked.Increment(ref this.nextProxySeq);
            var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.pendingProxyRequests[seq] = completion;

            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            try
            {
                var request = new JsonObject
                {
                    ["seq"] = seq,
                    ["type"] = "request",
                    ["command"] = command,
                    ["arguments"] = arguments,
                };

                var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
                await this.WriteLockedAsync(this.upstreamWriter!, this.upstreamWriteLock, bytes, CancellationToken.None).ConfigureAwait(false);
                return await completion.Task.ConfigureAwait(false);
            }
            finally
            {
                this.pendingProxyRequests.TryRemove(seq, out _);
            }
        }

        public void Run() => this.RunAsync().GetAwaiter().GetResult();

        private async Task RunAsync()
        {
            var clientReader = PipeReader.Create(this.clientInput);
            this.clientWriter = PipeWriter.Create(this.clientOutput);
            var upstreamReader = PipeReader.Create(this.process.StandardOutput.BaseStream);
            this.upstreamWriter = PipeWriter.Create(this.process.StandardInput.BaseStream);

            this.process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Debug.WriteLine($"lldb-dap stderr: {e.Data}");
                    Console.Error.WriteLine(e.Data);
                }
            };
            this.process.BeginErrorReadLine();

            var toUpstream = this.PumpAsync(clientReader, this.upstreamWriter, this.upstreamWriteLock, MessageDirection.ClientToAdapter);
            var toClient = this.PumpAsync(upstreamReader, this.clientWriter, this.clientWriteLock, MessageDirection.AdapterToClient);

            // Finish as soon as either side closes.
            var finished = await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
            Debug.WriteLine($"Relay stopping: {(finished == toUpstream ? "client->adapter" : "adapter->client")} pump ended.");
            this.shutdown.Cancel();

            this.Shutdown();
            if (this.process.HasExited)
            {
                // Parameterless WaitForExit lets redirected stderr finish draining.
                this.process.WaitForExit();
            }

            Debug.WriteLine($"Upstream exited: {this.process.HasExited}, code: {(this.process.HasExited ? this.process.ExitCode.ToString() : "n/a")}");
        }

        private enum MessageDirection
        {
            ClientToAdapter,
            AdapterToClient,
        }

        private enum Disposition
        {
            Forward,
            Drop,
        }

        /// <summary>
        /// Inspects every relayed message. <paramref name="body"/> is the raw JSON payload, valid only for the
        /// duration of this call.
        /// </summary>
        private Disposition OnMessage(MessageDirection direction, in ReadOnlySequence<byte> body)
        {
            Debug.WriteLine($"{direction}: {Encoding.UTF8.GetString(body)}");

            if (!TryPeekEnvelope(body, out var type, out var command, out var eventName, out var seq, out var requestSeq))
            {
                return Disposition.Forward;
            }

            try
            {
                if (direction == MessageDirection.ClientToAdapter && type == "request")
                {
                    this.TrackRequest(command, seq, body);
                }
                else if (direction == MessageDirection.AdapterToClient)
                {
                    if (type == "response")
                    {
                        return this.OnAdapterResponse(command, requestSeq, body);
                    }

                    if (type == "event" && eventName is "stopped" or "continued" or "terminated")
                    {
                        // Variable references are only valid while the debuggee is stopped.
                        this.referenceFrames.Clear();
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or NotSupportedException or ArgumentException)
            {
                // Never break the session because an interceptor failed; forward the original message.
                Debug.WriteLine($"Failed to intercept {command} message: {ex}");
            }

            return Disposition.Forward;
        }

        private void TrackRequest(string? command, int seq, in ReadOnlySequence<byte> body)
        {
            switch (command)
            {
                case "variables":
                    var variables = ParseNode(body)?["arguments"]?.Deserialize<VariablesArguments>(SerializerOptions);
                    if (variables is not null)
                    {
                        this.pendingVariables[seq] = variables;
                    }

                    break;

                case "scopes":
                    var frameId = ParseNode(body)?["arguments"]?["frameId"]?.GetValue<int>();
                    if (frameId is not null)
                    {
                        this.pendingScopes[seq] = frameId.Value;
                    }

                    break;
            }
        }

        private Disposition OnAdapterResponse(string? command, int requestSeq, in ReadOnlySequence<byte> body)
        {
            // Responses to our own requests never reach the client.
            if (this.pendingProxyRequests.TryGetValue(requestSeq, out var completion))
            {
                completion.TrySetResult(ParseNode(body));
                return Disposition.Drop;
            }

            switch (command)
            {
                case "scopes" when this.pendingScopes.TryRemove(requestSeq, out var frameId):
                    if (ParseNode(body)?["body"]?["scopes"] is JsonArray scopes)
                    {
                        foreach (var reference in scopes.Select(s => s?["variablesReference"]?.GetValue<int>()))
                        {
                            if (reference is > 0)
                            {
                                this.referenceFrames[reference.Value] = frameId;
                            }
                        }
                    }

                    return Disposition.Forward;

                case "variables" when this.pendingVariables.TryRemove(requestSeq, out var arguments):
                    var message = ParseNode(body);
                    if (message?["success"]?.GetValue<bool>() != true || message["body"] is not JsonObject)
                    {
                        return Disposition.Forward;
                    }

                    // Handled off the relay thread: the hook may issue evaluate requests whose responses
                    // must be read by this very pump.
                    _ = Task.Run(() => this.ProcessVariablesAsync(message, arguments));
                    return Disposition.Drop;
            }

            return Disposition.Forward;
        }

        private async Task ProcessVariablesAsync(JsonNode message, VariablesArguments arguments)
        {
            var output = message;
            try
            {
                output = await this.RewriteVariablesAsync(message, arguments).ConfigureAwait(false) ?? message;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Variables interception failed: {ex}");
            }

            try
            {
                await this.WriteLockedAsync(this.clientWriter!, this.clientWriteLock, JsonSerializer.SerializeToUtf8Bytes(output), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
            {
                Debug.WriteLine($"Could not deliver variables response: {ex.Message}");
            }
        }

        private async Task<JsonNode?> RewriteVariablesAsync(JsonNode message, VariablesArguments arguments)
        {
            var bodyObject = (JsonObject)message["body"]!;
            var response = bodyObject.Deserialize<VariablesResponse>(SerializerOptions);
            if (response is null)
            {
                return null;
            }

            var before = JsonSerializer.SerializeToNode(response, SerializerOptions);
            int? frameId = this.referenceFrames.TryGetValue(arguments.VariablesReference, out var f) ? f : null;

            var result = await this.OnVariablesAsync(arguments, frameId, response, this.shutdown.Token).ConfigureAwait(false);
            var after = JsonSerializer.SerializeToNode(result, SerializerOptions);

            if (bodyObject["variables"] is not JsonArray original
                || before?["variables"] is not JsonArray beforeVariables
                || after?["variables"] is not JsonArray afterVariables
                || beforeVariables.Count != original.Count
                || afterVariables.Count != original.Count)
            {
                // The shape changed (variables added or removed); replace the whole array.
                if (!JsonNode.DeepEquals(before, after))
                {
                    bodyObject["variables"] = after?["variables"]?.DeepClone() ?? new JsonArray();
                }

                return message;
            }

            // Patch only the properties that changed, so fields the library types don't model are preserved.
            for (var i = 0; i < original.Count; i++)
            {
                if (original[i] is not JsonObject target
                    || beforeVariables[i] is not JsonObject oldVariable
                    || afterVariables[i] is not JsonObject newVariable)
                {
                    continue;
                }

                foreach (var (name, value) in newVariable)
                {
                    if (!JsonNode.DeepEquals(oldVariable[name], value))
                    {
                        target[name] = value?.DeepClone();
                    }
                }
            }

            // Variables the client now sees may have children that need the same frame.
            foreach (var variable in result.Variables ?? [])
            {
                if (variable.VariablesReference > 0 && frameId is not null)
                {
                    this.referenceFrames[variable.VariablesReference] = frameId.Value;
                }
            }

            return message;
        }

        private static JsonNode? ParseNode(in ReadOnlySequence<byte> body)
        {
            var reader = new Utf8JsonReader(body);
            return JsonNode.Parse(ref reader);
        }

        /// <summary>
        /// Reads only the top-level envelope fields without materializing the message.
        /// </summary>
        private static bool TryPeekEnvelope(
            in ReadOnlySequence<byte> body,
            out string? type,
            out string? command,
            out string? eventName,
            out int seq,
            out int requestSeq)
        {
            type = null;
            command = null;
            eventName = null;
            seq = 0;
            requestSeq = 0;

            try
            {
                var reader = new Utf8JsonReader(body);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    return false;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("type"u8))
                    {
                        reader.Read();
                        type = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    else if (reader.ValueTextEquals("command"u8))
                    {
                        reader.Read();
                        command = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    else if (reader.ValueTextEquals("event"u8))
                    {
                        reader.Read();
                        eventName = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                    }
                    else if (reader.ValueTextEquals("seq"u8))
                    {
                        reader.Read();
                        reader.TryGetInt32(out seq);
                    }
                    else if (reader.ValueTextEquals("request_seq"u8))
                    {
                        reader.Read();
                        reader.TryGetInt32(out requestSeq);
                    }
                    else
                    {
                        reader.Read();
                        reader.Skip();
                    }
                }

                return type is not null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private async Task PumpAsync(PipeReader reader, PipeWriter writer, SemaphoreSlim writeLock, MessageDirection direction)
        {
            var cancellationToken = this.shutdown.Token;
            try
            {
                while (true)
                {
                    var contentLength = await ReadHeadersAsync(reader, cancellationToken).ConfigureAwait(false);
                    if (contentLength < 0)
                    {
                        return;
                    }

                    var body = await ReadBodyAsync(reader, contentLength, cancellationToken).ConfigureAwait(false);
                    if (body is null)
                    {
                        return;
                    }

                    var message = body.Value;
                    if (this.OnMessage(direction, message) == Disposition.Forward)
                    {
                        if (!await this.WriteLockedAsync(writer, writeLock, message, cancellationToken).ConfigureAwait(false))
                        {
                            return;
                        }
                    }

                    // Only release the body once it has been consumed and written.
                    reader.AdvanceTo(message.End);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{direction} pump failed: {ex}");
            }
            finally
            {
                Debug.WriteLine($"{direction} pump finished.");
                await reader.CompleteAsync().ConfigureAwait(false);
                await writer.CompleteAsync().ConfigureAwait(false);
            }
        }

        /// <summary>Writes one framed message. Returns false if the destination has closed.</summary>
        private async Task<bool> WriteLockedAsync(PipeWriter writer, SemaphoreSlim writeLock, ReadOnlySequence<byte> body, CancellationToken cancellationToken)
        {
            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                WriteHeader(writer, (int)body.Length);
                foreach (var segment in body)
                {
                    writer.Write(segment.Span);
                }

                var flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                return !(flush.IsCompleted || flush.IsCanceled);
            }
            finally
            {
                writeLock.Release();
            }
        }

        private Task<bool> WriteLockedAsync(PipeWriter writer, SemaphoreSlim writeLock, byte[] body, CancellationToken cancellationToken) =>
            this.WriteLockedAsync(writer, writeLock, new ReadOnlySequence<byte>(body), cancellationToken);

        /// <summary>
        /// Reads the header section of one message and returns its Content-Length, or -1 when the stream ends.
        /// </summary>
        private static async ValueTask<int> ReadHeadersAsync(PipeReader reader, CancellationToken cancellationToken)
        {
            var contentLength = -1;

            // Header section: lines terminated by CRLF, ended by an empty line.
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var sequence = new SequenceReader<byte>(buffer);
                var headersComplete = false;

                while (sequence.TryReadTo(out ReadOnlySpan<byte> line, HeaderTerminator))
                {
                    if (line.IsEmpty)
                    {
                        headersComplete = true;
                        break;
                    }

                    if (line.StartsWith(ContentLengthHeader))
                    {
                        var value = line[ContentLengthHeader.Length..].Trim((byte)' ');
                        contentLength = int.Parse(value, CultureInfo.InvariantCulture);
                    }
                }

                if (headersComplete)
                {
                    reader.AdvanceTo(sequence.Position);
                    break;
                }

                if (result.IsCompleted)
                {
                    return -1;
                }

                // Consumed whole lines only; mark everything as examined so we wait for more data.
                reader.AdvanceTo(sequence.Position, buffer.End);
            }

            if (contentLength < 0)
            {
                throw new InvalidDataException("DAP message is missing a Content-Length header.");
            }

            return contentLength;
        }

        /// <summary>
        /// Waits until <paramref name="contentLength"/> bytes are buffered and returns them without copying.
        /// The caller must call <c>reader.AdvanceTo(body.End)</c> when done. Returns null when the stream ends.
        /// </summary>
        private static async ValueTask<ReadOnlySequence<byte>?> ReadBodyAsync(PipeReader reader, int contentLength, CancellationToken cancellationToken)
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;

                if (buffer.Length >= contentLength)
                {
                    return buffer.Slice(0, contentLength);
                }

                if (result.IsCompleted)
                {
                    return null;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
            }
        }

        private static void WriteHeader(PipeWriter writer, int length)
        {
            writer.Write("Content-Length: "u8);

            var span = writer.GetSpan(11);
            Utf8Formatter.TryFormat(length, span, out var written);
            writer.Advance(written);

            writer.Write("\r\n\r\n"u8);
        }

        private void Shutdown()
        {
            try
            {
                if (!this.process.WaitForExit(2000))
                {
                    this.process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }
}
