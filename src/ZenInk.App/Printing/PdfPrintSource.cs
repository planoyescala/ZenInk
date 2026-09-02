using System.Globalization;
using Microsoft.Graphics.Canvas.Printing;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using ZenInk.Core;
using CorePrintOrientation = ZenInk.Core.PrintOrientation;

namespace ZenInk_App.Printing;

/// <summary>
/// Plugs a <see cref="PdfPrintJob"/> into the Windows print experience.
///
/// The printer, the paper, the orientation, the copies and the colour mode are
/// Windows' own business and stay in its pane — reimplementing them would only
/// produce a worse version that disagrees with the driver. What Windows has no
/// idea about is what a plan needs: printing at true scale, or spilling an A0
/// across a stack of A3s. Those are added to the same pane as extra options, so
/// there is one dialog and one preview, and the preview is drawn by the very
/// code that draws the paper.
/// </summary>
public sealed class PdfPrintSource : IAsyncDisposable
{
    private const string ScaleOptionId = "zenink.scale";
    private const string PosterOptionId = "zenink.poster";
    private const string QualityOptionId = "zenink.quality";
    private const string MarginOptionId = "zenink.margins";
    private const string InkOptionId = "zenink.ink";

    private readonly PdfPrintJob _job;
    private readonly string _title;
    private readonly CanvasPrintDocument _document;
    private readonly IntPtr _window;

    /// <summary>The printer's own unprintable border, as the page description reports it.</summary>
    private PrintMarginsPt _hardwareMargins;

    private string _marginChoice = "printer";
    private PrintManager? _manager;
    private PrintTask? _task;

    /// <summary>Completed when Windows is finished with the job — printed or cancelled.</summary>
    private readonly TaskCompletionSource _finished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public PdfPrintSource(PdfPrintJob job, string title, IntPtr window)
    {
        _job = job;
        _title = title;
        _window = window;

        _document = new CanvasPrintDocument();
        _document.PrintTaskOptionsChanged += OnPrintTaskOptionsChanged;
        _document.Preview += OnPreview;
        _document.Print += OnPrint;
    }

    /// <summary>Set when a page failed to draw, so the caller can say what went wrong.</summary>
    public string? Problem
    {
        get => field ?? _job.Problem;
        private set;
    }

    /// <summary>
    /// Opens the Windows print pane and stays open until the reader is done
    /// with it.
    ///
    /// The waiting is the whole point. `ShowPrintUIForWindowAsync` completes as
    /// soon as the pane is *on screen*, not when it is dismissed — so returning
    /// there would let the caller tear this job down, closing the document out
    /// from under the preview that is still being drawn. The result of that is
    /// a pane that works perfectly and previews a blank sheet.
    /// </summary>
    public async Task ShowAsync()
    {
        _manager = PrintManagerInterop.GetForWindow(_window);
        _manager.PrintTaskRequested += OnPrintTaskRequested;

        try
        {
            bool shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(_window);
            if (!shown)
            {
                Problem = "Windows no pudo abrir el panel de impresión.";
                return;
            }

            // No task means Windows never asked us for one, so there is
            // nothing to wait for.
            if (_task is not null)
            {
                await _finished.Task;
            }
        }
        finally
        {
            _manager.PrintTaskRequested -= OnPrintTaskRequested;
            _manager = null;
        }
    }

    private void OnPrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var task = args.Request.CreatePrintTask(_title, request => request.SetSource(_document));
        _task = task;

        // The job has to outlive the pane, and this is the only signal that
        // says the pane is finished with it — whether it printed or was
        // cancelled.
        task.Completed += (_, completion) =>
        {
            if (completion.Completion == PrintTaskCompletion.Failed)
            {
                Problem ??= "Windows no pudo completar la impresión.";
            }
            _finished.TrySetResult();
        };

        // A landscape drawing on portrait paper prints at half the size it
        // could. Windows owns the setting, so the honest thing is to start it
        // where the drawing wants it and leave the reader free to change it.
        if (_job.Sheets.Count > 0)
        {
            int first = _job.Pages.Count > 0 ? _job.Pages[0] : 0;
            var sheet = _job.Sheets[Math.Clamp(first, 0, _job.Sheets.Count - 1)];
            task.Options.Orientation = sheet.WidthPt > sheet.HeightPt
                ? Windows.Graphics.Printing.PrintOrientation.Landscape
                : Windows.Graphics.Printing.PrintOrientation.Portrait;
        }

        var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
        details.DisplayedOptions.Clear();
        details.DisplayedOptions.Add(StandardPrintTaskOptions.Copies);
        details.DisplayedOptions.Add(StandardPrintTaskOptions.MediaSize);
        details.DisplayedOptions.Add(StandardPrintTaskOptions.Orientation);
        details.DisplayedOptions.Add(StandardPrintTaskOptions.ColorMode);

        AddScaleOption(details);
        AddMarginOption(details);
        AddQualityOption(details);
        AddPosterOption(details);
        AddInkOption(details);

