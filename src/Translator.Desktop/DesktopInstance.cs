using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Translator.Desktop;

internal sealed class DesktopInstance(FileStream lease, Mutex? sessionMutex, string activationPath) : IDisposable
{
    private readonly object activationGate = new();
    private FileSystemWatcher? watcher;

    public static DesktopInstance? Acquire()
    {
        string scope = GetScope();
        string directory = GetInstanceDirectory();
        Directory.CreateDirectory(directory);
        string lockPath = Path.Combine(directory, $"desktop-{scope}.lock");
        Mutex? mutex = null;
        bool ownsMutex = false;
        FileStream? file = null;
        try
        {
            // The session mutex also covers regular Windows launches from different installation paths.
            try
            {
                string prefix = OperatingSystem.IsWindows() ? @"Local\" : "";
                mutex = new Mutex(false, $"{prefix}Translater.Desktop.{scope}");
                try { ownsMutex = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                {
                    RequestActivation(lockPath, directory, scope);
                    return null;
                }
            }
            catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
            {
                mutex?.Dispose();
                mutex = null;
            }

            // Debug/Release and regular/isolated launches from this workspace share this file lease.
            try { file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read); }
            catch (IOException exception) when (IsFileLocked(exception))
            {
                RequestActivation(lockPath, directory, scope);
                return null;
            }

            Guid generation = Guid.NewGuid();
            file.SetLength(0);
            file.Write(Encoding.ASCII.GetBytes(generation.ToString("N")));
            file.Flush();
            var instance = new DesktopInstance(file, mutex, ActivationFile(directory, scope, generation));
            file = null;
            mutex = null;
            return instance;
        }
        finally
        {
            file?.Dispose();
            if (mutex is not null)
            {
                if (ownsMutex) mutex.ReleaseMutex();
                mutex.Dispose();
            }
        }
    }

    public void Listen(Action activate)
    {
        watcher = new FileSystemWatcher(Path.GetDirectoryName(activationPath)!, Path.GetFileName(activationPath))
        {
            NotifyFilter = NotifyFilters.FileName
        };
        watcher.Created += (_, _) => ConsumeActivation(activate);
        watcher.EnableRaisingEvents = true;
        ConsumeActivation(activate);
    }

    private void ConsumeActivation(Action activate)
    {
        lock (activationGate)
        {
            if (!File.Exists(activationPath)) return;
            try { File.Delete(activationPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        }

        activate();
    }

    private static void RequestActivation(string lockPath, string directory, string scope)
    {
        // A second launch can arrive before the primary has written its generation identifier.
        Span<byte> buffer = stackalloc byte[32];
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                using var owner = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                owner.ReadExactly(buffer);
                if (Guid.TryParseExact(Encoding.ASCII.GetString(buffer), "N", out Guid generation))
                {
                    using var signal = new FileStream(ActivationFile(directory, scope, generation), FileMode.OpenOrCreate,
                        FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    return;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { return; }
            Thread.Sleep(30);
        }
    }

    private static string ActivationFile(string directory, string scope, Guid generation) =>
        Path.Combine(directory, $"desktop-{scope}-{generation:N}.activate");

    private static bool IsFileLocked(IOException exception)
    {
        int error = exception.HResult & 0xffff;
        return error is 32 or 33 || (!OperatingSystem.IsWindows() && error is 11 or 35);
    }

    private static string GetInstanceDirectory()
    {
        for (DirectoryInfo? parent = new(AppContext.BaseDirectory); parent is not null; parent = parent.Parent)
        {
            if (File.Exists(Path.Combine(parent.FullName, "Translator.slnx")))
                return Path.Combine(parent.FullName, "runtime", "instance");
        }

        return Path.Combine(SettingsStore.UserDirectory, "runtime", "instance");
    }

    private static string GetScope()
    {
        string user = Environment.UserName;
        int session = 0;
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            using Process process = Process.GetCurrentProcess();
            user = identity.User?.Value ?? user;
            session = process.SessionId;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{user}:{session}")))[..24];
    }

    public void Dispose()
    {
        watcher?.Dispose();
        // Keep the lock file itself and release the OS handle; deleting it could race a new owner.
        lease.Dispose();
        if (sessionMutex is not null)
        {
            sessionMutex.ReleaseMutex();
            sessionMutex.Dispose();
        }
    }
}
