using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Klip.App.Services;
using Klip.Core.Clipboard;
using Klip.Core.Common;
using Klip.Core.Storage;
using Wpf.Ui.Appearance;

namespace Klip.App.Windows;

/// <summary>Editor tools.</summary>
public enum EditorTool
{
    Select,
    Pen,
    Highlighter,
    Eraser,
    Rect,
    Ellipse,
    Line,
    Arrow,
    Text,
    Crop,
    Blur,
    Emoji,
}

/// <summary>
/// Quick post-capture editor: non-destructive annotations over the base
/// image (InkCanvas holds strokes plus elements), auto-copy on every edit
/// and it hooks into the history.
/// </summary>
public partial class EditorWindow
{
    private readonly ClipboardWriteGuard _writeGuard;
    private readonly ClipboardIngestService _ingest;
    private readonly ClipboardItemRepository _repository;
    private readonly MediaStore _mediaStore;
    private readonly OcrService _ocr;
    private readonly DispatcherTimer _autoCopyDebounce;

    private EditorTool _tool = EditorTool.Pen;
    private BitmapSource? _base;
    private long? _historyItemId;       // item the editor created or updated
    private bool _hasChanges;

    // Shape drawing
    private Point _shapeStart;
    private Shape? _activeShape;
    private bool _drawingShape;

    // Crop
    private Point _cropStart;
    private Rect _cropSelection = Rect.Empty;
    private bool _croppingDrag;
    private bool _blurDrag;

    // emoji stamp: the emoji picked in the popup, dropped on the next click
    private string _pendingEmojiCode = "1f600";
    private bool _emojiPickerBuilt;

    // Pan with spacebar
    private bool _spaceHeld;
    private bool _panning;
    private Point _panStartScreen;
    private double _panStartH;
    private double _panStartV;

    // Undo/redo
    private readonly Stack<(Action undo, Action redo)> _undoStack = new();
    private readonly Stack<(Action undo, Action redo)> _redoStack = new();
    private bool _suppressStrokeUndo;

    private readonly List<(ToggleButton button, EditorTool tool)> _toolButtons = [];

    public EditorWindow(
        ClipboardWriteGuard writeGuard,
        ClipboardIngestService ingest,
        ClipboardItemRepository repository,
        MediaStore mediaStore,
        OcrService ocr)
    {
        _writeGuard = writeGuard;
        _ingest = ingest;
        _repository = repository;
        _mediaStore = mediaStore;
        _ocr = ocr;

        InitializeComponent();
        SystemThemeWatcher.Watch(this);

        // auto-copy debounced by 400 ms
        _autoCopyDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _autoCopyDebounce.Tick += (_, _) =>
        {
            _autoCopyDebounce.Stop();
            SyncToClipboardAndHistory();
        };

        WireToolbar();
        WireCanvas();
        SetTool(EditorTool.Pen);

        // fit needs a real layout, on the first Show the viewport is still 0
        Loaded += (_, _) =>
        {
            UpdateLayout();
            FitToWindow();
        };
    }

    // ----- Open -----

    /// <summary>Opens an image from history. First edit spawns a NEW item.</summary>
    public void OpenFromHistory(string absolutePngPath)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(absolutePngPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        OpenImage(bitmap);
    }

    /// <summary>Opens a fresh capture (from the toast or the open-straight-away setting).</summary>
    public void OpenImage(BitmapSource image)
    {
        _base = image;
        _historyItemId = null; // original stays untouched, editing makes a new item
        _hasChanges = false;
        _undoStack.Clear();
        _redoStack.Clear();

        _suppressStrokeUndo = true;
        Ink.Strokes.Clear();
        Ink.Children.Clear();
        _suppressStrokeUndo = false;

        BaseImage.Source = image;
        BaseImage.Width = image.PixelWidth;
        BaseImage.Height = image.PixelHeight;
        CanvasHost.Width = image.PixelWidth;
        CanvasHost.Height = image.PixelHeight;

        StatusText.Text = $"{image.PixelWidth} x {image.PixelHeight} px";
        if (IsLoaded)
        {
            UpdateLayout();
            FitToWindow();
        }
        RefreshUndoButtons();
    }

    // ----- Toolbar -----

