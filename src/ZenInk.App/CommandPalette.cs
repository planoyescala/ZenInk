using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using ZenInk_App.Rendering;

namespace ZenInk_App;

/// <summary>One thing the app can be asked to do, by name.</summary>
/// <param name="Keys">The shortcut, when there is one. Shown, never parsed.</param>
/// <param name="Also">
/// Other words someone might reach for. Nobody looks up «nube de revisión» by
/// typing "nube" only — they type "revision", or "marcar", or "globo".
/// </param>
public sealed record AppCommand(string Name, string Keys, string Also, Action Run);

public sealed partial class MainPage
{
    /// <summary>
    /// Every command, built fresh each time the palette opens.
    ///
    /// Built rather than kept, because what a command does depends on which tab
    /// is in front: a list held from one document to the next would run against
    /// the wrong one. Thirty-odd records is nothing to make.
    /// </summary>
    private List<AppCommand> BuildCommands()
    {
        var empty = new RoutedEventArgs();

        var commands = new List<AppCommand>
        {
            new("Abrir un documento", "", "archivo pdf cargar", () => OnOpenClicked(OpenButton, null!)),
            new("Guardar", "Ctrl+S", "escribir grabar", () => _ = SaveDocumentAsync()),
            new("Guardar como…", "", "copia duplicar exportar", () => OnSaveCopyClicked(this, empty)),
            new("Aplanar las marcas", "", "fijar quemar definitivo", () => OnFlattenClicked(this, empty)),
            new("Firmar el documento", "", "firma certificado fnmt digital", () => OnSignClicked(this, empty)),
            new("Descartar los cambios sin guardar", "", "deshacer todo revertir", () => OnDiscardChangesClicked(this, empty)),
            new("Imprimir", "Ctrl+P", "papel plotter", () => OnPrintClicked(this, empty)),
            new("Buscar texto", "Ctrl+F", "encontrar localizar", () => OnFindClicked(this, empty)),

            new("Deshacer", "Ctrl+Z", "atras", () => OnUndoClicked(this, empty)),
            new("Rehacer", "Ctrl+Y", "adelante", () => OnRedoClicked(this, empty)),

            new("Acercar", "", "zoom aumentar más", () => OnZoomInClicked(this, empty)),
            new("Alejar", "", "zoom reducir menos", () => OnZoomOutClicked(this, empty)),
            new("Ajustar al ancho", "Ctrl+1", "encajar", () => OnFitWidthClicked(this, empty)),
            new("Ajustar a la página", "Ctrl+2", "encajar entera", () => OnFitPageClicked(this, empty)),
            new("Tamaño real", "Ctrl+0", "cien por cien 100", () => OnActualSizeClicked(this, empty)),

            new("Vista continua", "", "tira seguido scroll", () => OnContinuousModeClicked(this, empty)),
            new("Página a página", "", "suelta individual", () => OnSingleModeClicked(this, empty)),
            new("Una página de ancho", "", "columna", () => OnOneColumnClicked(this, empty)),
            new("Dos páginas de ancho", "", "columnas doble libro", () => OnTwoColumnsClicked(this, empty)),
            new("Panel de hojas", "", "miniaturas lateral páginas índice marcadores", () => OnThumbnailsClicked(this, empty)),

            new("Girar la página a la izquierda", "", "rotar", () => OnRotateLeftClicked(this, empty)),
            new("Girar la página a la derecha", "", "rotar", () => OnRotateRightClicked(this, empty)),
            new("Girar todas a la izquierda", "", "rotar todo", () => OnRotateAllLeftClicked(this, empty)),
            new("Girar todas a la derecha", "", "rotar todo", () => OnRotateAllRightClicked(this, empty)),

            new("Subir la hoja", "Alt+↑", "mover reordenar antes arriba", () => Pages.Run(PageAction.MoveUp)),
            new("Bajar la hoja", "Alt+↓", "mover reordenar después abajo", () => Pages.Run(PageAction.MoveDown)),
            new("Mover la hoja a…", "", "llevar posición número reordenar colocar", () => Pages.Run(PageAction.MoveTo)),
            new("Duplicar la hoja", "", "copiar página repetir", () => Pages.Run(PageAction.Duplicate)),
            new("Quitar la hoja del documento", "", "borrar eliminar página suprimir", () => Pages.Run(PageAction.Delete)),
            new("Insertar hojas de otro PDF…", "", "añadir traer combinar unir juntar merge", () => Pages.Run(PageAction.InsertFromFile)),
            new("Insertar una hoja en blanco…", "", "añadir papel vacía nueva", () => Pages.Run(PageAction.InsertBlank)),
            new("Extraer hojas a un PDF nuevo…", "", "sacar separar exportar páginas", () => Pages.Run(PageAction.Extract)),
            new("Dividir el documento…", "", "partir separar trocear split", () => Pages.Run(PageAction.Split)),

            new("Comparar con otra revisión…", "", "superponer diferencias cambios version revisar overlay", () => _ = PickRevisionAsync()),
            new("Cambio siguiente", "F4", "diferencia comparar avanzar", () => StepChange(1)),
            new("Cambio anterior", "Mayús+F4", "diferencia comparar atras", () => StepChange(-1)),
            new("Intercambiar los colores de la comparación", "", "revisión rojo azul cambiar", () => OnCompareSwapClicked(this, empty)),
            new("Dejar de comparar", "", "salir cerrar revisión superposición", () => OnCompareStopClicked(this, empty)),

            new("Seleccionar toda la página", "", "todo copiar", () => OnSelectAllClicked(this, empty)),
            new("Copiar la selección", "Ctrl+C", "portapapeles", () => OnCopySelectionClicked(this, empty)),

            new("Tema del sistema", "", "apariencia claro oscuro", () => OnThemeSystemClicked(this, empty)),
            new("Tema claro", "", "apariencia blanco", () => OnThemeLightClicked(this, empty)),
            new("Tema oscuro", "", "apariencia negro", () => OnThemeDarkClicked(this, empty)),
        };

        // The marking tools, by name. Since they moved into the ribbon they
        // carry labels of their own, but typing still beats hunting for the
        // tab a rarely-used one lives on.
        commands.AddRange(new (string Name, string Also, ViewerTool Tool)[]
        {
            ("Herramienta: mano", "mover desplazar arrastrar", ViewerTool.Pan),
            ("Herramienta: zoom por ventana", "rectángulo acercar", ViewerTool.ZoomRectangle),
            ("Herramienta: seleccionar texto", "copiar", ViewerTool.SelectText),
            ("Herramienta: seleccionar marca", "mover editar flecha", ViewerTool.SelectAnnotation),
            ("Herramienta: línea", "recta", ViewerTool.Line),
            ("Herramienta: flecha", "señalar apuntar", ViewerTool.Arrow),
            ("Herramienta: polilínea", "quebrada varios tramos", ViewerTool.Polyline),
            ("Herramienta: rectángulo", "caja cuadro recuadro", ViewerTool.Rectangle),
            ("Herramienta: elipse", "círculo óvalo", ViewerTool.Ellipse),
            ("Herramienta: polígono", "cerrado varios lados", ViewerTool.Polygon),
            ("Herramienta: nube de revisión", "revisión globo marcar cambio", ViewerTool.Cloud),
            ("Herramienta: resaltado", "subrayar marcador amarillo", ViewerTool.Highlight),
            ("Herramienta: texto", "escribir rótulo nota", ViewerTool.FreeText),
            ("Herramienta: comentario", "nota post-it globo", ViewerTool.Note),
            ("Herramienta: lápiz", "mano alzada dibujar", ViewerTool.Ink),
            ("Capturar una zona al portapapeles", "recorte captura copiar imagen pantalla trozo", ViewerTool.CaptureRegion),
            ("Calibrar la hoja", "escala medir 1:100 distancia conocida", ViewerTool.Calibrate),
            ("Medir: distancia", "longitud largo metros cota", ViewerTool.Distance),
            ("Medir: perímetro", "contorno vuelta alrededor", ViewerTool.Perimeter),
            ("Medir: área", "superficie metros cuadrados m2 sala", ViewerTool.Area),
            ("Medir: ángulo", "grados esquina inclinación", ViewerTool.Angle),
        }.Select(entry => new AppCommand(entry.Name, "", entry.Also, () => SetTool(entry.Tool))));

        return commands;
    }

