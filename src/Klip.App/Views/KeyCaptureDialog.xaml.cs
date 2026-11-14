using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Klip.App.Controls;
using Klip.App.Localization;
using Wpf.Ui.Appearance;

using Klip.App.Services;

namespace Klip.App.Views;

/// <summary>
/// Caixa de atalho: captura a combinacao, valida (modificador, reservado, conflito com outra
/// acao do Klip), sugere teclas e explica o que acontece com o Windows ao salvar (assumir a
/// tecla nova, devolver a antiga). Quem grava e mexe no registro e quem abriu a caixa.
/// </summary>
public partial class KeyCaptureDialog
{
    /// <summary>Teclas que o Windows usa e o Klip consegue assumir pelo registro.</summary>
    public static bool IsSystemKey(string? gesture) =>
        gesture is "Win+V" or "Win+Shift+S" or "PrintScreen";

    // combinacoes que o Windows nunca entrega para um app (ou que quebrariam o uso normal)
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Win+L", "Win+D", "Win+E", "Win+R", "Win+I", "Win+Tab", "Win+X", "Win+A", "Win+N",
        "Alt+Tab", "Alt+F4", "Ctrl+Alt+Delete",
    };

    private readonly string _current;
    private readonly IReadOnlyDictionary<string, string> _usedByOthers;
    private string _pending;

    /// <summary>Resultado: o gesto escolhido, "" para remover, ou null se cancelou.</summary>
    public string? Result { get; private set; }

    /// <param name="actionName">Nome da acao, para o titulo.</param>
    /// <param name="current">Atalho atual ("" se nao definido).</param>
    /// <param name="optional">Se pode ficar sem atalho (mostra Remover).</param>
    /// <param name="suggestions">Teclas sugeridas para esta acao.</param>
    /// <param name="usedByOthers">Gesto -> nome da acao que ja o usa.</param>
    public KeyCaptureDialog(string actionName, string current, bool optional,
        IReadOnlyList<string> suggestions, IReadOnlyDictionary<string, string> usedByOthers)
    {
        InitializeComponent();
        ThemeManager.Watch(this);

        _current = current ?? "";
        _pending = "";
        _usedByOthers = usedByOthers;
        Title = actionName;
        TitleText.Text = string.Format(Loc.KeyDialogTitle, actionName);

        RemoveButton.Visibility = optional && _current.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (RemoveButton.Visibility != Visibility.Visible)
            ButtonsGrid.Columns = 2;

        foreach (var suggestion in suggestions)
        {
            var button = new Button
            {
                Margin = new Thickness(0, 0, 8, 4),
                Padding = new Thickness(4),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = new KeyChord { Chord = suggestion },
                Tag = suggestion,
                ToolTip = suggestion,
            };
            button.Click += (_, _) => SetPending((string)button.Tag);
            SuggestionsPanel.Children.Add(button);
        }
        if (suggestions.Count == 0)
            SuggestionsPanel.Visibility = Visibility.Collapsed;

        SaveButton.Click += (_, _) => { Result = _pending; DialogResult = true; };
        RemoveButton.Click += (_, _) => { Result = ""; DialogResult = true; };
        CancelButton.Click += (_, _) => DialogResult = false;

        PreviewKeyDown += OnPreviewKeyDown;
        // o Windows costuma mandar so o KEYUP do Print Screen (o KEYDOWN vai para o sistema)
        PreviewKeyUp += (_, e) =>
        {
            if (CaptureBox.IsKeyboardFocused && e.Key == Key.Snapshot && Keyboard.Modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                SetPending("PrintScreen");
            }
        };
        Loaded += (_, _) => CaptureBox.Focus();
        CaptureBox.MouseLeftButtonDown += (_, _) => CaptureBox.Focus();
        Render();
    }

    private void SetPending(string gesture)
    {
        _pending = gesture;
        Render();
        CaptureBox.Focus();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Tab, Enter e Esc continuam sendo navegacao da caixa quando o foco esta nos botoes
        if (!CaptureBox.IsKeyboardFocused)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
            return; // IsCancel fecha
        if (key == Key.Enter && SaveButton.IsEnabled)
        {
            e.Handled = true;
            Result = _pending;
            DialogResult = true;
            return;
        }

        e.Handled = true;
        var parts = new List<string>(4);
        // ordem do Windows: Win, Ctrl, Alt, Shift (bate com "Win+Shift+S", "Ctrl+Alt+V")
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");

        if (IsModifierKey(key))
        {
            // so modificadores: mostra ao vivo e espera a tecla
            _pending = string.Join("+", parts);
            Render(waitingForKey: true);
            return;
        }

        var name = KeyName(key);
        if (name.Length == 0)
            return;
        parts.Add(name);
        _pending = string.Join("+", parts);
        Render();
    }

    private void Render(bool waitingForKey = false)
    {
        PreviewChord.Chord = _pending;
        WaitingText.Visibility = _pending.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var (ok, message) = waitingForKey ? (false, "") : Validate(_pending);
        SaveButton.IsEnabled = ok;
        SaveButton.Content = ok && IsSystemKey(_pending) && !string.Equals(_pending, _current, StringComparison.OrdinalIgnoreCase)
            ? Loc.KeyDialogTakeSave
            : Loc.Save;

        MessageText.Text = message;
        MessageGlyph.Text = message.Length == 0 ? "" : ok ? "" : "";
        var brush = ok ? "AccentTextFillColorPrimaryBrush" : "SystemFillColorCriticalBrush";
        MessageGlyph.SetResourceReference(TextBlock.ForegroundProperty, brush);
        MessageText.SetResourceReference(TextBlock.ForegroundProperty, ok ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");

        var note = ok ? SystemNoteFor(_pending) : "";
        SystemNoteText.Text = note;
        SystemNote.Visibility = note.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private (bool Ok, string Message) Validate(string gesture)
    {
        if (gesture.Length == 0)
            return (false, "");
        if (string.Equals(gesture, _current, StringComparison.OrdinalIgnoreCase))
            return (true, Loc.KeyDialogCurrent);
        var parts = gesture.Split('+');
        var hasModifier = parts.Any(p => p is "Win" or "Ctrl" or "Alt");
        if (!hasModifier && gesture != "PrintScreen")
            return (false, Loc.KeyDialogNeedModifier);
        if (Reserved.Contains(gesture))
            return (false, Loc.KeyDialogReserved);
        if (_usedByOthers.TryGetValue(gesture, out var other))
            return (false, string.Format(Loc.KeyDialogTaken, other));
        return (true, Loc.KeyDialogAvailable);
    }

    private string SystemNoteFor(string gesture)
    {
        if (string.Equals(gesture, _current, StringComparison.OrdinalIgnoreCase))
            return "";
        var lines = new List<string>(3);
        if (IsSystemKey(gesture))
        {
            lines.Add(string.Format(Loc.KeyDialogSysWas, ToolOf(gesture)) + " " + Loc.KeyDialogSysTake);
            if (gesture == "Win+Shift+S")
                lines.Add(Loc.KeyDialogSysWinS);
            if (gesture == "Win+V")
                lines.Add(Loc.KeyDialogSysUac);
        }
        if (IsSystemKey(_current))
            lines.Add(string.Format(Loc.KeyDialogSysReturn, _current == "PrintScreen" ? "Print Screen" : _current, ToolOf(_current)));
        return string.Join("\n", lines);
    }

    private static string ToolOf(string gesture) =>
        gesture == "Win+V" ? Loc.KeyDialogToolHistory : Loc.KeyDialogToolSnipping;

    private static bool IsModifierKey(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>Nome da tecla no formato que o HotkeyGesture.TryParse entende.</summary>
    private static string KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => key.ToString()[1..],
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Snapshot => "PrintScreen",
        _ => string.Empty,
    };
}
