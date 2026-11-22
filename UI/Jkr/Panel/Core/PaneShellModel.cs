using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace RevitWebAppSync.UI.Jkr.Panel.Core
{
    /// <summary>What the pane body shows (the title and context bars always show).</summary>
    public enum ShellState
    {
        /// <summary>No project open, or the active document is a family / template.</summary>
        NoModel,
        /// <summary>The backend answered 401: sign in to BINA AI.</summary>
        LoginRequired,
        /// <summary>The audit screens (Start / Running / Results).</summary>
        Content,
    }

    /// <summary>The active Revit document, as plain data.</summary>
    public sealed class ActiveModelInfo
    {
        public static readonly ActiveModelInfo None = new ActiveModelInfo("", "", false);

        public string Title { get; }
        public string PathName { get; }
        /// <summary>Family document (.rfa) — can't be audited.</summary>
        public bool IsFamily { get; }

        public ActiveModelInfo(string title, string pathName, bool isFamily)
        {
            Title = title ?? "";
            PathName = pathName ?? "";
            IsFamily = isFamily;
        }

        public bool IsOpen => Title.Length > 0 || PathName.Length > 0;

        /// <summary>A project template (.rte) — can't be audited either.</summary>
        public bool IsTemplate => PathName.EndsWith(".rte", StringComparison.OrdinalIgnoreCase);

        public bool CanAudit => IsOpen && !IsFamily && !IsTemplate;

        /// <summary>File name for the context bar (prototype projectFile): the saved
        /// file's name, else the document title, else "—".</summary>
        public string FileLabel
        {
            get
            {
                if (PathName.Length > 0)
                {
                    try { return Path.GetFileName(PathName); } catch { /* odd path: fall back */ }
                }
                return Title.Length > 0 ? Title : "—";
            }
        }

        public override bool Equals(object obj) =>
            obj is ActiveModelInfo o && o.Title == Title && o.PathName == PathName && o.IsFamily == IsFamily;
        public override int GetHashCode() => (Title + "|" + PathName + "|" + IsFamily).GetHashCode();
    }

    /// <summary>
    /// The active model, published by the add-in (ViewActivated / DocumentClosed)
    /// and read by the pane shell. Revit-free: the add-in sets it from the Revit
    /// thread; the shell marshals to its own thread.
    /// </summary>
    public static class ActiveModelSource
    {
        private static ActiveModelInfo _current = ActiveModelInfo.None;
        public static ActiveModelInfo Current => _current;
        public static event Action<ActiveModelInfo> Changed;

        public static void Set(ActiveModelInfo info)
        {
            info = info ?? ActiveModelInfo.None;
            if (info.Equals(_current)) return;
            _current = info;
            Changed?.Invoke(info);
        }
    }

    /// <summary>
    /// The pane shell (prototype .tb title bar + .ctx context bar + body): which
    /// body state shows and the shell's strings. WPF-free.
    /// </summary>
    public sealed class PaneShellModel : INotifyPropertyChanged
    {
        /// <summary>Shell strings the prototype does not have: the "Waiting for a model"
        /// state (flow s-nomodel) and the kept login-required state.</summary>
        private static readonly Dictionary<string, string> Extra = new Dictionary<string, string>
        {
            ["waiting"] = "Waiting for a model",
            ["noModelT"] = "Open a Revit model to audit it",
            ["noModelS"] = "The pane picks up the active model as soon as one is open.",
            ["loginT"] = "Log in to BINA to run the audit",
            ["loginS"] = "Some checks run on the BINA backend. Log in from the Bina tab on the Revit ribbon, then retry.",
            ["loginRetry"] = "Retry",
        };

        private ActiveModelInfo _model = ActiveModelInfo.None;
        private bool _loginRequired;

        public event PropertyChangedEventHandler PropertyChanged;

        public ActiveModelInfo Model
        {
            get => _model;
            set { var v = value ?? ActiveModelInfo.None; if (v.Equals(_model)) return; _model = v; RaiseAll(); }
        }

        /// <summary>Set after a scan answers 401; cleared by the next successful scan.</summary>
        public bool LoginRequired
        {
            get => _loginRequired;
            set { if (_loginRequired == value) return; _loginRequired = value; RaiseAll(); }
        }

        /// <summary>No auditable model beats login (nothing to sign in for yet).</summary>
        public ShellState State =>
            !_model.CanAudit ? ShellState.NoModel
            : _loginRequired ? ShellState.LoginRequired
            : ShellState.Content;

        public bool ShowNoModel => State == ShellState.NoModel;
        public bool ShowLogin => State == ShellState.LoginRequired;
        public bool ShowContent => State == ShellState.Content;

        // ── strings ──
        public string T(string key) => Extra.TryGetValue(key, out var p) ? p : PanelStrings.Get(key);
        public string PanelTitle => T("panelTitle");
        public string ProjectLabel => T("project");
        public string FileLabel => _model.CanAudit ? _model.FileLabel : "—";
        /// <summary>Header second line: the file (mono) when a model is open, else "Waiting for a model".</summary>
        public string HeaderSubtitle => _model.CanAudit ? _model.FileLabel : T("waiting");
        public bool HeaderIsFile => _model.CanAudit;
        public string NoModelTitle => T("noModelT");
        public string NoModelText => T("noModelS");
        public string LoginTitle => T("loginT");
        public string LoginText => T("loginS");
        public string LoginRetry => T("loginRetry");
        public string CloseLabel => T("close");

        private void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
}
