//-----------------------------------------------------------------------------------------
// <copyright file="MainWindow.xaml.cs" company="plano y escala">
// Copyright (c) 2026 plano y escala.
//
// ZenInk, part of ZenBIM, is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
// </copyright>
// <author>plano y escala</author>
//-----------------------------------------------------------------------------------------

using Microsoft.UI.Xaml;

namespace ZenInk_App;

/// <summary>
/// The application window. UI and logic live in MainPage, which also supplies
/// the title bar's drag region now that the content is extended into it.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;

        // Por ruta completa, no relativa. Empaquetado daba igual —una ruta
        // relativa se resuelve contra la carpeta del paquete—, pero en una
        // instalación suelta se resuelve contra el directorio de trabajo, que
        // al abrir un plano desde el Explorador es la carpeta del plano. Ahí no
        // hay ningún icono, y el error es silencioso: la ventana se queda con
        // el icono genérico de Windows en la barra de tareas.
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));

        RootFrame.Navigate(typeof(MainPage));
    }
}
