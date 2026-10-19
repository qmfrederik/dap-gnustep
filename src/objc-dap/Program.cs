using System.Diagnostics;

namespace ObjcDap
{
    internal class Program
    {
        public static Task<int> Main(string[] args)
        {
            Debugger.Launch();

            var proxy = new DebugAdapterProxy(Console.OpenStandardInput(), Console.OpenStandardOutput());
            proxy.Run();
            return Task.FromResult(0);
        }
    }
}
