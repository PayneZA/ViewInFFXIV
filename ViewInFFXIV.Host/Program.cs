namespace ViewInFFXIV.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, ViewInFFXIV.Shared.IpcConstants.MutexName, out var created);
        if (!created)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new HostForm(args));
    }
}
