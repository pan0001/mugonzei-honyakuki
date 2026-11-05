using System.ComponentModel;
using System.Diagnostics;

namespace SteamVrTranslator;

internal static class SteamVrPresence
{
    private static readonly int Session = CurrentSession();
    private static int CurrentSession()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    // A runtime installation or attached headset is insufficient. Only attach
    // when the user has launched the server AND compositor in this login session.
    internal static int ReadServerPid()
    {
        int server = FindProcess("vrserver");
        return server != 0 && FindProcess("vrcompositor") != 0 ? server : 0;
    }

    private static int FindProcess(string name)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return 0; }
        try
        {
            foreach (var process in processes)
            {
                try { if (process.SessionId == Session && !process.HasExited) return process.Id; }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
            }
            return 0;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}

/// <summary>Samples the user's session in the background. OpenVR remains confined to its owner thread.</summary>
internal sealed class RuntimeModeMonitor(Func<Task<int>> sample, Func<bool> connected, Func<bool> attach, Action detach)
{
    private Task<int>? pending;
    private long nextProbe, nextAttach;
    private int observed, exiting, failures;

    internal void Refresh()
    {
        nextProbe = nextAttach = 0;
    }

    internal void RuntimeQuit()
    {
        // GetProcessesByName may still see a server that has already sent VREvent_Quit.
        exiting = observed;
        Refresh();
    }

    internal void Check(string preference, long now)
    {
        if (preference == "Desktop")
        {
            if (connected()) detach();
            observed = 0;
            pending = null;
            failures = 0;
            Refresh();
            return;
        }

        if (pending is null && now >= nextProbe)
        {
            pending = sample();
            nextProbe = now + 1500;
        }
        if (pending is { IsCompletedSuccessfully: true } result)
        {
            int current = result.Result;
            pending = null;
            if (current != observed)
            {
                if (connected()) detach();
                observed = current;
                if (current != exiting) exiting = 0;
                failures = 0;
                nextAttach = 0;
            }
        }
        else if (pending is { IsCompleted: true } failed)
        {
            _ = failed.Exception;
            pending = null;
        }
        if (connected() || observed == 0 || observed == exiting || now < nextAttach) return;
        if (attach()) failures = 0;
        else failures = Math.Min(failures + 1, 3);
        // During a slow SteamVR startup retry gently. A new PID resets the delay.
        nextAttach = now + (1500L << failures);
    }
}
