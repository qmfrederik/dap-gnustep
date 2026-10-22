using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using static System.Net.Mime.MediaTypeNames;

namespace ObjcDap
{
    /// <summary>
    /// A proxy for GNUstep (libobjc2) targets. Appends the result of sending <c>-description</c> to every
    /// Objective-C object variable (through <c>objc_msgSend</c>) to the variable's value.
    /// </summary>
    internal sealed partial class ObjCDebugAdapter : DebugAdapterProxy
    {
        // libobjc2 exports these; none has debug info, so each call goes through an explicit function-pointer cast.
        private const string RegisterSelector = "((void *(*)(const char *))sel_registerName)";
        private const string SendReturningObject = "((void *(*)(void *, void *))objc_msgSend)";
        private const string SendReturningString = "((const char *(*)(void *, void *))objc_msgSend)";

        private static readonly TimeSpan DescribeTimeout = TimeSpan.FromSeconds(5);

        // Uppercase-named C types that are commonly used through a pointer and are definitely not objects.
        private static readonly HashSet<string> NonObjectiveCTypes = new(StringComparer.Ordinal)
        {
            "FILE", "DIR", "HANDLE", "HMODULE", "HINSTANCE", "WCHAR", "CHAR", "BYTE", "DWORD", "BOOL",
        };

        // "id", "id<Proto>", "NSString *", "MyClass<Proto> *"
        [GeneratedRegex(@"^\s*(?:id(?:\s*<[^>]*>)?|(?<class>[A-Z]\w*)(?:\s*<[^>]*>)?\s*\*)\s*$")]
        private static partial Regex ObjectTypeRegex();

        [GeneratedRegex(@"^\s*0x(?<hex>[0-9a-fA-F]+)")]
        private static partial Regex LeadingAddressRegex();

        [GeneratedRegex(@"\s*[\r\n]+\s*")]
        private static partial Regex LineBreakRegex();

        public ObjCDebugAdapter(Stream input, Stream output, string? adapterPath = null)
            : base(input, output, adapterPath)
        {
        }

