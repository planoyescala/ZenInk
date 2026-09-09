//-----------------------------------------------------------------------------------------
// <copyright file="PageThumbnail.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;

using ZenInk.Core;

namespace ZenInk_App.Rendering;

/// <summary>
/// One sheet in the pages panel. The frame keeps the sheet's proportions from
/// the moment the document opens, so the panel has its final shape before any
/// rendering happens and nothing jumps as previews arrive.
/// </summary>
public sealed class PageThumbnail : INotifyPropertyChanged
{
    private BitmapSource? _image;
    private bool _isCurrent;

    public PageThumbnail(int pageIndex, SheetInfo sheet, double frameWidth)
    {
        PageIndex = pageIndex;
        Document = sheet.DocumentId;
        Preview = sheet.PageIndex;
        IsBlank = sheet.IsBlank;

        double ratio = sheet.Size.WidthPt <= 0 ? 1.0 : sheet.Size.HeightPt / sheet.Size.WidthPt;
        FrameHeight = Math.Clamp(frameWidth * ratio, 40, 240);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where this sheet sits in the document right now.</summary>
    public int PageIndex { get; }

    /// <summary>The open document its picture comes from, and the page of it.</summary>
    public int Document { get; }

    public int Preview { get; }

    public bool IsBlank { get; }

    public string Number => (PageIndex + 1).ToString();

    public double FrameHeight { get; }

    /// <summary>Blank paper says so, because an empty white frame reads as one still loading.</summary>
    public Visibility BlankNote => IsBlank ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// The word written across a sheet that has nothing on it.
    ///
    /// It comes from here rather than from the template, because x:Uid inside a
    /// DataTemplate is not resolved: the words would stay in whatever language
    /// they were typed in.
    /// </summary>
    public static string BlankLabel => Loc.Get("BlankSheetTag");

    public BitmapSource? Image
    {
        get => _image;
        set => Set(ref _image, value);
    }

    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (!Set(ref _isCurrent, value)) return;
            Notify(nameof(BorderBrush));
            Notify(nameof(BorderThickness));
            Notify(nameof(LabelBrush));
            Notify(nameof(LabelWeight));
        }
    }

    public Brush BorderBrush => _isCurrent
        ? Accent
        : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    public Thickness BorderThickness => new(_isCurrent ? 2 : 1);

    public Brush LabelBrush => _isCurrent
        ? Accent
        : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];

    public FontWeight LabelWeight => _isCurrent ? FontWeights.SemiBold : FontWeights.Normal;

    private static Brush Accent => (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }

    private void Notify(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// One entry of the document's own index, in the shape a <c>TreeView</c> binds
/// to. The engine's <see cref="OutlineEntry"/> is a plain record; this is the
/// same thing with children the control can walk.
/// </summary>
public sealed class OutlineNode
{
    private OutlineNode(OutlineEntry entry)
    {
        Title = string.IsNullOrWhiteSpace(entry.Title) ? Loc.Get("OutlineUntitled") : entry.Title;
        PageIndex = entry.PageIndex;
        Children = Tree(entry.Children);
    }

    public string Title { get; }

    /// <summary>The sheet it goes to, or −1 for an entry that is only a heading.</summary>
    public int PageIndex { get; }

    public IReadOnlyList<OutlineNode> Children { get; }

    public static IReadOnlyList<OutlineNode> Tree(IReadOnlyList<OutlineEntry> entries) =>
        [.. entries.Select(entry => new OutlineNode(entry))];
}
