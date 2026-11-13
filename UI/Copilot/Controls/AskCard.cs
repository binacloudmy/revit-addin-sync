#nullable enable
// The question card ("decision card", card spec of 12 Nov 2026 and its approved mockup). Every question the pane
// receives is drawn here: the backend's sheet questions (title, ready items, grouped fields with create/skip
// switches and plan tiles, "What you will get", "Make N sheets") and every question the model asks itself (no
// title or groups: the same look with a default heading and a "Continue" button).
//
// All the logic is in the model (AskCardModel + AskCardDecision.cs, Revit-free and tested); this file only draws
// and wires. The drafter's picks live on the model, so a redraw of the thread keeps them; the card updates in
// place (no rebuild), so focus and typed text stay.
//
// Kept from the card before: AskCardModel has already dropped approval-like options and credential fields; a
// question that asks for a credential gets no answer box, only "Don't answer"; a project note shows the fact and
// the pane's own "Save note" / "Don't save"; nothing is saved otherwise. English only for what this card writes.
//
// Keyboard, once the card has focus (click it): 1..9 pick the Nth choice of the active field (or option),
// C create, S skip, Up/Down move between fields, Enter sends when complete. A new waiting card takes the focus by
// itself only from this pane's EMPTY message box (AskCardFocus); while it holds that focus, a typed character it does
// not use goes back to the message box.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using BinaVibe.Copilot.Contracts;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    public static class AskCard
    {
        private const string TickIcon = "M3.5,8.5 L6.5,11.5 L12.5,4.5";
        private const string PlusIcon = "M8,3.5 V12.5 M3.5,8 H12.5";
        private const string SearchIcon = "M7,2.5 A4.5,4.5 0 1 1 7,11.5 A4.5,4.5 0 1 1 7,2.5 Z M10.4,10.4 L13.5,13.5";

        /// <param name="submit">The answer plus a short summary for the collapsed record.</param>
        /// <param name="messageBox">This pane's message box, for the focus on arrival (AskCardFocus); null: never.</param>
        public static FrameworkElement Build(AskCardModel m, bool live, Action<AnswerDto, string> submit, Func<TextBox?>? messageBox = null)
        {
            if (!live || m.AnsweredSummary != null) return Collapsed(m);
            return new LiveCard(m, submit, messageBox).Root;
        }

        // ─── After answering: "Your answers" ──────────────────────────────────────────────────────────────

        private static FrameworkElement Collapsed(AskCardModel m)
        {
            var outer = CardKit.Card("Cp.Line", "Cp.Bg");
            var sp = new StackPanel { Margin = new Thickness(14, 11, 14, 11) };
            sp.Children.Add(Kicker(AskCardModel.AnsweredHeading, new Thickness(0, 0, 0, 6)));
            var lines = m.AnsweredLines;
            if (lines != null && lines.Count > 0 && m.AnsweredSummary != null)
            {
                foreach (var l in lines) sp.Children.Add(AnsweredRow(l.Label, l.Pick));
            }
            else
            {
                // Answered by a typed message (or no longer the live question): the question, then what was said.
                var q = CardKit.Text(Clip(m.Question, 240), 12, "Cp.Muted", margin: new Thickness(0, 0, 0, 4));
                if (m.Question.Length > 240) q.ToolTip = m.Question;
                sp.Children.Add(q);
                sp.Children.Add(AnsweredRow("", m.AnsweredSummary ?? "answered"));
            }
            outer.Child = sp;
            return outer;
        }

        private static FrameworkElement AnsweredRow(string label, string pick)
        {
            var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var tick = Icon(TickIcon, "Cp.Green", 2, 13);
            tick.Margin = new Thickness(0, 2, 8, 0);
            tick.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(tick);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            if (!string.IsNullOrEmpty(label))
            {
                text.Inlines.Add(Ink(label, "Cp.Ink", FontWeights.SemiBold));
                text.Inlines.Add(new Run("  "));
            }
            text.Inlines.Add(Ink(pick, string.IsNullOrEmpty(label) ? "Cp.Ink" : "Cp.Muted", FontWeights.Normal));
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            return row;
        }

        // ─── The live card ────────────────────────────────────────────────────────────────────────────────

        private sealed class LiveCard
        {
            private readonly AskCardModel _m;
            private readonly Action<AnswerDto, string> _submit;
            private readonly List<Action> _refresh = new List<Action>();
            /// <summary>Opens a folded group when the keyboard moves onto one of its hidden rows.</summary>
            private readonly List<Action<AskFieldModel>> _reveal = new List<Action<AskFieldModel>>();
            private readonly Dictionary<string, Func<IReadOnlyList<AskOptionModel>>> _visible = new Dictionary<string, Func<IReadOnlyList<AskOptionModel>>>();
            /// <summary>Each field's frame, to show the keyboard's row when a key moves it off-screen.</summary>
            private readonly Dictionary<AskFieldModel, FrameworkElement> _frames = new Dictionary<AskFieldModel, FrameworkElement>();
            private readonly Border _outer;
            private Button? _footer;
            private bool _sent;
            private readonly TextBox _sink;
            /// <summary>The focus came from the keyboard (Tab, or a key the card used): the active field shows its outline.
            /// A mouse click clears it, so a click never leaves a focus ring.</summary>
            private bool _keyboardMode;
            private bool _focusByMouse;
            private readonly Func<TextBox?>? _messageBox;
            /// <summary>Set while the sink holds the focus it took from the empty message box on arrival: a typed
            /// character the card does not use goes back there. Cleared when the sink loses the focus or on a click.</summary>
            private TextBox? _handBackTo;

            public FrameworkElement Root => _outer;

            public LiveCard(AskCardModel m, Action<AnswerDto, string> submit, Func<TextBox?>? messageBox)
            {
                _m = m;
                _submit = submit;
                _messageBox = messageBox;
                _outer = CardKit.Card("Cp.Line", "Cp.Bg");
                var sp = new StackPanel();
                _outer.Child = sp;
                // The sink first, so Tab into the card lands on it; a zero-size canvas keeps it out of the layout.
                _sink = MakeSink();
                var hold = new Canvas { Width = 0, Height = 0, ClipToBounds = false };
                hold.Children.Add(_sink);
                // Focusing the sink (top of the card) must not scroll the pane up to it when the drafter clicks lower down:
                // its bring-into-view requests stop here (and FocusSink puts the scroll position back in any case).
                _sink.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => e.Handled = true), true);
                hold.RequestBringIntoView += (_, e) => e.Handled = true;
                sp.Children.Add(hold);

                sp.Children.Add(Header());
                if (m.Ready.Count > 0 && (m.Kind == AskCardKind.Fields || m.Kind == AskCardKind.Options)) sp.Children.Add(ReadyPills());

                switch (m.Kind)
                {
                    case AskCardKind.Credential: sp.Children.Add(CredentialBody()); break;
                    case AskCardKind.ProjectNote: sp.Children.Add(ProjectNoteBody()); break;
                    case AskCardKind.Options: sp.Children.Add(OptionsBody()); break;
                    case AskCardKind.Fields:
                        foreach (var g in m.Groups()) sp.Children.Add(GroupBlock(g));
                        if (m.ShowsPreview) sp.Children.Add(Preview());
                        break;
                    case AskCardKind.FreeText: sp.Children.Add(FreeTextBody()); break;
                }

                if (m.Kind == AskCardKind.Fields || m.Kind == AskCardKind.FreeText || (m.Kind == AskCardKind.Options && m.Multi))
                    sp.Children.Add(Footer());

                if (m.SomethingWasDropped)
                    sp.Children.Add(CardKit.Text("Some choices were hidden because they looked like approval, and BINA never asks for passwords or tokens here.",
                        10.5, "Cp.Faint", margin: new Thickness(14, 0, 14, 12)));

                _outer.PreviewKeyDown += OnKey;
                _outer.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnCardMouseDown), true);
                _outer.AddHandler(UIElement.MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnCardMouseDownBubble), false);
                _outer.IsKeyboardFocusWithinChanged += (_, __) => Refresh();
                Refresh();

                // A new waiting card may take the focus from the empty message box once it is on screen.
                if (messageBox != null && AskCardFocus.IsWaiting(m, live: true))
                {
                    RoutedEventHandler? onLoaded = null;
                    onLoaded = (_, __) =>
                    {
                        _outer.Loaded -= onLoaded;
                        // ContextIdle: after layout, after the pane's own scroll to the new message (Loaded priority), and
                        // after any key the drafter pressed meanwhile (Input priority), which then went to the message box.
                        _outer.Dispatcher.BeginInvoke(new Action(TakeFocusOnArrival), System.Windows.Threading.DispatcherPriority.ContextIdle);
                    };
                    _outer.Loaded += onLoaded;
                }
            }

            /// <summary>The one chance a new card has to take the focus (live round 6: the drafter pressed Enter in the
            /// message box, the card came, and S or 1 went into the message box). Only from this pane's message box, only
            /// while it is empty; never from the Revit canvas or anywhere else. The offer is used up here, so a later
            /// redraw of the card never takes the focus. No mouse was involved, so the keyboard outline shows; the
            /// pane's scroll position is kept (FocusSink).</summary>
            private void TakeFocusOnArrival()
            {
                try
                {
                    if (_sent || !_outer.IsLoaded || !_outer.IsVisible || !AskCardFocus.IsWaiting(_m, live: true)) return;
                    _m.FocusOffered = true;
                    var box = _messageBox?.Invoke();
                    if (!AskCardFocus.ShouldTakeFocus(FocusPlaceNow(box), box?.Text, cardWaiting: true)) return;
                    FocusSink(byMouse: false);
                    if (_sink.IsKeyboardFocused) _handBackTo = box;
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] ask card focus on arrival: " + ex.Message); }
            }

            /// <summary>Where the keyboard focus is now. WPF has no keyboard focus at all while another window (Revit's
            /// canvas) has the Win32 focus, so that reads as outside the pane.</summary>
            private AskFocusPlace FocusPlaceNow(TextBox? box)
            {
                if (box != null && box.IsKeyboardFocused) return AskFocusPlace.MessageBox;
                if (!(Keyboard.FocusedElement is DependencyObject f)) return AskFocusPlace.OutsidePane;
                var here = PresentationSource.FromDependencyObject(_outer);
                return here != null && ReferenceEquals(PresentationSource.FromDependencyObject(f), here)
                    ? AskFocusPlace.ElsewhereInPane : AskFocusPlace.OutsidePane;
            }

            /// <summary>Moves the focus back to the message box and types the character there, through WPF's own text
            /// input (so the box's caret, its "@" and "/" pickers and undo behave as if typed). False: nothing moved.</summary>
            private bool HandBack(string text)
            {
                var box = _handBackTo;
                _handBackTo = null;
                if (box == null || !box.IsLoaded || !box.IsVisible || !box.IsEnabled || box.IsReadOnly) return false;
                box.Focus();
                if (!box.IsKeyboardFocused) return false;
                TextCompositionManager.StartComposition(new TextComposition(InputManager.Current, box, text));
                return true;
            }

            private void Refresh()
            {
                foreach (var r in _refresh)
                {
                    try { r(); }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] ask card refresh: " + ex.Message); }
                }
            }

            // ─── Header, ready pills ──────────────────────────────────────────────────────────────────────

            private FrameworkElement Header()
            {
                var grid = new Grid { Margin = new Thickness(14, 13, 14, 11) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                // The heading is the frame's title, else the question itself (no filler heading). Everything starts
                // at the same left edge as the labels below.
                var left = new StackPanel();
                if (_m.HeaderTitle.Length > 0) left.Children.Add(CardKit.Text(_m.HeaderTitle, 14, "Cp.Ink", FontWeights.SemiBold));
                var body = _m.HeaderBody;
                if (body != null)
                {
                    if (_m.Title == null && AskCardModel.NeedsMarkdown(body))
                    {
                        // The rest of a model-written question with a list or table: markdown, as before. The renderer's
                        // text box insets its text by about 2 px; the negative margin puts it on the card's edge.
                        var q = CopilotMessageBubble.MarkdownText(body, 460);
                        q.Margin = new Thickness(-2, 4, 0, 0);
                        left.Children.Add(q);
                    }
                    else left.Children.Add(CardKit.Text(body, 12, "Cp.Muted", margin: new Thickness(0, _m.HeaderTitle.Length > 0 ? 2 : 0, 0, 0)));
                }
                grid.Children.Add(left);

                var progress = _m.Kind == AskCardKind.Fields ? _m.ProgressFields.Count : 0;
                if (progress >= 2 && _m.ShowsProgressCount)
                {
                    // Many fields: "3 / 12" keeps the title's width (a row of 12 segments squeezed it).
                    var count = CardKit.Text("", 11.5, "Cp.Muted", FontWeights.Medium, wrap: false, mono: true);
                    count.VerticalAlignment = VerticalAlignment.Top;
                    count.Margin = new Thickness(12, 2, 0, 0);
                    _refresh.Add(() => { count.Text = _m.ProgressText; count.ToolTip = _m.ProgressText.Replace(" / ", " of ") + " answered"; });
                    Grid.SetColumn(count, 1);
                    grid.Children.Add(count);
                }
                else if (progress >= 2)
                {
                    var segs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(12, 7, 0, 0) };
                    var bars = new List<Border>();
                    for (var i = 0; i < progress; i++)
                    {
                        var b = new Border { Width = 14, Height = 4, CornerRadius = new CornerRadius(2), Margin = new Thickness(i == 0 ? 0 : 3, 0, 0, 0) };
                        bars.Add(b);
                        segs.Children.Add(b);
                    }
                    _refresh.Add(() =>
                    {
                        var on = _m.Segments;
                        for (var i = 0; i < bars.Count && i < on.Count; i++)
                            bars[i].SetResourceReference(Border.BackgroundProperty, on[i] ? "Cp.Accent" : "Cp.Hair2");
                        segs.ToolTip = on.Count(x => x) + " of " + on.Count + " answered";
                    });
                    Grid.SetColumn(segs, 1);
                    grid.Children.Add(segs);
                }
                return grid;
            }

            private FrameworkElement ReadyPills()
            {
                var wrap = new WrapPanel { Margin = new Thickness(14, 0, 14, 10) };
                var kick = CardKit.Text(AskCardModel.ReadyHeading, 11, "Cp.Faint", wrap: false);
                kick.VerticalAlignment = VerticalAlignment.Center;
                kick.Margin = new Thickness(0, 0, 6, 4);
                wrap.Children.Add(kick);
                var hidden = _m.HiddenReady;
                var extra = new List<FrameworkElement>();
                for (var i = 0; i < _m.Ready.Count; i++)
                {
                    var r = _m.Ready[i];
                    var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 2, 8, 2), Margin = new Thickness(0, 0, 6, 4) };
                    pill.SetResourceReference(Border.BackgroundProperty, "Cp.Hover");
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    var tick = Icon(TickIcon, "Cp.Green", 2, 11);
                    tick.Margin = new Thickness(0, 0, 4, 0);
                    tick.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(tick);
                    var t = CardKit.Text(r.Label, 11.5, "Cp.Muted");
                    t.VerticalAlignment = VerticalAlignment.Center;
                    row.Children.Add(t);
                    pill.Child = row;
                    if (r.Detail != null) pill.ToolTip = r.Detail;
                    if (hidden > 0 && i >= AskCardModel.ReadyPreview) extra.Add(pill);
                    wrap.Children.Add(pill);
                }
                if (hidden > 0)
                {
                    // "+12 more": a pill that opens the rest in place; once open it reads "Show fewer" and folds them back.
                    var label = CardKit.Text("", 11.5, "Cp.Muted", FontWeights.Medium, wrap: false);
                    var more = Pressable(label, 10);
                    more.Padding = new Thickness(8, 2, 8, 2);
                    more.Margin = new Thickness(0, 0, 6, 4);
                    more.BorderThickness = new Thickness(1);
                    more.SetResourceReference(Control.BorderBrushProperty, "Cp.Hair2");
                    more.HorizontalContentAlignment = HorizontalAlignment.Center;
                    var names = string.Join(", ", _m.Ready.Skip(AskCardModel.ReadyPreview).Select(x => x.Label));
                    var open = false;
                    void Show()
                    {
                        var visible = _m.VisibleReady(open);
                        for (var k = 0; k < extra.Count; k++)
                            extra[k].Visibility = AskCardModel.ReadyPreview + k < visible ? Visibility.Visible : Visibility.Collapsed;
                        label.Text = AskCardModel.ReadyToggleLabel(hidden, open);
                        more.ToolTip = open ? null : names;
                    }
                    more.Click += (_, __) => { open = !open; Show(); };
                    Show();
                    wrap.Children.Add(more);
                }
                return wrap;
            }

            // ─── Credential, project note, options, free text ─────────────────────────────────────────────

            private FrameworkElement CredentialBody()
            {
                // Never an answer box for a password or token (review M2).
                var body = new StackPanel { Margin = new Thickness(14, 0, 14, 8) };
                body.Children.Add(CardKit.Text(_m.CredentialNote, 11.5, "Cp.AmberFg", margin: new Thickness(0, 0, 0, 8)));
                body.Children.Add(NoFocus(CardKit.Secondary(_m.DeclineLabel, () => Send(_m.AnswerDeclined(), _m.DeclineLabel, Line("", _m.DeclineLabel)))));
                return body;
            }

            private FrameworkElement ProjectNoteBody()
            {
                var body = new StackPanel { Margin = new Thickness(14, 0, 14, 8) };
                var note = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 10), BorderThickness = new Thickness(1) };
                note.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                note.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var ns = new StackPanel();
                ns.Children.Add(CardKit.Text("Project note to save", 10.5, "Cp.Faint", FontWeights.SemiBold));
                ns.Children.Add(CardKit.Text(_m.NoteText ?? "", 12, "Cp.Ink"));
                note.Child = ns;
                body.Children.Add(note);
                var row = new WrapPanel();
                row.Children.Add(NoFocus(CardKit.Primary(_m.SaveLabel, () => Send(_m.AnswerProjectNote(true), _m.SaveLabel, Line("", _m.SaveLabel)))));
                row.Children.Add(NoFocus(CardKit.Secondary(_m.DontSaveLabel, () => Send(_m.AnswerProjectNote(false), _m.DontSaveLabel, Line("", _m.DontSaveLabel)))));
                body.Children.Add(row);
                return body;
            }

            private FrameworkElement OptionsBody()
            {
                var body = new StackPanel { Margin = new Thickness(14, 0, 14, 12) };
                for (var i = 0; i < _m.Options.Count; i++)
                {
                    var o = _m.Options[i];
                    var hint = _m.Multi ? (i < 9 ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "") : AskCardModel.OptionKeyHint(o, i);
                    var tile = new Tile(o.Label, o.Meta, hint, big: true);
                    tile.Button.Margin = new Thickness(0, 0, 0, 6);
                    if (_m.Multi)
                    {
                        tile.Button.Click += (_, __) => { _m.ToggleMulti(o.Id); Refresh(); };
                        _refresh.Add(() => tile.Set(_m.MultiPicks.Contains(o.Id)));
                    }
                    else
                    {
                        tile.Button.Click += (_, __) => SendOption(o);   // one click sends
                    }
                    body.Children.Add(tile.Button);
                }
                body.Children.Add(CardKit.Text(AskCardModel.TypeOwnHint, 11, "Cp.Faint", margin: new Thickness(2, 2, 0, 0)));
                return body;
            }

            private void SendOption(AskOptionModel o) =>
                Send(_m.AnswerForOptions(new[] { o.Id }), o.Label, Line("", o.Label));

            private FrameworkElement FreeTextBody()
            {
                var body = new StackPanel { Margin = new Thickness(14, 0, 14, 12) };
                var (frame, box) = Input(_m.FreeTextDraft, number: false, placeholder: "Type your answer");
                box.TextChanged += (_, __) => { _m.FreeTextDraft = box.Text; Refresh(); };
                body.Children.Add(frame);
                return body;
            }

            // ─── Fields ───────────────────────────────────────────────────────────────────────────────────

            private FrameworkElement GroupBlock(AskFieldGroup g)
            {
                var block = new Border { Padding = new Thickness(14, 10, 14, 10), BorderThickness = new Thickness(0, 1, 0, 0) };
                block.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var sp = new StackPanel();
                block.Child = sp;

                var bulk = _m.CanBulk(g);
                if (g.Heading != null || bulk)
                {
                    var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 8) };
                    if (bulk)
                    {
                        var links = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                        links.Children.Add(LinkButton("Create all", () => { _m.Bulk(g, AskOptionKinds.Create); Refresh(); }));
                        var skipAll = LinkButton("Skip all", () => { _m.Bulk(g, AskOptionKinds.Skip); Refresh(); });
                        skipAll.Margin = new Thickness(8, 0, 0, 0);
                        links.Children.Add(skipAll);
                        DockPanel.SetDock(links, Dock.Right);
                        head.Children.Add(links);
                    }
                    var left = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                    if (g.Heading != null)
                    {
                        var k = Kicker(g.Heading, new Thickness(0, 0, 8, 0));
                        k.VerticalAlignment = VerticalAlignment.Center;
                        left.Children.Add(k);
                    }
                    var badge = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 0, 6, 1), VerticalAlignment = VerticalAlignment.Center,
                        Visibility = AskCardModel.CountsIn(g) ? Visibility.Visible : Visibility.Collapsed };
                    var badgeText = CardKit.Text("", 10.5, "Cp.AmberFg", FontWeights.SemiBold, wrap: false);
                    badge.Child = badgeText;
                    left.Children.Add(badge);
                    _refresh.Add(() =>
                    {
                        var leftN = _m.LeftIn(g);
                        badgeText.Text = AskCardModel.CountLabel(leftN);
                        badge.SetResourceReference(Border.BackgroundProperty, leftN > 0 ? "Cp.AmberBg" : "Cp.OkBg");
                        badgeText.SetResourceReference(TextBlock.ForegroundProperty, leftN > 0 ? "Cp.AmberFg" : "Cp.Green");
                    });
                    head.Children.Add(left);
                    sp.Children.Add(head);
                }

                // At most GroupPreview rows, then "Show N more" that opens the rest in place. Create all / Skip all act on
                // every row, shown or not. Moving the keyboard onto a hidden row opens the group too.
                var hiddenRows = AskCardModel.HiddenIn(g);
                var expanded = false;
                var rows = new List<FrameworkElement>();
                foreach (var f in g.Fields) { var row = FieldBlock(f); rows.Add(row); sp.Children.Add(row); }
                if (hiddenRows > 0)
                {
                    var toggle = ToggleRow(AskCardModel.ShowMoreLabel(hiddenRows), () => { expanded = !expanded; Refresh(); });
                    sp.Children.Add(toggle.Row);
                    // The keyboard moving onto a hidden row opens the group (Up/Down only, so "Show fewer" stays folded).
                    _reveal.Add(f => { if (!expanded && g.Fields.IndexOf(f) >= AskCardModel.GroupPreview) expanded = true; });
                    _refresh.Add(() =>
                    {
                        var visible = AskCardModel.VisibleIn(g, expanded);
                        for (var i = 0; i < rows.Count; i++)
                            rows[i].Visibility = i < visible ? Visibility.Visible : Visibility.Collapsed;
                        toggle.Set(expanded ? AskCardModel.ShowFewerLabel : AskCardModel.ShowMoreLabel(hiddenRows));
                    });
                }
                return block;
            }

            /// <summary>One field, in a frame that is outlined while it is the keyboard's field.</summary>
            private FrameworkElement FieldBlock(AskFieldModel f)
            {
                var shape = AskCardModel.ShapeOf(f);
                var frame = new Border
                {
                    CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = Brushes.Transparent,
                    // Margin -8 and border 1 + padding 7 put the field's content on the heading's left edge.
                    Padding = shape == AskFieldShape.Switch ? new Thickness(7, 3, 7, 3) : new Thickness(7, 7, 7, 8),
                    Margin = new Thickness(-8, 0, -8, 2),
                };
                _refresh.Add(() =>
                {
                    var active = _keyboardMode && _sink.IsKeyboardFocused && _m.Fields.Count > 1 && ReferenceEquals(_m.ActiveField, f);
                    if (active) frame.SetResourceReference(Border.BorderBrushProperty, "Cp.Accent");
                    else frame.BorderBrush = Brushes.Transparent;
                });
                // A click anywhere on the field makes it the keyboard's field.
                frame.AddHandler(UIElement.PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, __) =>
                {
                    var i = IndexIn(_m.OrderedFields, f);
                    if (i >= 0 && i != _m.Active) { _m.Active = i; Refresh(); }
                }), true);

                _frames[f] = frame;
                switch (shape)
                {
                    case AskFieldShape.Switch: frame.Child = SwitchRow(f); break;
                    case AskFieldShape.Tiles: frame.Child = TilesField(f); break;
                    case AskFieldShape.List: frame.Child = ListField(f); break;
                    default: frame.Child = TypedField(f, shape == AskFieldShape.Number); break;
                }
                return frame;
            }

            private FrameworkElement FieldName(AskFieldModel f, Thickness margin)
            {
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, FontSize = 12.5 };
                t.Inlines.Add(Ink(f.Label + (string.IsNullOrEmpty(f.Unit) || f.Type == AskFieldTypes.Choice ? "" : " (" + f.Unit + ")"), "Cp.Ink", FontWeights.SemiBold));
                if (f.Note != null)
                {
                    t.Inlines.Add(new Run("  "));
                    var note = Ink(f.Note, "Cp.Faint", FontWeights.Normal);
                    note.FontSize = 11.5;
                    t.Inlines.Add(note);
                }
                return t;
            }

            /// <summary>A level with no plan: the name left, "Create plan" | "Skip" right. The switch wraps under the
            /// name when the pane is too narrow for both.</summary>
            private FrameworkElement SwitchRow(AskFieldModel f)
            {
                var create = AskCardModel.CreateChoice(f)!;
                var skip = AskCardModel.SkipChoice(f)!;
                var grid = new Grid { MinHeight = 36 };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var name = FieldName(f, new Thickness(0, 0, 10, 0));
                name.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(name);

                var track = new Border { CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1), Padding = new Thickness(2), VerticalAlignment = VerticalAlignment.Center };
                track.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                track.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                var seg = new StackPanel { Orientation = Orientation.Horizontal };
                var cb = Segment(create.Label, PlusIcon, () => { _m.Pick(f.Id, create.Id); Refresh(); });
                var sb = Segment(skip.Label, null, () => { _m.Pick(f.Id, skip.Id); Refresh(); });
                sb.Button.Margin = new Thickness(2, 0, 0, 0);
                seg.Children.Add(cb.Button);
                seg.Children.Add(sb.Button);
                track.Child = seg;
                Grid.SetColumn(track, 1);
                grid.Children.Add(track);
                _refresh.Add(() =>
                {
                    var p = _m.Picked(f);
                    cb.Set(p != null && p.IsCreate, skipLook: false);
                    sb.Set(p != null && p.IsSkip, skipLook: true);
                });
                return grid;
            }

            private FrameworkElement TilesField(AskFieldModel f)
            {
                var sp = new StackPanel();
                sp.Children.Add(FieldName(f, new Thickness(0, 0, 2, 7)));
                var choices = AskCardModel.PickChoices(f);
                for (var i = 0; i < choices.Count; i++)
                {
                    var c = choices[i];
                    var tile = new Tile(c.Label, c.Meta, i < 9 ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "", big: false);
                    tile.Button.Margin = new Thickness(0, 0, 0, 6);
                    tile.Button.Click += (_, __) => { _m.Pick(f.Id, c.Id); Refresh(); };
                    _refresh.Add(() => tile.Set(_m.Picked(f)?.Id == c.Id));
                    sp.Children.Add(tile.Button);
                }
                AddSkipText(sp, f);
                return sp;
            }

            /// <summary>More choices than tiles: a filter box and the rows (the first few, then "Show N more").</summary>
            private FrameworkElement ListField(AskFieldModel f)
            {
                var sp = new StackPanel();
                sp.Children.Add(FieldName(f, new Thickness(0, 0, 2, 7)));
                var (frame, box) = Input("", number: false, placeholder: AskCardModel.FilterHint, icon: SearchIcon);
                frame.Margin = new Thickness(0, 0, 0, 6);
                sp.Children.Add(frame);
                var rows = new StackPanel();
                sp.Children.Add(rows);
                var expanded = false;
                IReadOnlyList<AskOptionModel> shown = Array.Empty<AskOptionModel>();
                _visible[f.Id] = () => shown;
                Action? draw = null;
                var more = LinkButton("", () => { expanded = true; draw?.Invoke(); });
                more.HorizontalAlignment = HorizontalAlignment.Center;
                more.Margin = new Thickness(0, 2, 0, 4);
                sp.Children.Add(more);
                var empty = CardKit.Text("Nothing matches.", 12, "Cp.Faint", margin: new Thickness(4, 4, 0, 6));
                sp.Children.Add(empty);

                var rowRefresh = new List<Action>();
                draw = () =>
                {
                    var all = AskCardModel.Filter(f, box.Text);
                    shown = expanded || all.Count <= AskCardModel.ListPreview + 1 ? all : all.Take(AskCardModel.ListPreview).ToList();
                    rows.Children.Clear();
                    rowRefresh.Clear();
                    for (var i = 0; i < shown.Count; i++)
                    {
                        var c = shown[i];
                        var row = new Tile(c.Label, c.Meta, i < 9 ? (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) : "", big: false, flat: true);
                        row.Button.Margin = new Thickness(0, 0, 0, 2);
                        row.Button.Click += (_, __) => { _m.Pick(f.Id, c.Id); Refresh(); };
                        rowRefresh.Add(() => row.Set(_m.Picked(f)?.Id == c.Id));
                        rows.Children.Add(row.Button);
                    }
                    var hidden = all.Count - shown.Count;
                    more.Content = "Show " + hidden + " more";
                    more.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
                    empty.Visibility = all.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    foreach (var r in rowRefresh) r();
                };
                box.TextChanged += (_, __) => draw();
                _refresh.Add(() => { foreach (var r in rowRefresh) r(); });
                draw();
                AddSkipText(sp, f);
                return sp;
            }

            /// <summary>A skip choice under tiles or a list: a quiet text button, lit when picked.</summary>
            private void AddSkipText(StackPanel sp, AskFieldModel f)
            {
                var skip = AskCardModel.SkipChoice(f);
                if (skip == null) return;
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                var icon = new Border { Width = 9, Height = 1.6, CornerRadius = new CornerRadius(1), Margin = new Thickness(1, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
                var text = CardKit.Text(skip.Label, 12, "Cp.Muted", wrap: true);
                text.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(icon);
                row.Children.Add(text);
                var b = Pressable(row, 6);
                b.Padding = new Thickness(4, 4, 6, 4);
                b.HorizontalAlignment = HorizontalAlignment.Left;
                b.Margin = new Thickness(-4, 0, 0, 0);   // its dash on the labels' edge; the padding is only the hover area
                b.Click += (_, __) => { _m.Pick(f.Id, skip.Id); Refresh(); };
                _refresh.Add(() =>
                {
                    var on = _m.Picked(f)?.Id == skip.Id;
                    var brush = on ? "Cp.BlueText" : "Cp.Muted";
                    text.SetResourceReference(TextBlock.ForegroundProperty, brush);
                    text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal;
                    icon.SetResourceReference(Border.BackgroundProperty, brush);
                });
                sp.Children.Add(b);
            }

            private FrameworkElement TypedField(AskFieldModel f, bool number)
            {
                var sp = new StackPanel();
                sp.Children.Add(FieldName(f, new Thickness(0, 0, 2, 6)));
                _m.Picks.TryGetValue(f.Id, out var current);
                var (frame, box) = Input(current ?? "", number, placeholder: number ? "Type a number" : "Type here");
                box.TextChanged += (_, __) => { _m.SetText(f.Id, box.Text); Refresh(); };
                sp.Children.Add(frame);
                return sp;
            }

            // ─── "What you will get" and the footer ───────────────────────────────────────────────────────

            private FrameworkElement Preview()
            {
                var block = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 10, 14, 10) };
                block.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                block.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var sp = new StackPanel();
                block.Child = sp;
                var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
                var count = CardKit.Text("", 12, "Cp.Muted", wrap: false);
                DockPanel.SetDock(count, Dock.Right);
                head.Children.Add(count);
                head.Children.Add(Kicker(AskCardModel.PreviewHeading, new Thickness(0)));
                sp.Children.Add(head);
                var list = new StackPanel();
                sp.Children.Add(list);
                // Many ready items are one line (the pills name them); the field lines stop at OutcomePreview, then
                // "Show N more" / "Show fewer".
                var expanded = false;
                var toggle = ToggleRow("", () => { expanded = !expanded; Refresh(); });
                sp.Children.Add(toggle.Row);
                _refresh.Add(() =>
                {
                    count.Text = _m.PreviewCount ?? "";
                    var (lines, hidden) = _m.OutcomeView(expanded);
                    list.Children.Clear();
                    foreach (var line in lines) list.Children.Add(OutcomeRow(line));
                    toggle.Row.Visibility = hidden > 0 ? Visibility.Visible : Visibility.Collapsed;
                    toggle.Set(expanded ? AskCardModel.ShowFewerLabel : AskCardModel.ShowMoreLabel(hidden));
                });
                return block;
            }

            private static FrameworkElement OutcomeRow(AskOutcomeLine line)
            {
                var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
                switch (line.State)
                {
                    case AskOutcomeState.Waiting:
                        t.Inlines.Add(Ink(line.Label, "Cp.AmberFg", FontWeights.SemiBold));
                        t.Inlines.Add(Ink(" · " + (line.Detail ?? AskCardModel.WaitingText), "Cp.AmberFg", FontWeights.Normal));
                        break;
                    case AskOutcomeState.Skipped:
                        var r = Ink(line.Label, "Cp.Faint", FontWeights.Medium);
                        r.TextDecorations = TextDecorations.Strikethrough;
                        t.Inlines.Add(r);
                        break;
                    default:
                        t.Inlines.Add(Ink(line.Label, "Cp.Ink", FontWeights.SemiBold));
                        if (!string.IsNullOrEmpty(line.Detail))
                        {
                            t.Inlines.Add(new Run(" "));
                            var d = Ink(line.Detail!, "Cp.Faint", FontWeights.Normal);
                            d.FontSize = 11.5;
                            t.Inlines.Add(d);
                        }
                        break;
                }
                grid.Children.Add(t);
                if (line.State == AskOutcomeState.New)
                {
                    var tag = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1), Margin = new Thickness(8, 1, 0, 0), VerticalAlignment = VerticalAlignment.Top };
                    tag.SetResourceReference(Border.BackgroundProperty, "Cp.BlueSoft");
                    tag.Child = CardKit.Text(AskCardModel.NewTag, 10.5, "Cp.BlueText", FontWeights.SemiBold, wrap: false);
                    Grid.SetColumn(tag, 1);
                    grid.Children.Add(tag);
                }
                return grid;
            }

            private FrameworkElement Footer()
            {
                var bar = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(14, 12, 14, 6) };
                bar.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                _footer = NoFocus(CardKit.Primary(_m.FooterLabel, SendDraft));
                _footer.HorizontalAlignment = HorizontalAlignment.Left;
                _footer.MinHeight = 34;
                bar.Child = _footer;
                _refresh.Add(() =>
                {
                    _footer.Content = _m.FooterLabel;
                    var on = _m.FooterEnabled;
                    _footer.IsEnabled = on;
                    // Disabled: the button's own 50 % fade made white on pale blue in the light theme (about 2:1).
                    // Instead a full-opacity tint with the accent's dark text: readable in both themes.
                    _footer.Opacity = 1;
                    _footer.SetResourceReference(Control.BackgroundProperty, on ? "Cp.Accent" : "Cp.BlueSoft");
                    _footer.SetResourceReference(Control.ForegroundProperty, on ? "Cp.AccentContrast" : "Cp.BlueText");
                });
                return bar;
            }

            private void SendDraft()
            {
                if (!_m.FooterEnabled) return;
                switch (_m.Kind)
                {
                    case AskCardKind.Fields:
                        Send(_m.AnswerForDraft(), _m.SummaryForDraft(), _m.LinesForDraft().ToArray());
                        break;
                    case AskCardKind.FreeText:
                        var text = (_m.FreeTextDraft ?? "").Trim();
                        Send(_m.AnswerForText(text), text, Line("", text));
                        break;
                    case AskCardKind.Options:
                        var picked = _m.MultiPicked;
                        var summary = string.Join(", ", picked.Select(o => o.Label));
                        Send(_m.AnswerForOptions(picked.Select(o => o.Id)), summary, Line("", summary));
                        break;
                }
            }

            private void Send(AnswerDto answer, string summary, params AskAnsweredLine[] lines)
            {
                if (_sent) return;
                _sent = true;
                _m.RecordLines(lines);
                _submit(answer, summary);
                // The pane did not take it (no live turn): the card stays usable, as before.
                if (_m.AnsweredSummary == null) { _sent = false; _m.AnsweredLines = null; }
            }

            private static AskAnsweredLine Line(string label, string pick) => new AskAnsweredLine { Label = label, Pick = pick };

            // ─── Keyboard ─────────────────────────────────────────────────────────────────────────────────

            /// <summary>Enter inside one of the card's own text boxes (filter, a text field, the free-text box) sends when the
            /// card is complete. Every other key reaches the card through the key sink (see <see cref="MakeSink"/>).</summary>
            private void OnKey(object sender, KeyEventArgs e)
            {
                if (ReferenceEquals(e.OriginalSource, _sink)) return;
                if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return;
                if ((e.Key == Key.Enter || e.Key == Key.Return) && e.OriginalSource is TextBox && AskCardKeys.ForEnter(_m).Kind == AskKeyKind.Send)
                {
                    SendDraft();
                    e.Handled = true;
                }
            }

            /// <summary>The key sink. Inside Revit a dockable pane gets letter, digit and Enter keys only while a TEXT BOX
            /// has keyboard focus (Revit's main window takes them as shortcuts otherwise; live R4: after a click on the card
            /// 1..9, C, S and Enter did nothing, Down only scrolled, while the message box and the card's own text box got
            /// every key). So the card owns an invisible text box that takes the focus when the card is clicked (anywhere
            /// but one of its real text boxes) or tabbed into. Its typed characters (TextInput, so the keyboard layout is
            /// respected) and its Up/Down/Enter go through AskCardKeys; nothing is ever written into it. The card's buttons
            /// are not focusable, so a click never parks the focus on a button where Revit would take the keys.</summary>
            private TextBox MakeSink()
            {
                var sink = new TextBox
                {
                    Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false, BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent, Foreground = Brushes.Transparent, CaretBrush = Brushes.Transparent,
                    AcceptsReturn = false, AcceptsTab = false, IsUndoEnabled = false, FocusVisualStyle = null,
                    Padding = new Thickness(0), Margin = new Thickness(0),
                };
                sink.ContextMenu = null;
                InputMethod.SetIsInputMethodEnabled(sink, false);
                System.Windows.Automation.AutomationProperties.SetName(sink, "Question card: 1 to 9 pick, C create, S skip, Up and Down move, Enter sends");
                DataObject.AddPastingHandler(sink, (_, e) => e.CancelCommand());
                sink.TextChanged += (_, __) => { if (sink.Text.Length > 0) sink.Text = ""; };
                sink.PreviewTextInput += (_, e) =>
                {
                    e.Handled = true;   // never typed into the sink
                    var text = e.Text ?? "";
                    // The focus was taken from the empty message box: a character the card does not use starts a message.
                    if (_handBackTo != null && text.Length == 1
                        && AskCardFocus.HandsBack(true, text, CommandModifierDown(), KeyFor(text[0]))
                        && HandBack(text)) return;
                    foreach (var ch in text) OnChar(ch);
                };
                sink.PreviewKeyDown += (_, e) =>
                {
                    if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
                    {
                        if (e.Key != Key.Tab) e.Handled = true;   // no paste, cut or select-all in the sink
                        return;
                    }
                    switch (e.Key)
                    {
                        case Key.Up:
                        case Key.Down:
                            var arrow = AskCardKeys.ForArrow(_m, e.Key == Key.Down);
                            if (arrow.Kind == AskKeyKind.None) return;   // a card with one field: the pane may scroll
                            _keyboardMode = true;
                            var was = _m.Active;
                            AskCardKeys.Apply(_m, arrow);
                            var now = _m.ActiveField;
                            if (now != null) foreach (var r in _reveal) r(now);
                            Refresh();
                            if (AskCardKeys.MovesActive(arrow, was, _m.Active)) ShowActiveRow();
                            e.Handled = true;
                            break;
                        case Key.Enter:   // Key.Return is the same value
                            e.Handled = true;
                            _keyboardMode = true;
                            if (AskCardKeys.ForEnter(_m).Kind == AskKeyKind.Send) SendDraft();
                            else Refresh();
                            break;
                        case Key.Space:
                        case Key.Back:
                        case Key.Delete:
                            e.Handled = true;
                            break;
                    }
                };
                sink.GotKeyboardFocus += (_, __) => { if (!_focusByMouse) _keyboardMode = true; Refresh(); };
                sink.LostKeyboardFocus += (_, __) => { _handBackTo = null; Refresh(); };
                return sink;
            }

            private static bool CommandModifierDown() =>
                (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

            /// <summary>What a typed character would do on the card now (nothing is applied).</summary>
            private AskKeyResult KeyFor(char ch)
            {
                var f = _m.ActiveField;
                var visible = f != null && _visible.TryGetValue(f.Id, out var v) ? v() : null;
                return AskCardKeys.ForChar(_m, ch, visible);
            }

            private void OnChar(char ch)
            {
                var r = KeyFor(ch);
                if (r.Kind == AskKeyKind.None) return;
                _keyboardMode = true;
                if (r.Kind == AskKeyKind.SendOption) { SendOption(r.Choice!); return; }
                var was = _m.Active;
                AskCardKeys.Apply(_m, r);
                var now = _m.ActiveField;
                if (now != null && was != _m.Active) foreach (var rv in _reveal) rv(now);
                Refresh();
                if (AskCardKeys.MovesActive(r, was, _m.Active)) ShowActiveRow();
            }

            /// <summary>A click in the card (not in one of its text boxes) gives the sink the focus, as a click on a text
            /// box would; the focus came from the mouse, so no keyboard outline shows.</summary>
            private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
            {
                _keyboardMode = false;
                _handBackTo = null;   // the drafter chose the card with the mouse: its keys stay its own
                if (InRealTextBox(e.OriginalSource as DependencyObject)) { Refresh(); return; }
                FocusSink();
                Refresh();
                // Whatever handles the rest of this click (a read-only markdown box in the header takes the focus for
                // itself), the sink gets the focus back once the mouse input is processed.
                _outer.Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_sent || !_outer.IsVisible || _sink.IsKeyboardFocused) return;
                    if (Keyboard.FocusedElement is TextBox t && !ReferenceEquals(t, _sink) && _outer.IsAncestorOf(t)) return;
                    if (_outer.IsKeyboardFocusWithin || !(Keyboard.FocusedElement is TextBox)) FocusSink();
                }), System.Windows.Threading.DispatcherPriority.Input);
            }

            /// <summary>The bubbling half of a click on the card's background (nothing under the mouse handled it): mark
            /// it handled. Otherwise it reaches the chat's ScrollViewer, whose OnMouseLeftButtonDown calls Focus() on
            /// itself and takes the focus away from the sink (live R5: after a click on the title, Down scrolled the pane
            /// 16 px, one ScrollViewer line, and 2 / C / S / Enter went nowhere). A pick worked because the tile button
            /// handles its own mouse-down, so the ScrollViewer never saw it.</summary>
            private void OnCardMouseDownBubble(object sender, MouseButtonEventArgs e)
            {
                if (e.Handled || InRealTextBox(e.OriginalSource as DependencyObject)) return;
                e.Handled = true;
            }

            /// <summary>Gives the sink the keyboard focus without moving the pane: the chat's scroll position is put back if
            /// the focus change scrolled it (live R5: the pane jumped 144 px on the first tile click after the background
            /// click, and the pick was lost because the tile moved away under the mouse).</summary>
            private void FocusSink(bool byMouse = true)
            {
                if (_sink.IsKeyboardFocused) return;
                var scroller = PaneScroller();
                var before = scroller?.VerticalOffset;
                _focusByMouse = byMouse;
                try { _sink.Focus(); }
                finally { _focusByMouse = false; }
                if (scroller == null || before == null) return;
                void Restore()
                {
                    if (Math.Abs(scroller.VerticalOffset - before.Value) > 0.5) scroller.ScrollToVerticalOffset(before.Value);
                }
                Restore();
                // A bring-into-view the focus change queued is applied by the next layout pass: undo that one as well.
                _outer.Dispatcher.BeginInvoke(new Action(Restore), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            /// <summary>Scrolls the pane just enough to show the keyboard's row. Called ONLY from the key handlers, after a
            /// key moved the active row (Up, Down, or a key pick that moved on); never from a mouse handler or from
            /// FocusSink, so a click or the card taking the focus never scrolls (round 4). Runs after layout (a folded
            /// group may just have opened), and uses the pane's own offset, not BringIntoView, which the sink blocks.</summary>
            private void ShowActiveRow()
            {
                var f = _m.ActiveField;
                if (f == null || !_frames.TryGetValue(f, out var frame)) return;
                _outer.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        var sv = PaneScroller();
                        if (sv == null || !frame.IsVisible || !sv.IsAncestorOf(frame)) return;
                        var box = frame.TransformToAncestor(sv).TransformBounds(new Rect(0, 0, frame.ActualWidth, frame.ActualHeight));
                        var to = AskCardKeys.RevealOffset(sv.VerticalOffset, box.Top, box.Bottom, sv.ViewportHeight);
                        if (to != null) sv.ScrollToVerticalOffset(to.Value);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] ask card reveal: " + ex.Message); }
                }), System.Windows.Threading.DispatcherPriority.Loaded);
            }

            /// <summary>The chat's ScrollViewer (the nearest one above the card).</summary>
            private ScrollViewer? PaneScroller()
            {
                DependencyObject? d = _outer;
                while (d != null)
                {
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
                    if (d is ScrollViewer sv) return sv;
                }
                return null;
            }

            private bool InRealTextBox(DependencyObject? d)
            {
                while (d != null)
                {
                    if (d is TextBox t) return !ReferenceEquals(t, _sink);
                    if (ReferenceEquals(d, _outer)) return false;
                    d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
                }
                return false;
            }

            // ─── Small parts ──────────────────────────────────────────────────────────────────────────────

            /// <summary>A full-width "Show N more" / "Show fewer" row under a thin rule (groups and the outcome list).</summary>
            private static (Border Row, Action<string> Set) ToggleRow(string label, Action onClick)
            {
                var link = LinkButton(label, onClick);
                link.HorizontalAlignment = HorizontalAlignment.Stretch;
                link.HorizontalContentAlignment = HorizontalAlignment.Center;
                link.Padding = new Thickness(4, 7, 4, 5);
                var rule = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Margin = new Thickness(0, 4, 0, 0), Child = link };
                rule.SetResourceReference(Border.BorderBrushProperty, "Cp.LineSoft");
                var text = link.Content as TextBlock;
                return (rule, t => { if (text != null) text.Text = t; else link.Content = t; });
            }

            private static Button NoFocus(Button b) { b.Focusable = false; return b; }

            private static Button LinkButton(string label, Action onClick)
            {
                var text = new TextBlock { Text = label, FontSize = 11.5, TextDecorations = TextDecorations.Underline };
                text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                var b = Pressable(text, 5);
                b.Padding = new Thickness(2, 1, 2, 1);
                b.Click += (_, __) => onClick();
                // "Show N more" sets Content to a string later: keep the look.
                b.SetResourceReference(Control.ForegroundProperty, "Cp.Muted");
                b.FontSize = 11.5;
                return b;
            }

            /// <summary>A themed text box: a Border of the card's own (no system-blue hover), the box inside, a placeholder.</summary>
            private static (Border Frame, TextBox Box) Input(string text, bool number, string placeholder, string? icon = null)
            {
                var frame = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(8, 0, 8, 0), MinHeight = 32 };
                frame.SetResourceReference(Border.BackgroundProperty, "Cp.Sunken");
                frame.SetResourceReference(Border.BorderBrushProperty, "Cp.Hair2");
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                if (icon != null)
                {
                    var p = Icon(icon, "Cp.Faint", 1.5, 13);
                    p.Margin = new Thickness(0, 0, 7, 0);
                    p.VerticalAlignment = VerticalAlignment.Center;
                    grid.Children.Add(p);
                }
                var box = new TextBox
                {
                    Text = text ?? "", FontSize = 12.5, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Padding = new Thickness(0, 6, 0, 6), VerticalContentAlignment = VerticalAlignment.Center, AcceptsReturn = false,
                    TextWrapping = TextWrapping.Wrap, FocusVisualStyle = null,
                };
                box.SetResourceReference(Control.ForegroundProperty, "Cp.Ink");
                box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.CaretBrushProperty, "Cp.Accent");
                box.SetResourceReference(System.Windows.Controls.Primitives.TextBoxBase.SelectionBrushProperty, "Cp.Accent");
                if (number)
                    box.PreviewTextInput += (_, e) => { e.Handled = !e.Text.All(c => char.IsDigit(c) || c == '.' || c == ',' || c == '-'); };
                var hint = CardKit.Text(placeholder, 12.5, "Cp.Faint", wrap: false);
                hint.IsHitTestVisible = false;
                hint.VerticalAlignment = VerticalAlignment.Center;
                hint.Margin = new Thickness(2, 0, 0, 0);
                hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
                box.TextChanged += (_, __) => hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;
                Grid.SetColumn(box, 1); Grid.SetColumn(hint, 1);
                grid.Children.Add(hint);
                grid.Children.Add(box);
                frame.Child = grid;
                box.GotKeyboardFocus += (_, __) => frame.SetResourceReference(Border.BorderBrushProperty, "Cp.Accent");
                box.LostKeyboardFocus += (_, __) => frame.SetResourceReference(Border.BorderBrushProperty, "Cp.Hair2");
                return (frame, box);
            }

            private static int IndexIn(IReadOnlyList<AskFieldModel> order, AskFieldModel f)
            {
                for (var i = 0; i < order.Count; i++) if (ReferenceEquals(order[i], f)) return i;
                return -1;
            }
        }

        // ─── Tiles and switch segments ────────────────────────────────────────────────────────────────────

        /// <summary>A choice as a tile (name, meta under it, a key hint right; a tick instead once picked), or as a flat
        /// list row (<paramref name="flat"/>: no border until picked).</summary>
        private sealed class Tile
        {
            public readonly Button Button;
            private readonly TextBlock _name;
            private readonly FrameworkElement _key;
            private readonly FrameworkElement _tick;
            private readonly bool _flat;

            public Tile(string label, string? meta, string keyHint, bool big, bool flat = false)
            {
                _flat = flat;
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                _name = CardKit.Text(label, big ? 13 : 12.5, "Cp.Ink", FontWeights.SemiBold);
                text.Children.Add(_name);
                if (!string.IsNullOrEmpty(meta)) text.Children.Add(CardKit.Text(meta!, 11.5, "Cp.Faint", margin: new Thickness(0, 1, 0, 0)));
                grid.Children.Add(text);

                var right = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                var key = new Border { MinWidth = 20, Height = 20, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1), Padding = new Thickness(4, 0, 4, 0) };
                key.SetResourceReference(Border.BorderBrushProperty, "Cp.Hair2");
                var keyText = CardKit.Text(keyHint, 10.5, "Cp.Faint", wrap: false, mono: true);
                keyText.HorizontalAlignment = HorizontalAlignment.Center;
                keyText.VerticalAlignment = VerticalAlignment.Center;
                key.Child = keyText;
                key.Visibility = string.IsNullOrEmpty(keyHint) ? Visibility.Collapsed : Visibility.Visible;
                _key = key;
                var tick = new Grid { Width = 20, Height = 20, Visibility = Visibility.Collapsed };
                var disc = new System.Windows.Shapes.Ellipse { Width = 20, Height = 20 };
                disc.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "Cp.Accent");
                tick.Children.Add(disc);
                var check = Icon(TickIcon, "Cp.AccentContrast", 2, 12);
                check.HorizontalAlignment = HorizontalAlignment.Center;
                check.VerticalAlignment = VerticalAlignment.Center;
                tick.Children.Add(check);
                _tick = tick;
                right.Children.Add(key);
                right.Children.Add(tick);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);

                Button = Pressable(grid, flat ? 8 : 10);
                Button.HorizontalAlignment = HorizontalAlignment.Stretch;
                Button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                Button.Padding = flat ? new Thickness(8, 6, 8, 6) : new Thickness(10, big ? 9 : 7, 10, big ? 9 : 7);
                Button.MinHeight = flat ? 0 : (big ? 46 : 40);
                Button.BorderThickness = new Thickness(1);
                Button.ToolTip = null;
                Set(false);
            }

            public void Set(bool picked)
            {
                if (picked)
                {
                    Button.SetResourceReference(Control.BackgroundProperty, "Cp.BlueSoft");
                    Button.SetResourceReference(Control.BorderBrushProperty, "Cp.PurpleLine");
                    _name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                }
                else
                {
                    Button.Background = Brushes.Transparent;
                    if (_flat) Button.BorderBrush = Brushes.Transparent;
                    else Button.SetResourceReference(Control.BorderBrushProperty, "Cp.Hair2");
                    _name.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                }
                _key.Visibility = picked || ((_key as Border)?.Child as TextBlock)?.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                _tick.Visibility = picked ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>One half of the create/skip switch.</summary>
        private sealed class SegmentButton
        {
            public Button Button = null!;
            public TextBlock Text = null!;
            public System.Windows.Shapes.Path? Icon;

            public void Set(bool on, bool skipLook)
            {
                if (on && !skipLook)
                {
                    Button.SetResourceReference(Control.BackgroundProperty, "Cp.BlueSoft");
                    Button.SetResourceReference(Control.BorderBrushProperty, "Cp.PurpleLine");
                    Text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.BlueText");
                }
                else if (on)
                {
                    Button.SetResourceReference(Control.BackgroundProperty, "Cp.Raised");
                    Button.SetResourceReference(Control.BorderBrushProperty, "Cp.Hair2");
                    Text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Ink");
                }
                else
                {
                    Button.Background = Brushes.Transparent;
                    Button.BorderBrush = Brushes.Transparent;
                    Text.SetResourceReference(TextBlock.ForegroundProperty, "Cp.Muted");
                }
                Text.FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium;
                Icon?.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, on && !skipLook ? "Cp.BlueText" : "Cp.Muted");
            }
        }

        private static SegmentButton Segment(string label, string? icon, Action onClick)
        {
            var s = new SegmentButton();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            if (icon != null)
            {
                s.Icon = Icon(icon, "Cp.Muted", 1.8, 11);
                s.Icon.Margin = new Thickness(0, 0, 5, 0);
                s.Icon.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(s.Icon);
            }
            s.Text = CardKit.Text(label, 12, "Cp.Muted", FontWeights.Medium, wrap: false);
            s.Text.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(s.Text);
            s.Button = Pressable(row, 7);
            s.Button.MinHeight = 28;
            s.Button.Padding = new Thickness(10, 0, 10, 0);
            s.Button.BorderThickness = new Thickness(1);
            s.Button.Click += (_, __) => onClick();
            s.Set(false, false);
            return s;
        }

        // ─── Shared helpers ───────────────────────────────────────────────────────────────────────────────

        private static readonly Dictionary<double, ControlTemplate> Templates = new Dictionary<double, ControlTemplate>();

        /// <summary>A button with no chrome of its own: Background/BorderBrush/Padding as set, a hover wash over it, a 2px
        /// accent ring outside it while it has keyboard focus, half opacity when disabled. Content stretches.</summary>
        private static Button Pressable(UIElement content, double radius)
        {
            if (!Templates.TryGetValue(radius, out var t))
            {
                var r = radius.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var ring = (radius + 3).ToString(System.Globalization.CultureInfo.InvariantCulture);
                t = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(
                    "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                    "<Grid>" +
                    "<Border x:Name='ring' Margin='-3' CornerRadius='" + ring + "' BorderThickness='2' BorderBrush='Transparent' IsHitTestVisible='False'/>" +
                    "<Border x:Name='bd' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='" + r + "' Padding='{TemplateBinding Padding}' SnapsToDevicePixels='True'>" +
                    "<ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='{TemplateBinding VerticalContentAlignment}' RecognizesAccessKey='False'/></Border>" +
                    "<Border x:Name='wash' CornerRadius='" + r + "' Background='Transparent' IsHitTestVisible='False'/>" +
                    "</Grid>" +
                    "<ControlTemplate.Triggers>" +
                    "<Trigger Property='IsMouseOver' Value='True'><Setter TargetName='wash' Property='Background' Value='{DynamicResource Cp.Hover}'/></Trigger>" +
                    "<Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='ring' Property='BorderBrush' Value='{DynamicResource Cp.Accent}'/></Trigger>" +
                    "<Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.5'/></Trigger>" +
                    "</ControlTemplate.Triggers></ControlTemplate>");
                Templates[radius] = t;
            }
            var b = new Button
            {
                Content = content,
                Template = t,
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FocusVisualStyle = null,
                // Never focusable: a focused button inside a Revit pane loses its keys to Revit, and a click must not
                // leave a ring. The card's keys go through the sink.
                Focusable = false,
                // A pick happens on mouse-down, on the element that was under the mouse, so nothing that moves the
                // layout before mouse-up (a scroll, a re-flow) can lose it (live R5: a tile click that moved away).
                ClickMode = ClickMode.Press,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            return b;
        }

        private static TextBlock Kicker(string text, Thickness margin) =>
            CardKit.Text((text ?? "").ToUpperInvariant(), 10.5, "Cp.Faint", FontWeights.SemiBold, margin: margin);

        private static Run Ink(string text, string brush, FontWeight weight)
        {
            var r = new Run(text ?? "") { FontWeight = weight };
            r.SetResourceReference(TextElement.ForegroundProperty, brush);
            return r;
        }

        private static System.Windows.Shapes.Path Icon(string data, string stroke, double thickness, double size)
        {
            var p = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data), Width = size, Height = size, Stretch = Stretch.Uniform, StrokeThickness = thickness,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            };
            p.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, stroke);
            return p;
        }

        private static string Clip(string s, int n) => string.IsNullOrEmpty(s) || s.Length <= n ? s ?? "" : s.Substring(0, n - 1) + "…";
    }
}