        protected override async ValueTask<VariablesResponse> OnVariablesAsync(
            VariablesArguments arguments,
            int? frameId,
            VariablesResponse response,
            CancellationToken cancellationToken)
        {
            if (response.Variables is not { Count: > 0 } variables)
            {
                return response;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(DescribeTimeout);

            await Task.WhenAll(variables
                .Where(IsObjectiveCObject)
                .Select(variable => this.GetVariableDetails(variable, frameId, timeout.Token))).ConfigureAwait(false);

            return response;
        }

        /// <summary>
        /// Decides whether a variable holds a pointer to an Objective-C object, based on its declared type.
        /// Sending a message to a non-object pointer can crash the target, so this is deliberately conservative.
        /// </summary>
        private static bool IsObjectiveCObject(Variable variable)
        {
            if (variable.Type is not { Length: > 0 } type)
            {
                return false;
            }

            var match = ObjectTypeRegex().Match(type);
            if (!match.Success)
            {
                return false;
            }

            if (match.Groups["class"] is { Success: true } cls && NonObjectiveCTypes.Contains(cls.Value))
            {
                return false;
            }

            return TryGetAddress(variable, out _);
        }

        private async Task GetVariableDetails(Variable variable, int? frameId, CancellationToken cancellationToken)
        {
            if (!TryGetAddress(variable, out var address))
            {
                return;
            }

            Stopwatch s = new Stopwatch();
            s.Start();

            // [[obj description] UTF8String], written in C:
            //   objc_msgSend(objc_msgSend(obj, sel_registerName("description")), sel_registerName("UTF8String"))
            var objectAddress = "(void *)0x" + address.ToString("x", CultureInfo.InvariantCulture);
            var getVariable = $"objc_dap_get_variable({objectAddress})";

            try
            {
                var result = await this.EvaluateAsync(getVariable, frameId, cancellationToken, language: "c").ConfigureAwait(false);
                var text = result is null ? null : ExtractCString(result);

                // lldb-dap can report a failed command as a successful response whose text holds the error,
                // so a missing string (not a null result) is what identifies a failure.
                if (text is null)
                {
                    Debug.WriteLine($"-description call produced no string. Raw result:\n{result}");
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    variable.Value = $"{variable.Value} {LineBreakRegex().Replace(text.Trim(), " ")}";
                }
            }
            catch (OperationCanceledException)
            {
                // Timed out or shutting down: leave the variable as lldb-dap reported it.
            }

            s.Stop();
            Debug.WriteLine($"-description call took {s.ElapsedMilliseconds} ms");

        }

        /// <summary>
        /// Uses <c>readMemory</c> to check whether the variable points to an Objective-C object (libobjc2, 64-bit layout)
        /// and, if so, appends the class name to the variable's value. Returns the class name, or null if it is not an object.
        /// </summary>
        private async Task<string?> DescribeClassAsync(Variable variable, CancellationToken cancellationToken)
        {
            const int PointerSize = 8;
            const int ClassNameOffset = 2 * PointerSize; // struct objc_class { isa; super_class; name; ... }
            const int MaxClassNameLength = 256;

            if (!TryGetAddress(variable, out var objectAddress) || objectAddress % PointerSize != 0)
            {
                return null;
            }

            Stopwatch s = new Stopwatch();
            s.Start();
            try
            {
                // The first word of an object is its isa (class) pointer.
                var isaBytes = await this.ReadMemoryAsync(objectAddress, PointerSize, cancellationToken).ConfigureAwait(false);
                if (isaBytes is null || isaBytes.Length < PointerSize)
                {
                    return null;
                }

                var classAddress = BitConverter.ToUInt64(isaBytes, 0);
                if (classAddress == 0 || classAddress % PointerSize != 0)
                {
                    return null;
                }

                var classBytes = await this.ReadMemoryAsync(classAddress, ClassNameOffset + PointerSize, cancellationToken).ConfigureAwait(false);
                if (classBytes is null || classBytes.Length < ClassNameOffset + PointerSize)
                {
                    return null;
                }

                var nameAddress = BitConverter.ToUInt64(classBytes, ClassNameOffset);
                if (nameAddress == 0)
                {
                    return null;
                }

                var nameBytes = await this.ReadMemoryAsync(nameAddress, MaxClassNameLength, cancellationToken).ConfigureAwait(false);
                if (nameBytes is null)
                {
                    return null;
                }

                var length = Array.IndexOf(nameBytes, (byte)0);
                if (length <= 0)
                {
                    return null;
                }

                // Class names are identifiers; anything else means this was not a class structure.
                var name = Encoding.ASCII.GetString(nameBytes, 0, length);
                if (!name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '$' or '.' or '<' or '>'))
                {
                    return null;
                }

                variable.Value = $"{variable.Value} {name}";

                s.Stop();
                Debug.WriteLine($"class description took {s.ElapsedMilliseconds} ms");
                return name;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        private async Task DescribeAsync(Variable variable, int? frameId, CancellationToken cancellationToken)
        {
            if (!TryGetAddress(variable, out var address))
            {
                return;
            }

            Stopwatch s = new Stopwatch();
            s.Start();

            // [[obj description] UTF8String], written in C:
            //   objc_msgSend(objc_msgSend(obj, sel_registerName("description")), sel_registerName("UTF8String"))
            var objectAddress = "(void *)0x" + address.ToString("x", CultureInfo.InvariantCulture);
            var description = $"{SendReturningObject}({objectAddress}, {RegisterSelector}(\"description\"))";
            var expression = $"{SendReturningString}({description}, {RegisterSelector}(\"UTF8String\"))";

            try
            {
                var result = await this.EvaluateAsync(expression, frameId, cancellationToken, language: "c").ConfigureAwait(false);
                var text = result is null ? null : ExtractCString(result);

                // lldb-dap can report a failed command as a successful response whose text holds the error,
                // so a missing string (not a null result) is what identifies a failure.
                if (text is null)
                {
                    Debug.WriteLine($"-description call produced no string. Raw result:\n{result}");
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    variable.Value = $"{variable.Value} {LineBreakRegex().Replace(text.Trim(), " ")}";
                }
            }
            catch (OperationCanceledException)
            {
                // Timed out or shutting down: leave the variable as lldb-dap reported it.
            }

            s.Stop();
            Debug.WriteLine($"-description call took {s.ElapsedMilliseconds} ms");
        }

        private async Task LogSymbolDiagnosticsAsync(CancellationToken cancellationToken)
        {
            // Are the libobjc2 entry points visible to lldb at all, and under what names?
            var send = await this.RunCommandAsync("image lookup -r -s msgSend", cancellationToken).ConfigureAwait(false);
            Debug.WriteLine($"symbol lookup 'msgSend':\n{send}");

            var selector = await this.RunCommandAsync("image lookup -s sel_registerName", cancellationToken).ConfigureAwait(false);
            Debug.WriteLine($"symbol lookup 'sel_registerName':\n{selector}");

            var modules = await this.RunCommandAsync("image list gnustep", cancellationToken).ConfigureAwait(false);
            Debug.WriteLine($"gnustep modules:\n{modules}");
        }

        private static bool TryGetAddress(Variable variable, out ulong address)
        {
            address = 0;
            if (variable.Value is null)
            {
                return false;
            }

            var match = LeadingAddressRegex().Match(variable.Value);
            return match.Success
                && ulong.TryParse(match.Groups["hex"].ValueSpan, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address)
                && address != 0;
        }

        /// <summary>
        /// Extracts the text from an lldb <c>const char *</c> result such as <c>0x00000123 "some \"text\""</c>.
        /// </summary>
        private static string? ExtractCString(string result)
        {
            var start = result.IndexOf('"');
            var end = result.LastIndexOf('"');
            if (start < 0 || end <= start)
            {
                return null;
            }

            var raw = result.AsSpan(start + 1, end - start - 1);
            var sb = new StringBuilder(raw.Length);
            for (var i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\\' || i + 1 >= raw.Length)
                {
                    sb.Append(raw[i]);
                    continue;
                }

                i++;
                sb.Append(raw[i] switch
                {
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    '0' => '\0',
                    _ => raw[i],
                });
            }

            return sb.ToString();
        }
    }
}
