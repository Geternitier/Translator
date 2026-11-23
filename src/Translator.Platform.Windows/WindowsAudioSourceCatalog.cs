using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Translator.Core;

namespace Translator.Platform.Windows;

public sealed class WindowsAudioSourceCatalog : IAudioSourceCatalog
{
    public IReadOnlyList<AudioSource> ListSources()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var audioSessions = ReadSessions();
        var sources = new List<(AudioSource Source, int Pid)>();
        using Process current = Process.GetCurrentProcess();
        int sessionId = current.SessionId;
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != sessionId) continue;
                    bool hasSession = audioSessions.TryGetValue(process.Id, out bool isActive);
                    if (!hasSession && process.MainWindowHandle == IntPtr.Zero) continue;

                    string id = string.Create(CultureInfo.InvariantCulture,
                        $"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}");
                    sources.Add((new AudioSource(id, ProgramName(process), isActive), process.Id));
                }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
                {
                    // A process can exit or become inaccessible between enumeration and inspection.
                }
            }
        }

        return sources.GroupBy(item => item.Source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .SelectMany(group => group.Count() == 1
                ? group.Select(item => item.Source)
                : group.Select(item => item.Source with { DisplayName = $"{item.Source.DisplayName}（PID {item.Pid}）" }))
            .OrderByDescending(source => source.IsActive)
            .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string ProgramName(Process process)
    {
        try
        {
            string? name = process.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
        return process.ProcessName;
    }

    [SupportedOSPlatform("windows")]
    private static Dictionary<int, bool> ReadSessions()
    {
        var sources = new Dictionary<int, bool>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (MMDevice endpoint in endpoints)
            {
                using (endpoint)
                {
                    try
                    {
                        using var manager = endpoint.AudioSessionManager;
                        var sessions = manager.Sessions;
                        for (int index = 0; index < sessions.Count; index++)
                        {
                            using var session = sessions[index];
                            if (session.IsSystemSoundsSession || session.State == AudioSessionState.AudioSessionStateExpired)
                                continue;
                            int pid = (int)session.GetProcessID;
                            if (pid > 0) sources[pid] = sources.GetValueOrDefault(pid)
                                || session.State == AudioSessionState.AudioSessionStateActive;
                        }
                    }
                    catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
                }
            }
        }
        catch (Exception exception) when (exception is COMException or InvalidOperationException) { }
        return sources;
    }
}