        details.OptionChanged += OnOptionChanged;
    }

    private void AddScaleOption(PrintTaskOptionDetails details)
    {
        var option = details.CreateItemListOption(ScaleOptionId, "Escala");
        option.AddItem("fit", "Ajustar al papel");
        option.AddItem("actual", "Tamaño real (1:1)");

        // The drawing scales come first among the numbers, and only for a
        // calibrated sheet: on one that has never been calibrated "1:100" would
        // be a promise the program cannot keep.
        if (_job.Settings.Scales is { Count: > 0 })
        {
            foreach (double ratio in DrawingRatios)
            {
                option.AddItem($"d{ratio:0.##}", $"1:{ratio:0.##} del dibujo");
            }
        }

        option.AddItem("25", "25 %");
        option.AddItem("50", "50 %");
        option.AddItem("71", "71 % (A1 a A2)");
        option.AddItem("100", "100 %");
        option.AddItem("141", "141 % (A4 a A3)");
        option.AddItem("200", "200 %");

        option.TrySetValue(CurrentScaleValue());
        details.DisplayedOptions.Add(ScaleOptionId);
    }

    /// <summary>
    /// The scales a set of building drawings is issued at. Not every scale in
    /// the world: a list you have to read is a list nobody reads.
    /// </summary>
    private static readonly double[] DrawingRatios = [20, 50, 100, 200, 500];

    private string CurrentScaleValue() => _job.Settings.ScaleMode switch
    {
        PrintScaleMode.ActualSize => "actual",
        PrintScaleMode.Drawing => $"d{_job.Settings.DrawingRatio:0.##}",
        PrintScaleMode.Custom => ((int)Math.Round(_job.Settings.CustomScalePercent)).ToString(),
        _ => "fit",
    };

    private void AddPosterOption(PrintTaskOptionDetails details)
    {
        var option = details.CreateToggleOption(PosterOptionId, "Repartir en varias hojas");
        option.TrySetValue(_job.Settings.Poster);
        details.DisplayedOptions.Add(PosterOptionId);
    }

    private void AddInkOption(PrintTaskOptionDetails details)
    {
        var option = details.CreateToggleOption(InkOptionId, "Convertir a grises");
        option.TrySetValue(_job.Settings.Monochrome);
        details.DisplayedOptions.Add(InkOptionId);
    }

    private void AddMarginOption(PrintTaskOptionDetails details)
    {
        var option = details.CreateItemListOption(MarginOptionId, "Márgenes");
        option.AddItem("printer", "Los del propio papel");
        option.AddItem("none", "Sin margen");
        option.AddItem("10", "10 mm");
        option.AddItem("20", "20 mm");

        option.TrySetValue(_marginChoice);
        details.DisplayedOptions.Add(MarginOptionId);
    }

    private void AddQualityOption(PrintTaskOptionDetails details)
    {
        var option = details.CreateItemListOption(QualityOptionId, "Calidad");
        option.AddItem("150", "Borrador (150 ppp)");
        option.AddItem("300", "Normal (300 ppp)");
        option.AddItem("600", "Alta (600 ppp)");

        option.TrySetValue(((int)_job.Dpi).ToString());
        details.DisplayedOptions.Add(QualityOptionId);
    }

    /// <summary>
    /// A plan-specific option changed, so the layout and the preview are
    /// rebuilt. The paper is not re-read here: it arrives with the page
    /// description on the next options pass.
    /// </summary>
    private void OnOptionChanged(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
    {
        if (args.OptionId is not string id) return;
        if (!sender.Options.TryGetValue(id, out var raw)) return;

        var settings = _job.Settings;

        switch (id)
        {
            case ScaleOptionId when raw is PrintCustomItemListOptionDetails { Value: string value }:
                settings = value switch
                {
                    "fit" => settings with { ScaleMode = PrintScaleMode.FitToPaper },
                    "actual" => settings with { ScaleMode = PrintScaleMode.ActualSize },
                    ['d', .. var ratio] => settings with
                    {
                        ScaleMode = PrintScaleMode.Drawing,
                        DrawingRatio = double.TryParse(
                            ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out double asked) ? asked : 100,
                    },
                    _ => settings with
                    {
                        ScaleMode = PrintScaleMode.Custom,
                        CustomScalePercent = double.TryParse(value, out double percent) ? percent : 100,
                    },
                };
                break;

            case MarginOptionId when raw is PrintCustomItemListOptionDetails { Value: string value }:
                _marginChoice = value;
                settings = settings with { Margins = MarginsFor(value) };
                break;

            case QualityOptionId when raw is PrintCustomItemListOptionDetails { Value: string value }:
                _job.Dpi = float.TryParse(value, out float dpi) ? dpi : 300f;
                break;

            case PosterOptionId when raw is PrintCustomToggleOptionDetails toggle:
                settings = settings with { Poster = toggle.Value is true };
                break;

            case InkOptionId when raw is PrintCustomToggleOptionDetails toggle:
                settings = settings with { Monochrome = toggle.Value is true };
                break;

            default:
                return;
        }

        _job.Configure(settings, _job.Paper);
        _document.SetPageCount((uint)Math.Max(1, _job.Pieces.Count));
        _document.InvalidatePreview();
    }

    /// <summary>
    /// "Los del propio papel" is whatever the printer physically cannot reach,
    /// which the page description reports; the fixed sizes are millimetres.
    /// </summary>
    private PrintMarginsPt MarginsFor(string value) => value switch
    {
        "printer" => _hardwareMargins,
        "10" => PrintMarginsPt.Uniform(10f * 72f / 25.4f),
        "20" => PrintMarginsPt.Uniform(20f * 72f / 25.4f),
        _ => PrintMarginsPt.Uniform(0f),
    };

    /// <summary>
    /// Windows tells us the paper here — its real size and the part of it the
    /// printer can actually reach — and asks how many sheets the job comes to.
    /// On a set of A0s that number is the one worth knowing beforehand.
    /// </summary>
    private void OnPrintTaskOptionsChanged(CanvasPrintDocument sender, CanvasPrintTaskOptionsChangedEventArgs args)
    {
        var description = args.PrintTaskOptions.GetPageDescription(1);

        var paper = new PdfPageSize(
            (float)(description.PageSize.Width / PdfPrintJob.PointsToDips),
            (float)(description.PageSize.Height / PdfPrintJob.PointsToDips));

        _hardwareMargins = HardwareMargins(description);

        // The paper is already the right way round: Windows applied the
        // orientation before handing it over, so it must not be turned again.
        _job.Configure(
            _job.Settings with
            {
                Orientation = CorePrintOrientation.AsGiven,
                Margins = MarginsFor(_marginChoice),
            },
            paper);

        // A layout with no sheets means nothing at all would come out, and a
        // blank preview says nothing about why. The usual culprit is a margin
        // that swallowed the paper, so drop it and try once more before
        // admitting defeat.
        if (_job.Pieces.Count == 0)
        {
            _job.Configure(_job.Settings with { Margins = PrintMarginsPt.Uniform(0f) }, paper);
        }

        Problem = _job.Pieces.Count == 0
            ? "Con este papel y esta escala no cabe ninguna hoja."
            : null;

        uint count = (uint)Math.Max(1, _job.Pieces.Count);
        sender.SetPageCount(count);

        uint requested = args.NewPreviewPageNumber;
        args.NewPreviewPageNumber = Math.Clamp(requested == 0 ? 1 : requested, 1, count);
    }

    /// <summary>
    /// The printer's unprintable border, read from the page description — and
    /// distrusted. Drivers report this inconsistently, and a border that came
    /// back in the wrong units would eat the whole page and print nothing at
    /// all. Anything that does not leave most of the paper usable is treated as
    /// the driver not having said, which is the safe reading: a hair of ink in
    /// the unprintable margin is a far smaller problem than a blank sheet.
    /// </summary>
    private static PrintMarginsPt HardwareMargins(PrintPageDescription description)
    {
        var page = description.PageSize;
        var imageable = description.ImageableRect;

        if (page.Width <= 0 || page.Height <= 0) return PrintMarginsPt.Uniform(0f);
        if (imageable.Width <= 0 || imageable.Height <= 0) return PrintMarginsPt.Uniform(0f);

        double usable = imageable.Width / page.Width * (imageable.Height / page.Height);
        if (usable < 0.5 || imageable.Right > page.Width + 1 || imageable.Bottom > page.Height + 1)
        {
            return PrintMarginsPt.Uniform(0f);
        }

        double toPoints = 1.0 / PdfPrintJob.PointsToDips;
        return new PrintMarginsPt(
            (float)Math.Max(0, imageable.Left * toPoints),
            (float)Math.Max(0, imageable.Top * toPoints),
            (float)Math.Max(0, (page.Width - imageable.Right) * toPoints),
            (float)Math.Max(0, (page.Height - imageable.Bottom) * toPoints));
    }

    private void OnPreview(CanvasPrintDocument sender, CanvasPreviewEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            int index = (int)args.PageNumber - 1;
            if (index < 0 || index >= _job.Pieces.Count) return;

            _job.DrawPiece(sender, args.DrawingSession, _job.Pieces[index]);
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void OnPrint(CanvasPrintDocument sender, CanvasPrintEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            // Set before the first page: it fixes the resolution everything in
            // the job is rasterized at.
            args.Dpi = _job.Dpi;

            foreach (var piece in _job.Pieces)
            {
                using var ds = args.CreateDrawingSession();
                _job.DrawPiece(sender, ds, piece);
            }
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            System.Diagnostics.Debug.WriteLine($"ZenInk: fallo al imprimir: {ex}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _document.PrintTaskOptionsChanged -= OnPrintTaskOptionsChanged;
        _document.Preview -= OnPreview;
        _document.Print -= OnPrint;

        await _job.DisposeAsync();
    }
}
