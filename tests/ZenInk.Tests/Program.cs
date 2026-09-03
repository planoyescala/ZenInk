//-----------------------------------------------------------------------------------------
// <copyright file="Program.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

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
    CompareTests.Run();
    MeasureTests.Run();
    await TextLayerTests.RunAsync();
    await RenderQueueTests.RunAsync();
    await RotationTests.RunAsync();
    await PageTests.RunAsync();
    await AnnotationTests.RunAsync();
    await SignatureTests.RunAsync();
    await SignatureTests.LockedAsync();
    await PrintLayoutTests.RunRenderAsync();
    await CompareTests.RunRenderAsync();
}
finally
{
    fpdfview.FPDF_DestroyLibrary();
}

return TestRunner.Summarise();
