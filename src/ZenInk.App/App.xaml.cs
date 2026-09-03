//-----------------------------------------------------------------------------------------
// <copyright file="App.xaml.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using ZenInk.Core;

namespace ZenInk_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public new static App Current => (App)Application.Current;

    public Window MainWindow => _window ?? throw new InvalidOperationException("La ventana principal aún no se ha creado.");

    /// <summary>The main window, or null before it exists — for startup-time callers.</summary>
    public Window? MainWindowOrNull => _window;

    /// <summary>
    /// The drawings this launch was asked to open — from double-clicking a PDF
    /// in the Explorer, mostly. Read by the page once it is on screen.
    /// </summary>
    public IReadOnlyList<string> LaunchFiles { get; private set; } = [];

    /// <summary>
    /// Drawings sent to the window that is already open, because a later launch
    /// handed its work over instead of starting a window of its own.
    /// </summary>
    public event Action<IReadOnlyList<string>>? FilesActivated;

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        LaunchFiles = DrawingsAsked();

        if (!SingleInstance.Claim(LaunchFiles))
        {
            // The drawings are with the window that was already open. There is
            // no orderly way down from here: nothing has been built, and the
            // framework's own shutdown expects a window that will never exist.
            Process.GetCurrentProcess().Kill();
            return;
        }

        SingleInstance.DrawingsReceived += OnDrawingsReceived;

        _window = new MainWindow();
        AppTheme.Apply(AppTheme.Current, persist: false);
        _window.Activate();
    }

    /// <summary>
    /// What this launch was asked to open.
    ///
    /// The command line is the answer that works. A packaged application with a
    /// file type association and a full-trust entry point is started by the
    /// shell with the paths as ordinary arguments — measured, not assumed — and
    /// on that same route the App SDK's own
    /// <c>AppInstance.GetActivatedEventArgs</c> throws "RPC server unavailable"
    /// and takes the process with it. It is still asked, because it is the
    /// answer on the file contract route and costs nothing when it fails, but
    /// nothing depends on it.
    /// </summary>
    private static IReadOnlyList<string> DrawingsAsked()
    {
        var drawings = new List<string>(PdfAssociation.DrawingsIn(Environment.GetCommandLineArgs()));

        foreach (string path in FromFileContract())
        {
            if (!drawings.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                drawings.Add(path);
            }
        }

        return drawings;
    }

    private static IReadOnlyList<string> FromFileContract()
    {
        try
        {
            AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();

            if (activation.Kind != ExtendedActivationKind.File) return [];
            if (activation.Data is not IFileActivatedEventArgs files) return [];

            return
            [
                .. files.Files
                    .OfType<StorageFile>()
                    .Where(file => string.Equals(file.FileType, ".pdf", StringComparison.OrdinalIgnoreCase))
                    .Select(file => file.Path)
            ];
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// A later launch handed us its drawings. This arrives on the listening
    /// thread, so everything it leads to is put back on the window's own.
    /// </summary>
    private void OnDrawingsReceived(IReadOnlyList<string> drawings)
    {
        if (_window is not { } window) return;

        window.DispatcherQueue.TryEnqueue(() =>
        {
            // The reader double-clicked a drawing; the window has to come to
            // them, minimised or buried as it may be.
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Restore();
            }

            window.Activate();

            if (drawings.Count > 0)
            {
                FilesActivated?.Invoke(drawings);
            }
        });
    }
}