    private void WireToolbar()
    {
        Wire(ToolSelect, EditorTool.Select);
        Wire(ToolPen, EditorTool.Pen);
        Wire(ToolHighlighter, EditorTool.Highlighter);
        Wire(ToolEraser, EditorTool.Eraser);
        Wire(ToolRect, EditorTool.Rect);
        Wire(ToolEllipse, EditorTool.Ellipse);
        Wire(ToolLine, EditorTool.Line);
        Wire(ToolArrow, EditorTool.Arrow);
        Wire(ToolText, EditorTool.Text);
        Wire(ToolCrop, EditorTool.Crop);
        Wire(ToolBlur, EditorTool.Blur);

        // emoji tool opens the picker instead of just switching mode
        ToolEmoji.Click += (_, _) =>
        {
            SetTool(EditorTool.Emoji);
            BuildEmojiPickerOnce();
            EmojiPopup.IsOpen = true;
        };

        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        SaveButton.Click += (_, _) => SaveAs();
        RemoveBgButton.Click += (_, _) => RemoveBackground();
        RotateLeftButton.Click += (_, _) => Rotate(clockwise: false);
        RotateRightButton.Click += (_, _) => Rotate(clockwise: true);
        TextActionsButton.Click += async (_, _) => await ExtractTextAsync();
        QuickRedactButton.Click += async (_, _) => await QuickRedactAsync();

        ThicknessSlider.ValueChanged += (_, _) => ApplyDrawingAttributes();
        foreach (var swatch in PropertiesBar.Children.OfType<RadioButton>())
            swatch.Checked += (_, _) => ApplyDrawingAttributes();

        ZoomSlider.ValueChanged += (_, _) => ApplyZoom();

        void Wire(ToggleButton button, EditorTool tool)
        {
            _toolButtons.Add((button, tool));
            button.Click += (_, _) => SetTool(tool);
        }
    }

    private void SetTool(EditorTool tool)
    {
        _tool = tool;
        CancelCrop();

        foreach (var (button, buttonTool) in _toolButtons)
            button.IsChecked = buttonTool == tool;

        Ink.EditingMode = tool switch
        {
            EditorTool.Pen or EditorTool.Highlighter => InkCanvasEditingMode.Ink,
            EditorTool.Eraser => InkCanvasEditingMode.EraseByStroke,
            EditorTool.Select => InkCanvasEditingMode.Select,
            _ => InkCanvasEditingMode.None,
        };
        Ink.UseCustomCursor = Ink.EditingMode == InkCanvasEditingMode.None;
        Ink.Cursor = tool switch
        {
            EditorTool.Text => Cursors.IBeam,
            EditorTool.Crop or EditorTool.Rect or EditorTool.Ellipse
                or EditorTool.Line or EditorTool.Arrow => Cursors.Cross,
            EditorTool.Blur => Cursors.Cross,
            EditorTool.Emoji => Cursors.Hand,
            _ => Cursors.Arrow,
        };
        ApplyDrawingAttributes();
    }

    private Color CurrentColor
    {
        get
        {
            var checkedSwatch = PropertiesBar.Children.OfType<RadioButton>()
                .FirstOrDefault(r => r.IsChecked == true);
            return checkedSwatch?.Background is SolidColorBrush brush ? brush.Color : Colors.Red;
        }
    }

    private double CurrentThickness => ThicknessSlider.Value;

    private void ApplyDrawingAttributes()
    {
        var attributes = new DrawingAttributes
        {
            Color = CurrentColor,
            Width = CurrentThickness,
            Height = CurrentThickness,
            FitToCurve = true, // smoother stroke
        };
        if (_tool == EditorTool.Highlighter)
        {
            attributes.IsHighlighter = true; // multiply, semi transparent
            attributes.Width = Math.Max(CurrentThickness * 4, 8);
            attributes.Height = Math.Max(CurrentThickness * 4, 8);
            attributes.StylusTip = StylusTip.Rectangle;
        }
        Ink.DefaultDrawingAttributes = attributes;
    }

    // ----- Canvas: strokes, shapes, text, crop -----

    private void WireCanvas()
    {
        // undo for strokes (added or erased) piggybacks on the InkCanvas event
        Ink.Strokes.StrokesChanged += (_, e) =>
        {
            if (_suppressStrokeUndo)
                return;
            var added = e.Added.ToList();
            var removed = e.Removed.ToList();
            PushUndo(
                undo: () =>
                {
                    _suppressStrokeUndo = true;
                    foreach (var s in added) Ink.Strokes.Remove(s);
                    foreach (var s in removed) Ink.Strokes.Add(s);
                    _suppressStrokeUndo = false;
                },
                redo: () =>
                {
                    _suppressStrokeUndo = true;
                    foreach (var s in removed) Ink.Strokes.Remove(s);
                    foreach (var s in added) Ink.Strokes.Add(s);
                    _suppressStrokeUndo = false;
                });
            MarkChanged();
        };

        // moving or resizing a selection counts as an edit too
        Ink.SelectionMoved += (_, _) => MarkChanged();
        Ink.SelectionResized += (_, _) => MarkChanged();

        // pan while space is held, like Figma or Photoshop
        CanvasScroll.PreviewMouseLeftButtonDown += OnPanMouseDown;
        CanvasScroll.PreviewMouseMove += OnPanMouseMove;
        CanvasScroll.PreviewMouseLeftButtonUp += OnPanMouseUp;

        Ink.PreviewMouseLeftButtonDown += OnCanvasMouseDown;
        Ink.PreviewMouseMove += OnCanvasMouseMove;
        Ink.PreviewMouseLeftButtonUp += OnCanvasMouseUp;
    }

    // ----- Pan with spacebar -----

