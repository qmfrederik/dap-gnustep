using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace ObjcDap
{
    /// <summary>
    /// Launches an external debug adapter (by default lldb-dap) and transparently relays
    /// DAP messages (Content-Length framed JSON) between the client and that adapter.
    /// </summary>
    internal class DebugAdapterProxy
    {
        public const string DefaultAdapterPath = @"C:\Program Files\LLVM\bin\lldb-dap.exe";

        private static ReadOnlySpan<byte> ContentLengthHeader => "Content-Length:"u8;
        private static ReadOnlySpan<byte> HeaderTerminator => "\r\n"u8;

        private readonly string adapterPath;
        private readonly Process process;
        private readonly Stream clientInput;
        private readonly Stream clientOutput;

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

        public void Run() => this.RunAsync().GetAwaiter().GetResult();

        private async Task RunAsync()
        {
            var clientReader = PipeReader.Create(this.clientInput);
            var clientWriter = PipeWriter.Create(this.clientOutput);
            var upstreamReader = PipeReader.Create(this.process.StandardOutput.BaseStream);
            var upstreamWriter = PipeWriter.Create(this.process.StandardInput.BaseStream);

            using var cts = new CancellationTokenSource();

            this.process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Debug.WriteLine($"lldb-dap stderr: {e.Data}");
                    Console.Error.WriteLine(e.Data);
                }
            };
            this.process.BeginErrorReadLine();

            var toUpstream = this.PumpAsync(clientReader, upstreamWriter, MessageDirection.ClientToAdapter, cts.Token);
            var toClient = this.PumpAsync(upstreamReader, clientWriter, MessageDirection.AdapterToClient, cts.Token);

            // Finish as soon as either side closes.
            var finished = await Task.WhenAny(toUpstream, toClient).ConfigureAwait(false);
            Debug.WriteLine($"Relay stopping: {(finished == toUpstream ? "client->adapter" : "adapter->client")} pump ended.");
            cts.Cancel();

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

        /// <summary>
        /// Hook for later interception. <paramref name="body"/> is the raw JSON payload, valid only for the
        /// duration of this call (parse it with <see cref="System.Text.Json.Utf8JsonReader"/>; copy anything you keep).
        /// Return true to forward the original message unchanged, or false to drop it.
        /// </summary>
        private bool OnMessage(MessageDirection direction, in ReadOnlySequence<byte> body)
        {
            Debug.WriteLine($"{direction}: {Encoding.UTF8.GetString(body)}");
            return true;
        }

        private async Task PumpAsync(PipeReader reader, PipeWriter writer, MessageDirection direction, CancellationToken cancellationToken)
        {
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
                    if (this.OnMessage(direction, message))
                    {
                        WriteMessage(writer, message);
                    }

                    // Only release the body once it has been consumed and written.
                    reader.AdvanceTo(message.End);

                    var flush = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (flush.IsCompleted || flush.IsCanceled)
                    {
                        return;
                    }
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

        private static void WriteMessage(PipeWriter writer, in ReadOnlySequence<byte> body)
        {
            writer.Write("Content-Length: "u8);

            var span = writer.GetSpan(11);
            Utf8Formatter.TryFormat((int)body.Length, span, out var written);
            writer.Advance(written);

            writer.Write("\r\n\r\n"u8);
            foreach (var segment in body)
            {
                writer.Write(segment.Span);
            }
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
