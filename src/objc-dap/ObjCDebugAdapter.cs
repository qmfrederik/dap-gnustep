using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Shared.VSCodeDebugProtocol.Messages;

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

        private int symbolDiagnosticsLogged;

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
                .Select(variable => this.DescribeAsync(variable, frameId, timeout.Token))).ConfigureAwait(false);

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

        private async Task DescribeAsync(Variable variable, int? frameId, CancellationToken cancellationToken)
        {
            if (!TryGetAddress(variable, out var address))
            {
                return;
            }

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