    private void OnPanMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_spaceHeld)
            return;
        _panning = true;
        _panStartScreen = PointToScreen(e.GetPosition(this));
        _panStartH = CanvasScroll.HorizontalOffset;
        _panStartV = CanvasScroll.VerticalOffset;
        CanvasScroll.CaptureMouse();
        e.Handled = true; // keep the InkCanvas from drawing while we pan
    }

    private void OnPanMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panning)
            return;
        var current = PointToScreen(e.GetPosition(this));
        CanvasScroll.ScrollToHorizontalOffset(_panStartH - (current.X - _panStartScreen.X));
        CanvasScroll.ScrollToVerticalOffset(_panStartV - (current.Y - _panStartScreen.Y));
        e.Handled = true;
    }

    private void OnPanMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning)
            return;
        _panning = false;
        CanvasScroll.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_spaceHeld)
            return; // pan mode is on
        var pos = e.GetPosition(Ink);

        switch (_tool)
        {
            case EditorTool.Rect or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow:
                _drawingShape = true;
                _shapeStart = pos;
                _activeShape = CreateShape();
                InkCanvas.SetLeft(_activeShape, 0);
                InkCanvas.SetTop(_activeShape, 0);
                Ink.Children.Add(_activeShape);
                UpdateActiveShape(pos);
                Ink.CaptureMouse();
                e.Handled = true;
                break;

            case EditorTool.Text:
                AddTextBox(pos);
                e.Handled = true;
                break;

            case EditorTool.Emoji:
                AddEmoji(pos);
                e.Handled = true;
                break;

            case EditorTool.Crop:
                _croppingDrag = true;
                _cropStart = pos;
                _cropSelection = new Rect(pos, pos);
                CropLayer.Visibility = Visibility.Visible;
                UpdateCropVisual();
                Ink.CaptureMouse();
                e.Handled = true;
                break;

            case EditorTool.Blur:
                // reusa o retangulo de selecao do crop so como preview visual
                _blurDrag = true;
                _cropStart = pos;
                _cropSelection = new Rect(pos, pos);
                CropLayer.Visibility = Visibility.Visible;
                UpdateCropVisual();
                Ink.CaptureMouse();
                e.Handled = true;
                break;
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(Ink);
        if (_drawingShape && _activeShape is not null)
        {
            UpdateActiveShape(pos);
        }
        else if (_croppingDrag)
        {
            _cropSelection = new Rect(_cropStart, pos);
            UpdateCropVisual();
        }
        else if (_blurDrag)
        {
            _cropSelection = new Rect(_cropStart, pos);
            UpdateCropVisual();
        }
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawingShape && _activeShape is not null)
        {
            _drawingShape = false;
            Ink.ReleaseMouseCapture();
            var shape = _activeShape;
            _activeShape = null;

            if (shape.Width < 3 && shape.Height < 3 && shape is not Line)
            {
                // clique sem arrastar, forma minúscula, joga fora
                Ink.Children.Remove(shape);
                return;
            }

            // shape becomes a live object, selectable in Select mode
            PushUndo(
                undo: () => Ink.Children.Remove(shape),
                redo: () => Ink.Children.Add(shape));
            MarkChanged();
            e.Handled = true;
        }
        else if (_croppingDrag)
        {
            _croppingDrag = false;
            Ink.ReleaseMouseCapture();
        }
        else if (_blurDrag)
        {
            _blurDrag = false;
            Ink.ReleaseMouseCapture();
            var selection = _cropSelection;
            CancelCrop();
            ApplyBlur(selection);
        }
    }

    private Shape CreateShape()
    {
        var stroke = new SolidColorBrush(CurrentColor);
        return _tool switch        {
            EditorTool.Rect => new Rectangle
            {
                Stroke = stroke,
                StrokeThickness = CurrentThickness,
                Fill = Brushes.Transparent,
                RadiusX = 2,
                RadiusY = 2,
            },
            EditorTool.Ellipse => new Ellipse
            {
                Stroke = stroke,
                StrokeThickness = CurrentThickness,
                Fill = Brushes.Transparent,
            },
            EditorTool.Line => new Line
            {
                Stroke = stroke,
                StrokeThickness = CurrentThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            },
            _ => new System.Windows.Shapes.Path // arrow
            {
                Stroke = stroke,
                StrokeThickness = CurrentThickness,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
            },
        };
    }

    private void UpdateActiveShape(Point current)
    {
        if (_activeShape is null)
            return;

        // Shift locks the ratio and snaps to 45 degree angles
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        switch (_activeShape)
        {
            case Line line:
                var end = shift ? Snap45(_shapeStart, current) : current;
                line.X1 = _shapeStart.X;
                line.Y1 = _shapeStart.Y;
                line.X2 = end.X;
                line.Y2 = end.Y;
                break;

            case System.Windows.Shapes.Path path:
                var arrowEnd = shift ? Snap45(_shapeStart, current) : current;
                path.Data = BuildArrowGeometry(_shapeStart, arrowEnd, Math.Max(CurrentThickness * 4, 12));
                break;

            default:
                var rect = new Rect(_shapeStart, current);
                if (shift)
                {
                    var side = Math.Max(rect.Width, rect.Height);
                    rect = new Rect(rect.X, rect.Y, side, side);
                }
                InkCanvas.SetLeft(_activeShape, rect.X);
                InkCanvas.SetTop(_activeShape, rect.Y);
                _activeShape.Width = rect.Width;
                _activeShape.Height = rect.Height;
                break;
        }
    }

    private static Point Snap45(Point start, Point current)
    {
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        var angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        var length = Math.Sqrt(dx * dx + dy * dy);
        return new Point(start.X + Math.Cos(angle) * length, start.Y + Math.Sin(angle) * length);
    }

    /// <summary>Arrow geometry: a line plus the V shaped head.</summary>
    private static Geometry BuildArrowGeometry(Point from, Point to, double headSize)
    {
        var angle = Math.Atan2(to.Y - from.Y, to.X - from.X);
        const double headAngle = Math.PI / 7;
        var p1 = new Point(
            to.X - headSize * Math.Cos(angle - headAngle),
            to.Y - headSize * Math.Sin(angle - headAngle));
        var p2 = new Point(
            to.X - headSize * Math.Cos(angle + headAngle),
            to.Y - headSize * Math.Sin(angle + headAngle));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(from, false, false);
            ctx.LineTo(to, true, true);
            ctx.BeginFigure(p1, false, false);
            ctx.LineTo(to, true, true);
            ctx.LineTo(p2, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Free text Excalidraw style, an editable box drops on click.</summary>
    private void AddTextBox(Point pos)
    {
        var textBox = new TextBox
        {
            MinWidth = 40,
            FontSize = 14 + CurrentThickness * 2,
            FontFamily = new FontFamily("Segoe UI Variable, Segoe UI"),
            Foreground = new SolidColorBrush(CurrentColor),
            Background = Brushes.Transparent,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x60, 0xA0, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(2),
            AcceptsReturn = true,
        };
        InkCanvas.SetLeft(textBox, pos.X);
        InkCanvas.SetTop(textBox, pos.Y);
        Ink.Children.Add(textBox);

        textBox.LostFocus += (_, _) =>
        {
            textBox.BorderBrush = Brushes.Transparent; // now it reads as a clean object
            if (string.IsNullOrWhiteSpace(textBox.Text))
            {
                Ink.Children.Remove(textBox);
            }
            else
            {
                MarkChanged();
            }
        };
        textBox.GotFocus += (_, _) =>
            textBox.BorderBrush = new SolidColorBrush(Color.FromArgb(0x80, 0x60, 0xA0, 0xFF));

        PushUndo(
            undo: () => Ink.Children.Remove(textBox),
            redo: () => Ink.Children.Add(textBox));

        textBox.Focus();
    }

    // ----- Emoji stamp -----

    /// <summary>Drops the picked emoji (colored Twemoji image) as a movable element.</summary>
    private void AddEmoji(Point pos)
    {
        var size = 32 + CurrentThickness * 8; // thickness slider scales the emoji
        var image = new Image
        {
            Source = LoadEmojiImage(_pendingEmojiCode),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false, // let Select grab it, not the image
        };
        // center it under the cursor
        InkCanvas.SetLeft(image, pos.X - size / 2);
        InkCanvas.SetTop(image, pos.Y - size / 2);
        Ink.Children.Add(image);

        PushUndo(
            undo: () => Ink.Children.Remove(image),
            redo: () => Ink.Children.Add(image));
        MarkChanged();
    }

    private static BitmapImage LoadEmojiImage(string code)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.UriSource = new Uri(Controls.EmojiRepository.ImageUri(code));
        img.EndInit();
        img.Freeze();
        return img;
    }

    private void BuildEmojiPickerOnce()
    {
        if (_emojiPickerBuilt)
            return;
        _emojiPickerBuilt = true;
        var repo = Controls.EmojiRepository.Instance;

        foreach (var category in repo.Categories)
        {
            var catButton = new Button
            {
                Content = new TextBlock
                {
                    Text = category.Glyph,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    FontSize = 15,
                },
                ToolTip = category.Name,
                Padding = new Thickness(7, 5, 7, 5),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
            };
            var cat = category;
            catButton.Click += (_, _) => { EmojiPopupSearch.Text = ""; FillEmojiPicker(cat.Emojis); };
            EmojiPopupCategories.Children.Add(catButton);
        }

        EmojiPopupSearch.TextChanged += (_, _) =>
            FillEmojiPicker(repo.Search(EmojiPopupSearch.Text));

        if (repo.Categories.Count > 0)
            FillEmojiPicker(repo.Categories[0].Emojis);
    }

    private void FillEmojiPicker(IReadOnlyList<Controls.EmojiRepository.Emoji> emojis)
    {
        EmojiPopupWrap.Items.Clear();
        var style = (Style)Resources["EmojiButton"];
        foreach (var emoji in emojis)
        {
            var button = new Button
            {
                Style = style,
                Content = new Image
                {
                    Source = LoadEmojiImage(emoji.Code),
                    Width = 20,
                    Height = 20,
                    Stretch = Stretch.Uniform,
                },
                ToolTip = emoji.Name,
            };
            var code = emoji.Code;
            button.Click += (_, _) =>
            {
                _pendingEmojiCode = code; // this one gets stamped on the next canvas click
                EmojiPopup.IsOpen = false;
                SetTool(EditorTool.Emoji);
            };
            EmojiPopupWrap.Items.Add(button);
        }
    }

    // ----- Crop -----

    private void UpdateCropVisual()
    {
        Canvas.SetLeft(CropRect, _cropSelection.X);
        Canvas.SetTop(CropRect, _cropSelection.Y);
        CropRect.Width = _cropSelection.Width;
        CropRect.Height = _cropSelection.Height;
    }

    private void ApplyCrop()
    {
        if (_base is null || _cropSelection.IsEmpty || _cropSelection.Width < 4 || _cropSelection.Height < 4)
            return;

        var offsetX = (int)Math.Max(0, _cropSelection.X);
        var offsetY = (int)Math.Max(0, _cropSelection.Y);
        var rect = new Int32Rect(
            offsetX,
            offsetY,
            Math.Min((int)_cropSelection.Width, _base.PixelWidth - offsetX),
            Math.Min((int)_cropSelection.Height, _base.PixelHeight - offsetY));

        // non-destructive: only the BASE gets cropped. annotations are shifted
        // and stay live (still editable) after the crop
        var oldBase = _base;
        var croppedBase = new CroppedBitmap(_base, rect);
        croppedBase.Freeze();

        void Translate(double dx, double dy)
        {
            // ink strokes
            var matrix = new Matrix();
            matrix.Translate(dx, dy);
            foreach (var stroke in Ink.Strokes)
                stroke.Transform(matrix, applyToStylusTip: false);
            // positioned elements (shapes, text)
            foreach (UIElement el in Ink.Children)
            {
                InkCanvas.SetLeft(el, InkCanvas.GetLeft(el) + dx);
                InkCanvas.SetTop(el, InkCanvas.GetTop(el) + dy);
            }
        }

        void SetBase(BitmapSource img)
        {
            _base = img;
            BaseImage.Source = img;
            BaseImage.Width = img.PixelWidth;
            BaseImage.Height = img.PixelHeight;
            CanvasHost.Width = img.PixelWidth;
            CanvasHost.Height = img.PixelHeight;
            StatusText.Text = $"{img.PixelWidth} x {img.PixelHeight} px";
        }

        _suppressStrokeUndo = true;
        SetBase(croppedBase);
        Translate(-offsetX, -offsetY); // content moves up and left by the crop offset
        _suppressStrokeUndo = false;
        CancelCrop();

        PushUndo(
            undo: () =>
            {
                _suppressStrokeUndo = true;
                SetBase(oldBase);
                Translate(offsetX, offsetY);
                _suppressStrokeUndo = false;
            },
            redo: () =>
            {
                _suppressStrokeUndo = true;
                SetBase(croppedBase);
                Translate(-offsetX, -offsetY);
                _suppressStrokeUndo = false;
            });
        MarkChanged();
    }

    private void CancelCrop()
    {
        _cropSelection = Rect.Empty;
        _croppingDrag = false;
        _blurDrag = false;
        CropLayer.Visibility = Visibility.Collapsed;
        CropRect.Width = 0;
        CropRect.Height = 0;
    }

    // ----- Blur / redact -----

    /// <summary>
    /// Pixelates a region and drops it as a live overlay image on the canvas.
    /// We use mosaic (not gaussian) on purpose: you cant un-blur a redaction.
    /// Non-destructive, the overlay is selectable/movable and goes on the undo stack.
    /// </summary>
    private void ApplyBlur(Rect area)
    {
        var overlay = CreateBlurOverlay(area);
        if (overlay is null)
            return;
        Ink.Children.Add(overlay);
        PushUndo(
            undo: () => Ink.Children.Remove(overlay),
            redo: () => Ink.Children.Add(overlay));
        MarkChanged();
    }

    /// <summary>Builds a pixelated overlay for a region, or null if too small. Does not add it.</summary>
    private Image? CreateBlurOverlay(Rect area)
    {
        if (_base is null || area.Width < 6 || area.Height < 6)
            return null;

        // clamp to the base bounds (in image pixels)
        var x = (int)Math.Max(0, area.X);
        var y = (int)Math.Max(0, area.Y);
        var w = Math.Min((int)area.Width, _base.PixelWidth - x);
        var h = Math.Min((int)area.Height, _base.PixelHeight - y);
        if (w < 6 || h < 6)
            return null;

        // grab just that rectangle from the current base
        var region = new CroppedBitmap(_base, new Int32Rect(x, y, w, h));
        var stride = w * 4;
        var pixels = new byte[h * stride];
        region.CopyPixels(pixels, stride, 0);

        // block size scales a bit with the area so small redactions still look chunky
        var block = Math.Clamp((int)(Math.Min(w, h) / 12.0), 6, 32);
        Core.Imaging.Pixelator.Pixelate(pixels, w, h, block);

        var mosaic = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        mosaic.Freeze();

        var overlay = new Image
        {
            Source = mosaic,
            Width = w,
            Height = h,
            Stretch = Stretch.Fill,
        };
        InkCanvas.SetLeft(overlay, x);
        InkCanvas.SetTop(overlay, y);
        return overlay;
    }

    // ----- Quick redact (OCR + auto blur) -----

    /// <summary>Runs OCR, finds emails/phones/cards/etc and pixelates each one.</summary>
    private async Task QuickRedactAsync()
    {
        if (_base is null)
            return;
        if (!_ocr.IsAvailable)
        {
            StatusText.Text = Localization.Loc.OcrUnavailable;
            return;
        }

        StatusText.Text = Localization.Loc.RedactWorking;
        var png = ScreenCaptureService.EncodePng(RenderCanvas());
        var regions = await _ocr.FindSensitiveRegionsAsync(png);
        if (regions.Count == 0)
        {
            StatusText.Text = Localization.Loc.RedactNothing;
            return;
        }

        // build one overlay per region, add them all under a single undo step
        var overlays = new List<Image>();
        foreach (var r in regions)
        {
            // pad a couple px so the blur fully covers the glyphs
            var padded = new Rect(r.X - 2, r.Y - 2, r.Width + 4, r.Height + 4);
            var overlay = CreateBlurOverlay(padded);
            if (overlay is not null)
                overlays.Add(overlay);
        }
        if (overlays.Count == 0)
        {
            StatusText.Text = Localization.Loc.RedactNothing;
            return;
        }

        foreach (var o in overlays) Ink.Children.Add(o);
        PushUndo(
            undo: () => { foreach (var o in overlays) Ink.Children.Remove(o); },
            redo: () => { foreach (var o in overlays) Ink.Children.Add(o); });
        MarkChanged();
        StatusText.Text = string.Format(Localization.Loc.RedactDone, overlays.Count);
    }

    // ----- Remove background -----

    private void RemoveBackground()
    {
        if (_base is null)
            return;

        // snapshot for undo (this flattens the annotations, same as the crop)
        var oldBase = _base;
        var oldStrokes = Ink.Strokes.Clone();
        var oldChildren = Ink.Children.Cast<UIElement>().ToList();

        var flattened = RenderCanvas();
        var width = flattened.PixelWidth;
        var height = flattened.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[height * stride];
        flattened.CopyPixels(pixels, stride, 0);

        var removed = Core.Imaging.BackgroundRemover.RemoveFromEdges(pixels, width, height);
        if (removed == 0)
        {
            // não achou fundo pra tirar, deixa a imagem como estava
            StatusText.Text = Localization.Loc.BgNotFound;
            return;
        }

        var result = BitmapSource.Create(width, height, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();

        void ApplyState(BitmapSource baseImage, StrokeCollection strokes, List<UIElement> children)
        {
            _base = baseImage;
            BaseImage.Source = baseImage;
            BaseImage.Width = baseImage.PixelWidth;
            BaseImage.Height = baseImage.PixelHeight;
            _suppressStrokeUndo = true;
            Ink.Strokes.Clear();
            foreach (var s in strokes) Ink.Strokes.Add(s);
            Ink.Children.Clear();
            foreach (var c in children) Ink.Children.Add(c);
            _suppressStrokeUndo = false;
        }

        ApplyState(result, [], []);
        PushUndo(
            undo: () => ApplyState(oldBase, oldStrokes, oldChildren),
            redo: () => ApplyState(result, [], []));
        MarkChanged();
        StatusText.Text = string.Format(Localization.Loc.BgRemoved, removed.ToString("N0"));
    }

    // ----- Rotate -----

    /// <summary>
    /// Rotates the whole picture 90 degrees. Flattens like remove-bg does, since
    /// rotating live strokes/shapes/text and re-mapping their coords is a pain and
    /// rotation is an image-wide op anyway (same as Paint).
    /// </summary>
    private void Rotate(bool clockwise)
    {
        if (_base is null)
            return;

        var oldBase = _base;
        var oldStrokes = Ink.Strokes.Clone();
        var oldChildren = Ink.Children.Cast<UIElement>().ToList();

        var flattened = RenderCanvas();
        var rotated = new TransformedBitmap(flattened, new RotateTransform(clockwise ? 90 : -90));
        var frozen = new WriteableBitmap(rotated); // detach from the transform source
        frozen.Freeze();

        void ApplyState(BitmapSource baseImage, StrokeCollection strokes, List<UIElement> children)
        {
            _base = baseImage;
            BaseImage.Source = baseImage;
            BaseImage.Width = baseImage.PixelWidth;
            BaseImage.Height = baseImage.PixelHeight;
            CanvasHost.Width = baseImage.PixelWidth;
            CanvasHost.Height = baseImage.PixelHeight;
            _suppressStrokeUndo = true;
            Ink.Strokes.Clear();
            foreach (var s in strokes) Ink.Strokes.Add(s);
            Ink.Children.Clear();
            foreach (var c in children) Ink.Children.Add(c);
            _suppressStrokeUndo = false;
            StatusText.Text = $"{baseImage.PixelWidth} x {baseImage.PixelHeight} px";
        }

        ApplyState(frozen, [], []);
        FitToWindow();
        PushUndo(
            undo: () => { ApplyState(oldBase, oldStrokes, oldChildren); FitToWindow(); },
            redo: () => { ApplyState(frozen, [], []); FitToWindow(); });
        MarkChanged();
    }

    // ----- Text actions (OCR) -----

    /// <summary>Runs OCR on the current picture, copies the text and shows a hint.</summary>
    private async Task ExtractTextAsync()
    {
        if (_base is null)
            return;
        if (!_ocr.IsAvailable)
        {
            StatusText.Text = Localization.Loc.OcrUnavailable;
            return;
        }

        StatusText.Text = Localization.Loc.OcrWorking;
        var text = await _ocr.ReadTextAsync(RenderCanvas());
        if (string.IsNullOrWhiteSpace(text))
        {
            StatusText.Text = Localization.Loc.OcrNoText;
            return;
        }

        _writeGuard.WriteText(text);
        StatusText.Text = Localization.Loc.OcrCopied;
    }

    // ----- Undo/redo -----
    private void PushUndo(Action undo, Action redo)
    {
        _undoStack.Push((undo, redo));
        _redoStack.Clear();
        RefreshUndoButtons();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        var op = _undoStack.Pop();
        op.undo();
        _redoStack.Push(op);
        RefreshUndoButtons();
        MarkChanged();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        var op = _redoStack.Pop();
        op.redo();
        _undoStack.Push(op);
        RefreshUndoButtons();
        MarkChanged();
    }

    private void RefreshUndoButtons()
    {
        UndoButton.IsEnabled = _undoStack.Count > 0;
        RedoButton.IsEnabled = _redoStack.Count > 0;
    }

    // ----- Render + auto-copy + history -----

    private void MarkChanged()
    {
        _hasChanges = true;
        if (AutoCopyToggle.IsChecked == true)
        {
            _autoCopyDebounce.Stop();
            _autoCopyDebounce.Start();
        }
    }

    /// <summary>Renders base plus annotations at exact pixels. Flatten only happens on export.</summary>
    private BitmapSource RenderCanvas()
    {
        // hide edit-only artifacts before the render
        var cropWasVisible = CropLayer.Visibility == Visibility.Visible;
        CropLayer.Visibility = Visibility.Collapsed;
        if (Ink.EditingMode == InkCanvasEditingMode.Select)
            Ink.Select(new StrokeCollection());

        CanvasHost.UpdateLayout();
        var rtb = new RenderTargetBitmap(
            (int)CanvasHost.Width, (int)CanvasHost.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(CanvasHost);
        rtb.Freeze();

        if (cropWasVisible)
            CropLayer.Visibility = Visibility.Visible;
        return rtb;
    }

    /// <summary>Clipboard plus a history item, new on the first edit, then it updates in place.</summary>
    private void SyncToClipboardAndHistory()
    {
        if (_base is null || !_hasChanges)
            return;

        try
        {
            var rendered = RenderCanvas();
            var png = ScreenCaptureService.EncodePng(rendered);
            _writeGuard.WriteImageFromPng(png, rendered);

            var hash = HashUtil.Sha256Hex(png);
            if (_historyItemId is null)
            {
                var item = _ingest.Ingest(new ClipboardSnapshot
                {
                    PngBytes = png,
                    ImageWidth = rendered.PixelWidth,
                    ImageHeight = rendered.PixelHeight,
                    SourceApp = "Klip",
                    SourceTitle = "Editor",
                    Origin = ClipboardItemOrigin.Editor,
                });
                _historyItemId = item?.Id;
            }
            else
            {
                var path = _mediaStore.SavePng(png, hash, DateTimeOffset.UtcNow);
                var oldPath = _repository.UpdateImageContent(
                    _historyItemId.Value, hash, png.Length, path,
                    rendered.PixelWidth, rendered.PixelHeight);
                if (oldPath is not null)
                    _mediaStore.DeleteFiles([oldPath]);
            }

            StatusText.Text = $"{rendered.PixelWidth} x {rendered.PixelHeight} px, {Localization.Loc.StatusCopied}";
        }
        catch (Exception ex)
        {
            StartupLog.WriteException("EditorSync", ex);
        }
    }

    private void SaveAs()
    {
        if (_base is null)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Localization.Loc.SaveImageFilter,
            FileName = $"Klip {DateTime.Now:yyyy-MM-dd HHmmss}.png",
        };
        if (dialog.ShowDialog() != true)
            return;

        var rendered = RenderCanvas();
        BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = File.Create(dialog.FileName);
        encoder.Save(stream);
    }

    // ----- Zoom -----

    private void ApplyZoom()
    {
        var scale = ZoomSlider.Value;
        ZoomWrapper.LayoutTransform = new ScaleTransform(scale, scale);
        ZoomLabel.Text = $"{(int)(scale * 100)}%";
    }

    private void FitToWindow()
    {
        if (_base is null)
            return;
        // usa o viewport real quando j[a tem, se não estima pelo tamanho da janela
        var availableW = CanvasScroll.ViewportWidth > 32 ? CanvasScroll.ViewportWidth : Width - 48;
        var availableH = CanvasScroll.ViewportHeight > 32 ? CanvasScroll.ViewportHeight : Height - 220;
        var scale = Math.Min(1.0, Math.Min(availableW / _base.PixelWidth, availableH / _base.PixelHeight));
        ZoomSlider.Value = Math.Clamp(scale, ZoomSlider.Minimum, ZoomSlider.Maximum);
    }

    // ----- Keyboard -----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // tool shortcuts don't fire while typing in a text box
        var typingText = Keyboard.FocusedElement is TextBox;

        // Ctrl+Shift+Z = redo (Excalidraw/Figma default)
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Z)
        {
            Redo();
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.Z: Undo(); e.Handled = true; return;
                case Key.Y: Redo(); e.Handled = true; return;                case Key.S: SaveAs(); e.Handled = true; return;
                case Key.C when !typingText:
                    _hasChanges = true;
                    SyncToClipboardAndHistory();
                    e.Handled = true;
                    return;
                case Key.D0:
                    FitToWindow();
                    e.Handled = true;
                    return;
            }
        }

        if (!typingText)
        {
            // space held = pan mode (hand cursor)
            if (e.Key == Key.Space && !_spaceHeld)
            {
                _spaceHeld = true;
                Ink.UseCustomCursor = true;
                Ink.Cursor = Cursors.SizeAll;
                e.Handled = true;
                return;
            }

            switch (e.Key)
            {
                case Key.V: SetTool(EditorTool.Select); e.Handled = true; return;
                case Key.P: SetTool(EditorTool.Pen); e.Handled = true; return;
                case Key.H: SetTool(EditorTool.Highlighter); e.Handled = true; return;
                case Key.E: SetTool(EditorTool.Eraser); e.Handled = true; return;
                case Key.R: SetTool(EditorTool.Rect); e.Handled = true; return;
                case Key.O: SetTool(EditorTool.Ellipse); e.Handled = true; return;
                case Key.L: SetTool(EditorTool.Line); e.Handled = true; return;
                case Key.A: SetTool(EditorTool.Arrow); e.Handled = true; return;
                case Key.T: SetTool(EditorTool.Text); e.Handled = true; return;
                case Key.C: SetTool(EditorTool.Crop); e.Handled = true; return;
                case Key.B: SetTool(EditorTool.Blur); e.Handled = true; return;
                case Key.Enter when _tool == EditorTool.Crop:
                    ApplyCrop();
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CancelCrop();
                    Ink.Select(new StrokeCollection());
                    e.Handled = true;
                    return;
                case Key.Delete when _tool == EditorTool.Select:
                    DeleteSelection();
                    e.Handled = true;
                    return;
            }
        }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space && _spaceHeld)
        {
            _spaceHeld = false;
            _panning = false;
            CanvasScroll.ReleaseMouseCapture();
            SetTool(_tool); // restore the active tool's cursor
            e.Handled = true;
        }
        base.OnPreviewKeyUp(e);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            ZoomSlider.Value = Math.Clamp(
                ZoomSlider.Value * (e.Delta > 0 ? 1.1 : 1 / 1.1),
                ZoomSlider.Minimum, ZoomSlider.Maximum);
            e.Handled = true;
        }
        base.OnPreviewMouseWheel(e);
    }

    private void DeleteSelection()
    {
        var strokes = Ink.GetSelectedStrokes().ToList();
        var elements = Ink.GetSelectedElements().ToList();
        if (strokes.Count == 0 && elements.Count == 0)
            return;

        _suppressStrokeUndo = true;
        foreach (var s in strokes) Ink.Strokes.Remove(s);
        foreach (var el in elements) Ink.Children.Remove(el);
        _suppressStrokeUndo = false;

        PushUndo(
            undo: () =>
            {
                _suppressStrokeUndo = true;
                foreach (var s in strokes) Ink.Strokes.Add(s);
                foreach (var el in elements) Ink.Children.Add(el);
                _suppressStrokeUndo = false;
            },
            redo: () =>
            {
                _suppressStrokeUndo = true;
                foreach (var s in strokes) Ink.Strokes.Remove(s);
                foreach (var el in elements) Ink.Children.Remove(el);
                _suppressStrokeUndo = false;
            });
        MarkChanged();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // flush a pending sync before we close
        if (_autoCopyDebounce.IsEnabled)
        {
            _autoCopyDebounce.Stop();
            SyncToClipboardAndHistory();
        }
        base.OnClosing(e);
    }
}
