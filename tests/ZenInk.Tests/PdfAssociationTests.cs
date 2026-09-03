//-----------------------------------------------------------------------------------------
// <copyright file="PdfAssociationTests.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using ZenInk.Core;
using static ZenInk.Tests.TestRunner;

namespace ZenInk.Tests;

/// <summary>Reading who opens PDFs, and deciding whether to bring it up.</summary>
public static class PdfAssociationTests
{
    public static void Run()
    {
        Recognising();
        Offering();
        Arguments();
    }

    private static void Arguments()
    {
        Section("PdfAssociation — what a launch was asked to open");

        const string exe = @"C:\Archivos\ZenInk.App.exe";

        Check("nothing asked for is nothing to open", PdfAssociation.DrawingsIn([exe]).Count == 0);

        var one = PdfAssociation.DrawingsIn([exe, @"C:\planos\A-01.pdf"]);
        Check("a drawing on the command line is opened", one.Count == 1 && one[0] == @"C:\planos\A-01.pdf");

        Check(
            "the program itself is not a drawing",
            PdfAssociation.DrawingsIn([@"C:\ZenInk.pdf", @"C:\planos\A-01.pdf"]).Count == 1);

        var several = PdfAssociation.DrawingsIn([exe, @"C:\planos\A-01.pdf", @"C:\planos\A-02.pdf"]);
        Check("several arrive in the order they were given", several.Count == 2 && several[1] == @"C:\planos\A-02.pdf");

        Check(
            "the same sheet twice is one tab",
            PdfAssociation.DrawingsIn([exe, @"C:\planos\A-01.pdf", @"c:\PLANOS\A-01.PDF"]).Count == 1);

        Check(
            "anything that is not a PDF is left alone",
            PdfAssociation.DrawingsIn([exe, @"C:\planos\A-01.dwg", "--depurar", "  "]).Count == 0);

        var quoted = PdfAssociation.DrawingsIn([exe, "\"C:\\mis planos\\A-01.pdf\""]);
        Check("quotes around a path with spaces come off", quoted.Count == 1 && quoted[0] == @"C:\mis planos\A-01.pdf");

        Check(
            "a drawing that is not there is still asked for, so it can say so",
            PdfAssociation.DrawingsIn([exe, @"Z:\no\existe.pdf"]).Count == 1);
    }

    private static void Recognising()
    {
        Section("PdfAssociation — recognising ourselves");

        const string aumid = "ZenInk_8wekyb3d8bbwe!App";
        const string exe = @"C:\Program Files\WindowsApps\ZenInk_1.0.0.0_x64__8wekyb3d8bbwe\ZenInk.App.exe";

        Check("the model id settles it", PdfAssociation.IsOurs(aumid, null, aumid, exe));
        Check("and it is not case sensitive", PdfAssociation.IsOurs(aumid.ToUpperInvariant(), null, aumid, exe));

        Check(
            "another program's model id is not ours",
            !PdfAssociation.IsOurs("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!App", null, aumid, exe));

        Check(
            "without a model id the binary answers",
            PdfAssociation.IsOurs(null, exe, aumid, exe));

        Check(
            "quoted, the way the registry keeps it",
            PdfAssociation.IsOurs(null, $"\"{exe}\"", aumid, exe));

        Check(
            "and reached through a different spelling of the same folder",
            PdfAssociation.IsOurs(null, @"C:\Program Files\WindowsApps\ZenInk_1.0.0.0_x64__8wekyb3d8bbwe\.\ZenInk.App.exe", aumid, exe));

        Check(
            "another program's binary is not ours",
            !PdfAssociation.IsOurs(null, @"C:\Program Files\Adobe\Acrobat\Acrobat.exe", aumid, exe));

        Check("nothing known is not a match", !PdfAssociation.IsOurs(null, null, aumid, exe));
        Check("and neither is emptiness on both sides", !PdfAssociation.IsOurs("", "  ", "", ""));

        Check(
            "something that is not a path does not throw",
            !PdfAssociation.IsOurs(null, "no|es<una>ruta", aumid, exe));
    }

    private static void Offering()
    {
        Section("PdfAssociation — whether to ask");

        Check("packaged, not the default, never asked", PdfAssociation.ShouldOffer(true, false, false));
        Check("already the default: nothing to ask", !PdfAssociation.ShouldOffer(true, true, false));
        Check("said no once: not asked again", !PdfAssociation.ShouldOffer(true, false, true));
        Check("unpackaged: not registered, so not offered", !PdfAssociation.ShouldOffer(false, false, false));
    }
}
