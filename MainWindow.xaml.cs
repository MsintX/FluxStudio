using FluxStudio.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using Windows.UI.Core;
using Microsoft.UI.Text;
using WinRT.Interop;

namespace FluxStudio;

public sealed partial class MainWindow : Window
{
    private int IndentSize => Math.Clamp(_settings.IndentSize, 1, 8);
    private string? _filePath;
    private string? _projectRoot;
    private bool _loading;
    private bool _dirty;
    private bool _applyingHighlight;
    private string _theme = "System";
    private FluxStudioSettings _settings = new();
    private bool _runInProgress;
    private int _wheelDeltaRemainder;
    private readonly DispatcherQueueTimer _analysisTimer;
    private readonly DispatcherQueueTimer _diagnosticTimer;
    private bool _diagnosticCheckInProgress;
    private string? _lastInterpreterIssue;

    private static readonly Regex RuntimeDiagnosticLine = new(
        @"^(?<path>.+?):(?<line>\d+):(?<column>\d+):\s*(?<severity>error|warning)\s+(?<code>[A-Za-z0-9_-]+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RuntimeGeneralDiagnosticLine = new(
        @"^(?:.*?:\s*)?(?<severity>error|warning)\s+(?<code>FLX-I\d+):\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Content, not formatting or selection changes, drives language-service updates.
    private string _lastKnownText = string.Empty;
    private string? _lastHighlightedText;
    private string? _lastDiagnosticsSource;
    private IReadOnlyList<FluxDiagnostic> _lastDiagnostics = Array.Empty<FluxDiagnostic>();
    private bool _syntaxColorsDirty = true;
    private bool _completionForced;
    private string? _completionFingerprint;
    private string? _lastUnderlinedSource;
    private string? _lastUnderlineFingerprint;

    private static readonly IReadOnlyList<FluxCompletionItem> CompletionCatalog = CreateCompletionCatalog();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Flux Studio";
        _settings = FluxStudioSettings.Load();
        _theme = _settings.Theme;
        RootGrid.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(GlobalKeyDown), true);
        Editor.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(Editor_PointerWheelChanged), true);
        RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;

        _analysisTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _analysisTimer.Interval = TimeSpan.FromMilliseconds(320);
        _analysisTimer.IsRepeating = false;
        _analysisTimer.Tick += AnalysisTimer_Tick;

        // The interpreter is the source of truth for diagnostics. Check once per
        // second, but only replace the Error List when the actual diagnostic set changes.
        _diagnosticTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _diagnosticTimer.Interval = TimeSpan.FromSeconds(1);
        _diagnosticTimer.IsRepeating = true;
        _diagnosticTimer.Tick += RuntimeDiagnosticsTimer_Tick;

        _loading = true;
        SetEditorText(WelcomeText);
        _loading = false;

        AppendOutput("Flux Studio Shell");
        AppendOutput("语言服务依据MsintX提供的Flux解释器，Flux Studio不为此付任何有关责任。（不兑啊不都是MsintX写的）");
        try
        {
            var appWindow = GetAppWindow();
            appWindow.Title = "Flux Studio";
            appWindow.Resize(new Windows.Graphics.SizeInt32(1440, 900));
        }
        catch { /* Optional window chrome customization is not required for the editor. */ }

        ApplyEditorPreferences();
        ApplyTheme();
        UpdateCaret();
        UpdateDocumentInfo();
        ApplySyntaxHighlighting();
        ApplyRuntimeDiagnostics(GetEditorText(), Array.Empty<FluxDiagnostic>());
        _diagnosticTimer.Start();
        _ = CheckRuntimeDiagnosticsAsync();
    }

    private static string WelcomeText =>
        "* Welcome to Flux Studio\n" +
        "* Flux syntax: https://github.com/MsintX/Flux\n\n" +
        "public int Main :\n" +
        "    var new name = \"Flux\" as string\n" +
        "    printfn \"Hello from {%s}!\" name -> System\n" +
        "    return 0\n" +
        "end\n";

    private AppWindow GetAppWindow()
    {
        IntPtr hwnd = WindowNative.GetWindowHandle(this);
        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    private string GetEditorText()
    {
        Editor.Document.GetText(TextGetOptions.None, out string text);
        // RichEdit documents keep one final paragraph mark internally. Remove exactly
        // that sentinel, preserving any newline the user intentionally typed.
        if (text.EndsWith("\r", StringComparison.Ordinal)) text = text[..^1];
        return FluxLexer.Normalize(text);
    }

    private void SetEditorText(string text)
    {
        Editor.Document.SetText(TextSetOptions.None, FluxLexer.Normalize(text).Replace("\n", "\r", StringComparison.Ordinal));
        int end = GetEditorText().Length;
        SetEditorSelection(end, end);
        _lastKnownText = GetEditorText();
        _lastHighlightedText = null;
        _syntaxColorsDirty = true;
    }

    private void SetEditorSelection(int start, int end)
    {
        // In WinUI 3, ITextRange does not expose a parameterless Select() method.
        // The active document selection is positioned with SetRange instead.
        int length = GetEditorText().Length;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, start, length);
        var (currentStart, currentEnd) = GetSelection();
        if (currentStart == start && currentEnd == end) return;
        Editor.Document.Selection.SetRange(start, end);
    }

    private (int Start, int End) GetSelection()
    {
        var selection = Editor.Document.Selection;
        // RichEditBox retains a final paragraph marker that GetEditorText removes.
        // Clamp both endpoints to the visible source so slicing/insertion never sees
        // a range beyond the text the user can actually edit.
        int length = GetEditorText().Length;
        int start = Math.Clamp(selection.StartPosition, 0, length);
        int end = Math.Clamp(selection.EndPosition, 0, length);
        return start <= end ? (start, end) : (end, start);
    }

    private void Editor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_applyingHighlight || _loading) return;

        string source = GetEditorText();
        // RichEditBox can raise TextChanged for document formatting as well as text.
        // Ignore these no-content-change events to prevent a perpetual analysis loop.
        if (string.Equals(source, _lastKnownText, StringComparison.Ordinal)) return;

        _lastKnownText = source;
        _completionForced = false;
        if (!_loading)
        {
            _dirty = true;
            UpdateTitle();
        }
        UpdateCaret();
        UpdateDocumentInfo();
        ScheduleAnalysis();
        UpdateCompletion(force: false);
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateCaret();
        // Highlighting restores the selection internally. It must not refresh the
        // completion popup or replace its ItemsSource.
        if (_applyingHighlight) return;
        if (CompletionPopup.Visibility == Visibility.Visible)
            UpdateCompletion(force: _completionForced);
    }

    private void ScheduleAnalysis()
    {
        _analysisTimer.Stop();
        _analysisTimer.Start();
    }

    private void AnalysisTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _analysisTimer.Stop();
        if (_loading || _applyingHighlight) return;

        string source = GetEditorText();
        if (_syntaxColorsDirty || !string.Equals(source, _lastHighlightedText, StringComparison.Ordinal))
        {
            ApplySyntaxHighlighting();
            // Reapply existing interpreter diagnostics only when they belong to this
            // exact source snapshot; stale line positions are never drawn on new text.
            if (string.Equals(_lastDiagnosticsSource, source, StringComparison.Ordinal))
                ApplyDiagnosticUnderlines(source);
        }
    }

    private async void RuntimeDiagnosticsTimer_Tick(DispatcherQueueTimer sender, object args)
        => await CheckRuntimeDiagnosticsAsync();

    private void ApplySyntaxHighlighting()
    {
        string source = GetEditorText();
        var (selectionStart, selectionEnd) = GetSelection();
        var lex = FluxLexer.Lex(source);
        // ActualTheme reflects the resolved WinUI theme (including the system theme).
        // Using it directly avoids stale colors immediately after switching themes.
        bool light = RootGrid.ActualTheme == ElementTheme.Light;
        Color normal = light ? Color.FromArgb(255, 35, 39, 45) : Color.FromArgb(255, 220, 224, 232);
        Color keyword = light ? Color.FromArgb(255, 106, 42, 155) : Color.FromArgb(255, 198, 120, 221);
        Color type = light ? Color.FromArgb(255, 0, 115, 125) : Color.FromArgb(255, 86, 182, 194);
        Color text = light ? Color.FromArgb(255, 163, 76, 30) : Color.FromArgb(255, 229, 157, 105);
        Color number = light ? Color.FromArgb(255, 9, 120, 62) : Color.FromArgb(255, 181, 206, 168);
        Color comment = light ? Color.FromArgb(255, 102, 119, 93) : Color.FromArgb(255, 125, 145, 120);
        Color op = light ? Color.FromArgb(255, 40, 90, 150) : Color.FromArgb(255, 120, 170, 230);
        Color preprocessor = light ? Color.FromArgb(255, 150, 76, 20) : Color.FromArgb(255, 220, 180, 100);

        _applyingHighlight = true;
        try
        {
            if (source.Length > 0)
            {
                var wholeRange = Editor.Document.GetRange(0, source.Length);
                wholeRange.CharacterFormat.ForegroundColor = normal;
                wholeRange.CharacterFormat.Underline = UnderlineType.None;
            }

            foreach (var token in lex.Tokens)
            {
                if (token.Length <= 0 || token.Start < 0 || token.End > source.Length) continue;
                Color? color = token.Kind switch
                {
                    FluxTokenKind.Keyword => keyword,
                    FluxTokenKind.Type => type,
                    FluxTokenKind.String or FluxTokenKind.Character => text,
                    FluxTokenKind.Number => number,
                    FluxTokenKind.Comment => comment,
                    FluxTokenKind.Operator => op,
                    FluxTokenKind.Preprocessor => preprocessor,
                    _ => null
                };
                if (color.HasValue)
                    Editor.Document.GetRange(token.Start, token.End).CharacterFormat.ForegroundColor = color.Value;
            }
        }
        catch (ArgumentException)
        {
            // The document may be changing while the dispatcher coalesces text input.
        }
        finally
        {
            _applyingHighlight = false;
            _lastHighlightedText = source;
            _syntaxColorsDirty = false;
            _lastUnderlinedSource = null;
            _lastUnderlineFingerprint = null;
            int max = GetEditorText().Length;
            selectionStart = Math.Clamp(selectionStart, 0, max);
            selectionEnd = Math.Clamp(selectionEnd, selectionStart, max);
            SetEditorSelection(selectionStart, selectionEnd);
        }
    }

    private void Editor_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool control = IsKeyDown(Windows.System.VirtualKey.Control);

        if (control)
        {
            if (e.Key == Windows.System.VirtualKey.Space)
            {
                UpdateCompletion(force: true);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.S) { _ = SaveAsync(); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.O) { _ = OpenAsync(); e.Handled = true; return; }
            if (e.Key == Windows.System.VirtualKey.F) { _ = FindDialogAsync(); e.Handled = true; return; }
        }

        if (CompletionPopup.Visibility == Visibility.Visible)
        {
            if (e.Key == Windows.System.VirtualKey.Down)
            {
                if (CompletionList.Items.Count > 0)
                    CompletionList.SelectedIndex = Math.Min(CompletionList.Items.Count - 1, Math.Max(0, CompletionList.SelectedIndex + 1));
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Up)
            {
                if (CompletionList.Items.Count > 0)
                    CompletionList.SelectedIndex = Math.Max(0, CompletionList.SelectedIndex - 1);
                e.Handled = true;
                return;
            }
            if (e.Key == Windows.System.VirtualKey.Enter && CompletionList.SelectedItem is FluxCompletionItem selected)
            {
                if (IsAlreadyTypedWord(selected))
                {
                    // If the selected suggestion is exactly the word already in the
                    // editor (for example `then` or `end`), Enter should insert a
                    // newline rather than repeatedly re-applying the same completion.
                    HideCompletion();
                }
                else
                {
                    AcceptCompletion(selected);
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                HideCompletion();
                e.Handled = true;
                return;
            }
        }

        if (e.Key == Windows.System.VirtualKey.Tab)
        {
            if (IsKeyDown(Windows.System.VirtualKey.Shift)) UnindentCurrentLine();
            else InsertAtSelection(" ", IndentSize);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            InsertNewLineWithIndent();
            e.Handled = true;
            return;
        }

    }

    private void GlobalKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // F5 and Ctrl+F5 both launch the configured Flux CLI. The CLI owns the
        // program execution; Visual Studio's debugger is attached to the IDE host.
        if (e.Key == Windows.System.VirtualKey.F5)
        {
            e.Handled = true;
            _ = RunFluxAsync();
        }
    }

    private void Editor_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) == 0) return;

        int delta = e.GetCurrentPoint(Editor).Properties.MouseWheelDelta;
        if (delta == 0) return;

        // High-resolution mouse wheels may report partial notches. Accumulate them
        // so zoom changes predictably and never trigger a formatting pass per tiny event.
        int accumulated = _wheelDeltaRemainder + delta;
        int notches = accumulated / 120;
        _wheelDeltaRemainder = accumulated % 120;
        e.Handled = true;
        if (notches == 0) return;

        SetEditorFontSize(_settings.EditorFontSize + notches, persist: true);
    }

    private void SetEditorFontSize(double value, bool persist)
    {
        double next = Math.Clamp(Math.Round(value), 8, 40);
        if (Math.Abs(_settings.EditorFontSize - next) < 0.001) return;

        _settings.EditorFontSize = next;
        if (persist) _settings.Save();

        // RichEditBox inherits font properties from the control. Do not walk and
        // rewrite CharacterFormat over every character: on a large source file that
        // makes a zoom or a settings save appear to hang, and can fire many formatting
        // notifications. Syntax colors/underlines are independent of the font size.
        ApplyEditorPreferences();
        StatusText.Text = $"编辑器字号：{_settings.EditorFontSize:0} pt（已保存）";
    }

    private void ApplyEditorPreferences()
    {
        if (Editor is null) return;
        Editor.FontSize = _settings.EditorFontSize;
        try { Editor.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily(_settings.EditorFontFamily); }
        catch { Editor.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"); }
    }

    private bool IsAlreadyTypedWord(FluxCompletionItem item)
    {
        if (item.Detail.Contains("模板", StringComparison.Ordinal)) return false;
        var (start, end) = GetSelection();
        if (start != end) return false;

        string source = GetEditorText();
        int prefixStart = end;
        while (prefixStart > 0 && FluxLexer.IsIdentifierPart(source[prefixStart - 1])) prefixStart--;
        string currentWord = source[prefixStart..end];
        return string.Equals(currentWord, item.InsertText, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(item.InsertText, item.FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKeyDown(Windows.System.VirtualKey key)
    {
        try
        {
            return (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }
        catch
        {
            // Keyboard state is only used for shortcuts. Editor typing still works if
            // the current host cannot provide modifier state.
            return false;
        }
    }

    private void InsertNewLineWithIndent()
    {
        string source = GetEditorText();
        var (start, end) = GetSelection();
        int caret = Math.Clamp(start, 0, source.Length);
        int lineStart = source.LastIndexOf('\n', Math.Max(0, caret - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        string prefix = source[lineStart..caret];
        string trimmed = prefix.Trim();
        int lineIndent = prefix.TakeWhile(c => c is ' ' or '\t').Sum(c => c == '\t' ? IndentSize : 1);
        lineIndent = Math.Max(0, lineIndent);
        if (!_settings.AutoIndent && !_settings.AutoInsertEnd)
        {
            Editor.Document.GetRange(start, end).Text = "\r";
            SetEditorSelection(start + 1, start + 1);
            ScheduleAnalysis();
            UpdateCaret();
            return;
        }

        // Pair a newly-entered block header with an `end`. The caret stays on the
        // indented body line; this only happens when there is no following code yet,
        // so it will not duplicate an existing closing block while editing mid-file.
        bool opensBlock = _settings.AutoInsertEnd && IsAutoEndHeader(trimmed);
        bool atDocumentTail = string.IsNullOrWhiteSpace(source[end..]);
        string insertion;
        int caretOffset;

        if (opensBlock && atDocumentTail)
        {
            int bodyIndent = _settings.AutoIndent ? lineIndent + IndentSize : lineIndent;
            insertion = "\r" + new string(' ', bodyIndent) + "\r" + new string(' ', lineIndent) + "end";
            caretOffset = 1 + bodyIndent;
        }
        else
        {
            int bodyIndent = _settings.AutoIndent ? lineIndent : 0;
            if (_settings.AutoIndent && (Regex.IsMatch(trimmed, @"\bthen\s*;?$") || trimmed.EndsWith(':') || Regex.IsMatch(trimmed, @"^else\b")))
                bodyIndent += IndentSize;
            else if (_settings.AutoIndent && Regex.IsMatch(trimmed, @"^end\b"))
                bodyIndent = Math.Max(0, bodyIndent - IndentSize);

            insertion = "\r" + new string(' ', bodyIndent);
            caretOffset = insertion.Length;
        }

        Editor.Document.GetRange(start, end).Text = insertion;
        int newCaret = Math.Clamp(start + caretOffset, 0, GetEditorText().Length);
        SetEditorSelection(newCaret, newCaret);
        ScheduleAnalysis();
        UpdateCaret();
    }

    private static bool IsAutoEndHeader(string trimmed)
    {
        if (trimmed.Length == 0 || Regex.IsMatch(trimmed, @"^else\b")) return false;
        if (Regex.IsMatch(trimmed, @"^(?:if|for)\b.*\bthen\s*;?$")) return true;
        // Flux declarations and handler/property sections use a trailing colon.
        return trimmed.EndsWith(':');
    }

    private void InsertAtSelection(string text, int repeat)
    {
        var (start, end) = GetSelection();
        string insertion = string.Concat(Enumerable.Repeat(text, repeat));
        Editor.Document.GetRange(start, end).Text = insertion;
        int caret = start + insertion.Length;
        SetEditorSelection(caret, caret);
        ScheduleAnalysis();
        UpdateCaret();
    }

    private void UnindentCurrentLine()
    {
        string source = GetEditorText();
        var (start, end) = GetSelection();
        int caret = Math.Clamp(start, 0, source.Length);
        int lineStart = source.LastIndexOf('\n', Math.Max(0, caret - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        int remove = 0;
        while (remove < IndentSize && lineStart + remove < source.Length && source[lineStart + remove] == ' ') remove++;
        if (remove == 0) return;
        Editor.Document.GetRange(lineStart, lineStart + remove).Text = string.Empty;
        int adjustedStart = Math.Max(lineStart, start - remove);
        int adjustedEnd = Math.Max(adjustedStart, end - remove);
        SetEditorSelection(adjustedStart, adjustedEnd);
        ScheduleAnalysis();
    }

    private void UpdateCaret()
    {
        if (Editor is null || CaretText is null) return;
        string source = GetEditorText();
        int caret = Math.Clamp(Editor.Document.Selection.StartPosition, 0, source.Length);
        string prefix = source[..caret];
        int line = prefix.Count(c => c == '\n') + 1;
        int lastNewline = prefix.LastIndexOf('\n');
        CaretText.Text = $"Ln {line}, Col {prefix.Length - lastNewline}";
        UpdateDocumentInfo(source);
    }

    private void UpdateDocumentInfo(string? sourceOverride = null)
    {
        if (DocumentInfoText is null || Editor is null) return;
        string source = sourceOverride ?? GetEditorText();
        int lineCount = source.Length == 0 ? 1 : source.Count(c => c == '\n') + (source.EndsWith('\n') ? 0 : 1);
        string name = _filePath is null ? "Flux" : Path.GetExtension(_filePath).Equals(".flux", StringComparison.OrdinalIgnoreCase) ? "Flux" : Path.GetExtension(_filePath).TrimStart('.').ToUpperInvariant();
        DocumentInfoText.Text = $"{name} - UTF-8 - LF - {lineCount} 行 · {source.Length} 字符";
    }

    private void UpdateTitle()
    {
        var name = _filePath is null ? "未命名.flux" : Path.GetFileName(_filePath);
        DocumentTitle.Text = (_dirty ? "- " : "") + name;
        Title = $"Flux Studio — {(_dirty ? "- " : "")}{name}";
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        _loading = true;
        SetEditorText(string.Empty);
        _loading = false;
        _filePath = null;
        _dirty = false;
        HideCompletion();
        UpdateTitle();
        StatusText.Text = "新建 Flux 源文件";
        ScheduleAnalysis();
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenAsync();

    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".flux");
        picker.FileTypeFilter.Add(".flpkg");
        picker.FileTypeFilter.Add(".json");
        picker.FileTypeFilter.Add(".txt");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null || !await ConfirmDiscardAsync()) return;
        await LoadOpenedFileAsync(file);
    }

    /// <summary>Loads a file delivered by the MSIX file-type activation contract.</summary>
    public async Task OpenActivatedFileAsync(StorageFile file)
    {
        if (!await ConfirmDiscardAsync()) return;
        await LoadOpenedFileAsync(file);
    }

    private async Task LoadOpenedFileAsync(StorageFile file)
    {
        try
        {
            string content = await FileIO.ReadTextAsync(file);
            _loading = true;
            SetEditorText(content);
            _loading = false;
            _filePath = file.Path;
            _dirty = false;
            if (string.Equals(file.Name, "flux.json", StringComparison.OrdinalIgnoreCase))
            {
                LoadProjectFromManifest(file.Path);
            }
            else if (string.Equals(Path.GetExtension(file.Name), ".flpkg", StringComparison.OrdinalIgnoreCase))
            {
                StatusText.Text = "已打开 Flux 外部包文件：" + file.Path;
            }
            else
            {
                StatusText.Text = "已打开：" + file.Path;
            }
            UpdateTitle();
            UpdateProjectTree();
            HideCompletion();
            ScheduleAnalysis();
        }
        catch (Exception ex)
        {
            _loading = false;
            await ShowErrorAsync("打开文件失败", ex.Message);
        }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null || !await ConfirmDiscardAsync()) return;
        try
        {
            LoadProjectFromManifest(file.Path);
            string source = ResolveProjectSource(file.Path);
            if (File.Exists(source))
            {
                _loading = true;
                SetEditorText(await File.ReadAllTextAsync(source));
                _loading = false;
                _filePath = source;
                _dirty = false;
                UpdateTitle();
            }
            StatusText.Text = "项目已打开：" + _projectRoot;
            UpdateProjectTree();
            ScheduleAnalysis();
        }
        catch (Exception ex)
        {
            _loading = false;
            await ShowErrorAsync("打开项目失败", ex.Message);
        }
    }

    private string ResolveProjectSource(string manifestPath)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        using var json = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string relative = json.RootElement.TryGetProperty("source", out var source)
            ? source.GetString() ?? Path.Combine("src", "Main.flux")
            : Path.Combine("src", "Main.flux");
        return Path.GetFullPath(Path.Combine(root, relative));
    }

    private void LoadProjectFromManifest(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        _projectRoot = Path.GetDirectoryName(Path.GetFullPath(path));
        string name = json.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "Flux Project" : "Flux Project";
        StatusText.Text = "项目：" + name;
        UpdateProjectTree();
    }

    private void UpdateProjectTree()
    {
        ProjectTree.RootNodes.Clear();
        if (_projectRoot is null || !Directory.Exists(_projectRoot)) return;
        var root = new TreeViewNode { Content = Path.GetFileName(_projectRoot) };
        try
        {
            foreach (string path in Directory.EnumerateFiles(_projectRoot, "*", SearchOption.AllDirectories).Take(300))
            {
                string relative = Path.GetRelativePath(_projectRoot, path);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => part is ".git" or "bin" or "obj" or ".vs")) continue;
                root.Children.Add(new TreeViewNode { Content = relative });
            }
        }
        catch (UnauthorizedAccessException) { /* Display the files we could enumerate. */ }
        ProjectTree.RootNodes.Add(root);
        root.IsExpanded = true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsync(forcePicker: true);

    private async Task<bool> SaveAsync(bool forcePicker = false)
    {
        string? path = _filePath;
        if (path is null || forcePicker)
        {
            var picker = new FileSavePicker
            {
                SuggestedFileName = path is null ? "Main" : Path.GetFileNameWithoutExtension(path)
            };
            picker.FileTypeChoices.Add("Flux source", new List<string> { ".flux" });
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return false;
            path = file.Path;
        }
        try
        {
            await File.WriteAllTextAsync(path, GetEditorText(), new UTF8Encoding(false));
            _filePath = path;
            _dirty = false;
            UpdateTitle();
            StatusText.Text = "已保存：" + path;
            UpdateProjectTree();
            return true;
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("保存失败", ex.Message);
            return false;
        }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!_dirty) return true;
        var dialog = new ContentDialog
        {
			Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
			Title = "放弃未保存的更改？",
            Content = "当前文件有尚未保存的修改。",
            PrimaryButtonText = "保存",
            SecondaryButtonText = "放弃修改",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) return await SaveAsync();
        return result == ContentDialogResult.Secondary;
    }

    private async Task CheckRuntimeDiagnosticsAsync()
    {
        if (_loading || _runInProgress || _diagnosticCheckInProgress) return;
        _diagnosticCheckInProgress = true;
        string source = GetEditorText();
        Process? process = null;
        try
        {
            string executable = ResolveRuntimeExecutable(_settings.InterpreterPath);
            if (string.IsNullOrWhiteSpace(executable))
            {
                ReportInterpreterCheckIssue("请先在设置中指定 Flux.Core 解释器路径。", "实时诊断不可用：未配置解释器");
                return;
            }

            bool executableIsPath = Path.IsPathFullyQualified(executable) ||
                executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar);
            if (executableIsPath && !File.Exists(executable))
            {
                ReportInterpreterCheckIssue("找不到 Flux 解释器：" + executable, "实时诊断不可用：解释器不存在");
                return;
            }

            string contextPath = GetDiagnosticContextPath();
            string defaultWorkingDirectory = Path.GetDirectoryName(contextPath) ?? Environment.CurrentDirectory;
            string workingDirectory = string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
                ? defaultWorkingDirectory
                : Environment.ExpandEnvironmentVariables(_settings.WorkingDirectory.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(workingDirectory))
                workingDirectory = Path.GetFullPath(Path.Combine(defaultWorkingDirectory, workingDirectory));
            if (!Directory.Exists(workingDirectory)) workingDirectory = defaultWorkingDirectory;

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--check-stdin " + QuoteArgument(contextPath),
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            process = new Process { StartInfo = startInfo };
            if (!process.Start()) throw new InvalidOperationException("无法启动所选 Flux.Core 解释器。");

            // Drain both pipes while sending the unsaved editor buffer. This avoids
            // deadlocks on large files and guarantees diagnostics use the current text,
            // while the context path preserves relative $copy resolution.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(source);
            process.StandardInput.Close();

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                await process.WaitForExitAsync();
                throw new TimeoutException("Flux.Core 语法检查超过 8 秒，已终止此次检查。");
            }

            string stdout = await stdoutTask;
            string stderr = await stderrTask;
            var diagnostics = ParseRuntimeDiagnostics(stderr + Environment.NewLine + stdout);
            if (diagnostics.Count == 0 && process.ExitCode != 0)
            {
                string message = (stderr + Environment.NewLine + stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0)
                    ?? $"解释器语法检查退出代码为 {process.ExitCode}。";
                diagnostics.Add(new FluxDiagnostic
                {
                    Severity = "error",
                    Code = "FLX-I0000",
                    Line = 1,
                    Column = 1,
                    Message = message
                });
            }

            // Discard a stale result if the user edited the document while the
            // interpreter was checking the previous snapshot. The next one-second
            // tick will check the latest buffer.
            if (!string.Equals(source, GetEditorText(), StringComparison.Ordinal)) return;

            _lastInterpreterIssue = null;
            ApplyRuntimeDiagnostics(source, diagnostics);
        }
        catch (Exception ex)
        {
            ReportInterpreterCheckIssue("Flux.Core 检查失败：" + ex.Message, "实时诊断失败");
        }
        finally
        {
            process?.Dispose();
            _diagnosticCheckInProgress = false;
        }
    }

    private string GetDiagnosticContextPath()
    {
        if (!string.IsNullOrWhiteSpace(_filePath)) return Path.GetFullPath(_filePath);
        string baseDirectory = !string.IsNullOrWhiteSpace(_projectRoot) && Directory.Exists(_projectRoot)
            ? Path.GetFullPath(_projectRoot)
            : Environment.CurrentDirectory;
        return Path.Combine(baseDirectory, "Untitled.flux");
    }

    private static List<FluxDiagnostic> ParseRuntimeDiagnostics(string text)
    {
        var diagnostics = new List<FluxDiagnostic>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var match = RuntimeDiagnosticLine.Match(line);
            if (match.Success &&
                int.TryParse(match.Groups["line"].Value, out int lineNumber) &&
                int.TryParse(match.Groups["column"].Value, out int columnNumber))
            {
                var diagnostic = new FluxDiagnostic
                {
                    Severity = match.Groups["severity"].Value,
                    Code = match.Groups["code"].Value,
                    Line = Math.Max(1, lineNumber),
                    Column = Math.Max(1, columnNumber),
                    Message = match.Groups["message"].Value
                };
                string key = $"{diagnostic.Severity}\u001f{diagnostic.Code}\u001f{diagnostic.Line}\u001f{diagnostic.Column}\u001f{diagnostic.Message}";
                if (seen.Add(key)) diagnostics.Add(diagnostic);
                continue;
            }

            match = RuntimeGeneralDiagnosticLine.Match(line);
            if (match.Success)
            {
                var diagnostic = new FluxDiagnostic
                {
                    Severity = match.Groups["severity"].Value,
                    Code = match.Groups["code"].Value,
                    Line = 1,
                    Column = 1,
                    Message = match.Groups["message"].Value
                };
                string key = $"{diagnostic.Severity}\u001f{diagnostic.Code}\u001f{diagnostic.Line}\u001f{diagnostic.Column}\u001f{diagnostic.Message}";
                if (seen.Add(key)) diagnostics.Add(diagnostic);
            }
        }

        return diagnostics.OrderBy(d => d.Line).ThenBy(d => d.Column).ThenBy(d => d.Code, StringComparer.Ordinal).ToList();
    }

    private void ApplyRuntimeDiagnostics(string source, IReadOnlyList<FluxDiagnostic> diagnostics)
    {
        bool diagnosticsChanged = !DiagnosticsEqual(_lastDiagnostics, diagnostics);
        if (diagnosticsChanged)
        {
            // Clear previous red foreground/wave formatting before applying a new
            // diagnostic set. If syntax highlighting already processed the new text,
            // this only happens when the actual diagnostic set differs.
            if (_lastUnderlinedSource is not null)
                ApplySyntaxHighlighting();
            _lastDiagnostics = diagnostics.ToArray();
            DiagnosticsList.ItemsSource = _lastDiagnostics;
        }
        _lastDiagnosticsSource = source;

        bool hasDiagnostics = _lastDiagnostics.Count > 0;
        DiagnosticsList.Visibility = hasDiagnostics ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsEmptyState.Visibility = hasDiagnostics ? Visibility.Collapsed : Visibility.Visible;
        UpdateDocumentInfo(source);
        ApplyDiagnosticUnderlines(source);
    }

    private void ReportInterpreterCheckIssue(string message, string status)
    {
        if (!string.Equals(_lastInterpreterIssue, message, StringComparison.Ordinal))
        {
            _lastInterpreterIssue = message;
            AppendOutput(message);
        }
        StatusText.Text = status;
    }

    private void ApplyDiagnosticUnderlines(string source)
    {
        string fingerprint = string.Join("\u001f", _lastDiagnostics.Select(d =>
            $"{d.Severity}:{d.Code}:{d.Line}:{d.Column}:{d.Message}"));
        if (string.Equals(source, _lastUnderlinedSource, StringComparison.Ordinal) &&
            string.Equals(fingerprint, _lastUnderlineFingerprint, StringComparison.Ordinal)) return;

        _applyingHighlight = true;
        try
        {
            foreach (var diagnostic in _lastDiagnostics)
            {
                if (!string.Equals(diagnostic.Severity, "error", StringComparison.OrdinalIgnoreCase) ||
                    diagnostic.Code.StartsWith("FLX-I", StringComparison.OrdinalIgnoreCase)) continue;
                var (start, end) = GetDiagnosticSpan(source, diagnostic);
                if (end <= start || start < 0 || end > source.Length) continue;
                var format = Editor.Document.GetRange(start, end).CharacterFormat;
                // RichEditBox uses the character foreground for the wave underline
                // color, so error spans use red text as well as a red wavy underline.
                format.ForegroundColor = Color.FromArgb(255, 238, 67, 67);
                format.Underline = UnderlineType.Wave;
            }
        }
        catch (ArgumentException)
        {
            // Ignore a stale range if an edit landed while a queued analysis was running.
        }
        finally
        {
            _applyingHighlight = false;
            _lastUnderlinedSource = source;
            _lastUnderlineFingerprint = fingerprint;
        }
    }

    private static (int Start, int End) GetDiagnosticSpan(string source, FluxDiagnostic diagnostic)
    {
        int lineStart = 0;
        for (int line = 1; line < Math.Max(1, diagnostic.Line) && lineStart < source.Length; line++)
        {
            int newline = source.IndexOf('\n', lineStart);
            lineStart = newline < 0 ? source.Length : newline + 1;
        }
        int newlineAt = source.IndexOf('\n', lineStart);
        int lineEnd = newlineAt < 0 ? source.Length : newlineAt;
        if (lineStart >= source.Length || lineEnd < lineStart) return (0, 0);

        string lineText = source[lineStart..lineEnd];
        int localStart = Math.Clamp(diagnostic.Column - 1, 0, lineText.Length);
        int localLength = 1;
        switch (diagnostic.Code)
        {
            case "FLX1015":
                localStart = Math.Max(0, lineText.IndexOf("end", StringComparison.Ordinal));
                localLength = Math.Min(3, lineText.Length - localStart);
                break;
            case "FLX1017": case "FLX1018": case "FLX1019": case "FLX1023": case "FLX1024":
                localStart = lineText.TakeWhile(char.IsWhiteSpace).Count();
                localLength = Math.Max(1, lineText.Length - localStart);
                break;
            case "FLX1021":
                int equals = lineText.LastIndexOf('=');
                if (equals >= 0) localStart = equals;
                localLength = 1;
                break;
            case "FLX1022":
                int asIndex = lineText.LastIndexOf("as", StringComparison.Ordinal);
                if (asIndex >= 0) localStart = asIndex;
                localLength = 2;
                break;
            default:
                if (localStart >= lineText.Length) localStart = Math.Max(0, lineText.Length - 1);
                if (localStart < lineText.Length && FluxLexer.IsIdentifierPart(lineText[localStart]))
                {
                    while (localStart > 0 && FluxLexer.IsIdentifierPart(lineText[localStart - 1])) localStart--;
                    int wordEnd = localStart;
                    while (wordEnd < lineText.Length && FluxLexer.IsIdentifierPart(lineText[wordEnd])) wordEnd++;
                    localLength = Math.Max(1, wordEnd - localStart);
                }
                break;
        }

        localStart = Math.Clamp(localStart, 0, Math.Max(0, lineText.Length - 1));
        localLength = Math.Clamp(localLength, 1, Math.Max(1, lineText.Length - localStart));
        int absoluteStart = lineStart + localStart;
        return (absoluteStart, Math.Min(lineEnd, absoluteStart + localLength));
    }

    private static bool DiagnosticsEqual(IReadOnlyList<FluxDiagnostic> left, IReadOnlyList<FluxDiagnostic> right)
    {
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            var a = left[i];
            var b = right[i];
            if (a.Severity != b.Severity || a.Code != b.Code || a.Line != b.Line ||
                a.Column != b.Column || a.Message != b.Message) return false;
        }
        return true;
    }

    private void DiagnosticsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not FluxDiagnostic diagnostic) return;
        string source = GetEditorText();
        int position = 0;
        int wantedLine = Math.Max(1, diagnostic.Line);
        for (int line = 1; line < wantedLine && position < source.Length; line++)
        {
            int newline = source.IndexOf('\n', position);
            position = newline < 0 ? source.Length : newline + 1;
        }
        position = Math.Min(source.Length, position + Math.Max(0, diagnostic.Column - 1));
        int end = Math.Min(source.Length, position + (position < source.Length ? 1 : 0));
        SetEditorSelection(position, end);
        Editor.Focus(FocusState.Programmatic);
    }

    private async void Find_Click(object sender, RoutedEventArgs e) => await FindDialogAsync();

    private async Task FindDialogAsync()
    {
        var box = new TextBox { PlaceholderText = "输入要查找的文本" };
        var dialog = new ContentDialog
        {
			Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
			Title = "查找文本",
            Content = box,
            PrimaryButtonText = "查找下一个",
            CloseButtonText = "取消",
            XamlRoot = RootGrid.XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrEmpty(box.Text)) return;
        string text = GetEditorText();
        int selectionEnd = GetSelection().End;
        int start = Math.Min(selectionEnd, text.Length);
        int index = text.IndexOf(box.Text, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = text.IndexOf(box.Text, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            Editor.Focus(FocusState.Programmatic);
            SetEditorSelection(index, index + box.Text.Length);
        }
        else StatusText.Text = "未找到匹配文本";
    }

    private void UpdateCompletion(bool force)
    {
        if (Editor is null || CompletionPopup is null) return;
        if (!_settings.EnableCompletion) { HideCompletion(); return; }
        if (force) _completionForced = true;
        bool showAll = force || _completionForced;

        string source = GetEditorText();
        var (_, selectionEnd) = GetSelection();
        int caret = Math.Clamp(selectionEnd, 0, source.Length);
        int prefixStart = caret;
        while (prefixStart > 0 && FluxLexer.IsIdentifierPart(source[prefixStart - 1])) prefixStart--;
        string prefix = source[prefixStart..caret];

        if (!showAll && (prefix.Length == 0 || IsInsideLiteralOrComment(source, caret)))
        {
            HideCompletion();
            return;
        }

        var candidates = new List<FluxCompletionItem>();
        foreach (var item in CompletionCatalog)
        {
            if (prefix.Length == 0 || item.FilterText.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                candidates.Add(item);
        }
        if (showAll && prefix.Length == 0)
        {
            // Put useful block/entry templates first for an empty Ctrl+Space request.
            candidates = candidates
                .OrderByDescending(item => item.Detail.Contains("模板", StringComparison.Ordinal))
                .ThenBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var lex = FluxLexer.Lex(source);
        var identifiers = lex.Tokens
            .Where(t => t.Kind == FluxTokenKind.Identifier)
            .Select(t => t.Text)
            .Distinct(StringComparer.Ordinal)
            .Where(name => prefix.Length == 0 || name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            // Do not suggest the word currently being typed as its own completion.
            .Where(name => prefix.Length == 0 || !string.Equals(name, prefix, StringComparison.OrdinalIgnoreCase))
            .Where(name => !candidates.Any(c => string.Equals(c.FilterText, name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Take(10);
        candidates.AddRange(identifiers.Select(name => new FluxCompletionItem
        {
            Label = name,
            FilterText = name,
            InsertText = name,
            Detail = "当前文件符号"
        }));

        // Keep exact matches visible (notably `end`) instead of hiding the popup
        // once the user has typed the complete keyword.
        if (candidates.Count == 0)
        {
            HideCompletion();
            return;
        }

        if (candidates.Count > 14) candidates = candidates.Take(14).ToList();
        string fingerprint = (showAll ? "all" : "prefix") + "\u001f" + prefix + "\u001e" +
            string.Join("\u001d", candidates.Select(item => item.Label + "\u001f" + item.FilterText + "\u001f" + item.InsertText + "\u001f" + item.Detail));

        // Selection/highlight events often arrive for the same prefix. Do not reset
        // ItemsSource or SelectedIndex unless the actual suggestions changed.
        if (!string.Equals(fingerprint, _completionFingerprint, StringComparison.Ordinal))
        {
            CompletionList.ItemsSource = candidates;
            CompletionList.SelectedIndex = candidates.Count > 0 ? 0 : -1;
            _completionFingerprint = fingerprint;
        }

        if (CompletionPopup.Visibility != Visibility.Visible)
            CompletionPopup.Visibility = Visibility.Visible;
    }

    private bool IsInsideLiteralOrComment(string source, int position)
    {
        var token = FluxLexer.Lex(source).Tokens.FirstOrDefault(t =>
            t.Start <= position && position < t.End &&
            t.Kind is FluxTokenKind.String or FluxTokenKind.Character or FluxTokenKind.Comment or FluxTokenKind.Preprocessor);
        return token is not null;
    }

    private void CompletionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FluxCompletionItem item) AcceptCompletion(item);
    }

    private void AcceptCompletion(FluxCompletionItem item)
    {
        string source = GetEditorText();
        var (selectionStart, selectionEnd) = GetSelection();
        int start = selectionStart;
        int end = selectionEnd;
        if (start == end)
        {
            while (start > 0 && FluxLexer.IsIdentifierPart(source[start - 1])) start--;
        }
        string insertText = item.InsertText.Replace("\n", "\r", StringComparison.Ordinal);
        Editor.Document.GetRange(start, end).Text = insertText;
        int caret = item.CaretOffset >= 0 ? start + item.CaretOffset : start + insertText.Length;
        caret = Math.Clamp(caret, start, start + insertText.Length);
        int selectEnd = Math.Clamp(caret + item.SelectionLength, caret, start + insertText.Length);
        SetEditorSelection(caret, selectEnd);
        HideCompletion();
        Editor.Focus(FocusState.Programmatic);
        ScheduleAnalysis();
    }

    private void HideCompletion()
    {
        if (CompletionPopup is not null && CompletionPopup.Visibility != Visibility.Collapsed)
            CompletionPopup.Visibility = Visibility.Collapsed;
        _completionForced = false;
        _completionFingerprint = null;
    }

    private static IReadOnlyList<FluxCompletionItem> CreateCompletionCatalog()
    {
        var items = new List<FluxCompletionItem>();
        foreach (string keyword in new[]
        {
            "var", "new", "auto", "as", "if", "then", "else", "for", "end", "break", "next", "return",
            "public", "private", "protected", "internal", "static", "abstract", "sealed", "override", "virtual",
            "class", "interface", "with", "is", "and", "or", "no", "extends", "implements", "init",
            "get", "set", "base", "this", "to", "report", "warning", "error", "throw", "try", "catch", "finally",
            "true", "false", "Null", "NoThing", "NoType", "NaN", "Infinity"
        }) items.Add(Word(keyword, "Flux 关键字"));

        foreach (string type in new[] { "int", "float", "double", "bool", "string", "char", "void" })
            items.Add(Word(type, "内置类型"));
        foreach (string function in new[] { "print", "printf", "printfn", "read", "pause" })
            items.Add(Word(function, "内置函数"));
        foreach (string directive in new[] { "$replace", "$open", "$copy" })
            items.Add(Word(directive, "预处理指令"));

        items.Add(new FluxCompletionItem
        {
            Label = "if … then … end",
            FilterText = "if",
            Detail = "条件代码块模板",
            InsertText = "if condition then\n    \nend",
            CaretOffset = 3,
            SelectionLength = "condition".Length
        });
        items.Add(new FluxCompletionItem
        {
            Label = "for … then … end",
            FilterText = "for",
            Detail = "条件循环模板",
            InsertText = "for condition then\n    \nend",
            CaretOffset = 4,
            SelectionLength = "condition".Length
        });
        items.Add(new FluxCompletionItem
        {
            Label = "Main 方法",
            FilterText = "main",
            Detail = "程序入口模板",
            InsertText = "public int Main :\n    \n    return 0\nend",
            CaretOffset = "public int ".Length,
            SelectionLength = "Main".Length
        });
        items.Add(new FluxCompletionItem
        {
            Label = "方法声明",
            FilterText = "method",
            Detail = "方法代码块模板",
            InsertText = "public void MethodName :\n    \nend",
            CaretOffset = "public void ".Length,
            SelectionLength = "MethodName".Length
        });
        items.Add(new FluxCompletionItem
        {
            Label = "var new …",
            FilterText = "var",
            Detail = "新变量声明模板",
            InsertText = "var new name = value as int",
            CaretOffset = "var new ".Length,
            SelectionLength = "name".Length
        });
        return items;
    }

    private static FluxCompletionItem Word(string word, string detail)
        => new() { Label = word, FilterText = word, InsertText = word, Detail = detail };

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_settings, this);
        bool saved = await dialog.ShowDialog();
        if (!saved || dialog.SavedSettings is null) return;

        var previous = _settings;
        var updated = dialog.SavedSettings;
        bool themeChanged = !string.Equals(previous.Theme, updated.Theme, StringComparison.Ordinal);
        bool fontChanged = !string.Equals(previous.EditorFontFamily, updated.EditorFontFamily, StringComparison.Ordinal) ||
                           Math.Abs(previous.EditorFontSize - updated.EditorFontSize) >= 0.001;
        bool completionChanged = previous.EnableCompletion != updated.EnableCompletion;
        bool interpreterChanged = !string.Equals(previous.InterpreterPath, updated.InterpreterPath, StringComparison.Ordinal) ||
                                  !string.Equals(previous.WorkingDirectory, updated.WorkingDirectory, StringComparison.Ordinal);

        _settings = updated;
        _settings.Save();
        _theme = _settings.Theme;

        // Apply only the settings that actually changed. In particular, changing
        // the indent width must not recolor the entire RichEditBox or run diagnostics;
        // doing that on a large document can make the UI look frozen.
        if (fontChanged) ApplyEditorPreferences();
        if (themeChanged) ApplyTheme();
        if (completionChanged)
        {
            if (!_settings.EnableCompletion) HideCompletion();
            else UpdateCompletion(force: false);
        }

        StatusText.Text = "设置已保存";
        if (interpreterChanged)
        {
            _lastInterpreterIssue = null;
            _ = CheckRuntimeDiagnosticsAsync();
        }
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunFluxAsync();

    private async Task RunFluxAsync()
    {
        if (_runInProgress)
        {
            BottomTabs.SelectedItem = OutputTab;
            AppendOutput("Flux 运行任务仍在执行中。请等待当前任务结束。");
            return;
        }

        // A named, already-saved file is saved back to its existing path. Running an
        // untitled buffer must not force a Save As dialog: create a short-lived .flux
        // snapshot for the interpreter and leave the editor's title/path/dirty state
        // unchanged. This is also useful for quick experiments before choosing a file.
        if (_filePath is not null && _dirty)
        {
            if (!await SaveAsync())
            {
                StatusText.Text = "运行已取消：源文件保存失败或已取消";
                return;
            }
        }

        _runInProgress = true;
        BottomTabs.SelectedItem = OutputTab;
        AppendOutput("──────────────── Flux 运行开始 ────────────────");
        StatusText.Text = "正在运行 Flux…";
        Process? process = null;
        string? temporaryRunFile = null;
        try
        {
            string defaultDirectory;
            string filePath;
            if (!string.IsNullOrWhiteSpace(_filePath))
            {
                filePath = Path.GetFullPath(_filePath);
                defaultDirectory = Path.GetDirectoryName(filePath)!;
                if (!string.IsNullOrWhiteSpace(_projectRoot) && Directory.Exists(_projectRoot) && IsWithinDirectory(filePath, _projectRoot))
                    defaultDirectory = _projectRoot;
            }
            else
            {
                // Run an unsaved editor buffer without changing the document's path or
                // bringing up Save As. Keep the runtime's working directory independent
                // from the temporary snapshot directory so relative file operations work.
                string tempRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FluxStudio", "TempRuns");
                Directory.CreateDirectory(tempRoot);
                temporaryRunFile = Path.Combine(tempRoot, $"Untitled-{Guid.NewGuid():N}.flux");
                await File.WriteAllTextAsync(temporaryRunFile, GetEditorText(), new UTF8Encoding(false));
                filePath = Path.GetFullPath(temporaryRunFile);
                defaultDirectory = !string.IsNullOrWhiteSpace(_projectRoot) && Directory.Exists(_projectRoot)
                    ? Path.GetFullPath(_projectRoot)
                    : Environment.CurrentDirectory;
            }

            string workingDirectory = string.IsNullOrWhiteSpace(_settings.WorkingDirectory)
                ? defaultDirectory
                : Environment.ExpandEnvironmentVariables(_settings.WorkingDirectory.Trim().Trim('"'));
            if (!Path.IsPathFullyQualified(workingDirectory))
                workingDirectory = Path.GetFullPath(Path.Combine(defaultDirectory, workingDirectory));
            if (!Directory.Exists(workingDirectory))
                throw new DirectoryNotFoundException("工作目录不存在：" + workingDirectory);

            string executable = ResolveRuntimeExecutable(_settings.InterpreterPath);
            if (string.IsNullOrWhiteSpace(executable))
            {
                AppendOutput("请先在设置中指定 Flux.Core 解释器路径。");
                StatusText.Text = "运行失败：未配置解释器";
                return;
            }
            bool isPath = Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar);
            if (isPath && !File.Exists(executable))
            {
                AppendOutput("找不到 Flux 解释器：" + executable);
                AppendOutput("Flux.Core 是独立的跨平台解释器，基于C++ 17");
                StatusText.Text = "运行失败：未找到解释器";
                return;
            }

            // The native runtime has a stable CLI contract; settings do not expose arbitrary arguments.
            string args = QuoteArgument(filePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            };
            // The native interpreter uses a private record-separator protocol on stdout
            // to ask the IDE for read/pause input. Regular program output remains plain text.
            startInfo.Environment["FLUX_STUDIO_INPUT_PROTOCOL"] = "1";
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            AppendOutput($"运行命令：{executable} {args}");
            AppendOutput("工作目录：" + workingDirectory);
            if (!process.Start()) throw new InvalidOperationException("Windows 未能启动 Flux 解释器。");
            Task stdoutPump = PumpRuntimeOutputAsync(process);
            Task stderrPump = PumpRuntimeErrorAsync(process);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdoutPump, stderrPump);
            int exitCode = process.ExitCode;
            AppendOutput($"[Flux] 运行结束，退出代码：{exitCode}");
            StatusText.Text = exitCode == 0 ? "运行完成" : $"运行失败（退出代码 {exitCode}）";
        }
        catch (Exception ex)
        {
            AppendOutput("运行失败：" + ex.Message);
            StatusText.Text = "运行失败";
        }
        finally
        {
            process?.Dispose();
            _runInProgress = false;
            if (temporaryRunFile is not null)
            {
                try { if (File.Exists(temporaryRunFile)) File.Delete(temporaryRunFile); }
                catch { /* Temporary run snapshots are best-effort cleanup. */ }
            }
            // Running switches to Output once and intentionally stays there after exit.
            UpdateDocumentInfo();
        }
    }

    private async Task PumpRuntimeOutputAsync(Process process)
    {
        const string inputMarker = "\u001eFLUX_READ:";
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            if (line.StartsWith(inputMarker, StringComparison.Ordinal))
            {
                string requestedType = line[inputMarker.Length..].Trim();
                await PromptForFluxInputAsync(process, requestedType);
                continue;
            }
            AppendOutput(line);
        }
    }

    private async Task PumpRuntimeErrorAsync(Process process)
    {
        while (await process.StandardError.ReadLineAsync() is { } line)
            AppendOutput("[stderr] " + line);
    }

    private async Task PromptForFluxInputAsync(Process process, string requestedType)
    {
        bool isPause = requestedType.Equals("pause", StringComparison.OrdinalIgnoreCase);
        var inputBox = new TextBox
        {
            MinWidth = 340,
            PlaceholderText = isPause ? "点击继续恢复执行" : $"输入 {requestedType} 值",
            IsEnabled = !isPause
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = isPause ? "Flux 程序已暂停。点击“继续”后将恢复执行。" : $"Flux 程序正在等待输入，目标类型：{requestedType}。",
            TextWrapping = TextWrapping.Wrap
        });
        if (!isPause) content.Children.Add(inputBox);

        var dialog = new ContentDialog
        {
			Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
			Title = isPause ? "Flux 程序暂停" : "Flux 程序输入",
            Content = content,
            PrimaryButtonText = isPause ? "继续" : "提交",
            CloseButtonText = isPause ? "终止输入" : "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme
        };
        ContentDialogResult result = await dialog.ShowAsync();
        // Cancellation sends an empty line so the native process never remains blocked.
        await process.StandardInput.WriteLineAsync(result == ContentDialogResult.Primary ? inputBox.Text : string.Empty);
        await process.StandardInput.FlushAsync();
    }

    private string ResolveRuntimeExecutable(string configuredPath)
    {
        // Respect the interpreter path selected in Settings. Relative paths are
        // relative to the app's base directory; a bare command name is resolved by PATH.
        string configured = Environment.ExpandEnvironmentVariables((configuredPath ?? string.Empty).Trim().Trim('"'));
        if (string.IsNullOrWhiteSpace(configured)) return string.Empty;
        if (Path.IsPathFullyQualified(configured)) return Path.GetFullPath(configured);
        if (configured.Contains(Path.DirectorySeparatorChar) || configured.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configured));
        return configured;
    }

    private static bool IsWithinDirectory(string filePath, string directory)
    {
        string fullFile = Path.GetFullPath(filePath);
        string fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(fullDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string QuoteArgument(string value)
        => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private void ThemeSystem_Click(object sender, RoutedEventArgs e) => SetTheme("System");
    private void ThemeLight_Click(object sender, RoutedEventArgs e) => SetTheme("Light");
    private void ThemeDark_Click(object sender, RoutedEventArgs e) => SetTheme("Dark");

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        // System theme changes also need to recolor RichEditBox document runs. WinUI
        // theme resources update automatically, but RichEditBox character formatting
        // is stored on the document and must be reapplied by the language highlighter.
        _syntaxColorsDirty = true;
        _lastHighlightedText = null;
        _lastUnderlinedSource = null;
        _lastUnderlineFingerprint = null;
        ScheduleAnalysis();
    }

    private void SetTheme(string theme)
    {
        _theme = theme;
        _settings.Theme = theme;
        _settings.Save();
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        _theme = _settings.Theme;
        RootGrid.RequestedTheme = _theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        // Explicitly keep the native text controls aligned with the selected theme.
        // RichEditBox stores token colors in its document, so recolor the full document
        // after the theme resource change has settled.
        Editor.RequestedTheme = RootGrid.RequestedTheme;
        OutputBox.RequestedTheme = RootGrid.RequestedTheme;
        DiagnosticsList.RequestedTheme = RootGrid.RequestedTheme;
        CompletionList.RequestedTheme = RootGrid.RequestedTheme;
        StatusText.Text = "主题：" + _theme;
        _syntaxColorsDirty = true;
        _lastHighlightedText = null;
        _lastUnderlinedSource = null;
        _lastUnderlineFingerprint = null;
        ScheduleAnalysis();
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
			Style = Application.Current.Resources["DefaultContentDialogStyle"] as Style,
			Title = "关于 Flux Studio",
            Content = "Flux Studio - WinUI\n由 MsintX 主导开发\n语言规范：https://github.com/MsintX/Flux",
            CloseButtonText = "确定",
            XamlRoot = RootGrid.XamlRoot
        };
        await dialog.ShowAsync();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "确定", XamlRoot = RootGrid.XamlRoot };
        await dialog.ShowAsync();
    }

    private void AppendOutput(string line) => OutputBox.Text += $"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}";
}
