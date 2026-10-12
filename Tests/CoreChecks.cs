using System.IO;
using AuroraTranslate;

internal static class CoreChecks
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        var path = args.Length > 0 ? args[0] : Path.GetFullPath("self-test.json");
        var success = await SelfTestRunner.RunAsync(path);
        Console.WriteLine(File.ReadAllText(path));
        return success ? 0 : 1;
    }
}