    private List<AppCommand> _commands = [];

    private void OnCommandPaletteAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ToggleCommandPalette();
    }

    private void OnCommandButtonClicked(object sender, RoutedEventArgs e) => ToggleCommandPalette();

    private void ToggleCommandPalette()
    {
        if (CommandPalette.Visibility == Visibility.Visible)
        {
            HideCommandPalette();
            return;
        }

        _commands = BuildCommands();
        CommandQuery.Text = string.Empty;
        ShowMatches(string.Empty);

        CommandPalette.Visibility = Visibility.Visible;
        CommandQuery.Focus(FocusState.Programmatic);
    }

    private void HideCommandPalette()
    {
        CommandPalette.Visibility = Visibility.Collapsed;
        ActiveViewer?.Focus(FocusState.Programmatic);
    }

    private void OnCommandQueryChanged(object sender, TextChangedEventArgs e) => ShowMatches(CommandQuery.Text);

    /// <summary>
    /// Filters by every typed word, against both the name and the extra words.
    /// Not fuzzy on purpose: a list that reorders itself on every keystroke is
    /// harder to aim at than one that only ever gets shorter.
    /// </summary>
    private void ShowMatches(string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        var matches = _commands
            .Where(command => words.All(word =>
                Folded(command.Name).Contains(Folded(word), StringComparison.Ordinal)
                || Folded(command.Also).Contains(Folded(word), StringComparison.Ordinal)))
            .ToList();

        CommandList.ItemsSource = matches
            .Select(command => command.Keys.Length > 0 ? $"{command.Name}      {command.Keys}" : command.Name)
            .ToList();

        _matches = matches;
        if (matches.Count > 0) CommandList.SelectedIndex = 0;
    }

    private List<AppCommand> _matches = [];

    /// <summary>
    /// Accents and case folded away, so «polilinea» finds «polilínea». The same
    /// reason the finder folds them: nobody reaches for the accent key while
    /// hunting for a button.
    /// </summary>
    private static string Folded(string text)
    {
        var folded = new System.Text.StringBuilder(text.Length);
        foreach (char c in text.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                folded.Append(char.ToLowerInvariant(c));
            }
        }
        return folded.ToString();
    }

    private void OnCommandQueryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                e.Handled = true;
                HideCommandPalette();
                break;

            case VirtualKey.Down:
                e.Handled = true;
                Step(1);
                break;

            case VirtualKey.Up:
                e.Handled = true;
                Step(-1);
                break;

            case VirtualKey.Enter:
                e.Handled = true;
                RunSelected();
                break;
        }
    }

    private void Step(int by)
    {
        if (_matches.Count == 0) return;

        int next = CommandList.SelectedIndex + by;
        CommandList.SelectedIndex = Math.Clamp(next, 0, _matches.Count - 1);
        CommandList.ScrollIntoView(CommandList.SelectedItem);
    }

    private void OnCommandChosen(object sender, ItemClickEventArgs e)
    {
        CommandList.SelectedIndex = CommandList.Items.IndexOf(e.ClickedItem);
        RunSelected();
    }

    private void RunSelected()
    {
        int at = CommandList.SelectedIndex;
        if (at < 0 || at >= _matches.Count) return;

        var chosen = _matches[at];
        HideCommandPalette();
        chosen.Run();
    }
}
