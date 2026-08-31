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
    PageTests.Run();
    RecentDocumentsTests.Run();
    PdfAssociationTests.Run();
    PrintLayoutTests.Run();
    RenderTests.Run();
    AnnotationTests.Run();
    SignatureTests.Run();
    await TextLayerTests.RunAsync();
    await RenderQueueTests.RunAsync();
    await RotationTests.RunAsync();
    await PageTests.RunAsync();
    await AnnotationTests.RunAsync();
    await SignatureTests.RunAsync();
    await SignatureTests.LockedAsync();
    await PrintLayoutTests.RunRenderAsync();
}
finally
{
    fpdfview.FPDF_DestroyLibrary();
}

return TestRunner.Summarise();
