//-----------------------------------------------------------------------------------------
// <copyright file="SingleInstance.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace ZenInk_App;

/// <summary>
/// One window, however many drawings.
///
/// Now that Windows can open PDFs with ZenInk, a folder of sheets is opened one
/// double-click at a time and each of those is a fresh process. Left alone they
/// would be a window each, which is the tab strip thrown away. The first one to
/// start keeps the window; the rest hand it their drawings and go.
///
/// A mutex decides who is first and a named pipe carries the paths. The Windows
/// App SDK has <c>AppInstance.RedirectActivationToAsync</c> for exactly this,
/// and it cannot be used here: on the route the shell actually takes for a
/// file association — a full-trust process started with the path on its
/// command line — <c>GetActivatedEventArgs</c> answers "RPC server unavailable"
/// and takes the application down with it. See <see cref="App.DrawingsAsked"/>.
///
/// Everything here fails towards opening a window of our own. A second window
/// is a nuisance; a drawing that will not open is not.
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\ZenInk.instancia";
    private const string PipeName = "ZenInk.instancia";

    /// <summary>How long a newcomer waits for the pipe before giving up and opening its own window.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(3);

    /// <summary>Held for the life of the process by whichever instance is first.</summary>
    private static Mutex? _claim;

    /// <summary>Drawings handed over by a later launch. Raised on a worker thread.</summary>
    public static event Action<IReadOnlyList<string>>? DrawingsReceived;

    /// <summary>
    /// True when this process is the one that owns the window. False when the
    /// drawings have been handed to a copy already running, and this process
    /// has nothing left to do.
    /// </summary>
    public static bool Claim(IReadOnlyList<string> drawings)
    {
        try
        {
            _claim = new Mutex(initiallyOwned: true, MutexName, out bool first);

            if (first)
            {
                Listen();
                return true;
            }
        }
        catch (Exception)
        {
            return true;
        }

        try
        {
            HandOver(drawings);
            return false;
        }
        catch (Exception)
        {
            // The other instance may be shutting down, or stuck before its
            // pipe was up. Opening a window is better than opening nothing.
            return true;
        }
    }

    /// <summary>Waits for later launches, one at a time, for as long as the window lives.</summary>
    private static void Listen()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);

                    pipe.WaitForConnection();

                    using var reader = new StreamReader(pipe);
                    var drawings = new List<string>();
                    while (reader.ReadLine() is { } line)
                    {
                        if (line.Length > 0) drawings.Add(line);
                    }

                    DrawingsReceived?.Invoke(drawings);
                }
                catch (Exception)
                {
                    // One botched hand-over must not end the listening. The
                    // pause is so a pipe that cannot be created at all — a name
                    // taken by something else — does not spin a core.
                    Thread.Sleep(500);
                }
            }
        })
        {
            IsBackground = true,
            Name = "ZenInk instancia única",
        };

        thread.Start();
    }

    private static void HandOver(IReadOnlyList<string> drawings)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
        pipe.Connect((int)Patience.TotalMilliseconds);

        // Foreground rights are the sender's to give away: without this the
        // window that is already open cannot raise itself, and the drawing
        // opens behind whatever the reader was looking at.
        AllowSetForegroundWindow(-1);

        using var writer = new StreamWriter(pipe);
        foreach (string path in drawings)
        {
            writer.WriteLine(path);
        }

        writer.Flush();
    }

    /// <summary>-1 is ASFW_ANY: any process may take the foreground from us.</summary>
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);
}
