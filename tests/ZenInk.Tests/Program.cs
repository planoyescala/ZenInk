using PDFiumCore;
using ZenInk.Tests;

// The rendering checks below drive PDFium directly rather than through the
// queue, so the library has to be up for them. The queue owns its own
// initialisation on its own thread, which is independent of this.
fpdfview.FPDF_InitLibrary();

Console.WriteLine("ZenInk engine checks");

try
{
    LayoutTests.Run();
    PrintLayoutTests.Run();
    RenderTests.Run();
    await TextLayerTests.RunAsync();
    await RenderQueueTests.RunAsync();
    await RotationTests.RunAsync();
    await PrintLayoutTests.RunRenderAsync();
}
finally
{
    fpdfview.FPDF_DestroyLibrary();
}

return TestRunner.Summarise();
