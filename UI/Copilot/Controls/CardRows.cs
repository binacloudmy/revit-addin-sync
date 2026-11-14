#nullable enable
// The rows of one result card, and the card's keys (result marks design, rules 7 and 12; build step 2).
//   one way in  the row is the button: a click (or Enter) shows its elements in the model; the small arrow at its right
//               (or Shift+Enter) also selects them in Revit. A row that is not about marks (a view, a sheet, a check's
//               handle) is still the button for what it is (open the view or sheet, select the handle's elements).
//   keys        with the card focused: Up and Down move between rows, Enter shows the row, Shift+Enter also selects it,
//               A shows all, H hides or shows the marks (CardKeys decides; a text box keeps its keys).
//   mark click  a click on one of this turn's marks in the model finds its row (MarkFocus.PickRow), scrolls the card to
//               it, and flashes it with "Tag 3 of 8 selected in Revit".
// Fixed labels are English (rule 11).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RevitWebAppSync.UI.Copilot.Highlights;
using RevitWebAppSync.UI.Copilot.Model;

namespace RevitWebAppSync.UI.Copilot.Controls
{
    internal sealed class CardRows
    {
        private sealed class Entry
        {
            public FrameworkElement Row = null!;
            public MarkGroup? Target;
            public Action? Show;
            public Action? Select;
            /// <summary>Paint the row as the card's current row, or not.</summary>
            public Action<bool> Paint = _ => { };
            /// <summary>Say a line under the row ("Tag 3 of 8 selected in Revit"); "" clears it.</summary>
            public Action<string> Note = _ => { };
        }

        private readonly TurnEvidenceActions _actions;
        private readonly List<Entry> _rows = new List<Entry>();
        private Entry? _current;

        public CardRows(TurnEvidenceActions actions) { _actions = actions; }

        public int Count => _rows.Count;

        /// <summary>Make a row of marked elements the button: a click shows them in the model, the arrow at its right selects
        /// them in Revit. A row with nothing to show (the server did not send its ids) only says why, in its tooltip.</summary>
        public void Link(Border row, MarkGroup target)
        {
            if (row == null || target == null) return;
            if (_actions.ShowGroup == null) return;
            if (!MarkFocus.CanShow(target)) { row.ToolTip = CardButtons.NothingToShowTip; return; }
            var content = row.Child;
            row.Child = null;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (content != null) grid.Children.Add(content);
            var arrow = Arrow(() => _actions.SelectGroupInRevit(target));
            Grid.SetColumn(arrow, 1);
            grid.Children.Add(arrow);
            var note = CardKit.Text("", 11.5, "Cp.Muted", margin: new Thickness(0, 2, 0, 0));
            note.Visibility = Visibility.Collapsed;
            Grid.SetRow(note, 1);
            grid.Children.Add(note);
            row.Child = grid;
            Add(row, target, () => _actions.ShowGroup!(target, false), () => _actions.ShowGroup!(target, true), BorderPaint(row), Text(note));
        }

        /// <summary>A row that is the button for something other than marks (open its view or sheet, select a check's elements).</summary>
        public void Button(Border row, Action click, string tip)
        {
            if (row == null || click == null) return;
            row.ToolTip = tip;
            Add(row, null, click, null, BorderPaint(row), _ => { });
        }

        /// <summary>A row drawn by its body (a type tile): the body paints it and says its note; this makes it the button and puts
        /// it in the card's order.</summary>
        public void Add(FrameworkElement row, MarkGroup? target, Action show, Action? select, Action<bool> paint, Action<string> note)
        {
            var e = new Entry { Row = row, Target = target, Show = show, Select = select, Paint = paint ?? (_ => { }), Note = note ?? (_ => { }) };
            _rows.Add(e);
            row.Focusable = true;
            row.FocusVisualStyle = null;
            row.Cursor = Cursors.Hand;
            if (row is Border b && b.Background == null) b.Background = Brushes.Transparent;   // the whole row takes the click
            if (row.ToolTip == null && target != null) row.ToolTip = CardButtons.RowTip;
            row.MouseLeftButtonUp += (_, args) =>
            {
                if (args.Handled) return;
                args.Handled = true;
                Activate(e, select: false);
            };
        }

        /// <summary>The small arrow at a row's right: select in Revit.</summary>
        internal static Button Arrow(Action onClick)
        {
            var b = CardKit.Ghost("", onClick, compact: true);
            var path = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(CardKit.Icons.Select), Width = 13, Height = 13, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            };
            path.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Cp.Muted");
            b.Content = path;
            b.MinHeight = 26; b.Width = 28; b.Padding = new Thickness(0); b.Margin = new Thickness(6, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Center;
            b.ToolTip = CardButtons.ArrowTip;
            System.Windows.Automation.AutomationProperties.SetName(b, CardButtons.ArrowTip);
            return b;
        }

        private static Action<bool> BorderPaint(Border row)
        {
            var thickness = row.BorderThickness; var padding = row.Padding;
            return on =>
            {
                if (on)
                {
                    row.SetResourceReference(Border.BackgroundProperty, "Cp.Raised");
                    // A 3px bar at the left, the content kept where it was.
                    row.BorderThickness = new Thickness(3, thickness.Top, thickness.Right, thickness.Bottom);
                    row.Padding = new Thickness(Math.Max(0, padding.Left - 3), padding.Top, padding.Right, padding.Bottom);
                    row.SetResourceReference(Border.BorderBrushProperty, "Cp.Accent");
                }
                else
                {
                    row.Background = Brushes.Transparent;
                    row.BorderThickness = thickness; row.Padding = padding;
                    row.SetResourceReference(Border.BorderBrushProperty, "Cp.Line");
                }
            };
        }

