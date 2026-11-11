using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace SwitchMonitor.App;

/// <summary>
/// The language of the app (English, Spanish). The windows are written in English; in Spanish each fixed text is looked up in
/// <see cref="LocSpanish"/> when its element loads, and all the open windows are walked again when the language changes.
/// Text that comes from the program while it works (bound texts) is not touched, except where the code calls <see cref="T"/>.
/// </summary>
public static class Loc
{
    private static string language = "en";
    private static bool registered;
    private static readonly ConditionalWeakTable<DependencyObject, Dictionary<string, string>> Originals = new();

    public static string Language => language;

    /// <summary>"es" when Windows is in Spanish, else "en": the language when none was chosen.</summary>
    public static string FromWindows() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es" ? "es" : "en";

    /// <summary>Starts watching the elements that load, and sets the language (before the first window is made).</summary>
    public static void Start(string? chosen)
    {
        language = chosen is "en" or "es" ? chosen : FromWindows();
        if (registered) return;
        registered = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (language == "es" && sender is DependencyObject element) Translate(element);
        }));
    }

    /// <summary>Changes the language and rewrites every open window.</summary>
    public static void Set(string code)
    {
        language = code == "es" ? "es" : "en";
        foreach (Window window in Application.Current.Windows) Walk(window, new HashSet<DependencyObject>());
        RefreshFollowed();
    }

    /// <summary>The text in the current language (the same text when there is no translation).</summary>
    public static string T(string english) => language == "es" ? Lookup(english) ?? english : english;

    /// <summary>The Spanish of a text (whole text, or a text with a changing part), or null when there is none.</summary>
    private static string? Lookup(string english)
    {
        if (LocSpanish.Texts.TryGetValue(english, out var spanish) || LocSpanishMore.Texts.TryGetValue(english, out spanish)) return spanish;
        return LocSpanishMore.Match(english);
    }

    private static void Walk(DependencyObject element, HashSet<DependencyObject> seen)
    {
        if (!seen.Add(element)) return;
        Translate(element);
        foreach (object child in LogicalTreeHelper.GetChildren(element))
            if (child is DependencyObject logical) Walk(logical, seen);
        if (element is Visual or System.Windows.Media.Media3D.Visual3D)
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++) Walk(VisualTreeHelper.GetChild(element, i), seen);
    }

    /// <summary>One element's own fixed texts: its text, content, tool tip, title.</summary>
    private static void Translate(DependencyObject element)
    {
        switch (element)
        {
            case TextBlock block:
                if (block.Inlines.Count > 1 || block.Inlines.FirstInline is not (null or Run))
                {
                    foreach (var inline in block.Inlines)
                        if (inline is Run run && !BindingOperations.IsDataBound(run, Run.TextProperty)) Fix(run, "Text", run.Text, v => run.Text = v);
                }
                else if (!BindingOperations.IsDataBound(block, TextBlock.TextProperty)) Fix(block, "Text", block.Text, v => block.Text = v);
                else Follow(block);
                break;
            case ContentControl { Content: string text } control when !BindingOperations.IsDataBound(control, ContentControl.ContentProperty):
                Fix(control, "Content", text, v => control.Content = v);
                break;
            case ContentControl bound when BindingOperations.IsDataBound(bound, ContentControl.ContentProperty):
                Follow(bound);
                break;
            case Window { Title: { Length: > 0 } title } window when !BindingOperations.IsDataBound(window, Window.TitleProperty):
                Fix(window, "Title", title, v => window.Title = v);
                break;
        }
        if (element is FrameworkElement { ToolTip: string tip } shown && !BindingOperations.IsDataBound(shown, FrameworkElement.ToolTipProperty))
            Fix(shown, "ToolTip", tip, v => shown.ToolTip = v);
    }

    // ---- Texts that come from the program while it works (bound texts) ----

    private static readonly List<WeakReference<DependencyObject>> followed = [];
    private static readonly DependencyProperty FollowedProperty = DependencyProperty.RegisterAttached(
        "Followed", typeof(bool), typeof(Loc), new PropertyMetadata(false));

    /// <summary>
    /// A bound text: each time the binding brings a new text, the Spanish one is shown in its place (the binding stays, and
    /// its next text is looked up again). When the language goes back to English the binding is read again.
    /// </summary>
    private static void Follow(DependencyObject element)
    {
        if ((bool)element.GetValue(FollowedProperty)) { Rewrite(element); return; }
        element.SetValue(FollowedProperty, true);
        var property = element is TextBlock ? TextBlock.TextProperty : ContentControl.ContentProperty;
        DependencyPropertyDescriptor.FromProperty(property, element.GetType())?.AddValueChanged(element, (s, _) => { if (s is DependencyObject d) Rewrite(d); });
        lock (followed) followed.Add(new WeakReference<DependencyObject>(element));
        Rewrite(element);
    }

    private static bool rewriting;

    private static void Rewrite(DependencyObject element)
    {
        if (rewriting || language != "es") return;
        rewriting = true;
        try
        {
            if (element is TextBlock block && block.Text is { Length: > 0 } text && Lookup(text) is { } spanish && spanish != text)
                block.SetCurrentValue(TextBlock.TextProperty, spanish);
            else if (element is ContentControl { Content: string content } control && Lookup(content) is { } spanishContent && spanishContent != content)
                control.SetCurrentValue(ContentControl.ContentProperty, spanishContent);
        }
        finally { rewriting = false; }
    }

    /// <summary>The language changed: bound texts are read from their sources again (English) or looked up again (Spanish).</summary>
    private static void RefreshFollowed()
    {
        List<DependencyObject> all = [];
        lock (followed)
        {
            followed.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in followed) if (w.TryGetTarget(out var d)) all.Add(d);
        }
        foreach (var element in all)
        {
            var property = element is TextBlock ? TextBlock.TextProperty : ContentControl.ContentProperty;
            if (language == "es") Rewrite(element);
            else BindingOperations.GetBindingExpression(element, property)?.UpdateTarget();
        }
    }

    /// <summary>
    /// Remembers the English text of a property the first time it is seen, and sets the text of the current language from it.
    /// A text the program changed in between (not the English one nor its translation) is taken as a new English text.
    /// </summary>
    private static void Fix(DependencyObject owner, string key, string current, Action<string> set)
    {
        if (string.IsNullOrEmpty(current)) return;
        var map = Originals.GetOrCreateValue(owner);
        string english = current;
        if (map.TryGetValue(key, out var known) &&
            (current == known || Lookup(known) == current))
            english = known;
        else map[key] = current;
        string want = T(english);
        if (want != current) set(want);
    }
}