        private static Action<string> Text(TextBlock note) => s =>
        {
            note.Text = s ?? "";
            note.Visibility = string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
        };

        // ─── Current row, show, select ────────────────────────────────────────

        private void SetCurrent(Entry? e)
        {
            if (ReferenceEquals(_current, e)) return;
            if (_current != null) { _current.Paint(false); _current.Note(""); }
            _current = e;
            e?.Paint(true);
        }

        private void Activate(Entry e, bool select)
        {
            SetCurrent(e);
            try { e.Row.Focus(); } catch { /* not focusable yet */ }
            try { (select ? e.Select ?? e.Show : e.Show)?.Invoke(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] card row: " + ex.Message); }
        }

        // ─── The card's keys ──────────────────────────────────────────────────

        /// <summary>Give the card its keys and have it hear clicks on its turn's marks. <paramref name="card"/> keeps this alive.</summary>
        public void Hook(FrameworkElement card)
        {
            if (card == null) return;
            card.Resources[typeof(CardRows)] = this;   // the card keeps its rows (the mark-click watcher holds them weakly)
            card.Focusable = true;
            card.FocusVisualStyle = null;
            card.PreviewKeyDown += (_, e) => OnKey(card, e);
            // A click on the card's empty parts gives it the keys (a row or a button takes its own click first).
            card.MouseLeftButtonDown += (_, e) => { if (!card.IsKeyboardFocusWithin) card.Focus(); };
            var weak = new WeakReference<CardRows>(this);
            _actions.WatchMarkClicks?.Invoke(c =>
            {
                if (!weak.TryGetTarget(out var rows)) return false;
                rows.OnMarkClicked(c);
                return true;
            });
        }

        private void OnKey(FrameworkElement card, KeyEventArgs e)
        {
            try
            {
                var key = e.Key == Key.System ? e.SystemKey : e.Key;
                var mods = Keyboard.Modifiers;
                var typing = e.OriginalSource is TextBox || e.OriginalSource is PasswordBox;
                var action = CardKeys.For(key.ToString(), (mods & ModifierKeys.Shift) != 0, (mods & (ModifierKeys.Control | ModifierKeys.Alt)) != 0, typing);
                switch (action)
                {
                    case CardKeyAction.Previous:
                    case CardKeyAction.Next:
                    {
                        var order = Ordered(card);
                        var at = CardKeys.Move(_current == null ? -1 : order.IndexOf(_current), order.Count, action);
                        if (at < 0) return;
                        var next = order[at];
                        SetCurrent(next);
                        next.Row.BringIntoView();
                        next.Row.Focus();
                        break;
                    }
                    case CardKeyAction.Show:
                    case CardKeyAction.ShowAndSelect:
                        if (_current == null) return;
                        Activate(_current, action == CardKeyAction.ShowAndSelect);
                        break;
                    case CardKeyAction.ShowAll:
                        if (_actions.ShowAll == null) return;
                        _actions.ShowAll();
                        break;
                    case CardKeyAction.ToggleMarks:
                        if (_actions.ShowMarks == null || _actions.HideMarks == null) return;
                        var state = Highlights.MarksState.None;
                        try { state = _actions.MarksState?.Invoke() ?? Highlights.MarksState.None; } catch { }
                        if (state == Highlights.MarksState.Shown) _actions.HideMarks(); else _actions.ShowMarks();
                        break;
                    default:
                        return;
                }
                e.Handled = true;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[BINA] card key: " + ex.Message); }
        }

        /// <summary>The rows the drafter can see, top to bottom (the Rules block is built before the card, so the order is
        /// where they are drawn, not when they were added).</summary>
        private List<Entry> Ordered(FrameworkElement card)
        {
            double Y(Entry r) { try { return r.Row.TranslatePoint(new Point(0, 0), card).Y; } catch { return double.MaxValue; } }
            double X(Entry r) { try { return r.Row.TranslatePoint(new Point(0, 0), card).X; } catch { return 0; } }
            return _rows.Where(r => r.Row.IsVisible).OrderBy(Y).ThenBy(X).ToList();
        }

        // ─── A mark clicked in the model ──────────────────────────────────────

        private void OnMarkClicked(MarkClick click)
        {
            var at = MarkFocus.PickRow(_rows.Select(r => r.Target).ToList(), click);
            if (at < 0) return;
            var e = _rows[at];
            // A row behind a fold ("Show all 24") is brought out so the card can scroll to it.
            if (e.Row.Visibility != Visibility.Visible) e.Row.Visibility = Visibility.Visible;
            SetCurrent(e);
            e.Note(click.Line);
            CardKit.Reveal(e.Row);
            Flash(e);
        }

        /// <summary>The row lights up in the amber wash for a moment, then stays the current row.</summary>
        private void Flash(Entry e)
        {
            if (!(e.Row is Border b)) return;
            b.SetResourceReference(Border.BackgroundProperty, "Cp.AmberBg");
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            timer.Tick += (_, __) =>
            {
                timer.Stop();
                if (ReferenceEquals(_current, e)) e.Paint(true); else e.Paint(false);
            };
            timer.Start();
        }
    }
}
